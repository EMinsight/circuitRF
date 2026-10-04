// brief-em3d-104 — smooth shading normals (R-em3d104-1), the shade stream (R-em3d104-2) and its lazy upload (R-em3d104-3).
// Builder-level, no GPU, except gate 11 (Metal offscreen, macOS only).

using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class ShadingNormalsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-shade-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static readonly Scene3DBuildOptions AtOrigin = new(DrawAirBox: false, Origin: (0, 0, 0));

    private static Em3dProblem Problem(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new Point3(-5e-3, -5e-3, -5e-3), new Point3(5e-3, 5e-3, 5e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
    }

    private static Em3dSolid Solid(string name, Em3dPrimitive p) => new(name, "Copper", Em3dRole.Conductor, p, 0);

    private static Scene3DModel Build(Em3dProblem problem, Scene3DBuildOptions? options = null)
    {
        var scene = Scene3DBuilder.Build(problem, 1, options: options ?? AtOrigin);
        AssertParallel(scene);
        return scene;
    }

    /// <summary>Gate 8 — the stream is parallel to the vertices and every index addresses both.</summary>
    private static void AssertParallel(Scene3DModel scene)
    {
        Assert.Equal(scene.Vertices.Length, scene.ShadeVertices.Length);
        Assert.All(scene.Indices, i => Assert.True(i < scene.ShadeVertices.Length));
    }

    private static Vector3 N(Scene3DModel s, int k) => new(s.ShadeVertices[k].Nx, s.ShadeVertices[k].Ny, s.ShadeVertices[k].Nz);
    private static Vector3 P(Scene3DModel s, int k) => new(s.Vertices[k].X, s.Vertices[k].Y, s.Vertices[k].Z);
    private static IEnumerable<int> Of(Scene3DModel s, string name)
    {
        var o = s.Objects.Single(x => x.Name == name);
        return Enumerable.Range(o.FirstVertex, o.VertexCount);
    }

    // ── 1. cylinder ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_ACylindersSideIsRadial_ItsCapsAxial_AndItsRimIsTwoVertices()
    {
        var scene = Build(Problem(Solid("via", new Em3dCylinder(new(0, 0, 0), new(0, 0, 1e-3), 2e-4))));
        foreach (int k in Of(scene, "via"))
        {
            var p = P(scene, k); var n = N(scene, k);
            var want = scene.Vertices[k].Face switch
            {
                2 => Vector3.Normalize(new Vector3(p.X, p.Y, 0)),
                0 => -Vector3.UnitZ,
                _ => Vector3.UnitZ,
            };
            Assert.True((n - want).Length() < 1e-6, $"vertex {k} face {scene.Vertices[k].Face}: {n} vs {want}");
        }
        var rim = Of(scene, "via").Where(k => MathF.Abs(new Vector2(P(scene, k).X, P(scene, k).Y).Length() - 2e-4f) < 1e-9f);
        Assert.All(rim.GroupBy(k => P(scene, k)), g => Assert.Equal(2, g.Count()));
    }

    // ── 2. box ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ABoxHas24ShadeVertices_AxisAlignedAndOutward()
    {
        var scene = Build(Problem(Solid("b", new Em3dBox(new(-1e-3, -2e-3, 0), new(1e-3, 2e-3, 5e-4)))));
        var ks = Of(scene, "b").ToList();
        Assert.Equal(24, ks.Count);
        var centre = new Vector3(0, 0, 2.5e-4f);
        foreach (int k in ks)
        {
            var n = N(scene, k);
            Assert.Equal(1, new[] { n.X, n.Y, n.Z }.Count(c => MathF.Abs(MathF.Abs(c) - 1) < 1e-6f));
            Assert.True(Vector3.Dot(n, P(scene, k) - centre) > 0, $"vertex {k} points in");
        }
    }

    // ── 3. a crease within one face ────────────────────────────────────────────────────────────

    /// <summary>An extrusion's walls and a polyhedron's faces are each a face of their own, so the crease rule is held by a kernel
    /// solid whose display mesh gives one face to six walls of a hexagonal prism (an imported face with internal creases).</summary>
    [Fact]
    public void Gate3_AHexPrismWhoseWallsAreOneFace_KeepsItsCornersSharp()
    {
        var verts = new List<Point3>();
        for (int k = 0; k < 6; k++) verts.Add(new Point3(1e-3 * Math.Cos(k * Math.PI / 3), 1e-3 * Math.Sin(k * Math.PI / 3), 0));
        for (int k = 0; k < 6; k++) verts.Add(new Point3(verts[k].X, verts[k].Y, 5e-4));
        var tris = new List<Em3dTriangle>();
        for (int k = 0; k < 6; k++)
        {
            int j = (k + 1) % 6;
            tris.Add(new Em3dTriangle(k, j, 6 + j, "hex", 0));
            tris.Add(new Em3dTriangle(k, 6 + j, 6 + k, "hex", 0));
        }
        for (int k = 1; k < 5; k++)
        {
            tris.Add(new Em3dTriangle(0, k + 1, k, "hex", 1));
            tris.Add(new Em3dTriangle(6, 6 + k, 6 + k + 1, "hex", 2));
        }
        var shape = new Em3dShapeSolid(ReadOnlyMemory<byte>.Empty, "0", new Em3dTriangleMesh(verts, tris), [], []);
        var scene = Build(Problem(Solid("hex", shape)));

        var walls = Of(scene, "hex").Where(k => scene.Vertices[k].Face == 0).ToList();
        Assert.Equal(24, walls.Count);                                   // 12 corners, each split between its two walls
        foreach (var corner in walls.GroupBy(k => P(scene, k)))
        {
            var n = corner.Select(k => N(scene, k)).ToList();
            Assert.Equal(2, n.Count);
            Assert.Equal(MathF.Cos(MathF.PI / 3), Vector3.Dot(n[0], n[1]), 1e-5f);   // the 60° the walls turn by
        }
    }

    // ── 4. sphere ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ASpheresNormalIsRadial_AndItsPolesExact()
    {
        var c = new Vector3(1e-4f, -2e-4f, 3e-4f);
        var scene = Build(Problem(Solid("ball", new Em3dSphere(new(c.X, c.Y, c.Z), 5e-4))));
        var ks = Of(scene, "ball").ToList();
        Assert.Equal(32 * 15 + 2, ks.Count);                               // smooth: no vertex split
        foreach (int k in ks)
        {
            var radial = Vector3.Normalize(P(scene, k) - c);
            Assert.True((N(scene, k) - radial).Length() < 1e-3, $"vertex {k}: {N(scene, k)} vs {radial}");
        }
        var poles = ks.Where(k => MathF.Abs(MathF.Abs(P(scene, k).Z - c.Z) - 5e-4f) < 1e-9f).ToList();
        Assert.Equal(2, poles.Count);
        Assert.All(poles, k => Assert.Equal(MathF.Sign(P(scene, k).Z - c.Z), N(scene, k).Z));
        Assert.All(poles, k => Assert.True(MathF.Abs(N(scene, k).X) < 1e-6f && MathF.Abs(N(scene, k).Y) < 1e-6f));
    }

    // ── 5. the unfaced path and a ball bond ──────────────────────────────────────────────────

    /// <summary>A ball bond is a solid on the builder's faced path with its face unknown; flattened on its pad, its sphere is smooth
    /// and only the cut's rim splits (sphere and flat). The welded, unfaced path (a port's sheet) takes the same rule: flat, unsplit.</summary>
    [Fact]
    public void Gate5_ABallBondIsSmooth_ExceptAtItsCut_AndAWeldedPortSheetIsNotSplit()
    {
        var bond = Solid("bond", new Em3dTruncatedSphere(new(0, 0, 0), 5e-4, -2.5e-4, 5e-4));
        var smooth = Build(Problem(bond));
        var whole = Build(Problem(bond), AtOrigin with { SplitShadingCreases = false });
        int rim = Of(whole, "bond").Count(k => MathF.Abs(P(whole, k).Z + 2.5e-4f) < 1e-9f && P(whole, k).X * P(whole, k).X + P(whole, k).Y * P(whole, k).Y > 1e-14f);
        Assert.Equal(32, rim);
        Assert.Equal(Of(whole, "bond").Count() + rim, Of(smooth, "bond").Count());

        var port = new Em3dPort(1, "port/1", "b", "airbox/zmin", new(0, -1e-4, -1e-3), new(0, 1e-4, 0),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(0, 0, -1e-3), new(0, 0, 1), 0));
        var withPort = Build(Problem(Solid("b", new Em3dBox(new(-1e-3, -1e-3, -1e-3), new(0, 1e-3, 0)))) with { Ports = [port] });
        var flat = Build(Problem(Solid("b", new Em3dBox(new(-1e-3, -1e-3, -1e-3), new(0, 1e-3, 0)))) with { Ports = [port] },
                         AtOrigin with { SplitShadingCreases = false });
        var sheet = withPort.Objects.Single(o => o.Kind == Scene3DKind.Port);
        Assert.Equal(flat.Objects.Single(o => o.Kind == Scene3DKind.Port).VertexCount, sheet.VertexCount);
        var n0 = N(withPort, sheet.FirstVertex);
        // A sheet is two-sided (R-em3d104-1g): the arrow over the cells is wound the other way, so compare up to sign.
        Assert.All(Of(withPort, sheet.Name), k => Assert.Equal(1f, MathF.Abs(Vector3.Dot(N(withPort, k), n0)), 1e-6f));
    }

    // ── 6. degenerate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_ZeroAreaTriangles_GiveNoNaN()
    {
        Vector3[] p = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(2, 0, 0), new(5, 5, 5)];
        uint[] idx = [0, 1, 2,   0, 1, 3,   4, 4, 4,   1, 3, 0];     // one good, one collinear, one a point, one collinear again
        var r = ShadingNormals.Compute(p, idx);
        Assert.Equal(p.Length + r.Duplicates.Length, r.Normals.Length);
        Assert.All(r.Normals, n => Assert.True(float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z) && MathF.Abs(n.Length() - 1) < 1e-5f, n.ToString()));
        Assert.Equal(Vector3.UnitZ, r.Normals[4]);                       // only a point uses it: +z
        Assert.Equal(Vector3.UnitZ, r.Normals[0]);                       // the good triangle's, unsplit by the slivers
        Assert.Empty(r.Duplicates);
    }

    // ── 7, 8. determinism and parallel ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate7And8_TheSameSceneTwiceIsByteIdentical_AndParallel()
    {
        var g = Scene3DGateTests.CaseA(_root);
        var a = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        var b = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        AssertParallel(a);
        Assert.True(MemoryMarshal.AsBytes(a.ShadeVertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.ShadeVertices.AsSpan())));
        Assert.True(MemoryMarshal.AsBytes(a.Vertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Vertices.AsSpan())));
        // brief-em3d-105 — each slot is now its object's row of the appearance table (it was 0 until 105 filled it).
        Assert.NotEmpty(a.Appearances);
        Assert.All(a.ShadeVertices, s => Assert.True(s.Slot < a.Appearances.Length));
    }

    // ── 9. lazy upload ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_TheStreamIsUploadedOnlyWhileWanted_AndCostsExactly16BytesAVertex()
    {
        var scene = Build(Problem(Solid("via", new Em3dCylinder(new(0, 0, 0), new(0, 0, 1e-3), 2e-4))));
        var fake = new PatchRecordingBackend();
        var session = new Viewer3DSession(() => fake);
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        void Frame(ulong f) => session.Frame(0, plan, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);

        Frame(1);
        Assert.Equal(scene.VertexBytes + scene.IndexBytes + scene.LineBytes, fake.Counters.UploadBytesTotal);
        Assert.Equal(0, fake.ShadeUploads);

        long off = fake.Counters.UploadBytesTotal;
        session.ShadeStream = true;
        Frame(2); Frame(3);
        Assert.Equal(1, fake.ShadeUploads);
        Assert.Equal(16L * scene.Vertices.Length, fake.Counters.UploadBytesTotal - off);

        session.ShadeStream = false;
        Frame(4);
        Assert.Equal(1, fake.ShadeReleases);
        Assert.Equal(off + 16L * scene.Vertices.Length, fake.Counters.UploadBytesTotal);
    }

    // ── 10. patch ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The box moves (its vertices change, its normals do not) and the cylinder tilts (both change): the shade patch is the
    /// cylinder's range and nothing else.</summary>
    [Fact]
    public void Gate10_APatchRewritesTheChangedObjectsShadeRange_AndNothingElse()
    {
        var a = Build(Problem(Solid("box", new Em3dBox(new(-2e-3, -2e-3, 0), new(-1e-3, -1e-3, 1e-3))),
                              Solid("via", new Em3dCylinder(new(0, 0, 0), new(0, 0, 1e-3), 2e-4))));
        var b = Build(Problem(Solid("box", new Em3dBox(new(-2e-3, -2e-3, 1e-4), new(-1e-3, -1e-3, 1.1e-3))),
                              Solid("via", new Em3dCylinder(new(0, 0, 0), new(3e-4, 0, 1e-3), 2e-4))));
        var patch = Scene3DPatch.Between(a, b);
        Assert.NotNull(patch);
        var via = b.Objects.Single(o => o.Name == "via");
        Assert.Equal([new Scene3DPatchRange(Scene3DPatchBuffer.Shade, via.FirstVertex * 16, via.VertexCount * 16)], patch!.ShadeRanges);

        var fake = new PatchRecordingBackend();
        var session = new Viewer3DSession(() => fake) { ShadeStream = true };
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        session.Frame(0, plan, 1, a, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        long before = fake.Counters.UploadBytesTotal;
        session.Frame(0, plan, 2, b, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        Assert.Equal(1, fake.ShadeUploads);
        Assert.Equal(via.VertexCount * 16L, fake.ShadePatchBytes);
        Assert.Equal(patch.Bytes + via.VertexCount * 16L, fake.Counters.UploadBytesTotal - before);
    }

    // ── 11. the default view draws the same pixels ───────────────────────────────────────────────

    [Fact]
    public void Gate11_Metal_TheDefaultViewIsPixelIdentical_WithAndWithoutDuplication()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var g = Scene3DGateTests.CaseA(_root);
        var split = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        var whole = Scene3DBuilder.Build(g.Problem!, 1, g.Origins, options: new Scene3DBuildOptions { SplitShadingCreases = false });
        Assert.True(split.Vertices.Length > whole.Vertices.Length, "the fixture splits no vertex: the comparison would prove nothing");

        byte[] Draw(Scene3DModel scene)
        {
            using var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
            const int w = 320, h = 200;
            metal.CreateOffscreenImages(w, h, 1);
            var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
            view.Adopt(scene, null);
            var session = new Viewer3DSession(() => metal);
            session.EnsureBackend();
            var plan = new Scene3DFramePlan();
            plan.Plan(scene, view, w, h, metal.FlipY, pick: false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, plan, 1, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
            return metal.ReadImage(0);
        }
        Assert.Equal(Draw(whole), Draw(split));
    }
}
