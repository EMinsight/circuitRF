// ================================================================
//  ModelComposerExampleAuthoring.cs — the Model Composer example (brief-modelcomposer-16 R-mc16-1), BUILT with the
//  functions the window and `circuitrf model` call rather than written as JSON. Skipped unless
//  CRF_AUTHOR_MODEL_COMPOSER=1, because it WRITES examples/Model Composer (the workspace folder is replaced; the README is
//  kept). It is kept so the workspace can be rebuilt after a format change, and so the steps the README describes are
//  the ones that produced the files.
//
//  The steps, in the order a user takes them:
//    New Workspace ▸ the GaN two-shell network as a cell of its own ▸ for synthetic-gan: New (Active, 4 × 125 µm), the
//    open/short pads, one import per data set, the settings (Lg, Ld, Ls free; Rth and τth typed; drain lag), Fit all,
//    Export ▸ for simulated-tables: New, one SimulatedTables import, Fit all ▸ for passive-spiral: New (Passive), one
//    family import, the spiral topology with its substrate sides tied, Fit, Export as a cell ▸ the four test benches,
//    drawn from their netlists ▸ Export Verilog-A of the GaN model, and the bench that places it beside the .cmodel.
//
//  The data is synthetic (SyntheticGan, SyntheticFetData, the spiral truth below) and every data set's Note says so.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ModelComposer;
using CircuitRF.Ui.Tests.ModelComposer.Passive;
using NumFlat;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ModelComposerExampleAuthoring(ITestOutputHelper output)
{
    public const string Folder = "Model Composer";
    public const string Network = "GaNTwoShell";
    public const string GanModel = "synthetic-gan/gan_4x125.cmodel";
    /// <summary>The same model as Verilog-A source, written from the <c>.cmodel</c> (brief-modelcomposer-28 R-mc28-4).</summary>
    public const string GanVa = "synthetic-gan/gan_4x125.va";
    public const string VerilogABench = "GanVerilogA";
    /// <summary>The export's provenance timestamp, fixed so a rebuild writes the same bytes.</summary>
    public const string Created = "2026-10-20T00:00:00Z";

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_MODEL_COMPOSER") != "1")
                Skip = "re-builds examples/Model Composer only with CRF_AUTHOR_MODEL_COMPOSER=1";
        }
    }

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string examples = Path.Combine(PalaceBackendTests.RepoRoot(), "examples");
        string ws = Path.Combine(examples, Folder);
        string readme = Path.Combine(ws, "README.md");
        string? keptReadme = File.Exists(readme) ? File.ReadAllText(readme) : null;
        var keptLayouts = HandLaid.Select(c => Path.Combine(ws, c, "schematic", c + ".csch"))
                                  .Where(File.Exists).ToDictionary(f => f, File.ReadAllText);
        if (Directory.Exists(ws)) Directory.Delete(ws, true);

        WorkspaceCreate.Create(examples, Folder, null);
        if (keptReadme is not null) File.WriteAllText(readme, keptReadme);
        // The workspace ignores *.npy, which is right for results; this example's tables are data, and each document's
        // cache holds its fitted arrays, which only a fit can rebuild.
        File.AppendAllText(Path.Combine(ws, ".gitignore"),
            "\n# The Model Composer example ships its simulated tables and each document's cache, which holds the fits.\n" +
            "!simulated-tables/*.npy\n!**/*.cmc-cache/**\n");
        File.AppendAllText(Path.Combine(ws, ".gitattributes"),
            "\n# The Verilog-A export is compared byte for byte with the writer's output; line-ending conversion would break that.\n" +
            "*.va -text\n");

        BuildSyntheticGan(ws);
        BuildSimulatedTables(ws);
        BuildPassiveSpiral(ws);
        BuildGanBenches(ws);
        BuildSpiralFilter(ws);
        BuildVerilogA(ws);
        foreach (var (file, text) in keptLayouts) File.WriteAllText(file, text);
    }

    /// <summary>The Verilog-A export and its bench alone, into the workspace as it stands: what a change to the writer or
    /// to the bench needs, without refitting every document.</summary>
    [AuthorFact]
    public void BuildTheVerilogABench()
    {
        string ws = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);
        string cell = Path.Combine(ws, VerilogABench);
        if (Directory.Exists(cell)) Directory.Delete(cell, true);
        BuildVerilogA(ws);
    }

    /// <summary>The bench schematics whose component placement was tidied by hand after they were drawn: a rebuild draws them
    /// from their netlists, then puts the hand-laid file back. A change to a bench's netlist is a change to its hand-laid
    /// schematic too: `circuitrf netlist` of the two must agree.</summary>
    private static readonly string[] HandLaid = ["GanPowerSweep"];

    // ── The network, a cell ───────────────────────────────────────────────────

    /// <summary>The stock network the document copied beside itself, made a cell of the workspace and named from there.</summary>
    private static void UseNetworkCell(string ws, string cmc)
    {
        string cellSch = Path.Combine(ws, Network, "schematic", Network + ".csch");
        string beside = Path.Combine(Path.GetDirectoryName(cmc)!, "gan-two-shell.csch");
        if (!File.Exists(cellSch))
        {
            CellCreate.Create(ws, Network, CellViews.Schematic);
            var (model, _, _) = SchematicPersistence.LoadFromFile(beside);
            File.WriteAllText(cellSch, SchematicPersistence.Serialize(model, Network));
        }
        File.Delete(beside);
        var doc = CmcFile.Read(cmc);
        string relative = Path.GetRelativePath(Path.GetDirectoryName(cmc)!, cellSch).Replace('\\', '/');
        CmcFile.Write(cmc, CmcExtrinsic.With(doc, CmcExtrinsic.Network(doc) with { Schematic = relative }));
    }

    // ── synthetic-gan ─────────────────────────────────────────────────────────

    private void BuildSyntheticGan(string ws)
    {
        string dir = Path.Combine(ws, "synthetic-gan");
        SyntheticGan.WriteRaw(dir);
        string cmc = SyntheticGanDocument.Create(dir);
        UseNetworkCell(ws, cmc);
        SyntheticGanDocument.Configure(cmc);
        foreach (var l in ModelCommands.Deembed(cmc)) output.WriteLine(l.Text);
        foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
        CmodelExport.Export(cmc, Path.Combine(ws, GanModel), created: Created);
    }

    // ── simulated-tables ──────────────────────────────────────────────────────

    private void BuildSimulatedTables(string ws)
    {
        string dir = Path.Combine(ws, "simulated-tables");
        Directory.CreateDirectory(dir);
        SyntheticGan.WriteTables(Path.Combine(dir, "tables.npy"));
        string cmc = Path.Combine(dir, "fet_tables.cmc");
        ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell", new BasePeriphery(4, "125 um"));
        UseNetworkCell(ws, cmc);
        ModelCommands.Import(cmc, new CmcDataSet
        {
            Name = "tables", Kind = DataSetKind.SimulatedTables, Sources = ["tables.npy"],
            Import = new ImportSettings
            {
                Format = ImportSettings.Npy,
                Arrays = new Dictionary<string, string>
                {
                    ["vgs"] = "vgs", ["vds"] = "vds", ["ig"] = "ig", ["id"] = "id", ["vgsi"] = "vgsi", ["vdsi"] = "vdsi",
                    ["Y"] = "Y", ["freq"] = "freq",
                },
            },
            Conditions = new DataConditions
            {
                ReferencePlane = ReferencePlane.Device, AmbientC = 25,
                Note = "Synthetic: tables simulated by circuitRF from a known device inside the GaN two-shell network, as another simulator would write them.",
            },
        });
        // The tables were simulated through the stock network at its stock values, so every value is held.
        var doc = CmcFile.Read(cmc);
        var n = CmcExtrinsic.Network(doc);
        CmcFile.Write(cmc, CmcExtrinsic.With(doc, n with
        {
            Variables = n.Variables.ToDictionary(kv => kv.Key, kv => kv.Value with { Lock = true }, StringComparer.Ordinal),
        }));
        foreach (var l in ModelCommands.Deembed(cmc)) output.WriteLine(l.Text);
        foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
    }

    // ── passive-spiral ────────────────────────────────────────────────────────

    public static readonly double[] Turns = [1.5, 2.5, 3.5, 4.5, 5.5, 6.5];
    public const string Family = "SpiralFamily";

    /// <summary>The spirals' element values: smooth in the turns but not polynomial (L ∝ t^1.8, R_sub ∝ 1/t) — the truth of
    /// <c>FamilyFitTests</c>.</summary>
    public static Dictionary<string, double> SpiralTruth(double t)
    {
        double cox = 30e-15 * t + 5e-15 * t * t, rsub = 1500 / t, csub = 6e-15 * t;
        return new()
        {
            ["Rs"] = 0.9 * t + 0.3, ["Ls"] = 0.55e-9 * Math.Pow(t, 1.8), ["Cs"] = 8e-15 * t,
            ["Cox1"] = cox, ["Rsub1"] = rsub, ["Csub1"] = csub, ["Cox2"] = cox, ["Rsub2"] = rsub, ["Csub2"] = csub,
        };
    }

    private void BuildPassiveSpiral(string ws)
    {
        string dir = Path.Combine(ws, "passive-spiral");
        Directory.CreateDirectory(Path.Combine(dir, "s"));
        double[] f = SyntheticPassiveData.Log(0.2e9, 20e9, 41);
        foreach (double t in Turns)
        {
            var block = SyntheticPassiveData.FromTopology("spiral", SpiralTruth(t), f, null, t);
            var mats = block.S.Select(m => { var a = new Mat<Complex>(2, 2); for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) a[i, j] = m[i, j]; return a; }).ToArray();
            TouchstoneIO.WriteFile(new SNP(f, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0)),
                                   Path.Combine(dir, "s", $"spiral_{t.ToString(CultureInfo.InvariantCulture)}t.s2p"), precision: "G12");
        }
        string cmc = Path.Combine(dir, "spiral.cmc");
        ModelCommands.New(cmc, CmcKind.Passive);
        ModelCommands.Import(cmc, new CmcDataSet
        {
            Name = "spirals", Kind = DataSetKind.PassiveSParams, Sources = ModelCommands.Sources(cmc, ["s"]),
            Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "spiral_{geometry}t", PatternSource = "name" },
            Conditions = new DataConditions { Note = "Synthetic: six spiral inductors simulated by circuitRF from known element values, not measured." },
        });
        var doc = CmcFile.Read(cmc);
        CmcFile.Write(cmc, PassiveStep.WithSettings(doc, new PassiveStepSettings
        {
            Topology = "spiral",
            Family = new FamilySettings { Geometry = "turns" },
            // The spiral is symmetric: the substrate network under port 2 is the one under port 1.
            Variables = new Dictionary<string, NetworkVariableSettings>(StringComparer.Ordinal)
            {
                ["Cox2"] = new() { Tie = new VariableTie("Cox1") },
                ["Rsub2"] = new() { Tie = new VariableTie("Rsub1") },
                ["Csub2"] = new() { Tie = new VariableTie("Csub1") },
            },
            Export = new PassiveExportSettings(Family, ".."),
        }));
        foreach (var l in ModelCommands.Fit(cmc, "passive")) output.WriteLine(l.Text);
        PassiveStep.ExportFile(cmc);
    }

    // ── The test benches ──────────────────────────────────────────────────────

    /// <summary>The class-AB stage at 2 GHz: an 8-finger instance of the 4-finger model, biased through chokes, 50 Ω in
    /// and out, drive swept to past compression.</summary>
    public const string PowerSweepNetlist = """
        Pin = 0
        RFfreq = 2 GHz
        VGS = -0.8
        VDD = 8
        temp = 25
        P1Tone:P1  n1  0  Num=1  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg
        C:C1  n1  Vin  C=100 pF
        IProbe:Iin  Vin  g
        L:L1  g  n2  L=100 nH
        Vdc:V1  n2  0  Vdc=VGS V
        ComposedFET:X1  g  Vout  0  File="synthetic-gan/gan_4x125.cmodel"  N=8
        L:L2  n3  Vout  L=100 nH
        IProbe:IDC  VDD  n3
        Vdc:V2  VDD  0  Vdc=VDD V
        IProbe:Iout  Vout  n4
        C:C2  n4  n5  C=100 pF
        R:RL  n5  0  R=50 Ohm
        analysis HB1 type=hb Tone="RFfreq" ToneUnit=GHz MaxHarm=5
        analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Start=-4 Stop=22 Step=1 Inner=HB1
        measure Pin_deliv_W = real(0.5*HB1.V("Vin",1)*conj(HB1.I("Iin",1)))
        measure Pout_W = real(0.5*HB1.V("Vout",1)*conj(HB1.I("Iout",1)))
        measure Pout_dBm = 10*log10(Pout_W*1000)
        measure Gt_dB = Pout_dBm - Pin
        measure PDC_W = real(HB1.V("VDD",0)*HB1.I("IDC",0))
        measure DE = Pout_W/PDC_W*100
        measure PAE = (Pout_W - Pin_deliv_W)/PDC_W*100
        labelednets Vin Vout VDD
        """;

    /// <summary>The same 8-finger device over a polar grid of loads at 2 GHz, each driven to 3 dB compression.</summary>
    public const string LoadpullNetlist = """
        RFfreq = 2 GHz
        VGS = -0.8
        VDS = 8
        temp = 25
        Tuner:SourceTuner1  Vin  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VGS  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6
        ComposedFET:X1  Vin  Vout  0  File="synthetic-gan/gan_4x125.cmodel"  N=8
        Tuner:LoadTuner1  Vout  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VDS  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6
        analysis LP1 type=loadpull Tone="RFfreq" ToneUnit=GHz MaxHarm=5 LoadTuner=LoadTuner1 SourceTuner=SourceTuner1 Grid="GanLoadpull/schematic/load_grid.gam" Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-4 PinStep=1 PinMax=26
        labelednets Vin Vout
        """;

    /// <summary>
    /// The shipped model and its Verilog-A export side by side (brief-modelcomposer-28 R-mc28-4): the 8-finger device as a
    /// <c>ComposedFET</c> (ports 1 and 2) and as a <c>VerilogA</c> (ports 3 and 4), each on its own copy of one bias
    /// network — chokes to the supplies, DC blocks to 50 Ω ports. Neither thermal terminal is wired: the VerilogA's is left
    /// off (<c>Pins=3</c>) and the ComposedFET's T pin is drawn unwired (the drawing needs a net for each of the symbol's
    /// pins, so <c>t1</c> names it and its label is removed), so both heat on the model's own network. A gate-voltage DC
    /// sweep, S-parameters at the bias, and a short HB drive sweep; the measures put each pair of numbers side by side.
    /// </summary>
    public const string VerilogANetlist = """
        Pin = 0
        RFfreq = 2 GHz
        VGS = -0.8
        VDD = 8
        temp = 25
        P1Tone:P1  a1  0  Num=1  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg
        IProbe:IIN1  a1  i1
        C:C1  i1  g1  C=100 pF
        L:L1  g1  b1  L=100 nH
        IProbe:IG1  b1  c1
        Vdc:V1  c1  0  Vdc=VGS V
        ComposedFET:X1  g1  d1  0  t1  File="synthetic-gan/gan_4x125.cmodel"  N=8
        L:L2  e1  d1  L=100 nH
        IProbe:ID1  VDD1  e1
        Vdc:V2  VDD1  0  Vdc=VDD V
        C:C2  d1  o1  C=100 pF
        Port:P2  o1  0  Num=2  Z=50 Ohm
        P1Tone:P3  a2  0  Num=3  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg
        IProbe:IIN2  a2  i2
        C:C3  i2  g2  C=100 pF
        L:L3  g2  b2  L=100 nH
        IProbe:IG2  b2  c2
        Vdc:V3  c2  0  Vdc=VGS V
        VerilogA:X2  g2  d2  0  File="synthetic-gan/gan_4x125.va"  Pins=3  N=8
        L:L4  e2  d2  L=100 nH
        IProbe:ID2  VDD2  e2
        Vdc:V4  VDD2  0  Vdc=VDD V
        C:C4  d2  o2  C=100 pF
        Port:P4  o2  0  Num=4  Z=50 Ohm
        analysis DC1 type=dc
        analysis DC1_sweep_VGS type=parametric_sweep Var=VGS Start=-2 Stop=0 Step=0.25 Inner=DC1
        analysis SP1 type=sparam Start=0.5 Stop=20 Npts=40 Unit=GHz
        analysis HB1 type=hb Tone="RFfreq" ToneUnit=GHz MaxHarm=5
        analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Start=0 Stop=16 Step=4 Inner=HB1
        measure Id_ComposedFET = DC1.I("ID1")
        measure Id_VerilogA = DC1.I("ID2")
        measure S21_ComposedFET = dB(SP1.S(2,1))
        measure S21_VerilogA = dB(SP1.S(4,3))
        measure Pout_W_ComposedFET = real(0.5*HB1.V("o1",1)*conj(HB1.V("o1",1)))/50
        measure Pout_W_VerilogA = real(0.5*HB1.V("o2",1)*conj(HB1.V("o2",1)))/50
        measure Pout_ComposedFET = 10*log10(Pout_W_ComposedFET*1000)
        measure Pout_VerilogA = 10*log10(Pout_W_VerilogA*1000)
        measure PAE_ComposedFET = (Pout_W_ComposedFET - real(0.5*HB1.V("a1",1)*conj(HB1.I("IIN1",1))))/real(VDD*HB1.I("ID1",0))*100
        measure PAE_VerilogA = (Pout_W_VerilogA - real(0.5*HB1.V("a2",1)*conj(HB1.I("IIN2",1))))/real(VDD*HB1.I("ID2",0))*100
        labelednets a1 g1 d1 o1 a2 g2 d2 o2
        """;

    /// <summary>The export of the shipped <c>.cmodel</c>, and the bench that places both.</summary>
    private static void BuildVerilogA(string ws)
    {
        CmodelVerilogA.Export(Path.Combine(ws, GanModel), Path.Combine(ws, GanVa));
        Draw(ws, VerilogABench, VerilogANetlist, unwired: "t1");
    }

    private void BuildGanBenches(string ws)
    {
        Draw(ws, "GanPowerSweep", PowerSweepNetlist);
        Draw(ws, "GanLoadpull", LoadpullNetlist);
        File.WriteAllText(Path.Combine(ws, "GanLoadpull", "schematic", "load_grid.gam"), LoadGrid());
    }

    /// <summary>
    /// The loadpull grid, centred on this device: rings about Γc = 0.25+j0.15, which lies between the maximum-power load
    /// (near 0.1∠180°) and the maximum-efficiency load (near 0.67∠27°), both found on a 277-point Γ map. The Loadpull
    /// example's polar grid about 50 Ω, which this bench first copied, put the efficiency optimum on its outer ring.
    /// </summary>
    private static string LoadGrid()
    {
        var c = new System.Numerics.Complex(0.25, 0.15);
        var lines = new List<string>
        {
            "# gamma Z0=50 mag_ang",
            "# 49 loads for the 8-finger GanLoadpull bench: rings of |Gamma - Gc| = 0.12, 0.24, 0.36, 0.48 at 12 angles about",
            "# Gc = 0.25+j0.15, which lies between the maximum-power load (near 0.1 at 180 deg) and the maximum-efficiency load",
            "# (near 0.67 at 27 deg), so both sit inside the grid with loads on every side. Points are |Gamma| and angle in degrees.",
        };
        var points = new List<System.Numerics.Complex> { c };
        foreach (double r in new[] { 0.12, 0.24, 0.36, 0.48 })
            for (int a = 0; a < 360; a += 30)
                points.Add(c + System.Numerics.Complex.FromPolarCoordinates(r, a * Math.PI / 180));
        foreach (var p in points)
            lines.Add(FormattableString.Invariant($"{p.Magnitude:F4}  {p.Phase * 180 / Math.PI:F2}"));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A test bench cell whose schematic is <paramref name="netlist"/> drawn — <c>netlist --to-schematic</c>'s own
    /// function. <paramref name="unwired"/> names a net on one pin that the drawing leaves unwired.</summary>
    private static SchematicEditModel Draw(string ws, string cell, string netlist, string? unwired = null)
    {
        var created = CellCreate.Create(ws, cell, CellViews.Schematic);
        string schDir = Path.GetDirectoryName(created.SchematicPath!)!;
        var (lib, tb) = new CnlReader().Read(netlist, cell, ws);
        var drawn = NetlistSchematic.Build(lib, tb, schDir);
        Assert.True(drawn.Schematic is not null, string.Join("; ", drawn.Refusals));
        // A net on one pin only is drawn as that pin's label; without it the pin is unwired, as a schematic leaves it.
        if (unwired is not null) Assert.Equal(1, drawn.Schematic!.NetLabels.RemoveAll(l => l.Name == unwired));
        // A schematic stores a File unquoted, relative to its workspace root; the drawing keeps the netlist's quotes.
        string json = SchematicPersistence.Serialize(drawn.Schematic!, cell)
            .Replace($"\"\\u0022{GanModel}\\u0022\"", $"\"{GanModel}\"", StringComparison.Ordinal)
            .Replace($"\"\\u0022{GanVa}\\u0022\"", $"\"{GanVa}\"", StringComparison.Ordinal);
        // The reader resolves a loadpull grid to an absolute path and the drawing keeps it; a schematic stores it relative
        // to its workspace root, as the Loadpull example's does.
        foreach (var lp in drawn.Schematic!.Analyses.OfType<CircuitRF.Core.Design.LoadpullAnalysis>())
            if (Path.IsPathRooted(lp.GridPath))
                json = json.Replace($"\"{lp.GridPath}\"", $"\"{Path.GetRelativePath(ws, lp.GridPath).Replace('\\', '/')}\"", StringComparison.Ordinal);
        File.WriteAllText(created.SchematicPath!, json);
        return drawn.Schematic!;
    }

    /// <summary>The fitted spiral family at 3 turns — between the 2.5 and 3.5 measured — in a π low-pass between two
    /// 50 Ω ports. Drawn from its netlist with an inductor where the spiral goes, then the inductor replaced by the
    /// family cell's instance and each of its pins wired to where the inductor's was.</summary>
    public const string SpiralNetlist = """
        Port:P1  in  0  Num=1  Z=50 Ohm
        C:C1  in  0  C=0.4 pF
        L:LSPIRAL  in  out  L=4 nH
        C:C2  out  0  C=0.4 pF
        Port:P2  out  0  Num=2  Z=50 Ohm
        analysis SP1 type=sparam Start=0.2 Stop=20 Npts=100 Unit=GHz
        measure S21_dB = dB(SP1.S(2,1))
        measure S11_dB = dB(SP1.S(1,1))
        """;

    private static void BuildSpiralFilter(string ws)
    {
        const string cell = "SpiralFilter";
        var m = Draw(ws, cell, SpiralNetlist);
        string sch = Path.Combine(ws, cell, "schematic", cell + ".csch");
        m.SchematicDirectory = Path.GetDirectoryName(sch);
        var placeholder = m.Components.Single(c => c.InstanceName == "LSPIRAL");
        var oldPins = m.PortDefsOf(placeholder).OrderBy(p => p.PortIndex).Select(p => m.PortWorldOf(placeholder, p)).ToList();

        string cellRef = "../../" + Family;
        var spiral = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, X = placeholder.X, Y = placeholder.Y, CellRef = cellRef,
            CellInterfaceHash = PlacedCellRef.HashFor(cellRef, m.SchematicDirectory),
        };
        var ccell = CellPersistence.LoadFromFile(Path.Combine(ws, Family, ".ccell"));
        // The geometry alone: every other parameter's default is a law in `turns`, which the cell evaluates in its own scope;
        // stated on the instance it would be evaluated in this schematic's, where `turns` means nothing.
        var turns = ccell.Parameters.Single(cp => cp.Name == "turns");
        spiral.Parameters.Add(new EditableParameter
        {
            Name = turns.Name, Unit = turns.Unit, Dimension = turns.Dimension, ShowOnSchematic = true, Expression = "3",
        });
        m.Components.Remove(placeholder);
        m.Components.Add(spiral);
        var newPins = m.PortDefsOf(spiral).OrderBy(p => p.PortIndex).Select(p => m.PortWorldOf(spiral, p)).ToList();
        Assert.Equal(2, newPins.Count);
        for (int k = 0; k < 2; k++)
        {
            if (oldPins[k] == newPins[k]) continue;
            var w = new EditableWire();
            w.Points.AddRange([oldPins[k], (newPins[k].X, oldPins[k].Y), newPins[k]]);
            m.Wires.Add(w);
        }
        File.WriteAllText(sch, SchematicPersistence.Serialize(m, cell));
    }
}
