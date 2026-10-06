// brief-em3d-65 R-em3d65-4 — what each 3D solver will not respect of a kernel solid, per object, before the run.
//
// The owner's question was "do the solvers respect a fillet or a chamfer?", and the answer differs by solver:
//   * openEMS STAIRCASES every curved or oblique face to its grid (FDTD's nature). How faithful that is depends on how many
//     cells span the feature, so each kernel solid gets one row naming its worst rounded feature (R-em3d65-4a).
//   * Palace RESPECTS the geometry — curvature sizing and second-order elements are already in GmshGeoWriter — but models a
//     conductor's loss as a flat surface's, which F0 Q6 measured low on a surface curved at a few skin depths (R-em3d65-4b);
//     and a small radius sets the whole mesh's size, which is worth knowing but not wrong (R-em3d65-4c).
//
// Computed from the face tables and the grid alone — no worker, no solver — so the run's notes, `check` and the editor's
// Setups dialog can all read the same rows on every edit. Nothing here blocks a run: the rows inform, the user decides.
// Kernel solids only (R-em3d65-4e): a managed cylinder's notes are existing run output and are not changed here.

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

/// <summary>The solver a fidelity row is about.</summary>
public enum Em3dFidelitySolver { Palace, OpenEms }

/// <summary>How much a fidelity row matters: a <see cref="Warning"/> says the answer near the feature is not the
/// geometry's; a <see cref="Note"/> says something true that changes nothing about the answer.</summary>
public enum Em3dFidelitySeverity { Note, Warning }

/// <summary>One row: the object, the face it is about (its worst, when it has several), the solver, and the sentence.</summary>
public sealed record Em3dFidelityFinding(string Object, string Face, Em3dFidelitySolver Solver, Em3dFidelitySeverity Severity,
                                         string Sentence);

public static class Em3dFidelity
{
    /// <summary>Cells across a rounded feature's smallest radius at which openEMS's staircase is called converging
    /// (R-em3d65-4a's table).</summary>
    public const double WellResolvedCells = 4;

    /// <summary>Skin depths at the bottom of the band below which Palace's flat-surface loss model is warned about —
    /// the threshold the run's existing round-conductor note uses (F0 Q6).</summary>
    public const double SkinDepths = 10;

    /// <summary>The fraction of the smallest size the Palace writer asks for elsewhere below which a curved face's own
    /// elements are said to set the mesh (R-em3d65-4c).</summary>
    public const double DominatesFraction = 0.25;

    /// <summary>
    /// Elements per full turn that Gmsh's curvature sizing puts on a curved surface — the ONE value
    /// <c>GmshGeoWriter.CurvatureElements</c> writes into every script, kept here so the note and the script cannot differ.
    /// </summary>
    public const int PalaceCurvatureElements = 12;

    /// <summary>The angular deflection, radians, an openEMS tessellation is requested at (R-em3d65-3a).</summary>
    public const double OpenEmsAngularRad = 0.25;

    private const double Mu0 = 4e-7 * Math.PI;

    /// <summary>
    /// The rows for <paramref name="solver"/>. openEMS needs the <paramref name="grid"/> (none: no rows); Palace's
    /// mesh-size note needs <paramref name="palaceSmallestSizeM"/>, the smallest element size the writer asks for anywhere
    /// else (its conductor and sheet refinement) — without it only the conductor-model rows are given.
    /// </summary>
    public static IReadOnlyList<Em3dFidelityFinding> For(Em3dProblem problem, Em3dFidelitySolver solver, FdtdGridResult? grid,
                                                        double? palaceSmallestSizeM = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var rows = new List<Em3dFidelityFinding>();
        // brief-em3d-120 — the rows read cells per world axis; a cylindrical grid has none (its Y holds radians), so none is given.
        if (solver == Em3dFidelitySolver.OpenEms && grid?.Cylinder is not null) return rows;
        foreach (var s in problem.Solids)
        {
            if (s.Primitive is not Em3dShapeSolid k) continue;
            if (solver == Em3dFidelitySolver.OpenEms)
            {
                if (grid is not null && OpenEms(s, k, grid) is { } row) rows.Add(row);
                continue;
            }
            if (PalaceLoss(problem, s, k) is { } loss) rows.Add(loss);
            if (palaceSmallestSizeM is { } smallest && PalaceMesh(s, k, smallest) is { } mesh) rows.Add(mesh);
        }
        return rows;
    }

    // ── R-em3d65-4a — openEMS: cells across the smallest rounded feature ────────────────────────

    private sealed record Feature(Em3dShapeFace Face, bool Chamfer, double SizeM, double CellM)
    {
        public double Ratio => SizeM / CellM;
        public Em3dFidelitySeverity Severity => Chamfer ? (Ratio < 1 ? Em3dFidelitySeverity.Warning : Em3dFidelitySeverity.Note)
                                                        : (Ratio < WellResolvedCells ? Em3dFidelitySeverity.Warning : Em3dFidelitySeverity.Note);
    }

    private static Em3dFidelityFinding? OpenEms(Em3dSolid s, Em3dShapeSolid k, FdtdGridResult grid)
    {
        var features = new List<Feature>();
        foreach (var f in k.Faces)
        {
            bool curved = Em3dShapeSolid.IsCurved(f.Kind) && f.MinRadiusM > 0;
            bool chamfer = f.Kind == "plane" && f.Name.StartsWith("chamfer(", StringComparison.Ordinal) && Em3dShapeSolid.NormalAxis(f) is null;
            if (!curved && !chamfer) continue;
            var axes = AxesAcross(k, f);
            double coarsest = axes.Max(a => LargestCell(grid.Axis((FdtdAxis)a).Lines, Lo(f.Box, a), Hi(f.Box, a)));
            double size = curved ? f.MinRadiusM : axes.Min(a => Hi(f.Box, a) - Lo(f.Box, a));
            if (coarsest > 0 && size > 0) features.Add(new Feature(f, chamfer, size, coarsest));
        }
        if (features.Count == 0) return null;

        var worst = features.OrderByDescending(x => x.Severity).ThenBy(x => x.Ratio).First();
        string obj = s.Name, face = worst.Face.Name;
        string r = FdtdGrid.FormatLength(worst.SizeM), cell = FdtdGrid.FormatLength(worst.CellM);
        int about = Math.Max(1, (int)Math.Round(worst.Ratio));
        bool fillet = face.StartsWith("fillet(", StringComparison.Ordinal);
        string sentence;
        if (worst.Chamfer)
            sentence = worst.Ratio < 1
                ? $"openEMS will not represent the {r} chamfer on '{obj}' ({face}): the grid cell there is {cell}, so the edge is solved as square."
                : $"openEMS solves the {r} chamfer on '{obj}' ({face}) as a staircase of about {about} step{(about == 1 ? "" : "s")}.";
        else if (worst.Ratio < 1)
            sentence = fillet
                ? $"openEMS will not represent the {r} fillet on '{obj}' ({face}): the grid cell there is {cell}, so the edge is solved as sharp."
                : $"openEMS will not represent the {r} radius of '{obj}' ({face}): the grid cell there is {cell}, so the curve is solved as a single step.";
        else if (worst.Ratio < WellResolvedCells)
            sentence = $"openEMS staircases the {r} {(fillet ? "fillet on" : "radius of")} '{obj}' ({face}) with about {about} cell{(about == 1 ? "" : "s")}; " +
                       "expect the answer near it to depend on the grid, not the radius.";
        else
            sentence = $"openEMS staircases the curved faces of '{obj}' at ≥ {WellResolvedCells.ToString(CultureInfo.InvariantCulture)} cells " +
                       $"across their smallest radius ({FdtdGrid.FormatLength(features.Where(x => !x.Chamfer).Min(x => x.SizeM))}); " +
                       "refining the grid converges them.";
        if (features.Count > 1 && !(worst.Severity == Em3dFidelitySeverity.Note && !worst.Chamfer))
            sentence += $" It is the worst of {features.Count} rounded features on '{obj}'.";
        return new Em3dFidelityFinding(obj, face, Em3dFidelitySolver.OpenEms, worst.Severity, sentence);
    }

    /// <summary>
    /// The axes a face's surface bends or slopes ACROSS — those its normal has a component along. A fillet along y is
    /// staircased on x and z, and the cells along y (its length) decide nothing about it; a sphere or torus bends across
    /// all three. Read from its triangles (a cylinder's facets contain its generators, so their normals are exactly
    /// perpendicular to its axis) or, with none, all three.
    /// </summary>
    private static IReadOnlyList<int> AxesAcross(Em3dShapeSolid k, Em3dShapeFace f)
    {
        double[] most = [0, 0, 0];
        for (int t = f.FirstTriangle; t < f.FirstTriangle + f.TriangleCount && t < k.Display.Triangles.Count; t++)
        {
            var tri = k.Display.Triangles[t];
            Point3 a = k.Display.Vertices[tri.A], b = k.Display.Vertices[tri.B], c = k.Display.Vertices[tri.C];
            double nx = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y);
            double ny = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
            double nz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(l > 0)) continue;
            most[0] = Math.Max(most[0], Math.Abs(nx / l));
            most[1] = Math.Max(most[1], Math.Abs(ny / l));
            most[2] = Math.Max(most[2], Math.Abs(nz / l));
        }
        var axes = Enumerable.Range(0, 3).Where(a => most[a] > 1e-3).ToList();
        return axes.Count > 0 ? axes : [0, 1, 2];
    }

    /// <summary>The largest cell of <paramref name="lines"/> reaching into [<paramref name="lo"/>, <paramref name="hi"/>] —
    /// for a range of no width, the larger of the two cells at it.</summary>
    internal static double LargestCell(IReadOnlyList<double> lines, double lo, double hi)
    {
        double best = 0;
        double tol = 1e-12 * Math.Max(1, Math.Abs(lines[^1] - lines[0]));
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            double a = lines[i], b = lines[i + 1];
            bool reaches = hi - lo > tol ? a < hi - tol && b > lo + tol : a <= lo + tol && b >= lo - tol;
            if (reaches) best = Math.Max(best, b - a);
        }
        return best;
    }

    /// <summary>The smallest cell of <paramref name="lines"/> reaching into [<paramref name="lo"/>, <paramref name="hi"/>].</summary>
    public static double SmallestCell(IReadOnlyList<double> lines, double lo, double hi)
    {
        double best = double.PositiveInfinity;
        double tol = 1e-12 * Math.Max(1, Math.Abs(lines[^1] - lines[0]));
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            double a = lines[i], b = lines[i + 1];
            if (a < hi + tol && b > lo - tol) best = Math.Min(best, b - a);
        }
        return best;
    }

    // ── R-em3d65-4b — Palace: the conductor model at a small radius ─────────────────────────────

    private static Em3dFidelityFinding? PalaceLoss(Em3dProblem problem, Em3dSolid s, Em3dShapeSolid k)
    {
        if (s.Role != Em3dRole.Conductor || problem.IsStatic) return null;
        if (problem.Materials.FirstOrDefault(m => m.Name == s.Material) is not { } metal ||
            !(metal.SigmaSm > 0) || double.IsInfinity(metal.SigmaSm)) return null;
        double f = problem.Type == Em3dProblemType.Eigenmode ? problem.EigenmodeTargetHz : problem.Frequency.StartHz;
        if (!(f > 0)) return null;
        double delta = SkinDepth(f, metal);
        var thin = k.Faces.Where(x => Em3dShapeSolid.IsCurved(x.Kind) && x.MinRadiusM > 0 && x.MinRadiusM < SkinDepths * delta).ToList();
        if (thin.Count == 0) return null;
        var worst = thin.MinBy(x => x.MinRadiusM)!;
        double depths = worst.MinRadiusM / delta;
        string sentence =
            $"Palace treats the surface of '{s.Name}' as flat for its loss; its {FdtdGrid.FormatLength(worst.MinRadiusM)} radius " +
            $"({worst.Name}) is about {Count(depths)} skin depth{(Math.Round(depths) == 1 ? "" : "s")} at {Ghz(f)}, where a round " +
            "conductor's loss was measured 9.5 % low at 5 skin depths. The loss there is likely under-stated by several percent." +
            (thin.Count > 1 ? $" It is the smallest of {thin.Count} such faces on '{s.Name}'." : "");
        return new Em3dFidelityFinding(s.Name, worst.Name, Em3dFidelitySolver.Palace, Em3dFidelitySeverity.Warning, sentence);
    }

    /// <summary>δ = 1/√(π f μ σ) — the skin depth <see cref="Em3dFaceSheets"/> sizes a conductive face's sheet with.</summary>
    public static double SkinDepth(double hz, Em3dMaterial metal) => 1 / Math.Sqrt(Math.PI * hz * Mu0 * metal.Mur * metal.SigmaSm);

    // ── R-em3d65-4c — Palace: a feature that dominates the mesh ─────────────────────────────────

    private static Em3dFidelityFinding? PalaceMesh(Em3dSolid s, Em3dShapeSolid k, double smallestM)
    {
        if (!(smallestM > 0)) return null;
        double Element(double r) => 2 * Math.PI * r / PalaceCurvatureElements;
        var small = k.Faces.Where(x => Em3dShapeSolid.IsCurved(x.Kind) && x.MinRadiusM > 0 && Element(x.MinRadiusM) < DominatesFraction * smallestM)
                           .ToList();
        if (small.Count == 0) return null;
        var worst = small.MinBy(x => x.MinRadiusM)!;
        double h = Element(worst.MinRadiusM);
        bool fillet = worst.Name.StartsWith("fillet(", StringComparison.Ordinal);
        string what = fillet ? $"The {FdtdGrid.FormatLength(worst.MinRadiusM)} fillet on '{s.Name}' ({worst.Name})"
                             : $"The {FdtdGrid.FormatLength(worst.MinRadiusM)} radius of '{s.Name}' ({worst.Name})";
        string sentence =
            $"{what} puts elements of about {FdtdGrid.FormatLength(h)} on it — {Fraction(smallestM / h)} of the smallest elsewhere " +
            "— and will dominate the mesh. " +
            (fillet ? "Disabling it (its Enabled box) shows whether it matters."
                    : "Disabling the operation that made it (its Enabled box) shows whether it matters.") +
            (small.Count > 1 ? $" It is the smallest of {small.Count} such faces on '{s.Name}'." : "");
        return new Em3dFidelityFinding(s.Name, worst.Name, Em3dFidelitySolver.Palace, Em3dFidelitySeverity.Note, sentence);
    }

    private static readonly string[] Ordinals =
    [
        "", "", "half", "a third", "a quarter", "a fifth", "a sixth", "a seventh", "an eighth", "a ninth", "a tenth",
        "an eleventh", "a twelfth", "a thirteenth", "a fourteenth", "a fifteenth", "a sixteenth", "a seventeenth",
        "an eighteenth", "a nineteenth", "a twentieth",
    ];

    /// <summary>"a twentieth", "a quarter" — 1/<paramref name="times"/> in words, rounded; "1/40" past twenty.</summary>
    internal static string Fraction(double times)
    {
        int n = Math.Max(2, (int)Math.Round(times));
        return n < Ordinals.Length ? Ordinals[n] : "1/" + n.ToString(CultureInfo.InvariantCulture);
    }

    private static string Count(double v) => v >= 2 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                                                    : v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Ghz(double hz) => (hz / 1e9).ToString("G4", CultureInfo.InvariantCulture) + " GHz";

    private static double Lo((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, int a) => a == 0 ? b.X0 : a == 1 ? b.Y0 : b.Z0;
    private static double Hi((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, int a) => a == 0 ? b.X1 : a == 1 ? b.Y1 : b.Z1;
}
