// brief-emi-5 R-emi5-3 — will an EMerge run fit? Answered from the mesh, the moment the driver has made it, through the run's
// MemoryGate exactly as Palace's after-meshing check is: a warning past 75 % of this machine, a confirmation past 150 %.

using System.Globalization;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Em3d;

public static class EmergeMemory
{
    /// <summary>
    /// The unknowns EMerge solves for on a mesh: two per edge and two per triangle at order 2, one per edge at order 1 (its
    /// Nédélec bases). Measured to the unit on the Cavity at order 2: 21,817 edges and 32,966 triangles, 109,566 unknowns.
    /// </summary>
    public static long Unknowns(long edges, long triangles, int elementOrder)
        => elementOrder == 1 ? edges : 2 * (edges + triangles);

    /// <summary>
    /// The peak resident memory of an EMerge run solving <paramref name="unknowns"/>: a fixed part (Python, EMerge, the mesh)
    /// and a part per unknown, which depends on the direct solver.
    ///
    /// <para><b>Calibrated on EMI-3's measurements</b> (src/Design/RESOLVED.md, R-emi3-1: the Cavity at 9,835 and 19,426 tets,
    /// peak resident memory of the whole process), with unknowns at EMI-4's 7.24 per tet: the fast macOS solver 1.85 and
    /// 3.52 GB, so 134 MB + 24.1 kB per unknown; the baseline single-threaded solver 2.87 and 5.24 GB, so 440 MB + 34.1 kB per
    /// unknown. Checked on a third point after writing it: the Cavity at 109,566 unknowns peaked at 2.59 GB on the fast solver
    /// against 2.77 GB here. EMerge's own guide (its <c>_check_ram</c>) is per TETRAHEDRON, 0.17 to 0.40 MB; read as per
    /// unknown it is about eight times high.</para>
    ///
    /// <para>An eigenmode solve and an unreported solver take the baseline figure: EMerge 3.0's eigensolver factorises with
    /// the baseline solver whatever is installed (the Cavity's eigenmode run peaked at 2.65 GB, under the 4.2 GB this gives),
    /// and an estimate that errs high is the safe one.</para>
    /// </summary>
    public static long PeakBytes(long unknowns, EmergeSpeedTier tier, bool eigenmode)
        => tier == EmergeSpeedTier.Fast && !eigenmode
            ? FastFixedBytes + FastBytesPerUnknown * unknowns
            : BaselineFixedBytes + BaselineBytesPerUnknown * unknowns;

    private const long FastFixedBytes = 134_000_000, FastBytesPerUnknown = 24_100;
    private const long BaselineFixedBytes = 440_000_000, BaselineBytesPerUnknown = 34_100;

    /// <summary>The verdict for a mesh's counts, against <paramref name="physicalBytes"/>, with the remedy a mesh this size can be
    /// priced for without meshing again: first-order elements.</summary>
    public static Em3dMemoryVerdict Verdict(EmergeMeshCounts mesh, EmergeSettings settings, EmergeSpeedTier tier, bool eigenmode,
                                            long physicalBytes)
    {
        long unknowns = Unknowns(mesh.Edges, mesh.Triangles, settings.ElementOrder);
        long estimate = PeakBytes(unknowns, tier, eigenmode);
        var remedies = new List<Em3dMemoryRemedy>();
        if (settings.ElementOrder != 1)
            remedies.Add(new("first-order elements (EMerge.ElementOrder: 1), which move a resonance by a few steps of a fine sweep",
                             PeakBytes(Unknowns(mesh.Edges, mesh.Triangles, 1), tier, eigenmode)));
        return Em3dMemoryVerdict.Evaluate(estimate, physicalBytes, remedies,
            basis: $"EMerge made {mesh.Tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra, " +
                   $"{unknowns.ToString("N0", CultureInfo.InvariantCulture)} unknowns.",
            solver: "EMerge");
    }
}
