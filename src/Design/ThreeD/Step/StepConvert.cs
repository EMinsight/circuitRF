// brief-em3d-130 — editing an imported piece: a Step object of one solid becomes an object the face and vertex editor already
// edits, and every editor, solver writer and exporter already understands.
//
//   PolyhedronOf  every flat face kept as a face of the same name — holes included (C3dFace.Holes) — and each curved face cut
//                 into flat facets face<n>.<k> of at most FacetRad (15°), from the kernel's tessellation. EXACT when every face
//                 is flat; otherwise an approximation stated (facets, volume change) and confirmed
//   PrismOf       that polyhedron, recognised (C3dRecognition.ZPrism, integer equality) as an extrusion along x, y or z →
//                 a C3dPrism whose outline and height are ordinary parameters; a gull-wing lead is one, its bends faceted
//   BoxOf         any piece → its bounding box in its own frame, an APPROXIMATION the user confirms: Summary states the size
//                 and the volume change
//   Apply         one for all three: the piece replaced AT ITS INDEX, its name, material, role, group, placement, visibility,
//                 transparency, appearance and solve flag kept, every face reference moved
//
// A ZERO-THICKNESS FIN IS KEPT AS A SHEET. A vendor's DFN draws each lead's plated end as a fin up the mould's side wall: two
// faces of the solid back to back on one plane, and a top face with no area. The fin has no volume, so it cannot be a polyhedron
// of its own: the polyhedron is what is left once the fin is cut out of both its faces, the fin becomes a C3dSheet of the same
// material, and the two go in a new group named after the piece, inside the piece's own group (CutFins).
//
// THE FRAME. Each is read at identity — the file's own frame, where the piece was built before its placement — and keeps the
// piece's placement, so nothing moves: a polyhedron's vertices and a prism's outline are the file's points rounded to a DBU.
//
// A REAL FILE IS SLOPPY. A vendor's package model puts its vertices up to 1.7 µm off its faces (the B-rep's own tolerance, which
// `loops` reports), and where four faces meet their planes do not meet in one point. A polyhedron through those vertices is not
// flat to a DBU, so Snap first moves every vertex onto its flat faces — and, where four or more meet, the faces by as little —
// and the volume and face-matching checks allow the file's tolerance rather than a DBU's.
//
// REFERENCES MOVE BY GEOMETRY, NEVER BY INDEX (overview rule 5). The replacement is built at identity and each referenced face
// goes to the one face of it that coincides — the same plane, centroid and area to the file's tolerance. A face with none (a box
// face the piece's chamfer cut back, a bend cut into facets) is a REFUSAL naming the reference, never a move to the nearest
// face. A polyhedron names its flat faces face<n> after the piece's own, so their references read as they did.
//
// The Step file is untouched: an object stops naming it, and the copy goes at the next save only if this session wrote it.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine;

namespace CircuitRF.Design.ThreeD.Step;

/// <summary>What a Step piece is replaced by.</summary>
public enum StepConvertKind { Prism, Box, Polyhedron }

/// <summary>What one replacement of a Step piece would do.</summary>
/// <param name="Kind">Prism, Box or Polyhedron.</param>
/// <param name="Object">The piece, by name.</param>
/// <param name="Index">Its index in the document's object list: where the replacement goes.</param>
/// <param name="Replacement">The object it becomes, carrying the piece's name and the rest; null when refused.</param>
/// <param name="Refusals">Every reason it cannot be made; empty when it can.</param>
public sealed record StepConvertPlan(StepConvertKind Kind, string Object, int Index, C3dObject? Replacement, IReadOnlyList<string> Refusals)
{
    /// <summary>Old face number → the replacement's face that coincides with it, for every referenced face.</summary>
    public IReadOnlyDictionary<int, string> Map { get; init; } = new Dictionary<int, string>();

    /// <summary>What the piece becomes besides <see cref="Replacement"/>: the sheet each zero-thickness fin is kept as, each after it
    /// in the object list. With any, the replacement and these are in the new group <see cref="GroupName"/>.</summary>
    public IReadOnlyList<C3dObject> Extras { get; init; } = [];

    /// <summary>The group the replacement and its <see cref="Extras"/> are put in, inside the piece's own; null when there are none.</summary>
    public string? GroupName { get; init; }

    /// <summary>Every face reference the replacement moves, in words.</summary>
    public IReadOnlyList<StepSplitMove> Moves { get; init; } = [];

    /// <summary>The piece's volume and the replacement's, µm³.</summary>
    public double VolumeUm3 { get; init; }
    public double NewVolumeUm3 { get; init; }

    /// <summary>What the replacement is, in one sentence: for a box, the size and the volume change the user confirms.</summary>
    public string Summary { get; init; } = "";

    /// <summary>True when the replacement is the piece exactly (to the file's tolerance); false for a box that is not the piece
    /// and for anything whose curved faces were cut into facets — what the user confirms first.</summary>
    public bool Exact { get; init; }

    public bool Refused => Refusals.Count > 0;
}

/// <summary>All three plans for one piece, read from one face table.</summary>
public sealed record StepConvertAnalysis(StepConvertPlan Prism, StepConvertPlan Box, StepConvertPlan Polyhedron)
{
    public StepConvertPlan this[StepConvertKind kind] => kind switch
    {
        StepConvertKind.Prism => Prism,
        StepConvertKind.Box => Box,
        _ => Polyhedron,
    };
}

public static class StepConvert
{
    /// <summary>The menu's words for each kind.</summary>
    public static string CommandOf(StepConvertKind kind) => kind switch
    {
        StepConvertKind.Prism => "Replace with Prism",
        StepConvertKind.Box => "Replace with Box",
        _ => "Convert to Polyhedron",
    };

    public static StepConvertPlan PrismOf(C3dDocument doc, string c3dPath, string objectName, GeometryKernel kernel, RunControl? control = null)
        => Analyse(doc, c3dPath, objectName, kernel, control).Prism;

    public static StepConvertPlan BoxOf(C3dDocument doc, string c3dPath, string objectName, GeometryKernel kernel, RunControl? control = null)
        => Analyse(doc, c3dPath, objectName, kernel, control).Box;

    public static StepConvertPlan PolyhedronOf(C3dDocument doc, string c3dPath, string objectName, GeometryKernel kernel, RunControl? control = null)
        => Analyse(doc, c3dPath, objectName, kernel, control).Polyhedron;

    /// <summary>The Step object named <paramref name="objectName"/>, analysed as <see cref="Analyse(C3dDocument, string, C3dStep, GeometryKernel, RunControl?)"/>.</summary>
    public static StepConvertAnalysis Analyse(C3dDocument doc, string c3dPath, string objectName, GeometryKernel kernel, RunControl? control = null)
    {
        var step = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dStep>()
                              .FirstOrDefault(s => string.Equals(s.Name, objectName, StringComparison.Ordinal));
        return step is null ? All(objectName, -1, $"No Step object is named '{objectName}'.") : Analyse(doc, c3dPath, step, kernel, control);
    }

    /// <summary>
    /// What each of the three replacements of <paramref name="step"/> would be — or every reason it cannot, gathered rather than
    /// stopping at the first. Reads the copied file's piece once (its solids, faces and loops) and, when a face is referenced,
    /// the faces of each replacement; writes nothing.
    /// </summary>
    /// <exception cref="GeometryKernelException">The kernel refused, crashed, or is absent.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing changed.</exception>
    public static StepConvertAnalysis Analyse(C3dDocument doc, string c3dPath, C3dStep step, GeometryKernel kernel, RunControl? control = null)
    {
        int index = doc.Objects.IndexOf(step);
        string name = step.Name;
        if (index < 0)
            return All(name, index, StepSplit.OperandOf(doc, step) is { } op
                ? StepSplit.OperandRefusal(step, op.Op, op.Role, "replace it")
                : $"'{name}' is not in this document.");

        string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, step.File);
        if (!File.Exists(file)) return All(name, index, $"'{name}' names '{step.File}', which is not in this 3D view's folder.");
        if (!string.Equals(StepImport.HashOf(File.ReadAllBytes(file)), step.Hash, StringComparison.OrdinalIgnoreCase))
            return All(name, index, $"'{step.File}' has changed since '{name}' was read from it, so its face numbers no longer mean what " +
                                    "the references on it assumed. Reload from Source first.");

        int dbu = Math.Max(doc.DbuPerMicron, 1);
        var tree = StepImport.StepTree(step, file, step.Hash, step.Solid, dbu);
        var built = kernel.Build(tree, null, control);
        if (built.Solids != 1)
            return All(name, index, $"'{name}' is {built.Solids} solids of part {step.Part} of '{step.File}'; Split into Solids first, then " +
                                    "replace each piece.");
        var faces = kernel.Faces(tree, null, control);
        var loops = kernel.Loops(tree, null, control);
        var refs = StepImport.References(doc, step);
        double volume = built.VolumeUm3;
        // The file's own tolerance — how far its vertices may sit from its faces — and never less than a DBU: a polyhedron
        // through the vertices is the piece only to that, so the volume it may differ by is the area times it.
        double tolUm = Math.Max(1.0 / dbu, loops.SelectMany(l => l.Loops).Select(l => l.ToleranceUm).DefaultIfEmpty(0).Max());
        double slack = faces.Sum(f => f.Area) * tolUm;

        Context cx = new(doc, step, index, faces, refs, kernel, control, volume, dbu, tolUm);

        // A curved face is cut into flat facets from the kernel's tessellation, each turning at most FacetRad. The mesher was
        // MEASURED, because neither of its limits means what it says: given a linear deflection it cuts at HALF its angle (15°
        // asked, 7.5° facets), and given a huge one it ignores the angle (30° facets whatever was asked). So the angle asked is
        // twice the facet's, and the linear deflection is the sag of a 2·FacetRad chord on the smallest radius — loose enough
        // never to bind before the angle does.
        GeometryKernelMesh? mesh = null;
        if (faces.Any(f => f.Kind != "plane"))
        {
            double r = faces.Where(f => f.Kind != "plane" && f.MinRadius > 0).Select(f => f.MinRadius).DefaultIfEmpty(100).Min();
            mesh = kernel.Tessellate(tree, r * (1 - Math.Cos(FacetRad)), 2 * FacetRad, null, control);
        }

        // Convert to Polyhedron.
        var pr = Polyhedron(name, faces, loops, mesh, volume, slack, dbu);
        var poly = pr.H;
        string facetted = pr.Facets > 0
            ? $"its {pr.Curved} {(pr.Curved!.StartsWith("1 ", StringComparison.Ordinal) ? "is" : "are")} cut into {pr.Facets} flat facets, each turning at most " +
              $"{FacetRad * 180 / Math.PI:0}°, {Change(pr.VolumeUm3, volume)} % volume"
            : "";
        var fins = pr.Fins ?? [];
        string kept = fins.Count == 0 ? "" : cx.FinWords(fins);
        var polyPlan = poly is null
            ? cx.Refuse(StepConvertKind.Polyhedron, pr.Why!)
            : fins.Count > 0
                ? cx.Finish(StepConvertKind.Polyhedron, poly, exact: false,
                            $"'{name}' becomes a polyhedron of {poly.Faces.Count} flat faces and {cx.SheetCount(fins.Count)}: {kept}" +
                            (pr.Facets > 0 ? $"; {facetted}." : "."), pr.VolumeUm3, fins)
            : pr.Facets == 0
                ? cx.Finish(StepConvertKind.Polyhedron, poly, exact: true, $"'{name}' becomes a polyhedron of {poly.Faces.Count} flat faces; nothing changes shape.")
                : cx.Finish(StepConvertKind.Polyhedron, poly, exact: false, $"'{name}' becomes a polyhedron: {facetted}.", pr.VolumeUm3);

        // Replace with Prism: the same polyhedron, recognised.
        StepConvertPlan prismPlan;
        if (poly is null)
            prismPlan = cx.Refuse(StepConvertKind.Prism, pr.Why!);
        else if (PrismOf(poly) is not { } prism)
            prismPlan = cx.Refuse(StepConvertKind.Prism, $"'{name}' is not an extrusion along x, y or z: no two opposite flat faces have the same " +
                                                         "outline with every other face square to them. Convert to Polyhedron keeps it" +
                                                         (pr.Facets > 0 ? ", its curved faces cut into flat facets." : " exactly."));
        else
        {
            char axis = prism.Plane switch { C3dPlane.YZ => 'x', C3dPlane.XZ => 'y', _ => 'z' };
            prismPlan = cx.Finish(StepConvertKind.Prism, prism, exact: pr.Facets == 0 && fins.Count == 0,
                $"'{name}' becomes a prism along {axis}: an outline of {prism.Outline.Count} points{(prism.Holes.Count > 0 ? $" with {prism.Holes.Count} hole{(prism.Holes.Count == 1 ? "" : "s")}" : "")}, " +
                $"{Length(Math.Abs(prism.Height) / (double)dbu)} long; " + (pr.Facets > 0 ? facetted + "." : "nothing changes shape.") +
                (fins.Count > 0 ? $" It comes with {cx.SheetCount(fins.Count)}: {kept}." : ""), pr.VolumeUm3, fins);
        }

        // Replace with Box: the tight box of every face, in the piece's own frame.
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (var f in faces.Where(f => f.Box.Length == 6))
            for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], f.Box[a]); hi[a] = Math.Max(hi[a], f.Box[a + 3]); }
        var min = new C3dPoint3(Dbu(lo[0], dbu), Dbu(lo[1], dbu), Dbu(lo[2], dbu));
        var max = new C3dPoint3(Dbu(hi[0], dbu), Dbu(hi[1], dbu), Dbu(hi[2], dbu));
        var box = new C3dBox { Min = min, Size = new C3dPoint3(max.X - min.X, max.Y - min.Y, max.Z - min.Z) };
        double[] size = [box.Size.X / (double)dbu, box.Size.Y / (double)dbu, box.Size.Z / (double)dbu];
        double boxVolume = size[0] * size[1] * size[2];
        bool boxExact = Math.Abs(boxVolume - volume) <= slack;
        string dims = Dimensions(size);
        string change = Change(boxVolume, volume);
        var boxPlan = cx.Finish(StepConvertKind.Box, box, boxExact, boxExact
            ? $"'{name}' is its own bounding box, {dims}; nothing changes shape."
            : $"'{name}' is replaced by its bounding box: {dims}, {change} % volume; its chamfers, drafts and curved faces are not kept.",
            boxVolume);

        return new StepConvertAnalysis(prismPlan, boxPlan, polyPlan);
    }

    /// <summary>
    /// The replacement, at the piece's index, with every reference re-pointed, and its <see cref="StepConvertPlan.Extras"/> after it.
    /// The one function the GUI and any headless path call.
    /// </summary>
    /// <exception cref="StepImportException">The plan was refused, or no longer fits the document.</exception>
    public static C3dObject Apply(StepConvertPlan plan, C3dDocument doc)
    {
        if (plan.Refused || plan.Replacement is not { } replacement)
            throw new StepImportException(StepDiagnostics.Convert(string.Join(" ", plan.Refusals)));
        if (plan.Index < 0 || plan.Index >= doc.Objects.Count || doc.Objects[plan.Index] is not C3dStep old || old.Name != plan.Object)
            throw new StepImportException(StepDiagnostics.Convert($"The document changed since '{plan.Object}''s {CommandOf(plan.Kind)} was planned: try it again."));
        if (StepImport.References(doc, old).FirstOrDefault(r => !plan.Map.ContainsKey(r.Face)) is { } stale)
            throw new StepImportException(StepDiagnostics.Convert($"{stale.What} is on {plan.Object}/face{stale.Face}, which the plan did not place: try it again."));

        StepImport.Repoint(doc, old, n => plan.Map.TryGetValue(n, out var face) ? (replacement, face) : null);
        doc.Objects[plan.Index] = replacement;
        doc.Objects.InsertRange(plan.Index + 1, plan.Extras);
        return replacement;
    }

    // ── the plans' shared half ──────────────────────────────────────────────────────────────

    private sealed partial record Context(C3dDocument Doc, C3dStep Step, int Index, IReadOnlyList<GeometryKernelFace> Faces,
                                  List<StepImport.FaceRef> Refs, GeometryKernel Kernel, RunControl? Control, double Volume, int Dbu,
                                  double TolUm)
    {
        public StepConvertPlan Refuse(StepConvertKind kind, string why) => new(kind, Step.Name, Index, null, [why]) { VolumeUm3 = Volume };

        /// <summary>The replacement carrying the piece's name and the rest, and every referenced face sent to the face of it that
        /// coincides — or a refusal naming each that has none.</summary>
        public StepConvertPlan Finish(StepConvertKind kind, C3dObject geometry, bool exact, string summary, double? newVolume = null,
                                      IReadOnlyList<StepFin>? fins = null)
        {
            var replacement = Carry(Step, geometry);
            string name = Step.Name;
            var extras = new List<C3dObject>();
            if (fins is { Count: > 0 })
            {
                string path = Step.Group is { Length: > 0 } g ? g + C3dGroups.Separator + NewGroup : NewGroup;
                replacement.Group = path;
                for (int k = 0; k < fins.Count; k++)
                {
                    var sheet = Carry(Step, C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(fins[k].Sheet)));
                    sheet.Name = SheetNames[k];
                    sheet.Group = path;
                    extras.Add(sheet);
                }
            }
            var refusals = new List<string>();
            var map = new Dictionary<int, string>();
            var moves = new List<StepSplitMove>();
            if (Refs.Count > 0)
            {
                var probe = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(replacement));
                probe.Placement = new C3dPlacement();
                var now = Kernel.Faces(GeometryKernelTree.From(probe, Dbu), null, Control);
                string what = kind switch { StepConvertKind.Box => "the box", StepConvertKind.Prism => "the prism", _ => "the polyhedron" };
                foreach (int n in Refs.Select(r => r.Face).Distinct().Order())
                {
                    string label = Refs.First(r => r.Face == n).What;
                    if (n < 1 || n > Faces.Count)
                    {
                        refusals.Add($"{label} is on {name}/face{n}, which '{name}' does not have.");
                        continue;
                    }
                    var hits = now.Where(f => Coincide(Faces[n - 1], f, TolUm)).ToList();
                    if (hits.Count == 1)
                    {
                        map[n] = hits[0].Name;
                        continue;
                    }
                    refusals.Add(hits.Count == 0
                        ? $"{label} is on {name}/face{n}, and no face of {what} coincides with it; circuitRF does not move it to the nearest face."
                        : $"{label} is on {name}/face{n}, which coincides with {hits.Count} faces of {what}; circuitRF does not choose one.");
                }
                foreach (var r in Refs.Where(r => map.ContainsKey(r.Face)))
                    moves.Add(new StepSplitMove(r.What, $"{name}/face{r.Face}", $"{name}/{map[r.Face]}"));
            }
            return new StepConvertPlan(kind, name, Index, refusals.Count == 0 ? replacement : null, refusals)
            {
                Extras = extras, GroupName = extras.Count > 0 ? NewGroup : null, Map = map, Moves = moves, VolumeUm3 = Volume, NewVolumeUm3 = newVolume ?? Volume, Summary = summary, Exact = exact,
            };
        }
    }

    // ── a fin kept as a sheet ───────────────────────────────────────────────────────────────

    /// <summary>A zero-thickness fin: its outline as a sheet on the plane it lies in (the piece's frame, no name yet), and the faces
    /// it was cut from.</summary>
    public sealed record StepFin(C3dSheet Sheet, string Between);

    private sealed partial record Context
    {
        /// <summary>The new group's name: the piece's, unless a group is already called that.</summary>
        public string NewGroup => _group ??= Free(Step.Name, C3dGroups.Names(Doc));
        private string? _group;

        /// <summary>The sheets' names: <c>&lt;piece&gt;_sheet</c>, or numbered when there are several; none already an object's.</summary>
        public IReadOnlyList<string> SheetNames => _sheets ??= Sheets();
        private IReadOnlyList<string>? _sheets;

        private IReadOnlyList<string> Sheets()
        {
            var used = Doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).Select(o => o.Name).Concat(Doc.Instances.Select(i => i.Name))
                          .ToHashSet(StringComparer.Ordinal);
            int count = Math.Max(1, _finCount);
            var names = new List<string>();
            for (int k = 1; k <= count; k++)
            {
                string n = Free(count == 1 ? $"{Step.Name}_sheet" : $"{Step.Name}_sheet{k}", used);
                used.Add(n);
                names.Add(n);
            }
            return names;
        }

        private int _finCount;

        /// <summary>"a sheet" or "3 sheets" — and how many names <see cref="SheetNames"/> makes.</summary>
        public string SheetCount(int n) { _finCount = n; return n == 1 ? "a sheet" : $"{n} sheets"; }

        /// <summary>The fins in words: where each was, how big, what it is kept as, and the group.</summary>
        public string FinWords(IReadOnlyList<StepFin> fins)
        {
            SheetCount(fins.Count);
            var each = fins.Select((f, k) =>
            {
                var o = f.Sheet.Outline;
                double du = (o.Max(p => p.U) - o.Min(p => p.U)) / (double)Dbu, dv = (o.Max(p => p.V) - o.Min(p => p.V)) / (double)Dbu;
                return $"the zero-thickness fin between {f.Between} ({Dims2(du, dv)}) is kept as the sheet '{SheetNames[k]}'";
            });
            string where = Step.Group is { Length: > 0 } g ? $" inside '{C3dGroups.NameOf(g)}'" : "";
            return $"{string.Join("; ", each)}. It has no volume, so the solid's volume does not change. The polyhedron and " +
                   $"{(fins.Count == 1 ? "the sheet" : "the sheets")} go in a new group '{NewGroup}'{where}";
        }

        private static string Free(string stem, ISet<string> used)
        {
            if (!used.Contains(stem)) return stem;
            for (int k = 2; ; k++) if (!used.Contains($"{stem}_{k}")) return $"{stem}_{k}";
        }

        private static string Dims2(double u, double v)
            => Math.Min(u, v) >= 100
                ? $"{(u / 1000).ToString("0.00", CultureInfo.InvariantCulture)} × {(v / 1000).ToString("0.00", CultureInfo.InvariantCulture)} mm"
                : $"{u.ToString("0.0", CultureInfo.InvariantCulture)} × {v.ToString("0.0", CultureInfo.InvariantCulture)} µm";
    }

    /// <summary>All three refused for one reason — what a caller reports when the kernel itself refused.</summary>
    public static StepConvertAnalysis Refused(string name, int index, string why) => All(name, index, why);

    private static StepConvertAnalysis All(string name, int index, string why)
        => new(new(StepConvertKind.Prism, name, index, null, [why]), new(StepConvertKind.Box, name, index, null, [why]),
               new(StepConvertKind.Polyhedron, name, index, null, [why]));

    /// <summary><paramref name="geometry"/> carrying <paramref name="step"/>'s name, material, role, group, placement,
    /// visibility, transparency, appearance, solve flag and unread keys. Its face images move with the references.</summary>
    private static C3dObject Carry(C3dStep step, C3dObject geometry)
    {
        var t = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(step));
        geometry.Name = t.Name;
        geometry.Material = t.Material;
        geometry.Role = t.Role;
        geometry.Group = t.Group;
        geometry.Placement = t.Placement;
        geometry.Hidden = t.Hidden;
        geometry.Transparency = t.Transparency;
        geometry.Appearance = t.Appearance;
        geometry.Model = t.Model;
        geometry.Unread = t.Unread;
        geometry.FaceImages = null;
        return geometry;
    }

    /// <summary>The same face to the file's tolerance (a DBU at least): the same kind and outward normal, the centroid within twice
    /// it, the area within what moving each edge by twice it makes of it.</summary>
    private static bool Coincide(GeometryKernelFace was, GeometryKernelFace now, double tolUm)
    {
        if (was.Kind != now.Kind || was.Centroid.Length != 3 || now.Centroid.Length != 3) return false;
        if (was.Normal.Length == 3 && now.Normal.Length == 3
            && was.Normal[0] * now.Normal[0] + was.Normal[1] * now.Normal[1] + was.Normal[2] * now.Normal[2] < 1 - 1e-6) return false;
        double d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (was.Centroid[i] - now.Centroid[i]) * (was.Centroid[i] - now.Centroid[i])));
        if (d > 2 * tolUm) return false;
        double extent = was.Box.Length == 6 ? was.Box[3] - was.Box[0] + was.Box[4] - was.Box[1] + was.Box[5] - was.Box[2] : 0;
        return Math.Abs(was.Area - now.Area) <= Math.Max(1e-6 * was.Area, 2 * tolUm * extent);
    }

    // ── Convert to Polyhedron ───────────────────────────────────────────────────────────────

    /// <summary>How far one facet of a curved face turns: 15°, so a 90° lead bend is six facets.</summary>
    public const double FacetRad = Math.PI / 12;

    /// <summary>A polyhedron, or why not; the curved faces in words, how many facets they became (0: exact) and its volume.</summary>
    private sealed record Poly(C3dPolyhedron? H, string? Why, string? Curved = null, int Facets = 0, double VolumeUm3 = 0,
                               IReadOnlyList<StepFin>? Fins = null);

    /// <summary>
    /// The piece as a polyhedron. A flat face k is the polyhedron's face<c>k</c>: from its loops when every face is flat, from the
    /// kernel's tessellation otherwise (<paramref name="mesh"/>), whose edge nodes the faces share so the facets meet the flat
    /// faces with no gap. A curved face k becomes flat facets <c>face&lt;k&gt;.1</c>, <c>.2</c> …: its triangles merged where they
    /// are coplanar (a cylinder's strips), left as triangles where the merge would not be flat to a DBU.
    /// <para>EVERY VERTEX IS SNAPPED onto the flat faces that meet at it before it is rounded to a DBU. A real file's vertices sit
    /// off its faces by its own tolerance (1.7 µm in a vendor's package model), which a polyhedron through them would inherit as
    /// faces that are not flat; the snap puts each on the planes it belongs to, the true corner.</para>
    /// Refused when the result does not close or is not flat, and — exact only — when it does not hold the piece's volume within
    /// the area times the file's tolerance.
    /// </summary>
    private static Poly Polyhedron(string name, IReadOnlyList<GeometryKernelFace> faces, IReadOnlyList<GeometryKernelFaceLoops> loops,
                                   GeometryKernelMesh? mesh, double volume, double slack, int dbu)
    {
        var curvedFaces = faces.Select((f, i) => (f.Kind, Name: StepImport.FaceName(i + 1))).Where(f => f.Kind != "plane").ToList();
        string? curved = curvedFaces.Count == 0 ? null : string.Join(" and ", curvedFaces.GroupBy(c => c.Kind).Select(g =>
            $"{g.Count()} {KindWord(g.Key)} face{(g.Count() == 1 ? "" : "s")} ({Names(g.Select(c => c.Name).ToList())})"));
        if (curved is not null && mesh is null)
            return new(null, $"'{name}' has {curved}; only a piece whose faces are all flat converts to a polyhedron.", curved);
        if (loops.Count != faces.Count) return new(null, $"The kernel read {faces.Count} faces of '{name}' and the loops of {loops.Count}; nothing was converted.", curved);

        // 1. The points, shared where they round to one DBU, and which flat faces each lies on.
        var raw = new List<double[]>();
        var rawAt = new Dictionary<C3dPoint3, int>();
        var onPlanes = new List<HashSet<int>>();
        int Raw(double x, double y, double z)
        {
            var key = new C3dPoint3(Dbu(x, dbu), Dbu(y, dbu), Dbu(z, dbu));
            if (!rawAt.TryGetValue(key, out int i)) { rawAt[key] = i = raw.Count; raw.Add([x, y, z]); onPlanes.Add([]); }
            return i;
        }
        // A face with no area (a fin's top, a line) has no plane to snap to: the kernel gives it the origin for a centroid.
        bool Flat(int f) => faces[f].Kind == "plane" && faces[f].Normal.Length == 3 && faces[f].Centroid.Length == 3 && faces[f].Area > 0;

        var rings = new List<List<List<int>>>();       // exact: per face, its loops
        var tris = new List<(int A, int B, int C)>[faces.Count];
        if (mesh is null)
        {
            var bent = loops.Select((l, i) => (l, i)).Where(x => x.l.Loops.Count == 0 || x.l.Loops.Any(r => !r.Straight))
                            .Select(x => StepImport.FaceName(x.i + 1)).ToList();
            if (bent.Count > 0)
                return new(null, $"'{name}''s flat face{(bent.Count == 1 ? "" : "s")} {Names(bent)} {(bent.Count == 1 ? "is" : "are")} bounded by a curved " +
                                 "edge; only a piece bounded by straight edges converts to a polyhedron.");
            for (int f = 0; f < faces.Count; f++)
            {
                var fr = new List<List<int>>();
                foreach (var l in loops[f].Loops)
                {
                    var ring = new List<int>();
                    for (int k = 0; k + 2 < l.Points.Length; k += 3)
                    {
                        int i = Raw(l.Points[k], l.Points[k + 1], l.Points[k + 2]);
                        if (Flat(f)) onPlanes[i].Add(f);
                        ring.Add(i);
                    }
                    fr.Add(ring);
                }
                rings.Add(fr);
            }
        }
        else
        {
            for (int f = 0; f < faces.Count; f++) tris[f] = [];
            var v = mesh.Vertices;
            for (int t = 0; t < mesh.TriangleFace.Length; t++)
            {
                int f = (int)mesh.TriangleFace[t];
                if (f >= faces.Count) continue;
                int Node(uint n) => Raw(v[3 * n], v[3 * n + 1], v[3 * n + 2]);
                var tri = (Node(mesh.Triangles[3 * t]), Node(mesh.Triangles[3 * t + 1]), Node(mesh.Triangles[3 * t + 2]));
                if (Flat(f)) { onPlanes[tri.Item1].Add(f); onPlanes[tri.Item2].Add(f); onPlanes[tri.Item3].Add(f); }
                tris[f].Add(tri);
            }
        }

        // 2. Each point snapped onto its flat faces, then rounded; points that round to one DBU become one vertex.
        var h = new C3dPolyhedron();
        var at = new Dictionary<C3dPoint3, int>();
        var final = new int[raw.Count];
        var snapped = Snap(raw, onPlanes, faces);
        for (int i = 0; i < raw.Count; i++)
        {
            var p = snapped[i];
            var q = new C3dPoint3(Dbu(p[0], dbu), Dbu(p[1], dbu), Dbu(p[2], dbu));
            if (!at.TryGetValue(q, out int k)) { at[q] = k = h.Vertices.Count; h.Vertices.Add(q); }
            final[i] = k;
        }

        // 3. The faces. Exact: each flat face's loops, less any zero-thickness fin (CutFins), which is kept as a sheet. A face with
        // no area — a fin's top, a line — encloses nothing and is left out; the closure check below says whether that was right.
        int facets = 0;
        var fins = new List<StepFin>();
        if (mesh is null)
        {
            var flat = new List<(string Name, double[] Normal, List<List<int>> Rings)>();
            for (int f = 0; f < faces.Count; f++)
            {
                string faceName = StepImport.FaceName(f + 1);
                var fr = rings[f].Select(r => Compact(r.Select(i => final[i]))).ToList();
                if (fr.Count > 0 && (fr[0].Count < 3 || Newell(h.Vertices, fr[0]) == (0, 0, 0))) continue;
                if (fr.Any(r => r.Count < 3))
                    return new(null, $"'{name}''s {faceName} has a loop that rounds to fewer than three points at a DBU; nothing was converted.");
                flat.Add((faceName, faces[f].Normal.Length == 3 ? faces[f].Normal : [0, 0, 0], fr));
            }
            CutFins(h, at, flat, fins);
            foreach (var (faceName, n, fr) in flat)
            {
                if (Along(h.Vertices, fr[0], n) < 0) fr[0].Reverse();
                foreach (var hole in fr.Skip(1)) if (Along(h.Vertices, hole, n) > 0) hole.Reverse();
                h.Faces.Add(new C3dFace { Name = faceName, Outer = fr[0], Holes = [.. fr.Skip(1)] });
            }
            if (fins.Count > 0) Prune(h);                              // a fin's top corners are no face's now
        }
        else for (int f = 0; f < faces.Count; f++)
        {
            string faceName = StepImport.FaceName(f + 1);
            double[] n = faces[f].Normal.Length == 3 ? faces[f].Normal : [0, 0, 0];
            var ft = tris[f].Select(t => (final[t.A], final[t.B], final[t.C])).Where(t => t.Item1 != t.Item2 && t.Item2 != t.Item3 && t.Item1 != t.Item3).ToList();
            if (ft.Count == 0) continue;     // a sliver thinner than a DBU: its neighbours close over it
            if (Flat(f))
            {
                if (Patch(h.Vertices, ft, n) is not { } face) return new(null, $"'{name}''s {faceName} does not make one face once rounded to a DBU; nothing was converted.");
                face.Name = faceName;
                h.Faces.Add(face);
                continue;
            }
            int k = 0;
            foreach (var group in Coplanar(h.Vertices, ft))
            {
                var tn = TriangleNormal(h.Vertices, group[0]);
                var merged = group.Count > 1 ? Patch(h.Vertices, group, tn) : null;
                if (merged is not null && Deviation(h.Vertices, merged.Outer) <= C3dKernel.PlanarityDbu)
                {
                    merged.Name = $"{faceName}.{++k}";
                    h.Faces.Add(merged);
                }
                else
                    foreach (var t in group) h.Faces.Add(new C3dFace { Name = $"{faceName}.{++k}", Outer = [t.Item1, t.Item2, t.Item3] });
            }
            facets += k;
        }

        // 4. It must be a solid, flat, and — when exact — the piece's volume.
        var b = C3dBrepBuild.Polyhedron(h);
        if (b.ClosureProblem() is { } open) return new(null, $"'{name}''s faces, rounded to a DBU, do not close: {open}", curved);
        if (b.Volume6 <= 0) return new(null, $"'{name}''s faces, rounded to a DBU, enclose no volume.", curved);
        var warped = Enumerable.Range(0, h.Faces.Count).Where(f => !b.IsPlanar(f)).Select(f => h.Faces[f].Name).ToList();
        if (warped.Count > 0) return new(null, $"'{name}''s {Names(warped)} {(warped.Count == 1 ? "is" : "are")} not flat to a DBU once rounded.", curved);
        double vol = (double)b.Volume6 / 6 / ((double)dbu * dbu * dbu);
        if (mesh is null && Math.Abs(vol - volume) > slack)
            return new(null, $"'{name}' as a polyhedron holds {vol:G6} µm³ against the piece's {volume:G6} µm³; nothing was converted.");
        return new(h, null, curved, facets, vol, fins);
    }

    /// <summary>
    /// Every zero-thickness fin cut out of <paramref name="flat"/>: on each axis-aligned plane where faces of the piece face both
    /// ways and overlap, the overlap is a fin's two sides back to back. It is cut out of each of those faces (a face left with
    /// nothing goes; one left in several pieces becomes <c>face&lt;n&gt;.1</c>, <c>.2</c> …), and each region of it is added to
    /// <paramref name="fins"/> as a sheet on that plane, in the piece's frame. Integer DBU throughout, so the faces left meet their
    /// neighbours exactly, and collinear points are kept so a neighbour's vertex on an edge is still on it.
    /// </summary>
    private static void CutFins(C3dPolyhedron h, Dictionary<C3dPoint3, int> at, List<(string Name, double[] Normal, List<List<int>> Rings)> flat,
                                List<StepFin> fins)
    {
        var v = h.Vertices;
        static (int B, int C) Others(int a) => a switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };
        int AxisOf(List<int> ring)
        {
            for (int a = 0; a < 3; a++) if (ring.All(i => C3dBrepBuild.Get(v[i], a) == C3dBrepBuild.Get(v[ring[0]], a))) return a;
            return -1;
        }
        Path64 Path(List<int> ring, int a)
        {
            var (b, c) = Others(a);
            return [.. ring.Select(i => new Point64(C3dBrepBuild.Get(v[i], b), C3dBrepBuild.Get(v[i], c)))];
        }
        C3dPoint3 Point(Point64 p, int a, long w)
        {
            var (b, c) = Others(a);
            return C3dBrepBuild.With(C3dBrepBuild.With(C3dBrepBuild.With(default, a, w), b, p.X), c, p.Y);
        }
        List<int> Lift(Path64 r, int a, long w)
        {
            var ring = new List<int>();
            foreach (var p in r)
            {
                var q = Point(p, a, w);
                if (!at.TryGetValue(q, out int k)) { at[q] = k = v.Count; v.Add(q); }
                ring.Add(k);
            }
            return Compact(ring);
        }

        var pieces = new Dictionary<int, List<List<List<int>>>>();
        var planes = Enumerable.Range(0, flat.Count).Select(i => (I: i, A: AxisOf(flat[i].Rings[0])))
                               .Where(x => x.A >= 0 && Math.Abs(flat[x.I].Normal[x.A]) > 0.5)
                               .GroupBy(x => (x.A, W: C3dBrepBuild.Get(v[flat[x.I].Rings[0][0]], x.A)));
        foreach (var g in planes)
        {
            int a = g.Key.A;
            long w = g.Key.W;
            var up = g.Where(x => flat[x.I].Normal[a] > 0).Select(x => x.I).ToList();
            var down = g.Where(x => flat[x.I].Normal[a] < 0).Select(x => x.I).ToList();
            if (up.Count == 0 || down.Count == 0) continue;
            Paths64 Of(IEnumerable<int> fs) => [.. fs.SelectMany(i => flat[i].Rings).Select(r => Path(r, a))];
            var overlap = Clip(ClipType.Intersection, Of(up), Of(down));
            if (overlap.Count == 0) continue;
            var cut = new Paths64(overlap.SelectMany(p => p));
            foreach (int i in up.Concat(down))
                pieces[i] = [.. Clip(ClipType.Difference, Of([i]), cut).Select(p => p.Select(r => Lift(r, a, w)).ToList())];
            var plane = a switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };
            List<C3dPoint2> Ring2(Path64 r) => [.. r.Select(p => { var (u, vv, _) = C3dBrepBuild.ToPlane(plane, Point(p, a, w)); return new C3dPoint2(u, vv); })];
            foreach (var poly in overlap)
            {
                var between = up.Concat(down).Where(i => Clip(ClipType.Intersection, Of([i]), new Paths64(poly)).Count > 0)
                                .Select(i => flat[i].Name).Order(StringComparer.Ordinal).ToList();
                fins.Add(new StepFin(new C3dSheet
                {
                    Plane = plane, Offset = C3dBrepBuild.ToPlane(plane, Point(poly[0][0], a, w)).W,
                    Outline = Ring2(poly[0]), Holes = [.. poly.Skip(1).Select(Ring2)],
                }, string.Join(" and ", between)));
            }
        }
        for (int i = flat.Count - 1; i >= 0; i--)
        {
            if (!pieces.TryGetValue(i, out var rest)) continue;
            var (name, n, _) = flat[i];
            flat.RemoveAt(i);
            rest = [.. rest.Where(p => p[0].Count >= 3 && p.All(r => r.Count >= 3))];
            for (int k = 0; k < rest.Count; k++) flat.Insert(i + k, (rest.Count == 1 ? name : $"{name}.{k + 1}", n, rest[k]));
        }
    }

    /// <summary>The vertices no face uses, removed, and every face renumbered.</summary>
    private static void Prune(C3dPolyhedron h)
    {
        var used = h.Faces.SelectMany(f => f.Outer.Concat(f.Holes.SelectMany(x => x))).ToHashSet();
        var map = new int[h.Vertices.Count];
        var kept = new List<C3dPoint3>();
        for (int i = 0; i < map.Length; i++) map[i] = used.Contains(i) ? Add(h.Vertices[i]) : -1;
        int Add(C3dPoint3 p) { kept.Add(p); return kept.Count - 1; }
        h.Vertices = kept;
        foreach (var f in h.Faces)
        {
            f.Outer = [.. f.Outer.Select(i => map[i])];
            f.Holes = [.. f.Holes.Select(x => x.Select(i => map[i]).ToList())];
        }
    }

    /// <summary><paramref name="subject"/> clipped by <paramref name="clip"/> (even-odd, so a face's holes are holes), collinear points
    /// kept: each polygon of the result, its outline first and then its holes.</summary>
    private static List<List<Path64>> Clip(ClipType type, Paths64 subject, Paths64 clip)
    {
        var c = new Clipper64 { PreserveCollinear = true };
        c.AddSubject(subject);
        c.AddClip(clip);
        var tree = new PolyTree64();
        c.Execute(type, FillRule.EvenOdd, tree);
        var polys = new List<List<Path64>>();
        void Collect(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var solid = node[i];
                if (solid.Polygon is not { Count: >= 3 } outline || Math.Abs(Clipper.Area(outline)) == 0) continue;
                var poly = new List<Path64> { outline };
                for (int j = 0; j < solid.Count; j++)
                {
                    if (solid[j].Polygon is { Count: >= 3 } hole) poly.Add(hole);
                    Collect(solid[j]);
                }
                polys.Add(poly);
            }
        }
        Collect(tree);
        return polys;
    }

    /// <summary>
    /// The points moved onto their flat faces so that every face is flat. Where three faces meet, that is their corner; where
    /// four or more do, a file's planes rarely meet in one point, so the faces may move too, parallel to themselves: alternately
    /// each point goes to the least-squares meeting of its faces and each face to the mean offset of its points, until no point
    /// is off its faces by more than a hundredth of a nanometre. Nothing moves further than the file's own sloppiness.
    /// </summary>
    private static double[][] Snap(List<double[]> raw, List<HashSet<int>> onPlanes, IReadOnlyList<GeometryKernelFace> faces)
    {
        var x = raw.Select(p => (double[])p.Clone()).ToArray();
        var d = new Dictionary<int, double>();
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < raw.Count; i++)
            foreach (int f in onPlanes[i])
            {
                if (!members.TryGetValue(f, out var list)) { members[f] = list = []; d[f] = Dot(faces[f].Normal, faces[f].Centroid); }
                list.Add(i);
            }
        const double Eps = 1e-6;
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            for (int i = 0; i < x.Length; i++)
            {
                if (onPlanes[i].Count == 0) continue;
                var a = new double[3, 3];
                var b = new double[3];
                for (int r = 0; r < 3; r++) { a[r, r] = Eps; b[r] = Eps * x[i][r]; }
                foreach (int f in onPlanes[i])
                {
                    var n = faces[f].Normal;
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++) a[r, c] += n[r] * n[c];
                        b[r] += n[r] * d[f];
                    }
                }
                x[i] = Solve(a, b);
            }
            double worst = 0;
            foreach (var (f, list) in members)
            {
                var n = faces[f].Normal;
                double mean = list.Average(i => Dot(n, x[i]));
                d[f] = mean;
                foreach (int i in list) worst = Math.Max(worst, Math.Abs(Dot(n, x[i]) - mean));
            }
            if (worst < 1e-5) break;
        }
        return x;
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    /// <summary>Gaussian elimination with partial pivoting, for <see cref="Snap"/>'s 3 × 3 systems.</summary>
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            for (int j = 0; j < n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            (x[c], x[piv]) = (x[piv], x[c]);
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                for (int j = c; j < n; j++) m[r, j] -= f * m[c, j];
                x[r] -= f * x[c];
            }
        }
        for (int c = n - 1; c >= 0; c--)
        {
            for (int j = c + 1; j < n; j++) x[c] -= m[c, j] * x[j];
            x[c] /= m[c, c];
        }
        return x;
    }

    /// <summary>A ring with repeated neighbours (and a closing repeat) removed.</summary>
    private static List<int> Compact(IEnumerable<int> ids)
    {
        var ring = new List<int>();
        foreach (int i in ids) if (ring.Count == 0 || ring[^1] != i) ring.Add(i);
        while (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    /// <summary>
    /// The face a set of outward-wound triangles makes: its boundary (each edge no other triangle of the set runs back along),
    /// chained into rings — the one running along <paramref name="normal"/> its outline, the others its holes, which the
    /// triangles' winding already turns the other way. Null when the boundary does not chain into one outline.
    /// </summary>
    private static C3dFace? Patch(IReadOnlyList<C3dPoint3> v, List<(int, int, int)> tris, double[] normal)
    {
        var edges = new HashSet<(int, int)>();
        foreach (var (a, b, c) in tris)
            foreach (var e in new[] { (a, b), (b, c), (c, a) })
                if (!edges.Remove((e.Item2, e.Item1))) edges.Add(e);
        var next = new Dictionary<int, int>();
        foreach (var (a, b) in edges) if (!next.TryAdd(a, b)) return null;      // the boundary touches itself
        var rings = new List<List<int>>();
        var left = new HashSet<int>(next.Keys);
        while (left.Count > 0)
        {
            int s = left.First(), cur = s;
            var ring = new List<int>();
            do
            {
                if (!left.Remove(cur)) return null;
                ring.Add(cur);
                if (!next.TryGetValue(cur, out cur)) return null;
            } while (cur != s);
            rings.Add(ring);
        }
        var outer = rings.Where(r => Along(v, r, normal) > 0).ToList();
        if (outer.Count != 1) return null;
        return new C3dFace { Outer = outer[0], Holes = [.. rings.Where(r => !ReferenceEquals(r, outer[0]))] };
    }

    /// <summary>A curved face's triangles in groups that share an edge and lie in one plane.</summary>
    private static List<List<(int, int, int)>> Coplanar(IReadOnlyList<C3dPoint3> v, List<(int, int, int)> tris)
    {
        var parent = Enumerable.Range(0, tris.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var normals = tris.Select(t => TriangleNormal(v, t)).ToList();
        var byEdge = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                if (byEdge.Remove((q, p), out int u))
                {
                    if (Dot(normals[t], normals[u]) > 1 - 1e-9) parent[Find(t)] = Find(u);
                }
                else byEdge[(p, q)] = t;
            }
        }
        return [.. Enumerable.Range(0, tris.Count).GroupBy(Find).Select(g => g.Select(i => tris[i]).ToList())];
    }

    private static double[] TriangleNormal(IReadOnlyList<C3dPoint3> v, (int A, int B, int C) t)
    {
        var (a, b, c) = (v[t.A], v[t.B], v[t.C]);
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, wx = c.X - a.X, wy = c.Y - a.Y, wz = c.Z - a.Z;
        double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
        double m = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return m > 0 ? [nx / m, ny / m, nz / m] : [0, 0, 0];
    }

    /// <summary>The largest distance of a ring's vertices from its Newell plane through their centroid, DBU.</summary>
    private static double Deviation(IReadOnlyList<C3dPoint3> v, List<int> ring)
    {
        var (nx, ny, nz) = Newell(v, ring);
        double m = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (m == 0) return double.MaxValue;
        double cx = ring.Average(i => (double)v[i].X), cy = ring.Average(i => (double)v[i].Y), cz = ring.Average(i => (double)v[i].Z);
        return ring.Max(i => Math.Abs(nx * (v[i].X - cx) + ny * (v[i].Y - cy) + nz * (v[i].Z - cz)) / m);
    }

    private static double Along(IReadOnlyList<C3dPoint3> v, List<int> ring, double[] n)
    {
        var (x, y, z) = Newell(v, ring);
        return x * n[0] + y * n[1] + z * n[2];
    }

    private static (double X, double Y, double Z) Newell(IReadOnlyList<C3dPoint3> v, List<int> ring)
    {
        double x = 0, y = 0, z = 0;
        for (int k = 0; k < ring.Count; k++)
        {
            var a = v[ring[k]];
            var c = v[ring[(k + 1) % ring.Count]];
            x += (double)(a.Y - c.Y) * (a.Z + c.Z);
            y += (double)(a.Z - c.Z) * (a.X + c.X);
            z += (double)(a.X - c.X) * (a.Y + c.Y);
        }
        return (x, y, z);
    }
    // ── Replace with Prism ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The polyhedron as a prism along z, then y, then x — the first that C3dRecognition.ZPrism recognises once the axis is
    /// turned to z (an XZ plane's frame is left-handed, so its rings are turned too). Exact: the recognition is integer
    /// equality, and the prism's own volume must be the polyhedron's to the unit.
    /// </summary>
    private static C3dPrism? PrismOf(C3dPolyhedron h)
    {
        var volume = Int128.Abs(C3dBrepBuild.Polyhedron(h).Volume6);
        foreach (var plane in new[] { C3dPlane.XY, C3dPlane.XZ, C3dPlane.YZ })
        {
            bool flip = plane == C3dPlane.XZ;
            var turned = new C3dPolyhedron
            {
                Vertices = [.. h.Vertices.Select(p => { var (u, v, w) = C3dBrepBuild.ToPlane(plane, p); return new C3dPoint3(u, v, w); })],
                Faces = [.. h.Faces.Select(f => new C3dFace
                {
                    Name = f.Name,
                    Outer = flip ? [.. Enumerable.Reverse(f.Outer)] : [.. f.Outer],
                    Holes = [.. f.Holes.Select(x => flip ? Enumerable.Reverse(x).ToList() : x.ToList())],
                })],
            };
            if (C3dRecognition.ZPrism(turned) is not { } z) continue;
            var prism = new C3dPrism { Plane = plane, Offset = z.Z0, Height = z.Z1 - z.Z0, Outline = z.Rings[0], Holes = [.. z.Rings.Skip(1)] };
            if (Int128.Abs(C3dBrepBuild.Prism(prism).Volume6) == volume) return prism;
        }
        return null;
    }

    // ── words ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The volume change in percent, signed: one decimal, or two when it is under a tenth.</summary>
    private static string Change(double now, double was)
    {
        if (was <= 0) return "+∞";
        double pc = (now - was) / was * 100;
        return pc.ToString(Math.Abs(pc) < 0.1 ? "+0.00;-0.00;0.00" : "+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    }

    private static long Dbu(double um, int dbu) => (long)Math.Round(um * dbu, MidpointRounding.AwayFromZero);

    private static string KindWord(string kind) => kind switch
    {
        "cylinder" => "cylindrical",
        "cone" => "conical",
        "sphere" => "spherical",
        "torus" => "toroidal",
        "bspline" => "freeform",
        _ => "curved",
    };

    /// <summary>Names, the first eight then how many more.</summary>
    private static string Names(IReadOnlyList<string> names)
        => string.Join(", ", names.Take(8)) + (names.Count > 8 ? $" and {names.Count - 8} more" : "");

    /// <summary>Three lengths in one unit: mm with two decimals, or µm with one when any is under 100 µm.</summary>
    private static string Dimensions(double[] um)
        => um.Min() >= 100
            ? string.Join(" × ", um.Select(v => (v / 1000).ToString("0.00", CultureInfo.InvariantCulture))) + " mm"
            : string.Join(" × ", um.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture))) + " µm";

    private static string Length(double um)
        => um >= 100 ? (um / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " mm" : um.ToString("0.0", CultureInfo.InvariantCulture) + " µm";
}
