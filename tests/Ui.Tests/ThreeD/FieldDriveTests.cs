// brief-em3d-100 §8 — a field plot's drive (pixels were not seen; every gate reads records, the layers' values, the hover text and
// the legend lines):
//
//   1  Palace's unit power, from the committed fixture: |V_inc|·|I_inc| = 1 and |V_inc| = √R, so its field is at
//      PalaceDrive.IncidentPowerW = 0.5 W time-averaged (peak phasors; Palace's docs/src/reference.md:200-202)
//   2  an openEMS step scaled by FieldDrive has a matched port's incident wave at √(2·R·P), phase 0; the scale is the only
//      "0.5 / v" in src/Render/Scene3D/Fields
//   3  an openEMS step's Z0 is the RUN's (its kept document.c3d), not the document open now
//   4  at 4× the drive, |E| reads 2× and |S| 4× at the same point (the hover value); in dB both move 6.02 dB; the indicator 1×
//   5  two plots of one solution at two drives read the step once, in the ratio √(P1/P2), in two range groups
//   6  Accepted reads Incident × 1/(1 − |S11|²); at a frequency port-V.csv lacks it is refused by sentence, the plot unchanged
//   7  a drive edit leaves SerializeForRun byte-identical (nothing stale); a pre-brief .c3d round-trips byte-identically
//   9  D3 — an openEMS run with no probe of the driven port draws, relative, with the "not referred" line and no unit
//
// Gate 8 (render --field prints the drive line LegendLines prints) is FieldRenderCliTests.Gate12. The driven Palace runs are the
// committed cavity fixture's steps re-listed under a driven collection, as FieldPlotTests' are; the openEMS runs are written here.

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class FieldDriveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em100-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static string Cavity => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");

    // ── 1. Palace's convention ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The pinned Palace (changeset 0dc74cd, the one that wrote this fixture) states P^inc = V^inc·[I^inc]* = 1 W on PEAK phasors,
    /// "twice the time-averaged power" (docs/src/reference.md:200-202; postprocessing.md:64 — port V and I are peak). The fixture
    /// carries exactly that product, so the time-averaged incident power of its field is ½·|V_inc|²/R = 0.5 W
    /// (src/Design/RESOLVED.md §brief-em3d-100).
    /// </summary>
    [Fact]
    public void Gate1_PalacesUnitPower_IsHalfAWattTimeAveraged_InThePeakConvention()
    {
        string post = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "B-via", "palace", "postpro");
        double Column(string file, string header)
        {
            var lines = File.ReadAllLines(Path.Combine(post, file));
            int c = lines[0].Split(',').Select(h => h.Trim()).ToList().IndexOf(header);
            Assert.True(c >= 0, $"{file} has no '{header}'");
            return double.Parse(lines[1].Split(',')[c], CultureInfo.InvariantCulture);
        }
        double vInc = Column("port-V.csv", "V_inc[1] (V)"), iInc = Column("port-I.csv", "I_inc[1] (A)");
        const double r = 50;                                                     // config.json: LumpedPort 1, "R": 50.0
        Assert.Equal(1, vInc * iInc, 9);
        Assert.Equal(Math.Sqrt(r), vInc, 9);
        Assert.Equal(0.5, PalaceDrive.IncidentPowerW);
        Assert.Equal(vInc, PalaceDrive.IncidentPeakVolts(r), 9);
        Assert.Equal(PalaceDrive.IncidentPowerW, 0.5 * vInc * iInc, 9);          // ½·Re(V·I*), circuitRF's peak convention
    }

    // ── 2 + 3 + 9. openEMS referred to its incident wave ─────────────────────────────────────

    [Fact]
    public void Gate2_AMatchedOpenEmsPort_ScaledByItsDrive_HasPalacesIncidentWave_AtPhaseZero()
    {
        string run = WriteOpenEmsRun(z0Run: 50, matched: true, out var raw);
        var sol = FieldRun.OpenOpenEms(run)!.Solutions.Single();
        Assert.True(sol.Drive is { Referred: true, Port: 1 });

        // The scale's own convention: the probes' incident wave times (scale / 0.5) — the ½ is the dump's ×2 — is √(2·R·P), phase 0.
        var (u, i) = Probes(run, sol.FrequencyHz);
        var v = (u + 50 * i) / 2 * (sol.DumpScale / 0.5);
        Assert.Equal(Math.Sqrt(2 * 50 * PalaceDrive.IncidentPowerW), v.Magnitude, 9);
        Assert.Equal(0, v.Phase, 9);
        Assert.Equal(0, sol.Drive!.Reflection!.Value.Magnitude, 9);            // matched

        // Every array the step carries takes it: E here, node for node.
        var e = FieldStep.Open(sol.VolumePvtu!, 1, sol.DumpScale).Load("E")!;
        for (int k = 0; k < raw.Length; k++)
        {
            var want = raw[k] * sol.DumpScale;
            var got = new Complex(e.Re[3 * k], e.Im![3 * k]);                    // the x component of node k
            Assert.True((got - want).Magnitude <= 1e-5 * want.Magnitude, $"node {k}: {got} against {want}");
        }

        // One convention, in one place: nothing else in the folder divides a field by a voltage.
        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "src", "Render", "Scene3D", "Fields");
        var hits = Directory.EnumerateFiles(dir, "*.cs").SelectMany(f => File.ReadAllLines(f).Where(l => l.Contains("0.5 / v"))
                                                                     .Select(l => $"{Path.GetFileName(f)}: {l.Trim()}")).ToList();
        Assert.Single(hits);
        Assert.StartsWith("FieldDrive.cs", hits[0]);
    }

    [Fact]
    public void Gate3_AnOpenEmsStepsZ0_IsTheRunsOwn_NotTheOpenDocuments()
    {
        string run = WriteOpenEmsRun(z0Run: 50, matched: false, out _);
        var before = FieldRun.OpenOpenEms(run)!.Solutions.Single();
        Assert.Equal(new Complex(50, 0), before.Drive!.Z0);

        // The document being edited states 75 Ω now; the run solved at 50 Ω, and its field is read at 50 Ω still.
        string open = Path.Combine(_root, "open.c3d");
        C3dPersistence.SaveToFile(open, Document(75));
        var after = FieldRun.OpenOpenEms(run)!.Solutions.Single();
        Assert.Equal(before.DumpScale, after.DumpScale);
        Assert.Equal(new Complex(50, 0), after.Drive!.Z0);
        Assert.Contains("Z₀ 50 Ω", FieldDrive.Read(after, "E", null, C3dDriveReferredTo.Incident).Line);
    }

    [Fact]
    public void Gate9_AnOpenEmsRunWithNoPortRecord_DrawsRelative_AndSaysSo()
    {
        string run = WriteOpenEmsRun(z0Run: 50, matched: true, out var raw);
        File.Delete(Path.Combine(run, "p1", CsxcadWriter.VoltageProbe(1)));
        var sol = FieldRun.OpenOpenEms(run)!.Solutions.Single();
        Assert.False(sol.Drive!.Referred);
        Assert.Equal(Complex.One, sol.DumpScale);
        var step = FieldStep.Open(sol.VolumePvtu!, 1, sol.DumpScale);
        Assert.Equal((float)raw[0].Real, step.Load("E")!.Re[0]);                // drawn as written

        var drive = FieldDrive.Read(sol, "E", 10, C3dDriveReferredTo.Incident);
        Assert.True(drive.Relative);
        Assert.Equal(1, drive.Factor);
        var q = FieldQuantity.Offered(step.Arrays, []).First(x => x.Array.Name == "E" && x.Mode == FieldMode.Peak);
        var slice = new FieldSurface { Channels = 6, Xyz = [0, 0, 0], Values = [1, 0, 0, 0, 0, 0] };
        var scale = FieldSurfacePlot.EmScale(q, [slice], db: false, 99, drive);
        Assert.Equal("", scale.Unit);
        var lines = FieldPlotResolver.LegendLines("Field1", q, scale, "2.4 GHz, port 1 driven", 0, null, drive: drive);
        Assert.Equal("|E|", lines[1]);                                           // no (V/m)
        Assert.Equal(FieldDrive.NotReferredLine, lines[^1]);
        Assert.Equal("Drive: not referred, this run kept no port record (relative values)", FieldDrive.NotReferredLine);
    }

    // ── 4 + 5. exponents, and one step for two drives ────────────────────────────────────────

    [Fact]
    public void Gate4_AtFourTimesTheDrive_EReadsTwice_SFourTimes_AndBothMove6dB_TheIndicatorNot()
    {
        var vm = Open(Driven());
        DrivenRun(vm, 10);
        vm.NewFieldPlot();                                                       // a cut through the middle, |E|, 10 GHz
        Built(vm, "Field1");
        double e1 = Hover(vm);
        Draw(vm, "4×", p => p.DrivePowerW = 4 * PalaceDrive.IncidentPowerW);
        double e4 = Hover(vm);
        output.WriteLine($"|E| at the centre: {e1:G6} → {e4:G6}");
        AssertRatio(2, e4 / e1);

        Draw(vm, "S", p => { p.Quantity = "S"; p.Mode = nameof(FieldMode.Peak); });
        double s4 = Hover(vm);
        Draw(vm, "default drive", p => p.DrivePowerW = null);
        double s1 = Hover(vm);
        output.WriteLine($"|S| at the centre: {s1:G6} → {s4:G6}");
        AssertRatio(4, s4 / s1);

        // dB: one rule, 10·log10(k) for every scaled array — an amplitude's 20·log of √k, a power density's 10·log of k
        double TopDb(string quantity, double? watts)
        {
            Draw(vm, quantity, p => { p.Quantity = quantity; p.Db = true; p.DrivePowerW = watts; });
            return vm.Viewer.LayerNamed("Field1")!.Scale!.Hi;
        }
        double eShift = TopDb("E", 4 * PalaceDrive.IncidentPowerW) - TopDb("E", null);
        double sShift = TopDb("S", 4 * PalaceDrive.IncidentPowerW) - TopDb("S", null);
        Assert.Equal(10 * Math.Log10(4), eShift, 6);
        Assert.Equal(10 * Math.Log10(4), sShift, 6);

        var sol = vm.Viewer.LayerNamed("Field1")!.Item!.Solution;
        Assert.Equal(1, FieldDrive.Read(sol, "Indicator", 4 * PalaceDrive.IncidentPowerW, C3dDriveReferredTo.Incident).Factor);
        Assert.Equal(1, FieldDrive.Read(sol, "E0_1", 4 * PalaceDrive.IncidentPowerW, C3dDriveReferredTo.Incident).Factor);
        var unknown = FieldDrive.Read(sol, "Mystery", 4 * PalaceDrive.IncidentPowerW, C3dDriveReferredTo.Incident);
        Assert.Equal(1, unknown.Factor);
        Assert.Equal("Drive: Mystery is not referred to the drive", unknown.Line);
    }

    [Fact]
    public void Gate5_TwoPlotsOfOneSolution_AtTwoDrives_ReadTheStepOnce_InTheRatio_InTwoRangeGroups()
    {
        var vm = Open(Driven());
        DrivenRun(vm, 10);
        vm.NewFieldPlot();
        Built(vm, "Field1");
        long reads = vm.Viewer.FieldStepReads;
        vm.NewFieldPlot();                                                       // the same cut, the same solution
        Assert.Null(vm.SetFieldPlot("Field2", "9×", p => p.DrivePowerW = 9 * PalaceDrive.IncidentPowerW));
        Built(vm, "Field1");
        Built(vm, "Field2");
        Settle();

        var v = vm.Viewer;
        Assert.Equal(reads, v.FieldStepReads);                                   // one solution, read once
        var (a, b) = (v.LayerNamed("Field1")!, v.LayerNamed("Field2")!);
        Assert.Equal(a.Surfaces.Sum(s => s.Values.Length), b.Surfaces.Sum(s => s.Values.Length));
        var va = a.Surfaces.SelectMany(s => s.Values).ToArray();
        var vb = b.Surfaces.SelectMany(s => s.Values).ToArray();
        int compared = 0;
        for (int k = 0; k < va.Length; k++)
            if (Math.Abs(va[k]) > 1e-9 * va.Max(Math.Abs)) { Assert.Equal(3, vb[k] / va[k], 9); compared++; }
        Assert.True(compared > 0);
        Assert.Equal(2, v.FieldLegendGroups.Count);                              // two drives: two ranges, two legends
        Assert.NotEqual(a.Scale, b.Scale);
        Assert.Contains(v.FieldLegendGroups[1].Lines, l => l.StartsWith("Drive: 4.5 W incident (available)", StringComparison.Ordinal));
    }

    // ── 6. Accepted ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_Accepted_DividesByOneMinusS11Squared_AndAFrequencyWithNoPortRowIsRefused()
    {
        var vm = Open(Driven());
        DrivenRun(vm, [6, 10], dir =>
        {
            // A port, and port-V.csv at 10 GHz only, Γ = 0.5: V = V_inc·(1 + Γ).
            string config = Path.Combine(dir, "config.json");
            File.WriteAllText(config, File.ReadAllText(config).Replace("\"LumpedPort\": []",
                "\"LumpedPort\": [ { \"Index\": 1, \"R\": 50.0, \"Excitation\": true } ]", StringComparison.Ordinal));
            double vi = Math.Sqrt(50);
            File.WriteAllText(Path.Combine(dir, "postpro", PalaceRun.PortVFile),
                "f (GHz), V_inc[1] (V), Re{V[1]} (V), Im{V[1]} (V)\n" +
                $"1.000000000e+01, {vi.ToString("R", CultureInfo.InvariantCulture)}, {(1.5 * vi).ToString("R", CultureInfo.InvariantCulture)}, 0\n");
        });
        Assert.All(vm.Viewer.FieldSolutions, s => Assert.Equal(1, s.Solution.Drive?.Port));
        Assert.Equal("10 GHz, port 1 driven", vm.Viewer.FieldSolutions[1].Label);

        vm.NewFieldPlot();
        Assert.Null(vm.SetFieldPlot("Field1", "10 GHz", p => p.Solution = new C3dFieldSolution { GHz = 10 }));
        Built(vm, "Field1");
        double incident = Hover(vm);
        var props = vm.Properties;
        vm.SelectedTreeItem = FieldPlotRow(vm);
        props.Reload();
        Assert.True(props.IsFieldPlot && props.PlotIsDriven);
        props.PlotDriveReferredTo = C3dPropertiesViewModel.PlotDriveReferences[1];
        Assert.Equal(C3dDriveReferredTo.Accepted, vm.FieldPlot("Field1")!.DriveReferredTo);
        Until(() => vm.Viewer.LayerNamed("Field1") is { Building: false } l && l.Drive.Line?.Contains("accepted") == true, "Accepted was never drawn");
        double accepted = Hover(vm);
        output.WriteLine($"|E|: incident {incident:G6}, accepted {accepted:G6}");
        AssertRatio(Math.Sqrt(1 / (1 - 0.25)), accepted / incident);            // an amplitude: √ of 1/(1 − |S11|²)
        Assert.Contains("Drive: 0.5 W accepted (|S11| −6.0 dB) on port 1, Z₀ 50 Ω, peak", vm.Viewer.FieldLegendLines());

        // 6 GHz: port-V.csv has no row. Moving there draws nothing and says why; asking for Accepted there is refused.
        Assert.Null(vm.SetFieldPlot("Field1", "6 GHz", p => p.Solution = new C3dFieldSolution { GHz = 6 }));
        const string why = "Palace wrote no port voltage at 6 GHz, so the accepted power there is not known; Incident still works.";
        Until(() => vm.Viewer.FieldPlotProblem == why, "the missing reflection was never reported");
        Assert.Null(vm.SetFieldPlot("Field1", "incident", p => p.DriveReferredTo = C3dDriveReferredTo.Incident));
        vm.SelectedTreeItem = FieldPlotRow(vm);
        props.Reload();
        int entries = vm.UndoEntries;
        props.PlotDriveReferredTo = C3dPropertiesViewModel.PlotDriveReferences[1];
        Assert.Equal(why, props.Error);
        Assert.Equal(C3dDriveReferredTo.Incident, vm.FieldPlot("Field1")!.DriveReferredTo);      // the plot stays as it was
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Equal(C3dPropertiesViewModel.PlotDriveReferences[0], props.PlotDriveReferredTo);
    }

    // ── 7. nothing stale ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_ADriveEdit_LeavesTheRunsDocumentAsItWas_AndAPreBriefDocumentRoundTrips()
    {
        var doc = new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = "Air", Size = new C3dPoint3(1000, 1000, 1000) }],
            FieldPlots = [new C3dFieldPlot { Name = "Field1", Solution = new C3dFieldSolution { GHz = 2.4, Port = 1 } }],
        };
        string pre = C3dPersistence.Serialize(doc);
        Assert.DoesNotContain("Drive", pre);
        Assert.Equal(pre, C3dPersistence.Serialize(C3dPersistence.Deserialize(pre)));          // a pre-brief .c3d, byte for byte

        string solved = C3dPersistence.SerializeForRun(doc);
        doc.FieldPlots[0].DrivePowerW = 10;
        doc.FieldPlots[0].DriveReferredTo = C3dDriveReferredTo.Accepted;
        Assert.Equal(solved, C3dPersistence.SerializeForRun(doc));
        Assert.False(C3dRunDocument.IsStale(solved, doc));                       // brief 98's solved mark stays set
        string after = C3dPersistence.Serialize(doc);
        Assert.Contains("\"DrivePowerW\": 10", after);
        Assert.Contains("\"DriveReferredTo\": \"Accepted\"", after);
        var back = C3dPersistence.Deserialize(after).FieldPlots[0];
        Assert.Equal((10.0, C3dDriveReferredTo.Accepted), (back.DrivePowerW!.Value, back.DriveReferredTo));

        Assert.True(C3dDrivePower.TryParse("30 dBm", out double w, out _));
        Assert.Equal(1, w, 12);
        Assert.True(C3dDrivePower.TryParse("250mW", out w, out _));
        Assert.Equal(0.25, w, 12);
        Assert.False(C3dDrivePower.TryParse("-3 W", out _, out string? why));
        Assert.Equal("A drive power is above zero.", why);
        Assert.Equal("0.5 W", C3dDrivePower.Format(PalaceDrive.IncidentPowerW));
    }

    // ── the fixtures ─────────────────────────────────────────────────────────────────────────

    private static C3dDocument Document(double z0) => new()
    {
        Ports = [new C3dPort { Number = 1, Name = "P1", Z0 = z0.ToString(CultureInfo.InvariantCulture) }],
    };

    /// <summary>
    /// An openEMS run as Em3dRunService leaves one, written here: port 1's run directory with its two probes (a Gaussian-modulated
    /// 2.4 GHz pulse; the current U/50 for a <paramref name="matched"/> port, else a mismatched one) and an E dump at 2.4 GHz on a
    /// 2×2×2 grid, and the document the run kept stating Z0 = <paramref name="z0Run"/>. <paramref name="raw"/> is the dump's complex
    /// x-component at each node, as written.
    /// </summary>
    private string WriteOpenEmsRun(double z0Run, bool matched, out Complex[] raw)
    {
        string run = Path.Combine(_root, "openems-run");
        string p1 = Path.Combine(run, "p1");
        Directory.CreateDirectory(p1);
        File.WriteAllText(C3dRunDocument.PathIn(run), C3dPersistence.SerializeForRun(Document(z0Run)));
        const double f0 = 2.4e9, dt = 1e-12, t0 = 1.5e-9, tau = 0.4e-9;
        var u = new System.Text.StringBuilder("% t/s voltage/V\n");
        var c = new System.Text.StringBuilder("% t/s current/A\n");
        for (int n = 0; n < 4000; n++)
        {
            double t = n * dt, x = Math.Exp(-Math.Pow((t - t0) / tau, 2)) * Math.Sin(2 * Math.PI * f0 * t);
            double y = matched ? x / 50 : 0.6 * x / 50 + 0.2 * Math.Exp(-Math.Pow((t - t0 - 0.1e-9) / tau, 2)) * Math.Cos(2 * Math.PI * f0 * t) / 50;
            u.Append(t.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            c.Append(t.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        File.WriteAllText(Path.Combine(p1, CsxcadWriter.VoltageProbe(1)), u.ToString());
        File.WriteAllText(Path.Combine(p1, CsxcadWriter.CurrentProbe(1)), c.ToString());

        raw = new Complex[8];
        var mag = new float[24];
        var arg = new float[24];
        for (int k = 0; k < 8; k++)
        {
            raw[k] = Complex.FromPolarCoordinates(100 + 10 * k, 0.3 * k);
            mag[3 * k] = (float)raw[k].Magnitude;
            arg[3 * k] = (float)raw[k].Phase;
        }
        string abs = Path.Combine(p1, "efield1_f=2.4e9_abs.vtr");
        WriteVtr(abs, mag);
        WriteVtr(FieldRun.ArgFile(abs), arg);
        raw = [.. raw.Select(z => Complex.FromPolarCoordinates((float)z.Magnitude, (float)z.Phase))];
        return run;
    }

    /// <summary>A 2×2×2 rectilinear grid (1 mm cube) with a 3-component Float32 field, inline binary, uncompressed.</summary>
    private static void WriteVtr(string path, float[] values)
    {
        static string B64(byte[] data) => Convert.ToBase64String(BitConverter.GetBytes((uint)data.Length)) + Convert.ToBase64String(data);
        string Coord(string n) => $"<DataArray type=\"Float64\" Name=\"{n}\" format=\"binary\">{B64([.. BitConverter.GetBytes(0.0), .. BitConverter.GetBytes(1e-3)])}</DataArray>";
        var bytes = new byte[4 * values.Length];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        File.WriteAllText(path,
            "<?xml version=\"1.0\"?>\n<VTKFile type=\"RectilinearGrid\" version=\"0.1\" byte_order=\"LittleEndian\" header_type=\"UInt32\">\n" +
            "<RectilinearGrid WholeExtent=\"0 1 0 1 0 1\"><Piece Extent=\"0 1 0 1 0 1\">\n" +
            $"<PointData><DataArray type=\"Float32\" Name=\"E-Field\" NumberOfComponents=\"3\" format=\"binary\">{B64(bytes)}</DataArray></PointData>\n" +
            $"<Coordinates>{Coord("x")}{Coord("y")}{Coord("z")}</Coordinates>\n</Piece></RectilinearGrid>\n</VTKFile>\n");
    }

    /// <summary>Port 1's U and I in its own run at <paramref name="hz"/>, through the DFT S was formed with.</summary>
    private static (Complex U, Complex I) Probes(string run, double hz)
    {
        var u = OpenEmsRun.ReadProbe(Path.Combine(run, "p1", CsxcadWriter.VoltageProbe(1)), out _)!;
        var i = OpenEmsRun.ReadProbe(Path.Combine(run, "p1", CsxcadWriter.CurrentProbe(1)), out _)!;
        return (FdtdPortTransform.Dft(u, [hz])[0], FdtdPortTransform.Dft(i, [hz])[0]);
    }

    /// <summary>A ratio of two hover values: each is printed to four significant figures (G4), so the ratio holds to 1e-3.</summary>
    private static void AssertRatio(double expected, double actual)
        => Assert.True(Math.Abs(actual / expected - 1) <= 1e-3, $"ratio {actual:G6}, expected {expected:G6}");

    /// <summary>The value under a cursor at the view's centre, where the plot's mid-z cut is.</summary>
    private double Hover(C3dEditorViewModel vm)
    {
        Settle();
        var v = vm.Viewer;
        v.View.CursorX = 200;
        v.View.CursorY = 150;
        string text = v.FieldValueUnderCursor(0, default, false);
        var m = Regex.Match(text, @"= ([-+0-9.eE]+)");
        Assert.True(m.Success, $"no value under the cursor: '{text}'");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Changes plot Field1 and waits until it is drawn again.</summary>
    private void Draw(C3dEditorViewModel vm, string what, Action<C3dFieldPlot> change)
    {
        long builds = vm.Viewer.LayerNamed("Field1")!.Builds;
        Assert.Null(vm.SetFieldPlot("Field1", what, change));
        Until(() => vm.Viewer.LayerNamed("Field1") is { Building: false, Scale: not null } l && l.Builds > builds, $"{what} was never drawn");
        Settle();
    }

    private static C3dTreeItem FieldPlotRow(C3dEditorViewModel vm)
        => vm.Tree.Single(g => g.Role == C3dTreeGroupRole.FieldPlots).Items.Single();

    private static EmSetup Driven()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "drive", Solver3D = Em3dSolver.Palace, Problem3D = Em3dProblemType.Driven,
            AirBox = new EmAirBox(none, none, none, none, none, none),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("2", "18", 9, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
    }

    private C3dEditorViewModel Open(EmSetup setup)
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");
        return vm;
    }

    /// <summary>A driven run saving <paramref name="ghz"/>: the cavity's first steps re-listed as those frequencies (FieldPlotTests').</summary>
    private string DrivenRun(C3dEditorViewModel vm, params double[] ghz) => DrivenRun(vm, ghz, null);

    /// <summary>… with <paramref name="edit"/> applied to the run directory before the run is read.</summary>
    private string DrivenRun(C3dEditorViewModel vm, double[] ghz, Action<string>? edit)
    {
        var run = vm.ActiveRunSetup!;
        string dir = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), run, Em3dSolver.Palace);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (string f in Directory.EnumerateFiles(Cavity, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(dir, Path.GetRelativePath(Cavity, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        string pv = Path.Combine(dir, "postpro", "paraview");
        foreach (string part in (string[])["", "_boundary"])
        {
            string from = Path.Combine(pv, "eigenmode" + part), to = Path.Combine(pv, "driven" + part);
            Directory.Move(from, to);
            File.Delete(Path.Combine(to, "eigenmode" + part + ".pvd"));
            File.WriteAllText(Path.Combine(to, "driven" + part + ".pvd"),
                "<?xml version=\"1.0\"?>\n<VTKFile type=\"Collection\" version=\"2.2\" byte_order=\"LittleEndian\">\n<Collection>\n" +
                string.Concat(ghz.Select((g, i) => $"<DataSet timestep=\"{g.ToString(CultureInfo.InvariantCulture)}\" group=\"\" part=\"0\" " +
                                                   $"file=\"Cycle00000{i + 1}/data.pvtu\" name=\"mesh\"/>\n")) +
                "</Collection>\n</VTKFile>\n");
        }
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);
        edit?.Invoke(dir);
        vm.RunFinished();
        Until(() => vm.Viewer.FieldSolutions.Count == ghz.Length, "the driven run was never read");
        return dir;
    }

    private void Built(C3dEditorViewModel vm, string name)
        => Until(() => vm.Viewer.LayerNamed(name) is { Builds: > 0, Scale: not null, Building: false, Item: not null } l && l.Name == name,
                 $"{name} was never drawn");

    private void Settle()
    {
        Thread.Sleep(150);
        while (_posted.TryDequeue(out var a)) a();
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);
}
