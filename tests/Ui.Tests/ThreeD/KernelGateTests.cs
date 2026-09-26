// ================================================================
//  KernelGateTests.cs — the gate for brief-em3d-47: the managed face/vertex kernel, headless (it is below the
//  firewall). Gate 7 (the drag's counters) needs the editor and is in KernelDragGateTests.cs.
// ================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class KernelGateTests
{
    private const long Um = 1000;                        // DBU per µm at the default 1000 DBU/µm

    // ── 1. closure invariants, property-tested ───────────────────────────────────────────────

    public static TheoryData<string> Solids => ["box", "prism", "l-prism-with-hole", "pyramid", "wedge"];

    [Theory]
    [MemberData(nameof(Solids))]
    public void Gate1_TwoHundredRandomEdits_KeepTheSolidClosedPlanarAndPositive_AndARefusalChangesNothing(string which)
    {
        var (start, genus) = Solid(which);
        var rng = new Random(4700 + which.Length);
        C3dObject obj = start;
        int accepted = 0, refused = 0;
        for (int step = 0; step < 200; step++)
        {
            string before = C3dPersistence.SerializeObject(obj);
            var editor = new C3dFaceEditor(obj);
            var names = editor.FaceNames;
            C3dFaceEditResult r;
            switch (rng.Next(3))
            {
                case 0:
                {
                    string face = names[rng.Next(names.Count)];
                    long d = rng.Next(-15, 16) * Um;
                    d = C3dFaceEditor.Clamp(d, editor.Limit(face, Math.Sign(d)));
                    r = editor.MoveAlongNormal(face, d);
                    break;
                }
                case 1:
                    r = editor.MoveFace(names[rng.Next(names.Count)], new C3dPoint3(rng.Next(-8, 9) * Um, rng.Next(-8, 9) * Um, rng.Next(-8, 9) * Um));
                    break;
                default:
                {
                    int v = rng.Next(editor.Vertices.Count);
                    var p = editor.Vertices[v];
                    r = editor.MoveVertex(v, p + new C3dPoint3(rng.Next(-6, 7) * Um, rng.Next(-6, 7) * Um, rng.Next(-6, 7) * Um));
                    break;
                }
            }
            Assert.Equal(before, C3dPersistence.SerializeObject(obj));            // the source is never touched
            if (!r.Ok) { refused++; continue; }
            accepted++;
            obj = r.Object!;
            var b = C3dBrepBuild.Of(obj)!;
            AssertSolid(b, genus, $"{which}, step {step}");
        }
        Assert.True(accepted >= 60, $"only {accepted} of 200 edits were accepted ({refused} refused): the property test tested little");
    }

    /// <summary>Gate 1's invariants, checked without the kernel's own locality: the WHOLE solid, every pair of faces.</summary>
    private static void AssertSolid(C3dBrep b, int genus, string where)
    {
        // Every edge used by exactly two faces, in opposite directions.
        Assert.True(b.ClosureProblem() is null, $"{where}: {b.ClosureProblem()}");
        var undirected = new HashSet<(int, int)>();
        foreach (var f in b.Faces)
            foreach (var ring in f.Rings())
                for (int i = 0; i < ring.Length; i++)
                    undirected.Add((Math.Min(ring[i], ring[(i + 1) % ring.Length]), Math.Max(ring[i], ring[(i + 1) % ring.Length])));
        int v = b.Faces.SelectMany(f => f.Rings()).SelectMany(r => r).Distinct().Count();
        int e = undirected.Count, faces = b.Faces.Count, holes = b.Faces.Sum(f => f.Holes.Length);
        // Euler–Poincaré for faces that may have holes: a face with h holes is not a disc, and counts 1 − h.
        Assert.True(v - e + faces - holes == 2 - 2 * genus, $"{where}: V − E + F − H = {v - e + faces - holes}, genus {genus}");
        for (int f = 0; f < b.Faces.Count; f++)
            Assert.True(b.Deviation(f) <= C3dKernel.PlanarityDbu, $"{where}: '{b.Faces[f].Name}' is {b.Deviation(f)} DBU off its plane");
        Assert.True(b.Volume6 > 0, $"{where}: the signed volume is not positive");
        Assert.True(C3dKernel.Validate(b) is null, $"{where}: {C3dKernel.Validate(b)}");
    }

    private static (C3dObject Obj, int Genus) Solid(string which) => which switch
    {
        "box" => (Box("blk", 0, 0, 0, 100, 80, 60), 0),
        "prism" => (new C3dPrism
        {
            Name = "pent", Material = "Gold", Height = 50 * Um,
            Outline = [P(0, 0), P(100, 0), P(120, 60), P(50, 110), P(-20, 60)],
        }, 0),
        "l-prism-with-hole" => (new C3dPrism
        {
            Name = "ell", Material = "Gold", Plane = C3dPlane.YZ, Offset = 10 * Um, Height = -40 * Um,
            Outline = [P(0, 0), P(150, 0), P(150, 50), P(60, 50), P(60, 140), P(0, 140)],
            Holes = [[P(15, 15), P(15, 35), P(45, 35), P(45, 15)]],
        }, 1),
        "pyramid" => (new C3dPolyhedron
        {
            Name = "pyr", Material = "Gold",
            Vertices = [V(0, 0, 0), V(100, 0, 0), V(100, 100, 0), V(0, 100, 0), V(50, 50, 80)],
            Faces =
            [
                new() { Name = "base", Outer = [0, 3, 2, 1] }, new() { Name = "s0", Outer = [0, 1, 4] }, new() { Name = "s1", Outer = [1, 2, 4] },
                new() { Name = "s2", Outer = [2, 3, 4] }, new() { Name = "s3", Outer = [3, 0, 4] },
            ],
        }, 0),
        // A frustum of an oblique triangular pyramid: every face planar, none along an axis but the base.
        _ => (new C3dPolyhedron
        {
            Name = "wedge", Material = "Gold",
            Vertices = [V(0, 0, 0), V(120, 0, 0), V(0, 90, 0), V(10, 8, 70), V(70, 8, 70), V(10, 53, 70)],
            Faces =
            [
                new() { Name = "bottom", Outer = [0, 2, 1] }, new() { Name = "top", Outer = [3, 4, 5] },
                new() { Name = "a", Outer = [0, 1, 4, 3] }, new() { Name = "b", Outer = [1, 2, 5, 4] }, new() { Name = "c", Outer = [2, 0, 3, 5] },
            ],
        }, 0),
    };

    // ── 2. primitives stay primitives ────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_PushPullOnEveryBoxAndPrismFace_KeepsTheKind_AndChangesOnlyTheTablesFields()
    {
        var box = Box("blk", 10, 20, 30, 100, 80, 60);
        foreach (string face in C3dBox.FaceNameList)
            foreach (long d in new[] { 7 * Um, -9 * Um })
            {
                var r = new C3dFaceEditor(box).MoveAlongNormal(face, d);
                var b = Assert.IsType<C3dBox>(r.Object);
                Assert.False(r.Converted);
                var changed = Changed(box, b);
                Assert.Equal(face.EndsWith("min", StringComparison.Ordinal) ? ["Min", "Size"] : ["Size"], changed);
            }

        var prism = new C3dPrism
        {
            Name = "ell", Material = "Gold", Plane = C3dPlane.XZ, Offset = 5 * Um, Height = 40 * Um,
            Outline = [P(0, 0), P(150, 0), P(150, 50), P(60, 50), P(60, 140), P(0, 140)],
            Holes = [[P(15, 15), P(15, 35), P(45, 35), P(45, 15)]],
        };
        foreach (string face in prism.FaceNames())
            foreach (long d in new[] { 3 * Um, -2 * Um })
            {
                var r = new C3dFaceEditor(prism).MoveAlongNormal(face, d);
                var p = Assert.IsType<C3dPrism>(r.Object);
                string[] expected = face switch
                {
                    "top" => ["Height"],
                    "bottom" => ["Offset", "Height"],
                    _ when face.StartsWith("hole", StringComparison.Ordinal) => ["Holes"],
                    _ => ["Outline"],
                };
                Assert.Equal(expected.Order(), Changed(prism, p).Order());
            }
    }

    /// <summary>The top-level properties whose written spelling differs.</summary>
    private static string[] Changed(C3dObject a, C3dObject b)
    {
        var ja = JsonNode.Parse(C3dPersistence.SerializeObject(a))!.AsObject();
        var jb = JsonNode.Parse(C3dPersistence.SerializeObject(b))!.AsObject();
        return [.. ja.Select(kv => kv.Key).Union(jb.Select(kv => kv.Key))
                     .Where(k => ja[k]?.ToJsonString() != jb[k]?.ToJsonString())];
    }

    // ── 3. push/pull keeps the neighbours' planes ────────────────────────────────────────────

    [Fact]
    public void Gate3_PushingATrapezoidsSlantedSideOut_LeavesBothNeighbouringSidesOnTheirPlanes()
    {
        var trapezoid = new C3dPrism
        {
            Name = "trap", Material = "Gold", Height = 30 * Um,
            Outline = [P(0, 0), P(100, 0), P(80, 50), P(20, 50)],
        };
        // The primitive (stays a prism) and the same solid as a polyhedron (the kernel's general path).
        var asPoly = (C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(trapezoid).Object!;
        foreach (C3dObject source in new C3dObject[] { trapezoid, asPoly })
        {
            var before = C3dBrepBuild.Of(source)!;
            var r = new C3dFaceEditor(source).MoveAlongNormal("side1", 7_123);
            Assert.True(r.Ok, r.Refusal);
            Assert.Equal(source.GetType(), r.Object!.GetType());
            var after = C3dBrepBuild.Of(r.Object)!;
            foreach (string n in new[] { "side0", "side2", "bottom", "top" })
            {
                var (n0, c0) = PlaneOf(before, n);
                var (n1, c1) = PlaneOf(after, n);
                Assert.Equal(n0.X, n1.X, 1e-12); Assert.Equal(n0.Y, n1.Y, 1e-12); Assert.Equal(n0.Z, n1.Z, 1e-12);
                Assert.True(Math.Abs(c0 - c1) <= 1e-12 * Math.Max(1, Math.Abs(c0)), $"{n}: {c0} → {c1}");
            }
            // And the pushed side itself moved out by the distance, to the DBU the rounding allows.
            var (m0, d0) = PlaneOf(before, "side1");
            var (_, d1) = PlaneOf(after, "side1");
            Assert.Equal(7_123, d1 - d0, 1.0);
            Assert.True(m0.X > 0);
        }
    }

    /// <summary>A face's unit normal and its plane's offset n·p (DBU), through every one of its vertices.</summary>
    private static ((double X, double Y, double Z) N, double C) PlaneOf(C3dBrep b, string face)
    {
        int f = b.Topology.IndexOf(face);
        var n = b.Normal(f);
        var ring = b.Faces[f].Outer;
        var offsets = ring.Select(i => n.X * b.Vertices[i].X + n.Y * b.Vertices[i].Y + n.Z * b.Vertices[i].Z).ToList();
        Assert.True(offsets.Max() - offsets.Min() <= 1e-9 * Math.Max(1, Math.Abs(offsets[0])), $"'{face}' is not planar");
        return (n, offsets.Average());
    }

    // ── 4. a free move tilts, it does not fold, when it can ─────────────────────────────────

    [Fact]
    public void Gate4_MovingABoxsXmaxFaceInY_GivesAPolyhedronWithTheSameSixNames_AndNoFold()
    {
        var box = Box("blk", 0, 0, 0, 100, 80, 60);
        var r = new C3dFaceEditor(box).MoveFace("xmax", new C3dPoint3(0, 25 * Um, 0));
        var poly = Assert.IsType<C3dPolyhedron>(r.Object);
        Assert.True(r.Converted);
        Assert.Empty(r.Folds);
        Assert.Equal(C3dBox.FaceNameList, poly.FaceNames());
        AssertSolid(C3dBrepBuild.Of(poly)!, 0, "moved xmax");
        // Its material, placement and name came with it.
        Assert.Equal(("blk", "Gold"), (poly.Name, poly.Material));
    }

    // ── 5. fold naming, and what was attached follows ───────────────────────────────────────

    [Fact]
    public void Gate5_AVertexMoveThatBendsAQuad_FoldsItIntoNamedPieces_AndABoundaryNamesBoth()
    {
        var box = Box("blk", 0, 0, 0, 100, 80, 60);
        var editor = new C3dFaceEditor(box);
        int corner = editor.NearestVertex((100 * Um, 80 * Um, 60 * Um), 1);
        var r = editor.MoveVertex(corner, new C3dPoint3(100 * Um, 80 * Um, 75 * Um));
        var poly = Assert.IsType<C3dPolyhedron>(r.Object);
        Assert.Equal(["zmax.0", "zmax.1"], poly.FaceNames().Where(n => n.StartsWith("zmax", StringComparison.Ordinal)));
        Assert.DoesNotContain("zmax", poly.FaceNames());
        Assert.Equal(["zmax.0", "zmax.1"], r.Folds["zmax"]);
        AssertSolid(C3dBrepBuild.Of(poly)!, 0, "bent corner");

        C3dFaceBoundary E(string obj, string face) => new() { Object = obj, Face = face, Kind = Em3dFaceBoundaryKind.Pec };
        var boundaries = new List<C3dFaceBoundary> { E("blk", "zmax"), E("blk", "xmin"), E("other", "zmax") };
        var followed = C3dFaceCommands.FollowFolds(boundaries, "blk", r.Folds)!;
        Assert.Equal(["blk/zmax.0", "blk/zmax.1", "blk/xmin", "other/zmax"], followed.Select(b => $"{b.Object}/{b.Face}"));
        Assert.All(followed, b => Assert.Equal(Em3dFaceBoundaryKind.Pec, b.Kind));
    }

    // ── 6. locality (counter) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_PushPullOnATenThousandFacePolyhedron_TouchesTheNeighbourhood_AndTestsABoundedNumberOfPairs()
    {
        // A comb: a strip whose top edge is 2,500 square teeth — 10,002 outline vertices, so 10,004 faces.
        const int teeth = 2500;
        long w = 10 * Um, h = 200 * Um, t = 30 * Um;
        var outline = new List<C3dPoint2> { new(0, 0), new(2 * w * teeth, 0) };
        for (int j = teeth - 1; j >= 0; j--)
        {
            outline.Add(new(2 * w * j + 2 * w, h + t));
            outline.Add(new(2 * w * j + w, h + t));
            outline.Add(new(2 * w * j + w, h));
            outline.Add(new(2 * w * j, h));
        }
        var prism = new C3dPrism { Name = "comb", Material = "Gold", Outline = outline, Height = 50 * Um };
        var poly = (C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(prism).Object!;
        Assert.Equal(10_004, poly.Faces.Count);

        var editor = new C3dFaceEditor(poly);
        // A tooth's top, half way along: a horizontal outline edge at h + t.
        string face = $"side{2 + 4 * 1250 + 0}";
        var r = editor.MoveAlongNormal(face, 12 * Um);
        Assert.True(r.Ok, r.Refusal);
        var s = r.Stats!;
        Assert.Equal(4, s.VerticesMoved);
        Assert.Equal(5, s.FacesTouched);                                    // itself, its two neighbours, top, bottom
        Assert.InRange(s.PairsTested, 0, 16);
        Assert.Equal(0, s.FacesFolded);

        // And a move that WOULD collide with the next tooth is found by the same few tests: a wall moved 12 µm across a 10 µm gap.
        var into = editor.MoveFace($"side{2 + 4 * 1250 + 1}", new C3dPoint3(-12 * Um, 0, 0));
        Assert.False(into.Ok);
        Assert.Contains("would pass through", into.Refusal);
        Assert.InRange(into.Stats!.PairsTested, 1, 16);
    }

    // ── 8. cylinder refusals, and Convert to Polyhedron's facet count ───────────────────────

    [Fact]
    public void Gate8_ACylinderRefusesAFreeMoveSayingWhatToDo_AndConvertsToTheStatedFacetCount()
    {
        var cyl = new C3dCylinder { Name = "pin", Material = "Gold", Base = V(0, 0, 0), Axis = C3dAxis.Z, Length = 40 * Um, Radius = 10 * Um };
        var editor = new C3dFaceEditor(cyl);
        foreach (string face in C3dCylinder.FaceNameList)
        {
            var r = editor.MoveFace(face, V(5, 0, 0));
            Assert.False(r.Ok);
            Assert.Contains("Convert to Polyhedron", r.Refusal);
        }
        Assert.Contains("Convert to Polyhedron", editor.MoveVertex(0, V(1, 1, 1)).Refusal);

        // Along the axis and the radius it stays a cylinder.
        Assert.Equal(55 * Um, Assert.IsType<C3dCylinder>(editor.MoveAlongNormal("top", 15 * Um).Object).Length);
        var down = Assert.IsType<C3dCylinder>(editor.MoveAlongNormal("bottom", 5 * Um).Object);
        Assert.Equal((V(0, 0, -5), 45 * Um), (down.Base, down.Length));
        Assert.Equal(12 * Um, Assert.IsType<C3dCylinder>(editor.MoveAlongNormal("side", 2 * Um).Object).Radius);

        var twelve = C3dFaceEditor.ConvertToPolyhedron(cyl, 12);
        var poly = Assert.IsType<C3dPolyhedron>(twelve.Object);
        Assert.Equal(24, poly.Vertices.Count);
        Assert.Equal(["bottom", "top", .. Enumerable.Range(0, 12).Select(k => $"side.{k}")], poly.FaceNames());
        Assert.Equal(12, twelve.Folds["side"].Count);
        AssertSolid(C3dBrepBuild.Of(poly)!, 0, "twelve sides");
        Assert.Equal(2 + Em3dTessellation.CylinderSegments, ((C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(cyl).Object!).Faces.Count);
    }

    // ── 9. Align to Face ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_AlignToFace_TouchingAndFlushAreExact_AndNonParallelFacesAreRefused()
    {
        var substrate = Box("sub", -200, -200, -254, 400, 400, 254);
        var die = Box("die", 0, 0, 0, 100, 100, 100);
        die.Placement.Origin = new C3dPoint3(3_217, -1_111, 777_777);

        var dieBottom = C3dFaceCommands.PlaneOf(die, "zmin", out _)!.Value;
        var dieTop = C3dFaceCommands.PlaneOf(die, "zmax", out _)!.Value;
        var subTop = C3dFaceCommands.PlaneOf(substrate, "zmax", out _)!.Value;

        var touch = C3dFaceCommands.Align(dieBottom, subTop, touching: true, out var why)!.Value;
        Assert.Null(why);
        Assert.True(touch.Exact);
        Assert.Equal(new C3dPoint3(0, 0, 0 - 777_777), touch.By);          // the die's bottom lands on z = 0
        var flush = C3dFaceCommands.Align(dieTop, subTop, touching: false, out _)!.Value;
        Assert.True(flush.Exact);
        Assert.Equal(new C3dPoint3(0, 0, 0 - (777_777 + 100 * Um)), flush.By);

        // Asked the other way round, each is refused rather than guessed.
        Assert.Null(C3dFaceCommands.Align(dieBottom, subTop, touching: false, out why));
        Assert.Contains("Touching", why);

        die.Placement.Rotate = [new C3dRotation { Axis = C3dAxis.X, Deg = 30 }];
        Assert.Null(C3dFaceCommands.Align(C3dFaceCommands.PlaneOf(die, "zmin", out _)!.Value, subTop, touching: true, out why));
        Assert.Contains("not parallel", why);
    }

    // ── R-em3d47-1c: the lowering recognises a box or a z-prism ─────────────────────────────

    [Fact]
    public void TheLowering_RecognisesAPolyhedronThatIsExactlyABoxOrAZPrism_AndKeepsTheFaceNames()
    {
        // A box edited into a polyhedron (xmax moved up) and back (moved down) is a polyhedron in the document …
        var box = Box("blk", 0, 0, 0, 100, 80, 60);
        var poly = (C3dPolyhedron)new C3dFaceEditor(box).MoveFace("xmax", V(0, 0, 25)).Object!;
        var back = (C3dPolyhedron)new C3dFaceEditor(poly).MoveFace("xmax", V(0, 0, -25)).Object!;
        // … and a box to the solver, face names and all.
        var lowered = C3dLowering.Lower(back, C3dTransform.Identity, 1000)!;
        Assert.Equal(C3dLowering.KindBox, lowered.Kind);
        Assert.Equal(C3dBox.FaceNameList, lowered.FaceNames);
        Assert.Equal(C3dLowering.KindPolyhedron, C3dLowering.Lower(poly, C3dTransform.Identity, 1000)!.Kind);

        var prism = new C3dPrism { Name = "p", Material = "Gold", Height = 30 * Um, Outline = [P(0, 0), P(100, 0), P(80, 50), P(20, 50)] };
        var asPoly = C3dFaceEditor.ConvertToPolyhedron(prism).Object!;
        var lp = C3dLowering.Lower(asPoly, C3dTransform.Identity, 1000)!;
        Assert.Equal(C3dLowering.KindExtrusion, lp.Kind);
        Assert.Equal(C3dLowering.Lower(prism, C3dTransform.Identity, 1000)!.FaceNames, lp.FaceNames);
    }

    /// <summary>R-em3d47-1c — a face planar to a DBU but not exactly (what the kernel and <c>check</c> call planar) reaches the
    /// solver as triangles carrying its name, so the neutral problem's own planarity check passes.</summary>
    [Fact]
    public void TheLowering_SplitsAFacePlanarOnlyToADbu_IntoNamedTriangles()
    {
        var box = (C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(Box("blk", 0, 0, 0, 100, 80, 60)).Object!;
        int corner = box.Vertices.IndexOf(V(100, 80, 60));
        box.Vertices[corner] = box.Vertices[corner] with { Z = box.Vertices[corner].Z + 1 };
        var b = C3dBrepBuild.Of(box)!;
        Assert.True(Enumerable.Range(0, b.Faces.Count).All(b.IsPlanar));                   // planar to a DBU …
        Assert.False(b.IsExactlyPlanar(b.Topology.IndexOf("zmax")));                      // … not exactly
        var poly = Assert.IsType<Em3dPolyhedron>(C3dLowering.Lower(box, C3dTransform.Identity, 1000)!.Solid);
        Assert.Empty(poly.Problems("blk"));
        Assert.Equal(2, poly.Faces.Count(f => f.Name == "zmax"));
        Assert.Equal(1, poly.Faces.Count(f => f.Name == "xmin"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static C3dPoint2 P(long u, long v) => new(u * Um, v * Um);

    private static C3dPoint3 V(long x, long y, long z) => new(x * Um, y * Um, z * Um);

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = V(x, y, z), Size = V(sx, sy, sz) };
}
