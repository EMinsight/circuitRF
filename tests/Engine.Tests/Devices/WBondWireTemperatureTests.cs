using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using CircuitRF.WBond;
using CircuitRF.WBond.Thermal;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// brief-wbond-wire-temperature §8 — the wire temperature through the engines: G5 (single-tone HB), G6 (a drive sweep, its
/// .npy, and two-tone), G6b (DC), G7/G7b (the stamp temperature), G8 (loadpull), G9 (the refusal) and the engine halves of G13.
/// </summary>
public sealed class WBondWireTemperatureTests(ITestOutputHelper output) : IDisposable
{
    private const double D = 2.54e-5, Sigma = 4.1e7, K = 318;
    private static double Area => Math.PI * D * D / 4;
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string f in _files) try { File.Delete(f); } catch { /* best effort */ }
    }

    private string Temp(string ext, string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wbt-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, text);
        _files.Add(path);
        return path;
    }

    /// <summary>A material library: "Flat" (σ and k constant — the closed forms' metal) and "NoK" (no thermal conductivity).</summary>
    private string Library() => Temp(".cmat", """
        { "Materials": [
            { "Name": "Flat", "Sigma20": 4.1e7, "Alpha20": 0, "ThermalK": 318 },
            { "Name": "NoK",  "Sigma20": 4.1e7, "Alpha20": 0.0039 } ] }
        """);

    /// <summary>Straight 1 mil wires 40 mil long at 4 mil, <paramref name="perArray"/> to an array, 6 mil apart.</summary>
    private string Design(string metal, int arrays = 1, int perArray = 1)
    {
        var design = new WBondDesign();
        int n = 0;
        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int i = 0; i < perArray; i++, n++)
                array.Wires.Add(new Wire
                {
                    Points = { Point3.Mils(0, 6 * n, 4), Point3.Mils(40, 6 * n, 4) },
                    DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil), Material = metal,
                });
            design.Arrays.Add(array);
        }
        string path = Path.Combine(Path.GetTempPath(), $"wbt-{Guid.NewGuid():N}.wBond");
        WBondIo.WriteFile(path, design);
        _files.Add(path);
        return path;
    }

    private static double L => WBondUnits.ToMetres(WBondUnits.ToNm(40, WBondUnit.Mil));

    /// <summary>G1's closed form: T = Ta + (Tb−Ta)s/L + q′s(L−s)/(2kA), its maximum.</summary>
    private static double ClosedFormMax(double q, double ta, double tb)
    {
        double s = L / 2 + (tb - ta) * K * Area / (q * L);
        if (!(s > 0 && s < L)) return Math.Max(ta, tb);
        return ta + (tb - ta) * s / L + q * s * (L - s) / (2 * K * Area);
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static DataSet RunDc(string cnl)
    {
        using var nl = Elaborate(cnl);
        return DcResultPacker.Pack(NonlinearDcEngine.Run(nl), nl);
    }

    private static (DataSet Ds, ElaboratedNetlist Nl) RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), nl.ResolvedGlobals);
        return (new HbEngine(nl, tb).Run(p).DataSet, nl);
    }

    private static DataSet RunSweep(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().First(), lib, tb);
    }

    private static double[] Wire(DataSet ds, string cube = WireTemperatureCubes.Cube) => ds[cube].RealValues;

    private string DcCnl(string extra = "", string current = "1.2") => $"""
        Ibias = {current}
        I_1Tone:I1  a 0  Idc=Ibias  Freq=1e9  I=0
        wBond:WB1   a b 0  File="{Design("Flat")}" MaterialLibrary="{Library()}" FixedTemp=false {extra}
        R:R1        b 0  R=1
        analysis DC1 type=dc
        """;

    // ── G6b: DC ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void G6b_DcCurrentThroughASolvedWBond_IsTheClosedForm()
    {
        var ds = RunDc(DcCnl());
        Assert.Equal(["WB1:G1"], ds[WireTemperatureCubes.Cube].Axes[0].Labels!);
        Assert.Equal("°C", ds[WireTemperatureCubes.Cube].Unit);
        Assert.Equal(1.0, Wire(ds, WireTemperatureCubes.StateCube)[0]);
        double expected = ClosedFormMax(1.2 * 1.2 / (Sigma * Area), 125, 85);
        output.WriteLine($"DC 1.2 A: WireTemp {Wire(ds)[0]:F5} °C against {expected:F5} °C");
        Assert.Equal(expected, Wire(ds)[0], 0.01);
    }

    [Fact]
    public void G6b_AOnePointSweep_IsTheStandaloneRun_AndABiasSweepGainsTheAxis()
    {
        var alone = RunDc(DcCnl());
        var one = RunSweep(DcCnl() + "\nanalysis SW type=parametric_sweep Var=Ibias Values=1.2 Inner=DC1");
        Assert.Equal(Wire(alone), Wire(one));
        Assert.Equal(Wire(alone, WireTemperatureCubes.StateCube), Wire(one, WireTemperatureCubes.StateCube));

        var swept = RunSweep(DcCnl() + "\nanalysis SW type=parametric_sweep Var=Ibias Values=0,0.6,1.2 Inner=DC1");
        var cube = swept[WireTemperatureCubes.Cube];
        Assert.Equal(["Ibias", WireTemperatureCubes.AxisName], cube.Axes.Select(a => a.Name));
        Assert.Equal("°C", cube.Unit);
        var t = cube.RealValues;
        Assert.Equal(125.0, t[0]);                 // zero current: the hotter end, exactly
        Assert.True(t[0] < t[1] && t[1] < t[2], string.Join(", ", t));
        Assert.Equal(Wire(alone)[0], t[2]);
    }

    // ── G13 (engine): a DC-blocked array ─────────────────────────────────────────────────────────────────────

    private string BlockedCnl(string analysis) => $"""
        I_1Tone:I1  a 0  Idc=1.0  Freq=2e9  I=1.0
        wBond:WB1   a b x y 0  File="{Design("Flat", arrays: 2, perArray: 2)}" MaterialLibrary="{Library()}" FixedTemp=false TempStart=100 TempEnd=100 IncludeCapacitance=false
        R:R1        b 0  R=1
        C:Cblk      x 0  C=1e-15
        R:Ry        y 0  R=1
        SDD:D1      b 0  Ports=1  I[1,0]=1e-4*_v1^3
        {analysis}
        """;

    [Fact]
    public void G13_ADcBlockedArray_ReadsTheHotterEnd_AndItsNeighbourIsUnaffected()
    {
        using var nl = Elaborate(BlockedCnl("analysis DC1 type=dc"));
        var dc = NonlinearDcEngine.Run(nl);
        var ds = DcResultPacker.Pack(dc, nl);
        var t = Wire(ds);
        Assert.Equal(0.0, dc.WBondArrayCurrents["WB1"][1], 12);
        Assert.Equal(100.0, t[1]);
        Assert.Equal(1.0, Wire(ds, WireTemperatureCubes.StateCube)[1]);
        Assert.Empty(nl.Warnings);

        // the live array is what it is alone: its own current, its own wires
        var model = nl.Components.Select(c => c.Model).OfType<WBondModel>().Single();
        var alone = WBondWireTemperature.Compute(model.Thermal, [dc.WBondArrayCurrents["WB1"][0], 0.0], [], []);
        Assert.Equal(alone[0].WireTempC, t[0]);
        Assert.True(t[0] > 101, $"{t[0]:F2} °C");
    }

    [Fact]
    public void G13_ADcBlockedArrayInHb_HeatsFromItsCirculatingCurrent()
    {
        var (ds, nl) = RunHb(BlockedCnl("analysis HB1 type=hb Tone=2e9 MaxHarm=2"));
        var t = Wire(ds);
        var model = nl.Components.Select(c => c.Model).OfType<WBondModel>().Single();
        // the blocked array's own current is ~nil (1 fF at 2 GHz): what heats it is its neighbour's, circulating
        var own = WBondWireTemperature.Compute(model.Thermal, [0.0, 0.0], [2e9], [[Complex.Zero, Complex.Zero]]);
        output.WriteLine($"blocked array {t[1]:F6} °C, live array {t[0]:F4} °C");
        Assert.Equal(100.0, own[1].WireTempC);
        Assert.True(t[1] > 100 + 1e-6, $"{t[1]:F9} °C");
        Assert.Equal(1.0, Wire(ds, WireTemperatureCubes.StateCube)[1]);
    }

    // ── G5: single-tone HB ───────────────────────────────────────────────────────────────────────────────────

    private string HbCnl(string wbond, string drive = "1") => $"""
        Vamp = {drive}
        V_1Tone:Vs  a 0  Vdc=0.5  Freq=2e9  V=Vamp  Phase=0
        IProbe:IP   a a2
        {wbond}
        R:RL        b 0  R=1
        SDD:D1      b 0  Ports=1  I[1,0]=1e-3*_v1^3
        analysis HB1 type=hb Tone=2e9 MaxHarm=3
        """;

    // capacitance off: with it on the probe also carries the input shunt's current, which does not flow in the wires
    private string SolvedWBond => $"wBond:WB1 a2 b 0 File=\"{Design("Gold")}\" FixedTemp=false IncludeCapacitance=false";

    [Fact]
    public void G5_SingleToneHb_IsTheSolveOnTheProbedCurrents_FixedReportsTemp_AndNoWBondHasNoCube()
    {
        var (ds, nl) = RunHb(HbCnl(SolvedWBond));
        var probe = ds["I"];
        int b = Array.IndexOf(probe.Axes[0].Labels!, "IP");
        var spec = Enumerable.Range(0, 4).Select(k => probe.ComplexValues[b * 4 + k]).ToArray();
        var model = nl.Components.Select(c => c.Model).OfType<WBondModel>().Single();
        var direct = WBondWireTemperature.Compute(model.Thermal, [spec[0].Real], [2e9, 4e9, 6e9], [[spec[1]], [spec[2]], [spec[3]]]);
        output.WriteLine($"HB WireTemp {Wire(ds)[0]:F6} °C, direct {direct[0].WireTempC:F6} °C; |I1| {spec[1].Magnitude:G4} A");
        Assert.True(direct[0].WireTempC > 125.5);
        Assert.Equal(direct[0].WireTempC, Wire(ds)[0], 1e-6);
        Assert.Equal(1.0, Wire(ds, WireTemperatureCubes.StateCube)[0]);

        var (fixedDs, _) = RunHb(HbCnl($"wBond:WB1 a2 b 0 File=\"{Design("Gold")}\" Temp=100"));
        Assert.Equal(100.0, Wire(fixedDs)[0]);
        Assert.Equal(0.0, Wire(fixedDs, WireTemperatureCubes.StateCube)[0]);

        var (none, _) = RunHb(HbCnl("R:Rw a2 b R=0.01"));
        Assert.False(none.Contains(WireTemperatureCubes.Cube));
        Assert.False(none.Contains(WireTemperatureCubes.StateCube));
    }

    // ── G6: a drive sweep, its .npy, two-tone ────────────────────────────────────────────────────────────────

    [Fact]
    public void G6_ADriveSweep_GainsTheAxis_RisesWithDrive_AndExportsToNpy()
    {
        var ds = RunSweep(HbCnl(SolvedWBond) + "\nanalysis SW type=parametric_sweep Var=Vamp Values=0.25,0.5,1,1.5 Inner=HB1");
        var cube = ds[WireTemperatureCubes.Cube];
        Assert.Equal(["Vamp", WireTemperatureCubes.AxisName], cube.Axes.Select(a => a.Name));
        var t = cube.RealValues;
        for (int i = 1; i < t.Length; i++) Assert.True(t[i] >= t[i - 1], string.Join(", ", t));
        Assert.Equal([1.0, 1.0, 1.0, 1.0], ds[WireTemperatureCubes.StateCube].RealValues);

        string npy = Path.Combine(Path.GetTempPath(), $"wbt-{Guid.NewGuid():N}.npy");
        _files.Add(npy);
        DataSetExporter.Export(ds, npy, ExportFormat.Npy);
        var back = DataSetImporter.Import(npy).DataSet;
        Assert.Equal(t, back[WireTemperatureCubes.Cube].RealValues);
    }

    [Fact]
    public void G6_TwoTone_HeatsWithEveryMixingProduct()
    {
        string cnl = $"""
            V_nTone:Vs  a 0  Vdc=0.4  NumFreqs=2  Freq[1]=1e9 V[1]=1 Phase[1]=0  Freq[2]=1.1e9 V[2]=0.7 Phase[2]=0
            IProbe:IP   a a2
            wBond:WB1   a2 b 0  File="{Design("Flat")}" MaterialLibrary="{Library()}" FixedTemp=false TempStart=100 TempEnd=100 IncludeCapacitance=false
            R:RL        b 0  R=1
            SDD:D1      b 0  Ports=1  I[1,0]=1e-2*_v1^3
            analysis HB1 type=hb NumFreqs=2 Tone[1]=1e9 Tone[2]=1.1e9 MaxMixOrder=3 MaxHarm=3
            """;
        var (ds, _) = RunHb(cnl);
        var cube = ds["I"];
        int b = Array.IndexOf(cube.Axes[0].Labels!, "IP");
        var mix = cube.Axes[1];
        // by hand: q′ = I_dc²/(σA) + Σ ½|I_m|²·R′_ac(|f_m|, σ), and the ends equal: T = 100 + q′L²/(8kA)
        double q = 0;
        int nonDc = 0;
        for (int m = 0; m < mix.Length; m++)
        {
            var i = cube.ComplexValues[b * mix.Length + m];
            double f = Math.Abs(mix.Values[m]);
            if (f == 0) q += i.Real * i.Real / (Sigma * Area);
            else
            {
                q += 0.5 * i.Magnitude * i.Magnitude * InternalImpedance.PerMetre(f, D / 2, Sigma).ResistancePerMetre;
                if (i.Magnitude > 1e-6) nonDc++;
            }
        }
        double expected = 100 + q * L * L / (8 * K * Area);
        output.WriteLine($"two-tone WireTemp {Wire(ds)[0]:F6} °C, by hand {expected:F6} °C over {nonDc} products carrying current");
        Assert.True(nonDc > 4);
        Assert.Equal(expected, Wire(ds)[0], 1e-6);
    }

    // ── G7/G7b: the stamp temperature ──────────────────────────────────────────────────────────────────────

    private static Complex[] Stamped(ElaboratedNetlist nl, double hz)
        => nl.Components.Select(c => c.Model).OfType<WBondModel>().Single().ArrayImpedance(hz);

    [Fact]
    public void G7_NoFixedTemp_IsFixedMode_BitForBit()
    {
        string wb = Design("Gold");
        using var absent = Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" Temp=85\nR:R a 0 R=50\nR:R2 b 0 R=50");
        using var stated = Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" Temp=85 FixedTemp=true\nR:R a 0 R=50\nR:R2 b 0 R=50");
        var model = absent.Components.Select(c => c.Model).OfType<WBondModel>().Single();
        Assert.False(model.ThermalSpec.Solved);
        Assert.Equal(85.0, model.Design.OperatingTempC);
        Assert.Equal(Stamped(stated, 5e9), Stamped(absent, 5e9));
    }

    [Fact]
    public void G7b_SolvedMode_StampsAtTheHotterEnd_WhicheverEndItIs()
    {
        string wb = Design("Gold");
        using var fixed125 = Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" Temp=125 FixedTemp=true\nR:R a 0 R=50");
        using var solved = Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" FixedTemp=false TempStart=125 TempEnd=85\nR:R a 0 R=50");
        using var swapped = Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" FixedTemp=false TempStart=85 TempEnd=125\nR:R a 0 R=50");
        foreach (double hz in new[] { 0.0, 2e9, 8e9 })
        {
            Assert.Equal(Stamped(fixed125, hz), Stamped(solved, hz));
            Assert.Equal(Stamped(fixed125, hz), Stamped(swapped, hz));
        }
    }

    /// <summary>The Inspector's GroundPlane picker writes the word: it must reach the model (it was silently dropped, so a
    /// payload stored with its plane off — the example's Pads.wBond — was refused whatever the picker said).</summary>
    [Fact]
    public void GroundPlaneWrittenAsAWord_ReachesTheModel()
    {
        var design = new WBondDesign { GroundPlane = new GroundPlane { Enabled = false } };
        design.Arrays.Add(new WireArray { Name = "G1", Wires = { new Wire { Points = { Point3.Mils(0, 0, 4), Point3.Mils(40, 0, 4) } } } });
        string path = Path.Combine(Path.GetTempPath(), $"wbt-{Guid.NewGuid():N}.wBond");
        WBondIo.WriteFile(path, design);
        _files.Add(path);
        foreach (var (word, plane) in new[] { ("true", true), ("false", false), ("1", true) })
        {
            using var nl = Elaborate($"wBond:WB1 a b 0 File=\"{path}\" GroundPlane={word}\nR:R a 0 R=50");
            Assert.Equal(plane, nl.Components.Select(c => c.Model).OfType<WBondModel>().Single().Design.GroundPlane.Enabled);
        }
    }

    // ── G9: the refusal ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void G9_SolvedModeWithAMetalStatingNoK_Refuses_AndFixedModeRuns()
    {
        string wb = Design("NoK"), lib = Library();
        var ex = Assert.ThrowsAny<Exception>(() =>
            Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" MaterialLibrary=\"{lib}\" FixedTemp=false\nR:R a 0 R=50"));
        Assert.Contains("wBond 'WB1' solves its wire temperature, and its wire material 'NoK' states no thermal conductivity. " +
                        "State ThermalK for 'NoK' in the Materials editor, or check Temp to fix the temperature.", ex.ToString());

        using var nl = Elaborate($"wBond:WB1 a b 0 File=\"{wb}\" MaterialLibrary=\"{lib}\" FixedTemp=true\nI_1Tone:I1 a 0 Idc=1 Freq=1e9 I=0\nR:R b 0 R=1");
        var ds = DcResultPacker.Pack(NonlinearDcEngine.Run(nl), nl);
        Assert.Equal(0.0, Wire(ds, WireTemperatureCubes.StateCube)[0]);
    }

    // ── G8: loadpull ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void G8_Loadpull_CarriesWireTempOnGridPinAndArray()
    {
        string cnl =
            "define MyFET (gate drain)\n" +
            "  Sv = -0.837\n  Sc = 0.71\n  TV0 = 4.268\n  TC = 1.507\n  th = 0.001\n" +
            "  a = 0.176\n  g = 0.089\n  lam = 0.0012\n  B = 1130\n" +
            "  SDD:X1  gate  0  drain  0  I[1,0]=_v1/5000  " +
            "I[2,0]=(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*" +
            "ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2\n" +
            "end MyFET\n\n" +
            "MyFET:X1  n1  n2\n" +
            $"wBond:WB1 n2 n3 0 File=\"{Design("Gold", perArray: 2)}\" FixedTemp=false TempStart=100 TempEnd=100\n" +
            "Tuner:SourceTuner1  n1  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=-3.05  Z[1]=50\n" +
            // the load tuner must face a nonlinear node: a negligible one beyond the wires
            "SDD:Dx  n3  0  Ports=1  I[1,0]=1e-9*_v1^3\n" +
            "Tuner:LoadTuner1  n3  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=28  Z[1]=50\n" +
            "analysis LPP1 type=loadpull_pursuit Tone=2.2e9 MaxHarm=3 " +
            "LoadTuner=LoadTuner1 SourceTuner=SourceTuner1 Sweep=Load TuneHarm=1 Compression=3 " +
            "GainType=Gt PinStart=0 PinStep=5 PinMax=10 MaxIter=100 Tol=1e-7 " +
            "SearchMethod=IteratedQuadratic CreateLoadpullResult=false\n";
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var pp = LoadpullPursuitEngine.Resolve(tb.Analyses.OfType<LoadpullPursuitAnalysis>().First(), nl.ResolvedGlobals);
        var grid = new GamReader.GamGrid([.. new[] { new Complex(30, 0), new Complex(20, 10) }
            .Select((z, i) => new GamReader.GamPoint(RfCore.RfHelpers.Z2G(z / 50), z, i))], 50);
        var ds = new LoadpullEngine(nl, tb).Run(pp.LpParams with { Grid = grid });

        var cube = ds[WireTemperatureCubes.Cube];
        Assert.Equal(["gridPoint", "pinStep", WireTemperatureCubes.AxisName], cube.Axes.Select(a => a.Name));
        var converged = ds["Converged"].RealValues;
        var t = cube.RealValues;
        int arrays = cube.Axes[2].Length;
        for (int i = 0; i < converged.Length; i++)
            if (converged[i] == 1.0)
                for (int k = 0; k < arrays; k++)
                    Assert.True(double.IsFinite(t[i * arrays + k]) && t[i * arrays + k] > 100, $"point {i}: {t[i * arrays + k]}");
        output.WriteLine($"loadpull WireTemp: {string.Join(", ", t.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)))}");
    }
}
