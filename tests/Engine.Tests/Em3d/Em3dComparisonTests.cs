using System.Numerics;
using CircuitRF.Engine.Em3d;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-10 §6 gates 1, 2 and 6 — the comparison's arithmetic and its notes, with nothing
// installed. Gates 3-5, 7 and 8 are tests/Ui.Tests/Em3d/RunBothTests.cs.
public sealed class Em3dComparisonTests
{
    private const double Um = 1e-6;

    /// <summary>Gate 1: dMag, dPhase (wrapped to (−180, 180]) and dVec, each against values computed
    /// here, on two hand-built 2-port sets — including the wrap across ±180° and both ends of it.</summary>
    [Fact]
    public void Gate1_Differences_MatchHandComputedValues_AndPhaseWrapsInto_Minus180_180()
    {
        double[] f = [1e9, 2e9, 3e9];
        var p = new Complex[3 * 4];
        var o = new Complex[3 * 4];
        for (int k = 0; k < p.Length; k++) { p[k] = new Complex(0.3, 0.1); o[k] = new Complex(0.3, 0.1); }
        // S21 (i = 2, j = 1 → element [k, 1, 0]) at each frequency:
        p[0 * 4 + 2] = Complex.FromPolarCoordinates(0.5, Deg(179));  o[0 * 4 + 2] = Complex.FromPolarCoordinates(0.25, Deg(-179));
        p[1 * 4 + 2] = new Complex(-1, 0);                           o[1 * 4 + 2] = new Complex(1, 0);
        p[2 * 4 + 2] = new Complex(-1, -0.0);                        o[2 * 4 + 2] = new Complex(1, 0);

        var (data, refusal) = Em3dComparison.Differences(Cube(f, p), Cube(f, o));
        Assert.Null(refusal);
        double[] mag = data![Em3dComparison.DMagCube].RealValues, phase = data[Em3dComparison.DPhaseCube].RealValues,
                 vec = data[Em3dComparison.DVecCube].RealValues;

        Assert.Equal(20 * Math.Log10(2), mag[2], 12);
        Assert.Equal(-2, phase[2], 9);                        // 179 − (−179) = 358 → −2
        Assert.Equal((p[2] - o[2]).Magnitude, vec[2], 12);
        Assert.Equal(180, phase[1 * 4 + 2], 12);               // exactly +180 stays
        Assert.Equal(180, phase[2 * 4 + 2], 12);               // −180 wraps to +180
        Assert.Equal(2, vec[1 * 4 + 2], 12);
        Assert.All(new[] { 0, 1, 3 }, e => Assert.Equal(0, mag[e]));
        Assert.Equal(p, data[Em3dComparison.PalaceCube].ComplexValues);
        Assert.Equal(o, data[Em3dComparison.OpenEmsCube].ComplexValues);

        string summary = Em3dComparison.Summary(data);
        Assert.StartsWith("Palace against openEMS: S21 by at most 6.02 dB (at 1 GHz) and 180° (at 2 GHz)", summary);
        Assert.Contains("; S11 by at most 0 dB", summary);
    }

    /// <summary>Gate 2: frequencies differing by 1 Hz at one point refuse, naming it — never interpolate.</summary>
    [Fact]
    public void Gate2_FrequenciesDifferingBy1HzAtOnePoint_AreRefused()
    {
        var s = Enumerable.Repeat(new Complex(0.1, 0.2), 3 * 4).ToArray();
        var (data, refusal) = Em3dComparison.Differences(Cube([1e9, 2e9, 3e9], s), Cube([1e9, 2e9 + 1, 3e9], s));
        Assert.Null(data);
        Assert.Contains("frequency 2 differs", refusal);
        Assert.Contains("does not interpolate", refusal);
    }

    /// <summary>Gate 6: a note appears only when its fact is true of the run — the lossy-dielectric,
    /// PEC and thin-wire notes and the unconverged warning are absent on a lossless run with none of
    /// them, and present on a lossy one that has them.</summary>
    [Fact]
    public void Gate6_NotesApplyOnlyWhenTrue()
    {
        var s = Enumerable.Repeat(new Complex(0.1, 0.2), 2 * 4).ToArray();
        double[] f = [1e9, 2e9];
        var none = new Em3dComparisonFacts(1.5e9, [], [], []);
        var some = new Em3dComparisonFacts(1.5e9, ["via1"], ["wire1"], [2]);

        var lossless = Em3dComparison.Compare(Cube(f, s), Cube(f, s), Microstrip(tanD: 0), none);
        Assert.DoesNotContain(lossless.Notes, n => n.Contains("lossy", StringComparison.Ordinal));
        Assert.DoesNotContain(lossless.Notes, n => n.Contains("perfect conductors", StringComparison.Ordinal));
        Assert.DoesNotContain(lossless.Notes, n => n.Contains("thin conductor", StringComparison.Ordinal));
        Assert.Empty(lossless.Warnings);
        Assert.Contains(Em3dComparison.CrossCheckSentence, lossless.Notes);

        var lossy = Em3dComparison.Compare(Cube(f, s), Cube(f, s), Microstrip(tanD: 0.004), some);
        Assert.Contains(lossy.Notes, n => n.StartsWith("'Alumina' is lossy", StringComparison.Ordinal) && n.Contains("1.5 GHz", StringComparison.Ordinal));
        Assert.Contains(lossy.Notes, n => n.Contains("perfect conductors", StringComparison.Ordinal) && n.Contains("'via1'", StringComparison.Ordinal));
        Assert.Contains(lossy.Notes, n => n.Contains("thin conductor", StringComparison.Ordinal) && n.Contains("'wire1'", StringComparison.Ordinal));
        Assert.Contains("port 2 did not reach", Assert.Single(lossy.Warnings));

        // The notes travel in the DataSet: the summary first, then the warning, each an axis label.
        var carried = lossy.Data!.CubesIn(Em3dComparison.Group)[Em3dComparison.NotesCube];
        Assert.Equal(lossy.Summary, carried.Axes[0].Labels![0]);
        Assert.Equal([0.0, 1.0], carried.RealValues[..2]);
    }

    /// <summary>brief-emi-6 §6.2 — Palace against openEMS, Palace the reference, writes the comparison file it wrote before
    /// Compare existed, byte for byte: the golden was written by the two-solver comparison EMI-6 generalised.</summary>
    [Fact]
    public void PalaceAgainstOpenEms_WritesTheGoldenComparison_ByteForByte()
    {
        var (data, _) = GoldenCase();
        string path = Path.Combine(Path.GetTempPath(), "crf-cmp-" + Guid.NewGuid().ToString("N")[..12] + ".npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(data, path, RfCore.Export.ExportFormat.Npy);
            string golden = Path.Combine(TestDataRoot(), "em3d", "compare", "palace-openems.compare_em.npy");
            Assert.Equal(File.ReadAllBytes(golden), File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>brief-emi-6 §6.2 — three solvers against Palace: every S cube, each pair's difference cubes with the other
    /// solver's token appended, and a summary naming the pair that differs most (by |ΔS|), the other pair's summary a note.</summary>
    [Fact]
    public void ThreeSolvers_WritePairwiseCubes_AndTheSummaryNamesTheWorstPair()
    {
        double[] f = [1e9, 2e9];
        Complex[] Filled(double s21) => [.. Enumerable.Range(0, f.Length * 4).Select(e => e % 4 is 1 or 2 ? new Complex(s21, 0) : new Complex(0.1, 0))];
        var palace = new Em3dComparisonLeg("Palace", "palace", Em3dSolverFit.Fem, Cube(f, Filled(0.9)));
        var openEms = new Em3dComparisonLeg("openEMS", "openems", Em3dSolverFit.Fdtd, Cube(f, Filled(0.89)), new Em3dComparisonFacts(1.5e9, [], [], []));
        var emerge = new Em3dComparisonLeg("EMerge", "emerge", Em3dSolverFit.Fem, Cube(f, Filled(0.8)));

        var r = Em3dComparison.Compare(palace, [openEms, emerge], Microstrip(tanD: 0));
        Assert.Null(r.Refusal);
        Assert.Equal(["S_palace", "S_openems", "S_emerge", "dMag_dB_openems", "dPhase_deg_openems", "dVec_openems",
                      "dMag_dB_emerge", "dPhase_deg_emerge", "dVec_emerge"],
                     r.Data!.Cubes.Keys.ToArray());
        Assert.Equal(0.1, r.Data["dVec_emerge"].RealValues[2], 12);
        Assert.StartsWith("The largest difference is Palace against EMerge: S21 by at most", r.Summary);
        Assert.StartsWith("Palace against openEMS: S21 by at most", r.Notes[0]);
        Assert.Contains(Em3dComparison.CrossCheckSentenceMany, r.Notes);
    }

    /// <summary>brief-emi-6 §6.3 — an EMerge leg's facts make its notes, and the same leg with no facts makes none of them.</summary>
    [Fact]
    public void EmergeFacts_MakeTheirNotes_AndAbsentFactsMakeNone()
    {
        double[] f = [1e9, 2e9];
        var s = Enumerable.Repeat(new Complex(0.1, 0.2), f.Length * 4).ToArray();
        var palace = new Em3dComparisonLeg("Palace", "palace", Em3dSolverFit.Fem, Cube(f, s));
        var facts = Em3dComparisonFacts.None with
        {
            UniformSheetPorts = true, SurfaceImpedanceSolids = ["housing"], AdaptiveFit = (12, 3e-4, 1e-3),
        };

        var said = Em3dComparison.Compare(palace, [new("EMerge", "emerge", Em3dSolverFit.Fem, Cube(f, s), facts)], Microstrip(tanD: 0.004));
        Assert.Contains(said.Notes, n => n.StartsWith("EMerge's lumped port drives one uniform field direction", StringComparison.Ordinal));
        Assert.Contains(said.Notes, n => n.StartsWith("EMerge gives 'housing' a surface impedance", StringComparison.Ordinal));
        Assert.Contains(said.Notes, n => n.StartsWith("EMerge's adaptive sweep fitted the band from 12 full solves", StringComparison.Ordinal));
        Assert.DoesNotContain(said.Notes, n => n.Contains("lossy", StringComparison.Ordinal));          // no dielectric fit: FEM
        Assert.DoesNotContain(said.Notes, n => n.StartsWith("Expected for this geometry", StringComparison.Ordinal)); // two FEM solvers
        Assert.Empty(said.Warnings);

        var unconverged = Em3dComparison.Compare(palace, [new("EMerge", "emerge", Em3dSolverFit.Fem, Cube(f, s), facts with { AdaptiveUnconverged = true })],
                                                 Microstrip(tanD: 0));
        Assert.Contains("EMerge's adaptive sweep stopped at its limit", Assert.Single(unconverged.Warnings));

        var bare = Em3dComparison.Compare(palace, [new("EMerge", "emerge", Em3dSolverFit.Fem, Cube(f, s))], Microstrip(tanD: 0.004));
        Assert.DoesNotContain(bare.Notes, n => n.StartsWith("EMerge", StringComparison.Ordinal));
        Assert.Contains(bare.Notes, n => n.StartsWith("Both solvers use lumped ports", StringComparison.Ordinal));
    }

    /// <summary>Two 2-port results that differ in every entry, on a lossy microstrip with every openEMS fact set.</summary>
    private static (DataSet Data, Em3dComparisonResult Result) GoldenCase()
    {
        double[] f = [1e9, 1.5e9, 2e9];
        var p = new Complex[f.Length * 4];
        var o = new Complex[f.Length * 4];
        for (int k = 0; k < f.Length; k++)
            for (int e = 0; e < 4; e++)
            {
                p[k * 4 + e] = Complex.FromPolarCoordinates(e is 1 or 2 ? 0.95 - 0.01 * k : 0.1 + 0.02 * e, -k - 0.1 * e);
                o[k * 4 + e] = Complex.FromPolarCoordinates(e is 1 or 2 ? 0.94 - 0.012 * k : 0.12 + 0.01 * e, -k - 0.1 * e - 0.03);
            }
        var r = Em3dComparison.Compare(Cube(f, p), Cube(f, o), Microstrip(tanD: 0.004),
                                       new Em3dComparisonFacts(1.5e9, ["via1"], ["wire1"], [2]));
        return (r.Data!, r);
    }

    private static string TestDataRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "testdata"))) return Path.Combine(dir.FullName, "testdata");
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static double Deg(double d) => d * Math.PI / 180;

    private static DataCube Cube(double[] f, Complex[] s)
        => new([new Axis("freq", f, "Hz"), new Axis("i", [1, 2], "port"), new Axis("j", [1, 2], "port")], s);

    /// <summary>A strip on a substrate of loss tangent <paramref name="tanD"/>, two lumped ports.</summary>
    private static Em3dProblem Microstrip(double tanD)
    {
        var faces = new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                  Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing);
        var box = new Em3dAirBox(new Point3(-1500 * Um, -2000 * Um, 0), new Point3(1500 * Um, 2000 * Um, 1254 * Um), faces);
        Em3dPort Port(int n, double y) => new(n, $"P{n}", "strip", "airbox/zmin", new Point3(-300 * Um, y, 0),
            new Point3(300 * Um, y, 254 * Um), new Point3(0, 0, 1), new Complex(50, 0),
            new Em3dReferencePlane(new Point3(0, y, 127 * Um), new Point3(0, 1, 0), 0));
        return new Em3dProblem(
            [new Em3dSolid("substrate", "Alumina", Em3dRole.Dielectric,
                           new Em3dBox(new Point3(-1500 * Um, -2000 * Um, 0), new Point3(1500 * Um, 2000 * Um, 254 * Um)), 1)],
            [new Em3dSheet("strip", "Copper",
                           [new Point2(-300 * Um, -2000 * Um), new Point2(300 * Um, -2000 * Um), new Point2(300 * Um, 2000 * Um), new Point2(-300 * Um, 2000 * Um)],
                           [], 254 * Um, 35 * Um, 2)],
            [new("Air", 1, null, 0, 1, 0), new("Alumina", 9.8, null, tanD, 1, 0), new("Copper", 1, null, 0, 1, 5.8e7)],
            [Port(1, -2000 * Um), Port(2, 2000 * Um)],
            box, new Em3dFrequency(1e9, 2e9, 2, Em3dSweepKind.Linear), 20);
    }
}
