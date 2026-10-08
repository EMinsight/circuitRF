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
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Stackup.Top != b.Stackup.Top)
            return $"Their stackups differ: the top boundary is {a.Stackup.Top} in {aName} and {b.Stackup.Top} in {bName}.";
        if (a.Stackup.Bottom != b.Stackup.Bottom)
            return $"Their stackups differ: the bottom boundary is {a.Stackup.Bottom} in {aName} and {b.Stackup.Bottom} in {bName}.";

        var la = Physical(a);
        var lb = Physical(b);
        if (la.Count != lb.Count)
            return $"Their stackups differ: {aName} has {la.Count} conductor and dielectric layers and {bName} has {lb.Count}.";

        for (int i = 0; i < la.Count; i++)
        {
            var (x, y) = (la[i], lb[i]);
            string which = $"layer {i + 1} from the top ('{x.Name}' in {aName}, '{y.Name}' in {bName})";
            if (x.Kind != y.Kind)
                return $"Their stackups differ: {which} is a {Kind(x)} in one and a {Kind(y)} in the other.";
            if (x.ThicknessDbu != y.ThicknessDbu)
                return $"Their stackups differ: {which} is {Microns(x.ThicknessDbu)} thick in {aName} and {Microns(y.ThicknessDbu)} in {bName}.";

            if (x.Kind == StackupKind.Dielectric)
            {
                if (Differs(x.Epsr, y.Epsr)) return $"Their stackups differ: {which} has εr {G(x.Epsr)} in {aName} and {G(y.Epsr)} in {bName}.";
                if (Differs(x.TanD, y.TanD)) return $"Their stackups differ: {which} has tanδ {G(x.TanD)} in {aName} and {G(y.TanD)} in {bName}.";
                if (Differs(x.Mur, y.Mur)) return $"Their stackups differ: {which} has μr {G(x.Mur)} in {aName} and {G(y.Mur)} in {bName}.";
            }
            else
            {
                if (Differs(x.SigmaSm, y.SigmaSm))
                    return $"Their stackups differ: {which} has a conductivity of {G(x.SigmaSm)} S/m in {aName} and {G(y.SigmaSm)} S/m in {bName}.";
                if (x.IsGroundReference != y.IsGroundReference)
                    return $"Their stackups differ: {which} is a ground reference in {(x.IsGroundReference ? aName : bName)} only.";
            }
        }
        return null;
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
