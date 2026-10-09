// Whether two technologies' STACKUPS say the same thing to a solver — brief-artsch-6 follow-up (owner: the
// divergence report must compare the stackup as well as the layer table).
//
// ExternalWorkspaceGate's layer-table comparison deliberately leaves the stackup out: it asks what a layout VIEW
// means, and a shape carries nothing but its key across a workspace boundary. A microstrip line asks a different
// question — what substrate it is computed on — and two technologies with identical layer tables can put it on
// 1.6 mm of FR-4 in one and 0.5 mm of εr 3.5 in the other. This is that question, and only that one.

using System.Globalization;

namespace CircuitRF.Design.Layout;

/// <summary>The first way two stackups disagree electrically, or null when they agree.</summary>
public static class StackupComparison
{
    private const double RelativeTolerance = 1e-9;

    /// <summary>
    /// Compares the conductor and dielectric entries in order — kind, thickness, a dielectric's εr, tanδ and μr, a
    /// conductor's conductivity and ground designation — and the two boundaries. Names, materials' names, drawing
    /// layers and vias are not compared: they are what the stackup is CALLED and what draws on it, not what a line
    /// on it computes (the layer table is <see cref="Workspace.ExternalWorkspaceGate"/>'s to compare).
    /// </summary>
    /// <param name="aName">How <paramref name="a"/> is named in the sentence.</param>
    /// <param name="bName">How <paramref name="b"/> is named.</param>
    public static string? Difference(Technology a, Technology b, string aName, string bName)
        => Differences(a, b, aName, bName).FirstOrDefault();

    /// <summary>
    /// <see cref="Difference"/>'s comparison, reporting EVERY entry that differs rather than the first — for a
    /// caller that has to list them (a Gerber import whose job file disagrees with the technology it was imported
    /// into, brief-gerber-import-target-technology D7). A different count of physical layers is one sentence and
    /// ends the list: past it there is no entry-by-entry pairing to compare.
    /// </summary>
    public static IEnumerable<string> Differences(Technology a, Technology b, string aName, string bName)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Stackup.Top != b.Stackup.Top)
            yield return $"Their stackups differ: the top boundary is {a.Stackup.Top} in {aName} and {b.Stackup.Top} in {bName}.";
        if (a.Stackup.Bottom != b.Stackup.Bottom)
            yield return $"Their stackups differ: the bottom boundary is {a.Stackup.Bottom} in {aName} and {b.Stackup.Bottom} in {bName}.";

        var la = Physical(a);
        var lb = Physical(b);
        if (la.Count != lb.Count)
        {
            yield return $"Their stackups differ: {aName} has {la.Count} conductor and dielectric layers and {bName} has {lb.Count}.";
            yield break;
        }

        for (int i = 0; i < la.Count; i++)
        {
            var (x, y) = (la[i], lb[i]);
            string which = $"layer {i + 1} from the top ('{x.Name}' in {aName}, '{y.Name}' in {bName})";
            if (x.Kind != y.Kind)
            {
                yield return $"Their stackups differ: {which} is a {Kind(x)} in one and a {Kind(y)} in the other.";
                continue;
            }
            if (x.ThicknessDbu != y.ThicknessDbu)
            {
                yield return $"Their stackups differ: {which} is {Microns(x.ThicknessDbu)} thick in {aName} and {Microns(y.ThicknessDbu)} in {bName}.";
                continue;
            }

            if (x.Kind == StackupKind.Dielectric)
            {
                if (Differs(x.Epsr, y.Epsr)) yield return $"Their stackups differ: {which} has εr {G(x.Epsr)} in {aName} and {G(y.Epsr)} in {bName}.";
                else if (Differs(x.TanD, y.TanD)) yield return $"Their stackups differ: {which} has tanδ {G(x.TanD)} in {aName} and {G(y.TanD)} in {bName}.";
                else if (Differs(x.Mur, y.Mur)) yield return $"Their stackups differ: {which} has μr {G(x.Mur)} in {aName} and {G(y.Mur)} in {bName}.";
            }
            else
            {
                if (Differs(x.SigmaSm, y.SigmaSm))
                    yield return $"Their stackups differ: {which} has a conductivity of {G(x.SigmaSm)} S/m in {aName} and {G(y.SigmaSm)} S/m in {bName}.";
                else if (x.IsGroundReference != y.IsGroundReference)
                    yield return $"Their stackups differ: {which} is a ground reference in {(x.IsGroundReference ? aName : bName)} only.";
            }
        }
    }

    private static List<StackupLayer> Physical(Technology t) =>
        [.. t.Stackup.Layers.Where(l => l.Kind is StackupKind.Conductor or StackupKind.Dielectric)];

    private static string Kind(StackupLayer l) => l.Kind == StackupKind.Conductor ? "conductor" : "dielectric";

    private static bool Differs(double p, double q) =>
        Math.Abs(p - q) > RelativeTolerance * Math.Max(Math.Abs(p), Math.Abs(q));

    private static string Microns(long dbu) =>
        (dbu / (double)LayoutUnits.DefaultDbuPerMicron).ToString("0.###", CultureInfo.InvariantCulture) + " µm";

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
