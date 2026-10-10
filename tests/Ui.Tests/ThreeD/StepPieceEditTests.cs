// ================================================================
//  StepPieceEditTests.cs — the gate for brief-em3d-130 (options (b) then (a)): a Step piece replaced by a prism or its bounding
//  box, or converted to a polyhedron. Each piece is written to STEP by circuitRF's own writer and read back as a Step object of
//  one solid, offset and given a material in a group: an L-section with a slot through it (a prism along y — the left-handed
//  frame — whose caps are holed faces), a block chamfered on two crossing edges (box only, the volume change stated), a strip
//  rounded on one edge as a gull-wing lead's bend is (its bend cut into facets, stated, still a prism) and a body whose corner
//  the file's text puts 1.5 µm off its four faces (a vendor file's sloppiness). Which face is which is decided by geometry.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class StepPieceEditTests : IDisposable
{
    private static readonly C3dPoint3 Offset = new(250_000, -100_000, 50_000);     // DBU: 250, −100, 50 µm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step130-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public StepPieceEditTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── an extruded L-section with a slot: a prism exactly, and a polyhedron with holed faces ──

    [KernelFact]
    public void SlottedL_IsAPrismAlongY_AndAPolyhedronWithHoledCaps_ExactlyWhereItWas_TheReferenceMovingToTheCoincidingFace()
    {
        using var kernel = KernelForTests.New();
        var l = new C3dPrism
        {
            Name = "l", Plane = C3dPlane.XZ, Height = 400_000,
            Outline = [new(0, 0), new(600_000, 0), new(600_000, 200_000), new(200_000, 200_000), new(200_000, 500_000), new(0, 500_000)],
            Holes = [[new(300_000, 50_000), new(500_000, 50_000), new(500_000, 150_000), new(300_000, 150_000)]],
        };
        var (path, doc) = Piece(kernel, l, "lead");
        int cap = FaceOf(kernel, path, doc, f => f.Normal[1] < -0.999);       // the y = 0 cap, which has the slot in it
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "lead", Face = $"face{cap}", Kind = Em3dFaceBoundaryKind.Pec });
        var before = Elaborate(kernel, doc, path).Extent()!.Value;

        var analysis = StepConvert.Analyse(doc, path, "lead", kernel);
        Assert.Empty(analysis.Polyhedron.Refusals);
        var poly = Assert.IsType<C3dPolyhedron>(analysis.Polyhedron.Replacement);
        Assert.Equal(12, poly.Faces.Count);
        Assert.Equal(2, poly.Faces.Count(f => f.Holes.Count == 1));
        Assert.Equal($"face{cap}", analysis.Polyhedron.Map[cap]);

        Assert.Empty(analysis.Prism.Refusals);
        var prism = Assert.IsType<C3dPrism>(StepConvert.Apply(analysis.Prism, doc));
        Assert.Equal((C3dPlane.XZ, 6, 1, 400_000L), (prism.Plane, prism.Outline.Count, prism.Holes.Count, Math.Abs(prism.Height)));
        Assert.Equal(("lead", "Copper", "pkg", Offset), (prism.Name, prism.Material, prism.Group, prism.Placement.Origin));
        var after = Elaborate(kernel, doc, path).Extent()!.Value;
        Assert.Equal([before.X0, before.Y0, before.Z0, before.X1, before.Y1, before.Z1], [after.X0, after.Y0, after.Z0, after.X1, after.Y1, after.Z1],
                     (x, y) => Math.Abs(x - y) < 1e-12);
        var fb = Assert.Single(doc.FaceBoundaries);
        Assert.Equal("lead", fb.Object);
        AssertFace(kernel, doc, prism, fb.Face, [0, -1, 0], 1);
    }

    // ── a block chamfered on two crossing edges: box only, the volume change stated, an unmatched face refused ──

    [KernelFact]
    public void ChamferedBlock_IsNoPrism_IsAnExactPolyhedron_AndItsBoxStatesTheVolumeChange_RefusingTheCutBackFace()
    {
        using var kernel = KernelForTests.New();
        var block = new C3dChamfer
        {
            Name = "block", Distance = 50_000, Edges = ["xmax|zmax", "ymax|zmax"],
            Target = new C3dBox { Size = new(600_000, 400_000, 300_000) },
        };
        var (path, doc) = Piece(kernel, block, "body");
        int bottom = FaceOf(kernel, path, doc, f => f.Normal[2] < -0.999);
        int top = FaceOf(kernel, path, doc, f => f.Normal[2] > 0.999);
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "body", Face = $"face{bottom}", Kind = Em3dFaceBoundaryKind.Pec });

        var analysis = StepConvert.Analyse(doc, path, "body", kernel);
        Assert.Equal("'body' is not an extrusion along x, y or z: no two opposite flat faces have the same outline with every other face " +
                     "square to them. Convert to Polyhedron keeps it exactly.", Assert.Single(analysis.Prism.Refusals));
        Assert.Empty(analysis.Polyhedron.Refusals);
        Assert.Empty(analysis.Box.Refusals);
        Assert.False(analysis.Box.Exact);
        Assert.True(analysis.Box.NewVolumeUm3 > analysis.Box.VolumeUm3);
        Assert.StartsWith("'body' is replaced by its bounding box: 0.60 × 0.40 × 0.30 mm, +", analysis.Box.Summary, StringComparison.Ordinal);
        Assert.EndsWith(" % volume; its chamfers, drafts and curved faces are not kept.", analysis.Box.Summary, StringComparison.Ordinal);

        // The chamfers cut the top back, so no box face is it: refused, never moved to zmax.
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "body", Face = $"face{top}", Kind = Em3dFaceBoundaryKind.Pmc });
        analysis = StepConvert.Analyse(doc, path, "body", kernel);
        Assert.Equal($"the Pmc boundary on 'body' is on body/face{top}, and no face of the box coincides with it; circuitRF does not move it to " +
                     "the nearest face.", Assert.Single(analysis.Box.Refusals));
        Assert.Throws<StepImportException>(() => StepConvert.Apply(analysis.Box, doc));
        Assert.Empty(analysis.Polyhedron.Refusals);

        doc.FaceBoundaries.RemoveAt(1);
        var box = Assert.IsType<C3dBox>(StepConvert.Apply(StepConvert.Analyse(doc, path, "body", kernel).Box, doc));
        Assert.Equal((new C3dPoint3(0, 0, 0), new C3dPoint3(600_000, 400_000, 300_000)), (box.Min, box.Size));
        Assert.Equal("zmin", Assert.Single(doc.FaceBoundaries).Face);
    }

    // ── a strip rounded on one edge (a gull-wing lead's bend): its bend cut into facets, stated, and still a prism ──

    [KernelFact]
    public void RoundedStrip_BecomesAPolyhedronAndAPrismAlongY_ItsBendCutIntoFlatFacets_StatedAndNotExact()
    {
        using var kernel = KernelForTests.New();
        var (path, doc) = Strip(kernel);
        int round = FaceOf(kernel, path, doc, f => f.Kind == "cylinder");
        int end = FaceOf(kernel, path, doc, f => f.Normal[0] > 0.999);       // the strip's flat end, which a face move pulls
        doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "strip", Face = $"face{end}", Kind = Em3dFaceBoundaryKind.Pec });

        var analysis = StepConvert.Analyse(doc, path, "strip", kernel);
        Assert.Empty(analysis.Polyhedron.Refusals);
        Assert.False(analysis.Polyhedron.Exact);
        Assert.StartsWith($"'strip' becomes a polyhedron: its 1 cylindrical face (face{round}) is cut into 6 flat facets, each turning at most 15°, -",
                          analysis.Polyhedron.Summary, StringComparison.Ordinal);
        var poly = Assert.IsType<C3dPolyhedron>(analysis.Polyhedron.Replacement);
        Assert.Equal(6, poly.Faces.Count(f => f.Name.StartsWith($"face{round}.", StringComparison.Ordinal)));
        Assert.Equal($"face{end}", analysis.Polyhedron.Map[end]);

        Assert.Empty(analysis.Prism.Refusals);
        Assert.False(analysis.Prism.Exact);
        var prism = Assert.IsType<C3dPrism>(StepConvert.Apply(analysis.Prism, doc));
        Assert.Equal(C3dPlane.XZ, prism.Plane);
        AssertFace(kernel, doc, prism, Assert.Single(doc.FaceBoundaries).Face, [1, 0, 0], 0, 1_000);
    }

    // ── a file whose vertex sits off its faces by its own tolerance still converts exactly ──

    [KernelFact]
    public void ASloppyFile_WhoseCornerSitsOffItsFourFaces_StillConvertsExactly_TheCornerSnappedOntoThem()
    {
        using var kernel = KernelForTests.New();
        C3dPoint3 P(long x, long y, long z) => new(x * 1000, y * 1000, z * 1000);
        // A banded body drafted to a smaller top: each band-top corner is where FOUR faces meet.
        var body = new C3dPolyhedron
        {
            Name = "body",
            Vertices = [P(0, 0, 0), P(600, 0, 0), P(600, 400, 0), P(0, 400, 0), P(0, 0, 200), P(600, 0, 200), P(600, 400, 200), P(0, 400, 200),
                        P(100, 100, 300), P(500, 100, 300), P(500, 300, 300), P(100, 300, 300)],
            Faces =
            [
                new() { Name = "b", Outer = [0, 3, 2, 1] }, new() { Name = "s0", Outer = [0, 1, 5, 4] }, new() { Name = "s1", Outer = [1, 2, 6, 5] },
                new() { Name = "s2", Outer = [2, 3, 7, 6] }, new() { Name = "s3", Outer = [3, 0, 4, 7] }, new() { Name = "d0", Outer = [4, 5, 9, 8] },
                new() { Name = "d1", Outer = [5, 6, 10, 9] }, new() { Name = "d2", Outer = [6, 7, 11, 10] }, new() { Name = "d3", Outer = [7, 4, 8, 11] },
                new() { Name = "t", Outer = [8, 9, 10, 11] },
            ],
        };
        // The corner at (600, 0, 200) µm moved 1.5 µm off its four faces in the file's text: the faces stay where they were.
        var (path, doc) = Piece(kernel, body, "body", text => MoveVertex(text, "(0.6,0.,0.2)", "(0.6015,0.,0.2)"));

        var analysis = StepConvert.Analyse(doc, path, "body", kernel);
        Assert.Empty(analysis.Polyhedron.Refusals);
        Assert.True(analysis.Polyhedron.Exact);
        var poly = Assert.IsType<C3dPolyhedron>(analysis.Polyhedron.Replacement);
        Assert.Contains(P(600, 0, 200), poly.Vertices);
    }

    // ── the command: enabled for a bent strip, its facets confirmed, one undo entry ───────────

    [KernelFact]
    public async Task TheCommand_IsEnabledForABentStrip_AsksBeforeCuttingItsBend_AndOneUndoRestoresTheDocumentByteForByte()
    {
        using var kernel = KernelForTests.New();
        var (path, _) = Strip(kernel);
        var vm = Editor(path, kernel);
        string? asked = null;
        vm.Confirm = q => { asked = q; return Task.FromResult(true); };

        await vm.AnalyseForConvert(0);
        Assert.Null(vm.ConvertRefusal(StepConvertKind.Polyhedron, 0));
        Assert.Null(vm.ConvertRefusal(StepConvertKind.Box, 0));

        string original = C3dPersistence.Serialize(vm.Document);
        int entries = vm.UndoEntries;
        await vm.ReplaceStepAsync(StepConvertKind.Polyhedron, 0);
        Assert.StartsWith("'strip' becomes a polyhedron: its 1 cylindrical face", asked, StringComparison.Ordinal);
        Assert.EndsWith(" Convert it?", asked, StringComparison.Ordinal);
        Assert.Equal(entries + 1, vm.UndoEntries);
        var poly = Assert.IsType<C3dPolyhedron>(Assert.Single(vm.Document.Objects));
        Assert.Equal(("strip", "Copper", "pkg", Offset), (poly.Name, poly.Material, poly.Group, poly.Placement.Origin));
        Assert.Equal("Undo \"Convert strip to a polyhedron\"", vm.UndoRedo.UndoDescription);
        vm.UndoRedo.Undo();
        Assert.Equal(original, C3dPersistence.Serialize(vm.Document));
    }

    // ── a lead with a zero-thickness fin: a polyhedron and a sheet, in a group of their own ──

    // A vendor DFN draws each lead's plated end as a fin up the mould's side wall: two faces back to back on one plane and a top
    // face that is a line. The pad converts exactly; the fin, which has no volume, is kept as a sheet; both go in a new group
    // inside the piece's own.
    [KernelFact]
    public void ALeadWithAZeroThicknessFin_BecomesAPolyhedronAndASheet_InANewGroupInsideItsOwn()
    {
        using var kernel = KernelForTests.New();
        var (path, doc) = Piece(kernel, new C3dBox { Size = new(400_000, 300_000, 50_000) }, "lead", FinnedLead);

        var plan = StepConvert.Analyse(doc, path, "lead", kernel).Polyhedron;
        Assert.Empty(plan.Refusals);
        Assert.False(plan.Exact);                                         // asked first: the piece becomes two objects
        Assert.Contains("is kept as the sheet 'lead_sheet'", plan.Summary, StringComparison.Ordinal);
        Assert.Equal(400.0 * 300 * 50, plan.NewVolumeUm3, 1e-3);

        StepConvert.Apply(plan, doc);
        var poly = Assert.IsType<C3dPolyhedron>(doc.Objects[0]);
        var sheet = Assert.IsType<C3dSheet>(doc.Objects[1]);
        Assert.Equal((6, 8), (poly.Faces.Count, poly.Vertices.Count));
        Assert.Equal(("lead", "Copper", "pkg/lead", Offset), (poly.Name, poly.Material, poly.Group, poly.Placement.Origin));
        Assert.Equal(("lead_sheet", "Copper", "pkg/lead", Offset), (sheet.Name, sheet.Material, sheet.Group, sheet.Placement.Origin));
        Assert.Equal((C3dPlane.YZ, 0L), (sheet.Plane, sheet.Offset));
        var o = sheet.Outline;
        Assert.Equal(new[] { 0L, 300_000, 50_000, 450_000 }, new[] { o.Min(q => q.U), o.Max(q => q.U), o.Min(q => q.V), o.Max(q => q.V) });
        Elaborate(kernel, doc, path);
    }

    /// <summary>The exported box's solid swapped for a 400 × 300 × 50 µm pad with a fin at x = 0 up to z = 450 µm, written as the
    /// vendor's file writes it: the outer side one face from 0 to 450 µm, the fin's inner side another on the same plane, and a top
    /// face bounded by two edges between the same two corners.</summary>
    private static string FinnedLead(string exported)
    {
        double[][] v =
        [
            [0, 0, 0], [0.4, 0, 0], [0.4, 0.3, 0], [0, 0.3, 0], [0, 0, 0.05], [0.4, 0, 0.05], [0.4, 0.3, 0.05], [0, 0.3, 0.05],
            [0, 0, 0.45], [0, 0.3, 0.45],
        ];
        (int A, int B)[] e =
        [
            (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
            (4, 8), (7, 9), (8, 9), (9, 8),                                              // the fin's sides, its top twice
        ];
        // Each face's edges in order, + along the edge, - against it (inward-wound here; written reversed), and its outward normal.
        (double[] N, int[] Loop)[] faces =
        [
            ([0, 0, -1], [1, 2, 3, 4]), ([0, 0, 1], [5, 6, 7, 8]), ([0, -1, 0], [1, 10, -5, -9]), ([0, 1, 0], [3, 12, -7, -11]),
            ([1, 0, 0], [2, 11, -6, -10]), ([-1, 0, 0], [9, 13, 15, -14, -12, 4]), ([1, 0, 0], [-8, 14, 16, -13]), ([0, 0, 1], [-15, 16]),
        ];
        var sb = new System.Text.StringBuilder();
        int id = 10_000;
        int Add(string entity) { sb.Append($"#{id} = {entity};\n"); return id++; }
        static string R(double x) => x.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
        string Xyz(double[] p) => $"({R(p[0])},{R(p[1])},{R(p[2])})";
        var points = v.Select(p => Add($"CARTESIAN_POINT('',{Xyz(p)})")).ToArray();
        var vertices = points.Select(p => Add($"VERTEX_POINT('',#{p})")).ToArray();
        var edges = e.Select(x =>
        {
            double[] d = [.. Enumerable.Range(0, 3).Select(k => v[x.B][k] - v[x.A][k])];
            double len = Math.Sqrt(d.Sum(c => c * c));
            int dir = Add($"DIRECTION('',{Xyz([.. d.Select(c => c / len)])})");
            int line = Add($"LINE('',#{points[x.A]},#{Add($"VECTOR('',#{dir},{R(len)})")})");
            return Add($"EDGE_CURVE('',#{vertices[x.A]},#{vertices[x.B]},#{line},.T.)");
        }).ToArray();
        var faceIds = faces.Select(f =>
        {
            var oriented = f.Loop.Reverse().Select(k => Add($"ORIENTED_EDGE('',*,*,#{edges[Math.Abs(k) - 1]},{(k < 0 ? ".T." : ".F.")})"));
            int loop = Add($"EDGE_LOOP('',({string.Join(",", oriented.ToList().Select(x => $"#{x}"))}))");
            int origin = points[e[Math.Abs(f.Loop[0]) - 1].A];
            double[] reference = Math.Abs(f.N[0]) > 0.5 ? [0, 1, 0] : [1, 0, 0];
            int axes = Add($"AXIS2_PLACEMENT_3D('',#{origin},#{Add($"DIRECTION('',{Xyz(f.N)})")},#{Add($"DIRECTION('',{Xyz(reference)})")})");
            return Add($"ADVANCED_FACE('',(#{Add($"FACE_OUTER_BOUND('',#{loop},.T.)")}),#{Add($"PLANE('',#{axes})")},.T.)");
        }).ToList();
        int shell = Add($"CLOSED_SHELL('',({string.Join(",", faceIds.Select(x => $"#{x}"))}))");
        string text = System.Text.RegularExpressions.Regex.Replace(exported, @"MANIFOLD_SOLID_BREP\('([^']*)',#\d+\)", $"MANIFOLD_SOLID_BREP('$1',#{shell})");
        int end = text.LastIndexOf("ENDSEC;", StringComparison.Ordinal);
        return text[..end] + sb + text[end..];
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>A 1 × 0.2 × 0.1 mm strip rounded on one top edge.</summary>
    private (string Path, C3dDocument Doc) Strip(GeometryKernel kernel)
        => Piece(kernel, new C3dFillet
        {
            Name = "strip", Radius = 50_000, Edges = ["xmax|zmax"],
            Target = new C3dBox { Size = new(1_000_000, 200_000, 100_000) },
        }, "strip");

    /// <summary>A workspace whose 3D view holds <paramref name="shape"/>, written to STEP by circuitRF and read back as the Step
    /// object <paramref name="name"/> of one solid: offset, Copper, in group 'pkg'.</summary>
    private (string Path, C3dDocument Doc) Piece(GeometryKernel kernel, C3dObject shape, string name, Func<string, string>? edit = null)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Pkg", "3d");
        Directory.CreateDirectory(dir);
        byte[] bytes = kernel.Export([new GeometryKernelExportItem(GeometryKernelTree.From(shape, 1000), name)], "step", "mm");
        if (edit is not null) bytes = System.Text.Encoding.ASCII.GetBytes(edit(System.Text.Encoding.ASCII.GetString(bytes)));
        File.WriteAllBytes(Path.Combine(dir, "piece.step"), bytes);
        string part = kernel.ImportStep(bytes).Parts.Single().Path;
        string path = Path.Combine(dir, "Pkg.c3d");
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dStep
                {
                    Name = name, Material = "Copper", Group = "pkg", File = "piece.step", Part = part, Solid = 1, Unit = "millimetre",
                    Hash = StepImport.HashOf(bytes), Placement = new() { Origin = Offset },
                },
            ],
        };
        C3dPersistence.SaveToFile(path, doc);
        return (path, doc);
    }

    /// <summary>The STEP text with the VERTEX_POINT at <paramref name="from"/> moved to <paramref name="to"/> — its point only, so
    /// the edges and faces through it stay where they were and the vertex sits off them.</summary>
    private static string MoveVertex(string text, string from, string to)
    {
        var vertexPoints = System.Text.RegularExpressions.Regex.Matches(text, @"VERTEX_POINT\('[^']*',#(\d+)\)").Select(m => m.Groups[1].Value).ToHashSet();
        var point = System.Text.RegularExpressions.Regex.Matches(text, @"#(\d+) = CARTESIAN_POINT\('[^']*',(\([^)]*\))\);")
            .Single(m => vertexPoints.Contains(m.Groups[1].Value) && m.Groups[2].Value == from);
        return text.Replace(point.Value, point.Value.Replace(from, to, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>The one face of the piece, read in the file's own frame, that <paramref name="where"/> picks.</summary>
    private static int FaceOf(GeometryKernel kernel, string path, C3dDocument doc, Func<GeometryKernelFace, bool> where)
    {
        var step = (C3dStep)doc.Objects[0];
        var faces = kernel.Faces(GeometryKernelTree.From(new C3dStep
        {
            Name = "w", File = Path.Combine(Path.GetDirectoryName(path)!, step.File), Hash = step.Hash, Part = step.Part, Solid = step.Solid,
        }, 1000));
        return 1 + Enumerable.Range(0, faces.Count).Single(i => where(faces[i]));
    }

    /// <summary>The face <paramref name="face"/> of <paramref name="obj"/>, built at identity, has that outward normal and lies
    /// at <paramref name="atUm"/> on <paramref name="axis"/>.</summary>
    private static void AssertFace(GeometryKernel kernel, C3dDocument doc, C3dObject obj, string face, double[] normal, int axis, double atUm = 0)
    {
        var probe = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(obj));
        probe.Placement = new();
        var f = kernel.Faces(GeometryKernelTree.From(probe, doc.DbuPerMicron)).Single(x => x.Name == face);
        for (int i = 0; i < 3; i++) Assert.Equal(normal[i], f.Normal[i], 1e-9);
        Assert.Equal(atUm, f.Centroid[axis], 1e-6);
    }

    private static C3dElaboration Elaborate(GeometryKernel kernel, C3dDocument doc, string path)
    {
        var e = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        return e;
    }

    private C3dEditorViewModel Editor(string path, GeometryKernel kernel)
    {
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, _posted.Enqueue, kernel: kernel);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Elaboration is not null;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
        return vm;
    }
}
