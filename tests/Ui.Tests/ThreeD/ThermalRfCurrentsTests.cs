// ================================================================
//  ThermalRfCurrentsTests.cs — brief-em3d-78's design-side gates, with no mesher: the share of a drawn array against the
//  .wBond reduction of the same wires (gate 2) and against an independently solved X = L⁻¹A (gate 3), Peak against RMS and a
//  missing As (gate 4), the refusal for two arrays on one conductor and the Array form (gate 7), and a two-port driven from
//  both ends (gate 8). The solver's gates (1, 5, 6) are tests/Thermal.Tests/RfHarmonicGateTests.cs.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.WBond;
using BondStyle = CircuitRF.WBond.BondStyle;
using WireCrossSection = CircuitRF.WBond.WireCrossSection;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalRfCurrentsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th78-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per µm: a DBU is a nanometre, as a .wBond's is

    // ── gates 2 and 3: the share ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Five drawn wires on a .wBond's five centrelines: the per-wire share is that .wBond's own (ArrayShare.For — the path a placed
    /// layout's wires take in a run) bit for bit, so the wires' RF currents, the only thing the share hands the solver, are
    /// identical too (gate 2); and it equals the X = L⁻¹A column, solved here by plain elimination and scaled to unit current, to
    /// 1e-12 (gate 3). The edge wires carry more than the centre one.
    /// </summary>
    [Fact]
    public void Gate2And3_TheShare_IsWBonds()
    {
        var (e, lowering, plan) = Plan(Doc(count: 5), Current(port: 1, "pad/zmax", ("1", ThermalAmplitude.Peak)));
        Assert.Single(plan.Arrays);
        Assert.Equal("w1", plan.Arrays[0].Name);
        var names = lowering.Wires.Select(w => w.Name).ToList();
        var share = Enumerable.Range(0, 5).Select(k => plan.Share[0][names.IndexOf($"w1[{k}]")]).ToArray();

        // the .wBond of the same five wires (a .c3d states no ground plane, so neither does it)
        var design = new WBondDesign { GroundPlane = new GroundPlane { Enabled = false } };
        design.Arrays.Add(new WireArray { Name = "w1", Wires = [.. Enumerable.Range(0, 5).Select(k => new Wire
        {
            Points = [.. Axis().Select(q => new CircuitRF.WBond.Point3(q.X, q.Y + k * Pitch, q.Z))], DiameterNm = 25_400,
        })] });
        var ours = ArrayShare.For(design).PerUnitCurrent(0);
        output.WriteLine($"share: {string.Join(", ", share.Select(s => s.ToString("R")))}");
        for (int k = 0; k < 5; k++) Assert.Equal(ours[k], share[k]);                // bit for bit
        var peaks = plan.At(Value(e), 1);
        for (int k = 0; k < 5; k++) Assert.Equal(Math.Abs(ours[k]), peaks[names.IndexOf($"w1[{k}]")].Single().PeakA);
        Assert.True(share[0] > share[2] && share[4] > share[2], "the edge wires carry more than the centre one");
        Assert.Equal(1, share.Sum(), 1e-12);

        // gate 3: X = L⁻¹A by Gaussian elimination, independent of wBond's Cholesky, normalised to one ampere into the array
        var mesh = WireMesh.Build(design);
        var l = InductanceMatrix.Fill(mesh);
        var x = Solve([.. Enumerable.Range(0, 25).Select(i => l[i / 5, i % 5])], [1, 1, 1, 1, 1]);
        double sum = x.Sum();
        for (int k = 0; k < 5; k++) Assert.Equal(x[k] / sum, share[k], 1e-12);
    }

    // ── gate 4: Peak and RMS ────────────────────────────────────────────────────────────────────────

    /// <summary>1 A RMS and 1.4142135623730951 A peak give every wire the same current, bit for bit; a harmonic with no As is a
    /// check error naming the entry.</summary>
    [Fact]
    public void Gate4_PeakAndRms_AreOneCurrent_AndAMissingAsIsAnError()
    {
        var (e, _, rms) = Plan(Doc(count: 3), Current(1, "pad/zmax", ("1", ThermalAmplitude.Rms)));
        var (_, _, peak) = Plan(Doc(count: 3), Current(1, "pad/zmax", ("1.4142135623730951", ThermalAmplitude.Peak)));
        var a = rms.At(Value(e), 1);
        var b = peak.At(Value(e), 1);
        for (int j = 0; j < a.Length; j++) Assert.Equal(b[j].Single().PeakA, a[j].Single().PeakA);
        Assert.Equal(2e9, a[0].Single().FrequencyHz);

        var doc = Doc(count: 3);
        var c = Current(1, "pad/zmax", ("1", ThermalAmplitude.Peak));
        c.Harmonics!.Add(new CemThermalHarmonic { N = 2, Amp = "0.12" });
        var found = Check(doc, c);
        output.WriteLine(string.Join("\n", found));
        Assert.Contains(found, f => f.Contains("port 1's harmonic entry 2 (N = 2) no As") && f.Contains("Peak or Rms"));
        Assert.Single(found);
    }

    // ── gate 7: which array ─────────────────────────────────────────────────────────────────────────

    /// <summary>Two arrays land on port 1's positive conductor (the pad): the port's harmonics are refused, naming both arrays and
    /// the Array form; the same harmonics stated per array are accepted, and reach that array's wires only.</summary>
    [Fact]
    public void Gate7_TwoArraysOnOneConductor_AreRefused_AndTheArrayFormIsAccepted()
    {
        var doc = Doc(count: 2, second: true);
        string ws = Workspace();
        var (e, lowering) = Lower(ws, doc, Current(1, "pad/zmax", ("1", ThermalAmplitude.Peak)));
        var refused = ThermalRfPlan.Build(e, lowering, Setup(Current(1, "pad/zmax", ("1", ThermalAmplitude.Peak))).Thermal!, out string? why);
        output.WriteLine(why);
        Assert.Null(refused);
        Assert.Contains("'w1'", why);
        Assert.Contains("'w2'", why);
        Assert.Contains("\"Array\"", why);

        var byArray = new CemThermalCurrent { Array = "w1", F0 = "2 GHz", Harmonics = [new CemThermalHarmonic { N = 1, Amp = "1", As = ThermalAmplitude.Peak }] };
        var (e2, lowering2, plan) = Plan(doc, byArray);
        var peaks = plan.At(Value(e2), 1);
        var names = lowering2.Wires.Select(w => w.Name).ToList();
        Assert.Equal(1, peaks[names.IndexOf("w1[0]")].Single().PeakA + peaks[names.IndexOf("w1[1]")].Single().PeakA, 1e-12);
        // w2 is an undriven array of one wire: its circulating current sums to zero over it
        Assert.True(peaks[names.IndexOf("w2")].All(h => Math.Abs(h.PeakA) < 1e-12));
        Assert.Empty(Check(doc, byArray));
        Assert.Contains(Check(doc, new CemThermalCurrent { Array = "w9", F0 = "2 GHz", Harmonics = byArray.Harmonics }), f => f.Contains("no such wire array"));
    }

    // ── gate 8: both ends driven ────────────────────────────────────────────────────────────────────

    /// <summary>Port 1 on the pad and port 2 on the lead both give the one array its first harmonic, 1 A and 0.8 A peak: the wires
    /// take the larger, and the notes give both and the difference — whether or not the two F0s are SPELLED alike (the review
    /// found "2 GHz" beside "2e9" labelled apart and both heating every wire).</summary>
    [Theory]
    [InlineData("2 GHz")]
    [InlineData("2e9")]
    public void Gate8_BothEndsDriven_TheLargerIsUsed_AndTheNotesSayBoth(string f0Lead)
    {
        var doc = Doc(count: 2);
        var lead = Current(2, "lead/zmax", ("0.8", ThermalAmplitude.Peak));
        lead.F0 = f0Lead;
        var (e, lowering, plan) = Plan(doc, Current(1, "pad/zmax", ("1", ThermalAmplitude.Peak)), lead);
        Assert.Equal(2, plan.Entries.Count);
        Assert.All(plan.Entries, x => Assert.Equal(0, x.Array));
        var notes = new List<string>();
        var peaks = plan.At(Value(e), 1, notes);
        output.WriteLine(string.Join("\n", notes.Concat(plan.Notes)));
        Assert.Equal(1, peaks.Sum(w => w.Sum(h => h.PeakA)), 1e-12);
        Assert.Contains(notes, n => n.Contains("port 1 1 A") && n.Contains("port 2 0.8 A") && n.Contains("0.2 A apart"));
    }

    // ── the run, end to end ─────────────────────────────────────────────────────────────────────────

    /// <summary>A two-wire array carrying port 1's harmonics only (no DC), solved in process: the run succeeds, every wire's peak
    /// current per harmonic and its RF heat are in the result (R-em3d78-4e), the wires sit above their 25 °C ends, the energy
    /// balance closes with the RF heat counted, and the notes state the share and the RF heat.</summary>
    [GmshFact]
    public void TheRun_SolvesHarmonicsOnly_AndTheResultHoldsTheRfTable()
    {
        string ws = Workspace();
        var doc = Doc(count: 2);
        var current = Current(1, "pad/zmax", ("0.8", ThermalAmplitude.Peak), ("0.2", ThermalAmplitude.Rms));
        doc.Setups = [EmSetupPersistence.ToEmbedded(Setup(current))];
        string path = WriteC3d(ws, doc);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, "Rf").Setup!, path), doc, path, null, Path.Combine(ws, "results"));
        output.WriteLine($"{clock.Elapsed.TotalSeconds:F1} s");
        output.WriteLine(string.Join("\n", run.Notes ?? []));
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        var data = run.Data!;
        double sum = 0;
        foreach (string w in new[] { "w1[0]", "w1[1]" })
        {
            double i1 = data[$"thermal.Irf:{w}:h1"].RealValues.Single(), i2 = data[$"thermal.Irf:{w}:h2"].RealValues.Single();
            Assert.Equal(0.2 * Math.Sqrt(2) / 0.8, i2 / i1, 1e-12);
            sum += i1;
            double prf = data[$"thermal.Prf:{w}"].RealValues.Single();
            Assert.Equal(prf, data[$"thermal.Prf:{w}:h1"].RealValues.Single() + data[$"thermal.Prf:{w}:h2"].RealValues.Single(), 1e-12 * prf);
            Assert.True(prf > 0);
            Assert.True(data[$"thermal.Twire:{w}:max"].RealValues.Single() > 25.01);
        }
        Assert.Equal(0.8, sum, 1e-12);
        Assert.All(data["thermal.Energy:balance"].RealValues, b => Assert.True(b <= 1e-6, $"balance {b}"));
        Assert.Contains(run.Notes!, n => n.Contains("RF heat") && n.Contains("W in the wires"));
        Assert.Contains(run.Notes!, n => n.Contains("wBond's share"));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    private const long Pitch = 80 * Um;

    /// <summary>The first wire's axis, nm: pad to lead, a flat top.</summary>
    private static (long X, long Y, long Z)[] Axis() =>
        [(100 * Um, 100 * Um, 50 * Um), (250 * Um, 100 * Um, 250 * Um), (650 * Um, 100 * Um, 250 * Um), (850 * Um, 100 * Um, 50 * Um)];

    /// <summary>A copper pad and lead, <paramref name="count"/> gold wires (an array 80 µm apart) from the pad to the lead, two
    /// ports; with <paramref name="second"/>, a second lead and a wire w2 from the pad to it.</summary>
    private static C3dDocument Doc(int count, bool second = false)
    {
        C3dWire Wire(string name, long dy, long toY, int n) => new()
        {
            Name = name, Material = "Gold", DiameterUm = 25.4, Section = WireCrossSection.Round,
            Points = [.. Axis().Select((q, i) => new C3dPoint3(q.X, (i == 3 ? toY : q.Y) + dy, q.Z))],
            Start = new C3dWireEnd { Style = BondStyle.Wedge, FootLengthUm = 30 }, End = new C3dWireEnd { Style = BondStyle.Wedge, FootLengthUm = 30 },
            Array = n > 1 ? new C3dWireArray { Count = n, Pitch = new C3dPoint3(0, Pitch, 0) } : null,
        };
        List<C3dObject> objects = [Box("pad", 0, 0, 200, 450), Box("lead", 750, 0, 200, 450), Wire("w1", 0, 100 * Um, count)];
        if (second)
        {
            objects.Add(Box("lead2", 750, 600, 200, 200));
            objects.Add(Wire("w2", 300 * Um, 400 * Um, 1));
        }
        return new C3dDocument
        {
            Objects = objects,
            Ports = [new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(200, 150, 550, 150) },
                     new C3dPort { Number = 2, Name = "P2", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(200, 320, 550, 100) }],
        };
    }

    private static CemThermalCurrent Current(int port, string enter, params (string Amp, ThermalAmplitude As)[] h) => new()
    {
        Port = port, EnterFace = enter, F0 = "2 GHz",
        Harmonics = [.. h.Select((x, i) => new CemThermalHarmonic { N = i + 1, Amp = x.Amp, As = x.As })],
    };

    private static EmSetup Setup(params CemThermalCurrent[] currents) => new()
    {
        Name = "Rf", Problem3D = Em3dProblemType.Thermal,
        Thermal = new CemThermal
        {
            Boundaries = [new CemThermalBoundary { Face = "pad/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" },
                          new CemThermalBoundary { Face = "lead/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }],
            Currents = [.. currents],
        },
    };

    private (C3dElaboration E, ThermalLowering Lowering) Lower(string ws, C3dDocument doc, params CemThermalCurrent[] currents)
    {
        string path = WriteC3d(ws, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var lowering = ThermalLowerings.Build(doc, e, Setup(currents).Thermal!, 1, out string? why);
        Assert.True(lowering is not null, why);
        return (e, lowering!);
    }

    private (C3dElaboration E, ThermalLowering Lowering, ThermalRfPlan Plan) Plan(C3dDocument doc, params CemThermalCurrent[] currents)
    {
        var (e, lowering) = Lower(Workspace(), doc, currents);
        var plan = ThermalRfPlan.Build(e, lowering, Setup(currents).Thermal!, out string? why);
        Assert.True(plan is not null, why);
        return (e, lowering, plan!);
    }

    private List<string> Check(C3dDocument doc, CemThermalCurrent current)
    {
        string path = WriteC3d(Workspace(), doc);
        var res = C3dResolver.Resolve(doc, C3dCell.Of(path));
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        return [.. C3dThermal.Setup("Rf", Setup(current), doc, e, res).Select(d => d.Render()).Where(m => m.Contains("current") || m.Contains("harmonic") || m.Contains("array"))];
    }

    private static Func<string?, string, ThermalQuantity, double> Value(C3dElaboration e) => (text, _, q) => C3dThermal.Evaluate(e.Resolution!, text!, out _, q) ?? double.NaN;

    /// <summary>A dense solve by Gaussian elimination with partial pivoting.</summary>
    private static double[] Solve(double[] a, double[] b)
    {
        int n = b.Length;
        a = (double[])a.Clone();
        b = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = Enumerable.Range(c, n - c).MaxBy(r => Math.Abs(a[r * n + c]));
            for (int j = 0; j < n; j++) (a[c * n + j], a[p * n + j]) = (a[p * n + j], a[c * n + j]);
            (b[c], b[p]) = (b[p], b[c]);
            for (int r = c + 1; r < n; r++)
            {
                double f = a[r * n + c] / a[c * n + c];
                for (int j = c; j < n; j++) a[r * n + j] -= f * a[c * n + j];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int j = r + 1; j < n; j++) s -= a[r * n + j] * x[j];
            x[r] = s / a[r * n + r];
        }
        return x;
    }

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dBox Box(string name, long x, long y, long dx, long dy)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, y * Um, 0), Size = new C3dPoint3(dx * Um, dy * Um, 50 * Um) };

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Alpha20 = 0.00393, ThermalK = 400 },
                new TechMaterial { Name = "Gold", Sigma20 = 4.514e7, Alpha20 = 0.0037, ThermalK = 318 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, C3dDocument doc)
    {
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }
}
