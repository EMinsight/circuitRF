// brief-em3d-10 R-em3d10-2/3 — solvers' S on one problem, and what the difference means
// (em-3d.md §4.4, §12). brief-emi-6 R-emi6-4 — any number of solvers, each against one reference.
//
// Pure functions, in the numeric layer so the arithmetic is tested with nothing installed. The run
// (src/Design/Em3d/Em3dRunService) hands in each solver's S cube and the facts its run reported;
// this returns a DataSet and the sentences that go beside it.
//
// THE FILE'S LAYOUT. Palace against openEMS, Palace the reference, is the layout the format's Both has
// always written — S_palace, S_openems, dMag_dB, dPhase_deg, dVec — and it is kept byte for byte, so
// every reader, .cdd and test of it goes on working. Any other set writes each solver's S_<token>, then
// each pair's three difference cubes with the other solver's token appended (dMag_dB_emerge).
//
// SAME GRID OR NO COMPARISON (R-em3d10-2a). The two frequency vectors must be EQUAL, element for
// element. Nothing here interpolates one onto the other: interpolation error would be reported as
// solver disagreement, which is exactly the misreading §12 lists as a risk.
//
// THE NOTES (R-em3d10-3) each come from a fact about THIS run — a lossy dielectric, a solid written
// as PEC, an unconverged openEMS run. A note that applies to every comparison says nothing, so the
// only unconditional ones are the cross-check sentence (§4.4's first limit), the reference planes
// (§4.4's second) and the lumped ports, which every Tier A problem has.
//
// WHERE THE NOTES LIVE IN THE FILE. A DataSet has no free-text field and the .npy writer none either,
// so the notes are a real cube over an axis whose LABELS are the sentences — the one string an axis
// already carries through the writer and back through the reader. Its values are the severity
// (0 a note, 1 a warning).

using System.Globalization;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.Em3d;

/// <summary>What one solver's lowering and run reported that makes a difference from another solver expected
/// (brief 9 §2c, §2d, §3d; brief-emi-6 R-emi6-4a). Plain values, so the numeric layer needs no Design type. A fact
/// a solver does not have is left at its empty value, and a note appears only for a fact that is set.</summary>
/// <param name="DielectricFitHz">The frequency an FDTD solver's dielectric loss is exact at; NaN for a solver that
/// holds tanδ constant.</param>
/// <param name="PecSolids">Solid conductors it wrote as perfect conductors.</param>
/// <param name="SubCellWires">Wires thinner than its FDTD cell, written as thin conductors.</param>
/// <param name="UnconvergedPorts">Ports whose run stopped before its end criterion.</param>
public sealed record Em3dComparisonFacts(
    double                DielectricFitHz,
    IReadOnlyList<string> PecSolids,
    IReadOnlyList<string> SubCellWires,
    IReadOnlyList<int>    UnconvergedPorts)
{
    /// <summary>A solver with nothing to report (Palace, today).</summary>
    public static Em3dComparisonFacts None { get; } = new(double.NaN, [], [], []);

    /// <summary>Its lumped port drives one uniform field direction across the port's sheet (EMerge's port model).</summary>
    public bool UniformSheetPorts { get; init; }

    /// <summary>Conductors it gives a surface impedance on their faces and does not solve inside.</summary>
    public IReadOnlyList<string> SurfaceImpedanceSolids { get; init; } = [];

    /// <summary>An adaptive sweep's full solves, its reached estimated error on |S| and its tolerance; null when the sweep was
    /// not adaptive or did not report them.</summary>
    public (int Samples, double Error, double Tolerance)? AdaptiveFit { get; init; }

    /// <summary>The adaptive sweep stopped at its limit above its tolerance.</summary>
    public bool AdaptiveUnconverged { get; init; }
}

/// <summary>brief-emi-6 R-emi6-4a — one solver's result in a comparison: its name in a sentence, its result token, its
/// method, its S cube [freq, i, j], and what its run reported.</summary>
public sealed record Em3dComparisonLeg(string Name, string Token, Em3dSolverFit Method, DataCube S, Em3dComparisonFacts? Facts = null)
{
    public Em3dComparisonFacts FactsOrNone => Facts ?? Em3dComparisonFacts.None;
}

/// <summary>The comparison, or why there is none.</summary>
/// <param name="Data">The comparison DataSet; null when refused.</param>
/// <param name="Refusal">Why no comparison was made; null when it was.</param>
/// <param name="Summary">R-em3d10-3c's line — the one a user reads first.</param>
/// <param name="Notes">Everything else the comparison says, in the order it says it.</param>
/// <param name="Warnings">What must be fixed before reading any difference.</param>
public sealed record Em3dComparisonResult(
    DataSet?              Data,
    string?               Refusal,
    string?               Summary,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings);

public static class Em3dComparison
{
    public const string PalaceCube  = "S_palace";
    public const string OpenEmsCube = "S_openems";
    public const string DMagCube    = "dMag_dB";
    public const string DPhaseCube  = "dPhase_deg";
    public const string DVecCube    = "dVec";

    /// <summary>The group holding what the comparison says and the reference planes it assumed.</summary>
    public const string Group = "compare";

    /// <summary>The notes cube: one element per sentence, the sentence as the axis label, the value
    /// its severity (0 a note, 1 a warning).</summary>
    public const string NotesCube = "Notes";

    /// <summary>Each port's reference-plane shift, metres — zero for every Tier A lumped port.</summary>
    public const string ReferencePlaneCube = "ReferencePlaneShift";

    /// <summary>R-em3d10-1b's one sentence. Nothing may call agreement "validated".</summary>
    public const string CrossCheckSentence =
        "This comparison is a cross-check, not a reference: both solvers read the same generated 3D problem, " +
        "so an error in generating it appears in both results and their agreement cannot reveal it (em-3d.md §4.4).";

    /// <summary>The cross-check sentence for three or more solvers.</summary>
    public const string CrossCheckSentenceMany =
        "This comparison is a cross-check, not a reference: every solver reads the same generated 3D problem, " +
        "so an error in generating it appears in every result and their agreement cannot reveal it (em-3d.md §4.4).";

    /// <summary>brief-emdc-2 R-emdc2-5 — said once when the results carry a 0 Hz row: it is not the solvers' answer, so the
    /// agreement statistics leave it out.</summary>
    public const string DcRowSentence =
        "The 0 Hz row is circuitRF's own conduction solve of the conductors, identical in every solver's result, so the " +
        "differences above leave it out.";

    private const string PalaceToken = "palace", OpenEmsToken = "openems";

    /// <summary>A solver's S cube in a comparison file: <c>S_&lt;token&gt;</c>.</summary>
    public static string SCube(string token) => "S_" + token;

    /// <summary>
    /// A difference cube's name for the pair whose other solver is <paramref name="otherToken"/>: <paramref name="name"/> as it is
    /// (<see cref="DMagCube"/>) in Both's layout, with <c>_&lt;token&gt;</c> appended in any other.
    /// </summary>
    public static string PairCube(string name, string otherToken, bool bothLayout) => bothLayout ? name : name + "_" + otherToken;

    /// <summary>Whether <paramref name="reference"/> and <paramref name="others"/> are the format's Both — Palace against openEMS —
    /// whose file keeps the layout it has always had.</summary>
    public static bool IsBothLayout(Em3dComparisonLeg reference, IReadOnlyList<Em3dComparisonLeg> others)
        => others.Count == 1 && reference.Token == PalaceToken && others[0].Token == OpenEmsToken;

    /// <summary>
    /// Palace against openEMS — the format's Both: the difference cubes (<see cref="Differences"/>) and the sentences
    /// (R-em3d10-3), or the refusal when the two grids are not the same.
    /// </summary>
    public static Em3dComparisonResult Compare(DataCube palace, DataCube openEms, Em3dProblem problem, Em3dComparisonFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Compare(new Em3dComparisonLeg("Palace", PalaceToken, Em3dSolverFit.Fem, palace),
                       [new Em3dComparisonLeg("openEMS", OpenEmsToken, Em3dSolverFit.Fdtd, openEms, facts)], problem);
    }

    /// <summary>
    /// brief-emi-6 R-emi6-4 — every one of <paramref name="others"/> against <paramref name="reference"/>, on the same frequency
    /// grid: each pair's difference cubes, the sentences each solver's facts make true (R-em3d10-3), and the summary naming
    /// the pair that differs most. The first pair whose grids are not the same refuses the whole comparison.
    /// </summary>
    public static Em3dComparisonResult Compare(Em3dComparisonLeg reference, IReadOnlyList<Em3dComparisonLeg> others, Em3dProblem problem)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(problem);
        if (others.Count == 0) throw new ArgumentException("A comparison has at least one solver besides its reference.", nameof(others));

        bool both = IsBothLayout(reference, others);
        var pairs = new List<Pair>();
        foreach (var other in others)
        {
            var (pair, refusal) = PairOf(reference, other);
            if (pair is null) return new Em3dComparisonResult(null, refusal, null, [], []);
            pairs.Add(pair);
        }

        var data = new DataSet();
        var axes = pairs[0].Axes;
        data.Add(SCube(reference.Token), new DataCube(axes, reference.S.ComplexValues));
        foreach (var o in others) data.Add(SCube(o.Token), new DataCube(axes, o.S.ComplexValues));
        foreach (var pair in pairs)
        {
            data.Add(PairCube(DMagCube, pair.Other.Token, both),   pair.Mag);
            data.Add(PairCube(DPhaseCube, pair.Other.Token, both), pair.Phase);
            data.Add(PairCube(DVecCube, pair.Other.Token, both),   pair.Vec);
        }

        // R-emi6-4b — the line a user reads first names the pair that differs most (largest |ΔS| anywhere); with one pair it
        // is that pair's summary, as Both's has always been.
        // brief-emdc-2 R-emdc2-5 — a 0 Hz row is circuitRF's own and the same in every result: no statistic reads it.
        int skip = DcRows(axes[0].Values) * axes[1].Length * axes[2].Length;
        var worst = pairs.MaxBy(p => p.Vec.RealValues.Skip(skip).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max())!;
        string summary = pairs.Count == 1 ? Summary(worst) : $"The largest difference is {Summary(worst)}";

        var legs = new List<Em3dComparisonLeg> { reference };
        legs.AddRange(others);
        var warnings = legs.SelectMany(l => Warnings(l)).Distinct().ToList();
        var notes = new List<string>();
        if (pairs.Count > 1) notes.AddRange(pairs.Where(p => p != worst).Select(Summary));
        notes.Add(legs.Count == 2 ? CrossCheckSentence : CrossCheckSentenceMany);
        if (skip > 0) notes.Add(DcRowSentence);
        notes.Add(ReferencePlanes(problem, legs.Count));
        notes.AddRange(Expectations(problem, legs));
        foreach (var leg in legs)
            notes.AddRange(SystematicDifferences(problem, leg, leg == reference ? others : [reference]));
        notes.AddRange(PortNotes(problem, legs));
        notes = [.. notes.Distinct()];

        // Carried in the file (R-em3d10-3): the summary first, as it is read first, then the warnings.
        var said = new List<(string Text, double Severity)> { (summary, 0) };
        said.AddRange(warnings.Select(w => (w, 1.0)));
        said.AddRange(notes.Select(n => (n, 0.0)));
        data.AddToGroup(Group, NotesCube, new DataCube(
            [new Axis("note", [.. Enumerable.Range(0, said.Count).Select(k => (double)k)], "", [.. said.Select(s => s.Text)])],
            [.. said.Select(s => s.Severity)]));

        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        data.AddToGroup(Group, ReferencePlaneCube, new DataCube(
            [new Axis("port", [.. ports.Select(p => (double)p.Number)], "port")],
            [.. ports.Select(p => p.ReferencePlane.ShiftM)]) { Unit = "m" });

        return new Em3dComparisonResult(data, null, summary, notes, warnings);
    }

    /// <summary>
    /// R-em3d10-2a/b — Palace against openEMS: the five cubes, each [freq, i, j] as the S cube is: the two S matrices as they
    /// came, the magnitude difference in dB, the phase difference wrapped to (−180°, 180°], and |S_palace − S_openems|, which
    /// stays meaningful where |S| is small and a dB difference explodes. Refused, with a sentence, when the two frequency
    /// vectors or port counts are not identical.
    /// </summary>
    public static (DataSet? Data, string? Refusal) Differences(DataCube palace, DataCube openEms)
    {
        ArgumentNullException.ThrowIfNull(palace);
        ArgumentNullException.ThrowIfNull(openEms);
        var (pair, refusal) = PairOf(new Em3dComparisonLeg("Palace", PalaceToken, Em3dSolverFit.Fem, palace),
                                     new Em3dComparisonLeg("openEMS", OpenEmsToken, Em3dSolverFit.Fdtd, openEms));
        if (pair is null) return (null, refusal);
        var ds = new DataSet();
        ds.Add(PalaceCube,  new DataCube(pair.Axes, palace.ComplexValues));
        ds.Add(OpenEmsCube, new DataCube(pair.Axes, openEms.ComplexValues));
        ds.Add(DMagCube,    pair.Mag);
        ds.Add(DPhaseCube,  pair.Phase);
        ds.Add(DVecCube,    pair.Vec);
        return (ds, null);
    }

    /// <summary>One solver against the reference: the three difference cubes on the reference's axes.</summary>
    private sealed record Pair(Em3dComparisonLeg Reference, Em3dComparisonLeg Other, Axis[] Axes, DataCube Mag, DataCube Phase, DataCube Vec);

    private static (Pair? Pair, string? Refusal) PairOf(Em3dComparisonLeg reference, Em3dComparisonLeg other)
    {
        DataCube a = reference.S, b = other.S;
        string an = reference.Name, bn = other.Name;
        if (a.Axes.Count != 3 || b.Axes.Count != 3 || a.DataKind != DataKind.Complex || b.DataKind != DataKind.Complex)
            return (null, "Both results must be complex S matrices over frequency, and one of them is not.");

        int na = a.Axes[1].Length, nb = b.Axes[1].Length;
        if (na != nb || a.Axes[2].Length != na || b.Axes[2].Length != nb)
            return (null, $"{an}'s result has {na} port(s) and {bn}'s {nb}, so there is nothing to compare port for port.");

        double[] fa = a.Axes[0].Values, fb = b.Axes[0].Values;
        if (fa.Length != fb.Length)
            return (null, $"{an}'s result has {fa.Length} frequencies and {bn}'s {fb.Length}. The comparison needs the " +
                          "same frequencies on both sides and does not interpolate, since interpolation error would read " +
                          "as a difference between the solvers.");
        for (int k = 0; k < fa.Length; k++)
            if (fa[k] != fb[k])
                return (null, $"The two results' frequency {k + 1} differs: {an}'s is {fa[k].ToString("R", CultureInfo.InvariantCulture)} Hz " +
                              $"and {bn}'s {fb[k].ToString("R", CultureInfo.InvariantCulture)} Hz. The comparison needs the same " +
                              "frequencies on both sides and does not interpolate, since interpolation error would read as a " +
                              "difference between the solvers.");

        Complex[] sa = a.ComplexValues, sb = b.ComplexValues;
        var mag   = new double[sa.Length];
        var phase = new double[sa.Length];
        var vec   = new double[sa.Length];
        for (int k = 0; k < sa.Length; k++)
        {
            mag[k]   = Db(sa[k]) - Db(sb[k]);
            phase[k] = PhaseDifferenceDeg(sa[k], sb[k]);
            vec[k]   = (sa[k] - sb[k]).Magnitude;
        }

        var axes = new[] { new Axis("freq", fa, "Hz"), a.Axes[1], a.Axes[2] };
        return (new Pair(reference, other, axes, new DataCube(axes, mag) { Unit = "dB" }, new DataCube(axes, phase) { Unit = "deg" },
                         new DataCube(axes, vec)), null);
    }

    /// <summary>1 when the frequency axis starts at 0 Hz (circuitRF's DC row, brief-emdc-2), else 0: the rows a statistic skips.</summary>
    private static int DcRows(double[] f) => f.Length > 0 && f[0] == 0 ? 1 : 0;

    /// <summary>arg(a) − arg(b) in degrees, wrapped to (−180, 180].</summary>
    public static double PhaseDifferenceDeg(Complex a, Complex b)
    {
        double d = (a.Phase - b.Phase) * 180 / Math.PI;
        d %= 360;                       // (−360, 360)
        if (d > 180) d -= 360;
        else if (d <= -180) d += 360;
        return d;
    }

    /// <summary>
    /// R-em3d10-3c — the largest |dMag_dB| and |dPhase| over the band, and where, for the first
    /// transmission term (S21) and for S11; a one-port has only S11. Palace against openEMS, as Both writes it.
    /// </summary>
    public static string Summary(DataSet data)
        => Summary("Palace", "openEMS", data[DMagCube], data[DPhaseCube], data[DVecCube]);

    private static string Summary(Pair pair) => Summary(pair.Reference.Name, pair.Other.Name, pair.Mag, pair.Phase, pair.Vec);

    private static string Summary(string reference, string other, DataCube mag, DataCube phase, DataCube vec)
    {
        int n = mag.Axes[1].Length;
        double[] f = mag.Axes[0].Values;

        string Term(int i, int j)
        {
            var (dm, fm) = Largest(mag.RealValues, i, j);
            var (dp, fpk) = Largest(phase.RealValues, i, j);
            var (dv, fv) = Largest(vec.RealValues, i, j);
            return $"S{i + 1}{j + 1} by at most {F(dm)} dB (at {Ghz(fm)}) and {F(dp)}° (at {Ghz(fpk)}), " +
                   $"|ΔS| at most {F(dv)} (at {Ghz(fv)})";
        }

        (double Value, double AtHz) Largest(double[] v, int i, int j)
        {
            double best = double.NaN, at = double.NaN;
            for (int k = DcRows(f); k < f.Length; k++)
            {
                double x = Math.Abs(v[k * n * n + i * n + j]);
                if (double.IsNaN(x)) continue;
                if (double.IsNaN(best) || x > best) { best = x; at = f[k]; }
            }
            return (best, at);
        }

        return n >= 2
            ? $"{reference} against {other}: {Term(1, 0)}; {Term(0, 0)}."
            : $"{reference} against {other}: {Term(0, 0)}.";
    }

    // ── the sentences ───────────────────────────────────────────────────────────────────────────

    private static string ReferencePlanes(Em3dProblem problem, int solvers)
    {
        string all = solvers == 2 ? "Both" : "All";
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var shifted = ports.Where(p => p.ReferencePlane.ShiftM != 0).ToList();
        return shifted.Count == 0
            ? $"{all} results are referred to the same reference planes: each port's own sheet, with no shift ({(ports.Count == 1 ? "a lumped port" : $"{ports.Count} lumped ports")}). " +
              "A comparison is only fair when both are referred to the same plane (em-3d.md §4.4)."
            : $"{all} results are referred to the same reference planes: " +
              string.Join(", ", ports.Select(p => $"port {p.Number} shifted {F(p.ReferencePlane.ShiftM * 1e6)} µm")) +
              " (em-3d.md §4.4).";
    }

    /// <summary>R-em3d10-3a — brief 5's guidance function, as it stands, and which method the
    /// accuracy rows (4, 7) expect to be closer here. Only when the solvers compared include both an FEM and an FDTD one:
    /// the guidance is about the methods, and says nothing between two solvers of one.</summary>
    private static IEnumerable<string> Expectations(Em3dProblem problem, IReadOnlyList<Em3dComparisonLeg> legs)
    {
        var fem = legs.Where(l => l.Method == Em3dSolverFit.Fem).Select(l => l.Name).ToList();
        if (fem.Count == 0 || fem.Count == legs.Count) yield break;

        var guidance = Em3dSolverGuidance.For(problem);
        foreach (var g in guidance) yield return "Expected for this geometry: " + g.Sentence;

        var accuracy = guidance.Where(g => g.Row is 4 or 7).ToList();
        if (accuracy.Count > 0)
            yield return $"{Describe(accuracy)}: FEM ({Names(fem)}) is expected to be the more accurate here.";
        else if (guidance.Any(g => g.Row == 1))
            yield return "Manhattan geometry: FDTD's staircasing costs nothing on it, so the geometry alone gives neither " +
                         "solver the accuracy advantage here.";
    }

    private static string Describe(List<Em3dGuidance> rows)
        => string.Join(" and ", rows.Select(g => g.Row == 4 ? "bond wires, round or diagonal metal present" : "fine features in a large volume"));

    /// <summary>R-em3d10-3b — each known systematic difference <paramref name="leg"/>'s facts make true of THIS run, against
    /// <paramref name="against"/> (the reference, or for the reference's own facts every other solver).</summary>
    private static IEnumerable<string> SystematicDifferences(Em3dProblem problem, Em3dComparisonLeg leg, IReadOnlyList<Em3dComparisonLeg> against)
    {
        var facts = leg.FactsOrNone;
        string me = leg.Name, them = Names(against.Select(l => l.Name).ToList());
        bool one = against.Count == 1;

        var lossy = LossyDielectrics(problem);
        if (lossy.Count > 0 && double.IsFinite(facts.DielectricFitHz))
            yield return $"{Join(lossy)} {(lossy.Count == 1 ? "is" : "are")} lossy. {me} fits a dielectric's loss so that " +
                         $"tanδ is exact at {Ghz(facts.DielectricFitHz)} only, where {them} {(one ? "holds" : "hold")} tanδ constant across the band, so " +
                         $"the expected |S21| difference from this grows with distance from {Ghz(facts.DielectricFitHz)}.";

        if (facts.PecSolids.Count > 0)
            yield return $"{me} writes solid conductors as perfect conductors, where {them} {(one ? "gives" : "give")} them their conductivity: " +
                         $"{Join(facts.PecSolids)}. {me}'s result carries none of their loss.";

        if (facts.SubCellWires.Count > 0)
            yield return $"{Join(facts.SubCellWires)} {(facts.SubCellWires.Count == 1 ? "is" : "are")} thinner than {me}'s grid " +
                         "cell and modelled there as a thin conductor, whose effective radius the cell sets rather than the wire; " +
                         $"{them} {(one ? "meshes" : "mesh")} the wire's own surface.";

        if (facts.SurfaceImpedanceSolids.Count > 0)
            yield return $"{me} gives {Join(facts.SurfaceImpedanceSolids)} a surface impedance on {(facts.SurfaceImpedanceSolids.Count == 1 ? "its" : "their")} " +
                         $"faces and does not solve inside, so {me}'s loss there is that of a conductor many skin depths thick.";

        if (facts.AdaptiveFit is { } fit && !facts.AdaptiveUnconverged)
            yield return $"{me}'s adaptive sweep fitted the band from {fit.Samples} full solve{(fit.Samples == 1 ? "" : "s")} to an estimated " +
                         $"error of {F(fit.Error)} on |S| (tolerance {F(fit.Tolerance)}), so a |ΔS| no larger than that is within " +
                         $"{me}'s own fit.";
    }

    /// <summary>The port sentence: a solver with its own port model says so; otherwise every solver uses lumped ports, whose
    /// parasitics are similar but not identical.</summary>
    private static IEnumerable<string> PortNotes(Em3dProblem problem, IReadOnlyList<Em3dComparisonLeg> legs)
    {
        var own = legs.Where(l => l.FactsOrNone.UniformSheetPorts).ToList();
        if (own.Count == 0)
        {
            yield return legs.Count == 2
                ? "Both solvers use lumped ports, so their port parasitics are similar but not identical, which is part " +
                  "of any difference in the ports' reflections."
                : "Every solver uses lumped ports, so their port parasitics are similar but not identical, which is part " +
                  "of any difference in the ports' reflections.";
            yield break;
        }
        if (!problem.Ports.Any(p => p.Kind == Em3dPortKind.Lumped)) yield break;
        foreach (var leg in own)
            yield return $"{leg.Name}'s lumped port drives one uniform field direction across the port's sheet, a different port " +
                         $"model from {Names(legs.Where(l => l != leg).Select(l => l.Name).ToList())}'s, so part of any difference in " +
                         "the ports' reflections, and in the depth of a resonance seen through them, can come from the port models " +
                         "rather than the fields.";
    }

    private static IEnumerable<string> Warnings(Em3dComparisonLeg leg)
    {
        var facts = leg.FactsOrNone;
        string me = leg.Name;
        if (facts.UnconvergedPorts.Count > 0)
            yield return
                $"{me}'s run{(facts.UnconvergedPorts.Count == 1 ? "" : "s")} exciting port{(facts.UnconvergedPorts.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", facts.UnconvergedPorts)} did not reach {(facts.UnconvergedPorts.Count == 1 ? "its" : "their")} end " +
                $"criterion. Fix this first — raise the {me} section's MaxTimeSteps — before reading any difference: " +
                $"{me}'s result is missing the energy still in the structure.";
        if (facts.AdaptiveUnconverged)
            yield return
                $"{me}'s adaptive sweep stopped at its limit of full solves " +
                (facts.AdaptiveFit is { } fit ? $"with estimated error {F(fit.Error)}, above its tolerance {F(fit.Tolerance)}" : "above its tolerance") +
                $". Fix this first — raise the {me} section's MaxSamples — before reading any difference: {me}'s fitted points " +
                "are less accurate than it was asked for.";
    }

    /// <summary>Dielectric materials with tanδ above zero that some dielectric solid is made of.</summary>
    public static IReadOnlyList<string> LossyDielectrics(Em3dProblem problem)
    {
        var lossy = problem.Materials.Where(m => m.TanD > 0).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        return [.. problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && lossy.Contains(s.Material))
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static double Db(Complex s) => 20 * Math.Log10(s.Magnitude);

    private static string Join(IReadOnlyList<string> names)
    {
        var q = names.Select(n => $"'{n}'").ToList();
        return q.Count == 1 ? q[0] : string.Join(", ", q.Take(q.Count - 1)) + " and " + q[^1];
    }

    /// <summary>Solver names as a sentence lists them, unquoted: <c>A</c>, <c>A and B</c>, <c>A, B and C</c>.</summary>
    private static string Names(IReadOnlyList<string> names)
        => names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static string F(double v) => v.ToString("G3", CultureInfo.InvariantCulture);

    private static string Ghz(double hz) => (hz / 1e9).ToString("G4", CultureInfo.InvariantCulture) + " GHz";
}
