// 3D editor round 3 — metal always takes precedence over dielectric where two solids overlap (em-3d.md §6.3a): in
// openEMS's priorities, in the gmsh script's cut order, in the section view's paint order, and in the 3D view's depth
// test and pick. Construction order decides only within each class.

using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class MetalPrecedenceTests
{
    private const double mm = 1e-3;

    /// <summary>A substrate (order 1), a pad sunk into it flush with its top (order 2), and a dielectric cover drawn LAST
    /// (order 3) over the pad — the case construction order alone would hand to the dielectric.</summary>
    private static Em3dProblem Problem(bool coverLast = true)
    {
        var materials = new[]
        {
            new Em3dMaterial("Sub", 4.4, null, 0, 1, 0),
            new Em3dMaterial("Mold", 3.9, null, 0, 1, 0),
            new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7),
        };
        var solids = new List<Em3dSolid>
        {
            new("substrate", "Sub", Em3dRole.Dielectric, new Em3dBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 0.2 * mm)), 1),
            new("pad", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.3 * mm, -0.3 * mm, 0.1 * mm), new(0.3 * mm, 0.3 * mm, 0.2 * mm)), 2),
        };
        if (coverLast)
            solids.Add(new("cover", "Mold", Em3dRole.Dielectric, new Em3dBox(new(-0.5 * mm, -0.5 * mm, 0.15 * mm), new(0.5 * mm, 0.5 * mm, 0.4 * mm)), 3));
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 1 * mm),
            new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                          Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));
        // One lumped port from the ground plane up to the pad's underside — a writer refuses a problem with nothing to excite.
        var port = new Em3dPort(1, "port/1", "pad", "airbox/zmin", new(-0.3 * mm, -0.1 * mm, 0), new(-0.3 * mm, 0.1 * mm, 0.1 * mm),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(-0.3 * mm, 0, 0), new(0, 0, 1), 0));
        return new Em3dProblem(solids, [], materials, [port], box, new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    [Fact]
    public void TheRule_RaisesEveryMetalAboveEveryDielectric_AndChangesNothingAlreadyInThatOrder()
    {
        var p = Problem();
        var prec = Em3dPrecedence.Of(p);
        int pad = prec.Of(p.Solids[1]), cover = prec.Of(p.Solids[2]), sub = prec.Of(p.Solids[0]);
        Assert.True(pad > cover && cover > sub, $"pad {pad}, cover {cover}, substrate {sub}");
        Assert.True(pad < prec.Max + 1);

        // Metal already above every dielectric: priorities ARE the construction order, so the files do not move.
        var ordered = Problem(coverLast: false);
        var same = Em3dPrecedence.Of(ordered);
        Assert.Equal(0, same.Shift);
        Assert.All(ordered.Solids, s => Assert.Equal(s.Order, same.Of(s)));
    }

    [Fact]
    public void OpenEms_WritesThePadAtAHigherPriorityThanTheDielectricDrawnAfterIt()
    {
        var p = Problem();
        var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
        var low = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        var doc = System.Xml.Linq.XDocument.Parse(low.Model!);
        int Priority(string name) => (int)doc.Descendants().Single(e => (string?)e.Attribute("Name") == name)
                                             .Descendants("Box").Single().Attribute("Priority")!;
        Assert.True(Priority("pad") > Priority("cover"), $"pad {Priority("pad")}, cover {Priority("cover")}");
        Assert.True(Priority("cover") > Priority("substrate"));
    }

    [Fact]
    public void Palace_CutsTheDielectricByThePad_NeverThePadByTheDielectric()
    {
        var p = Problem();
        var low = GmshGeoWriter.Write(p, PalaceSettings.Resolve(new CemPalace()));
        Assert.True(low.Ok, low.Refusal);
        var cuts = low.Geo!.Split('\n').Where(l => l.Contains("BooleanDifference{ Volume{s")).ToList();
        // s0 substrate, s1 pad, s2 cover: the pad loses nothing; the cover and the substrate lose the pad.
        Assert.DoesNotContain(cuts, l => l.StartsWith("s1[] = "));
        Assert.Contains(cuts, l => l.StartsWith("s2[] = ") && l.Contains("s1[]"));
        Assert.Contains(cuts, l => l.StartsWith("s0[] = ") && l.Contains("s1[]"));
    }

    [Fact]
    public void TheSectionView_PaintsTheMetalLast()
    {
        var p = Problem();
        var scene = CircuitRF.Render.Em3dSectionScene.Build(p, new(CircuitRF.Render.Em3dViewKind.SectionX, 0));
        var order = scene.Regions.Select(r => r.Object).ToList();
        Assert.Equal("pad", order[^1]);
    }

    [Fact]
    public void TheViewport_GivesTheMetalFace_WhereItLiesOnADielectricsFace()
    {
        // Pad top (z 0.2 mm) lies exactly in the substrate's top face; looked at from straight above.
        var p = Problem(coverLast: false);
        var scene = Scene3DBuilder.Build(p, 1);
        uint padId = scene.Objects.Single(o => o.Name == "pad").Id;
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(StandardView3D.Top);
        const int w = 101, h = 101;

        Assert.Equal(padId, Scene3DPicking.PairAtPixel(scene, cam, 50, 50, w, h, default).Id);
        Assert.Equal(padId, Scene3DPicking.Pick(scene, cam, 50, 50, w, h, default).Id);
        var patch = new Scene3DIdPatch();
        patch.Render(scene, cam, 50.5f, 50.5f, w, h, 5, default);
        Assert.All(patch.Ids.Take(patch.Size * patch.Size), id => Assert.Equal(padId, id));

        // The GPU's half: the substrate's translucent draw and its pick draw carry the bias; the pad's do not.
        var view = new Viewer3DViewState { Camera = cam, CursorX = 50, CursorY = 50 };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);   // the viewer opens with the outermost dielectric hidden; the editor shows it
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, w, h, false, true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Translucent && d.Behind);
        Assert.DoesNotContain(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Opaque && d.Behind);
        Assert.Contains(plan.PickDraws.Take(plan.PickDrawCount), d => d.Behind);
        Assert.Contains(plan.PickDraws.Take(plan.PickDrawCount), d => !d.Behind);
    }

    /// <summary>A .c3d dielectric reaches the scene as a Body (C3dElaborator's origin), not a Dielectric: a saw-cut package's
    /// lead, its end flush with the mould's side wall, lost that face to the mould in half the pixels and flickered.</summary>
    [Fact]
    public void TheViewport_GivesALeadEndOverAMouldBodysSideWall()
    {
        var materials = new[] { new Em3dMaterial("Mold", 3.9, null, 0, 1, 0), new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7) };
        var mould = new Em3dSolid("mould", "Mold", Em3dRole.Dielectric, new Em3dBox(new(-0.5 * mm, -0.5 * mm, 0), new(0.5 * mm, 0.5 * mm, 0.4 * mm)), 1);
        var lead = new Em3dSolid("lead", "Cu", Em3dRole.Conductor, new Em3dBox(new(0.3 * mm, -0.1 * mm, 0), new(0.5 * mm, 0.1 * mm, 0.1 * mm)), 2);
        var p = Problem(coverLast: false) with { Solids = [mould, lead], Materials = materials, Ports = [] };
        var origins = new Dictionary<string, Em3dObjectOrigin>
        {
            ["mould"] = new(Em3dObjectKind.Body, null, null, null), ["lead"] = new(Em3dObjectKind.Conductor, null, null, null),
        };
        var scene = Scene3DBuilder.Build(p, 1, origins);
        uint mouldId = scene.Objects.Single(o => o.Name == "mould").Id, leadId = scene.Objects.Single(o => o.Name == "lead").Id;
        Assert.Equal(Scene3DKind.Body, scene.Objects[mouldId - 1].Kind);
        Assert.Equal(Scene3DDepthTie.Behind, Scene3DFramePlan.TieOf(scene, mouldId));
        Assert.Equal(Scene3DDepthTie.None, Scene3DFramePlan.TieOf(scene, leadId));

        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(StandardView3D.Right);
        const int w = 201, h = 201;
        var q = cam.Project(scene.ToLocal(0.5 * mm, 0, 0.05 * mm), w, h);
        Assert.Equal(leadId, Scene3DPicking.Pick(scene, cam, q.X, q.Y, w, h, default).Id);
    }

    /// <summary>3D editor bugs round 9 — a pad on a substrate, a via through both flush with the pad's top, and a lumped
    /// port lying ON the pad's top beside the via: three coincident faces at z 0.2 mm.</summary>
    private static (Scene3DModel Scene, uint Pad, uint Via, uint Port) Stack()
    {
        var p = Problem(coverLast: false);
        var via = new Em3dSolid("via", "Cu", Em3dRole.Conductor, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.2 * mm), 0.05 * mm), 3);
        var port = new Em3dPort(1, "port/1", "pad", "pad", new(0.1 * mm, -0.1 * mm, 0.2 * mm), new(0.25 * mm, 0.1 * mm, 0.2 * mm),
                                new(1, 0, 0), 50, new Em3dReferencePlane(new(0.1 * mm, 0, 0.2 * mm), new(1, 0, 0), 0));
        p = p with { Solids = [.. p.Solids, via], Ports = [port] };
        var origins = new Dictionary<string, Em3dObjectOrigin> { ["via"] = new(Em3dObjectKind.Via, null, null, null) };
        var scene = Scene3DBuilder.Build(p, 1, origins);
        uint Id(string name) => scene.Objects.Single(o => o.Name == name).Id;
        return (scene, Id("pad"), Id("via"), Id("port/1"));
    }

    [Fact]
    public void TheViewport_GivesAViaFaceOverItsPad_AndAPortOverBoth()
    {
        var (scene, pad, via, port) = Stack();
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(StandardView3D.Top);
        const int w = 201, h = 201;
        (float X, float Y) At(double x, double y) { var q = cam.Project(scene.ToLocal(x, y, 0.2 * mm), w, h); return (q.X, q.Y); }

        foreach (var (x, y, want) in new[] { (0.0, 0.0, via), (0.2 * mm, 0.0, port), (-0.2 * mm, 0.0, pad) })
        {
            var (px, py) = At(x, y);
            Assert.Equal(want, Scene3DPicking.PairAtPixel(scene, cam, px, py, w, h, default).Id);
            Assert.Equal(want, Scene3DPicking.Pick(scene, cam, px, py, w, h, default).Id);
            var patch = new Scene3DIdPatch();
            patch.Render(scene, cam, px, py, w, h, 1, default);
            Assert.Equal(want, patch.Ids[0]);
        }

        // The GPU's half: each draw carries its object's tie, which the backends turn into a polygon offset.
        var view = new Viewer3DViewState { Camera = cam, CursorX = 100, CursorY = 100 };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, w, h, false, true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        Scene3DDepthTie TieOf(uint id)
        {
            var b = scene.Batches.Single(x => x.ObjectId == id);
            return plan.Draws.Take(plan.DrawCount).First(d => d.Buffer == Scene3DBuffer.Scene && d.First == b.FirstIndex).Tie;
        }
        Assert.Equal(Scene3DDepthTie.None, TieOf(pad));
        Assert.Equal(Scene3DDepthTie.Via, TieOf(via));
        Assert.Equal(Scene3DDepthTie.Port, TieOf(port));
        Assert.True(Scene3DFramePlan.DepthBias(Scene3DDepthTie.Port).Constant < Scene3DFramePlan.DepthBias(Scene3DDepthTie.Via).Constant);
        Assert.True(Scene3DFramePlan.DepthBias(Scene3DDepthTie.Via).Constant < 0 && Scene3DFramePlan.DepthBias(Scene3DDepthTie.Behind).Constant > 0);
    }

    [Fact]
    public void ALumpedPort_IsACheckerboardOverExactlyItsOwnRectangle_WithAnArrowOnItToThePlusEdge()
    {
        var (scene, _, _, port) = Stack();
        var o = scene.Objects[port - 1];
        var b = scene.Batches.Single(x => x.ObjectId == port);
        var verts = Enumerable.Range(b.FirstIndex, b.IndexCount).Select(i => scene.Vertices[scene.Indices[i]]).ToList();
        const float tol = 1e-9f;
        // 0.15 x 0.2 mm: cells 0.075 mm, two along x and three along y (rounded) in two colours, then the arrow's shaft
        // (two triangles) and head (one) in a third.
        Assert.Equal(3 * (2 * 2 * 3 + 3), b.IndexCount);
        Assert.Equal(3, verts.Select(v => v.Rgba).Distinct().Count());
        // The arrow is drawn last and points along +x (the port's direction): its tip is its farthest vertex that way.
        var arrow = verts.Skip(3 * 2 * 2 * 3).ToList();
        Assert.Single(arrow.Select(v => v.Rgba).Distinct());
        Assert.Equal(arrow[^2].X, arrow.Max(v => v.X));
        Assert.True(arrow[^2].X > (scene.ToLocal(0.1 * mm, 0, 0).X + scene.ToLocal(0.25 * mm, 0, 0).X) / 2);
        // Centred across the sheet's width: the tip on the port's centre line (y 0), the shaft symmetric about it.
        Assert.Equal(scene.ToLocal(0, 0, 0).Y, arrow[^2].Y, tol);
        Assert.Equal(scene.ToLocal(0, 0, 0).Y, (arrow.Min(v => v.Y) + arrow.Max(v => v.Y)) / 2, tol);
        var lo = scene.ToLocal(0.1 * mm, -0.1 * mm, 0.2 * mm);
        var hi = scene.ToLocal(0.25 * mm, 0.1 * mm, 0.2 * mm);
        Assert.Equal(lo.X, verts.Min(v => v.X), tol); Assert.Equal(hi.X, verts.Max(v => v.X), tol);
        Assert.Equal(lo.Y, verts.Min(v => v.Y), tol); Assert.Equal(hi.Y, verts.Max(v => v.Y), tol);
        Assert.All(verts, v => Assert.Equal(lo.Z, v.Z, tol));
        // No arrow standing off the sheet: the port's lines are its outline, in its plane.
        var lines = scene.LineBatches.Where(l => l.ObjectId == port)
                         .SelectMany(l => scene.LineVertices.Skip(l.FirstVertex).Take(l.VertexCount)).ToList();
        Assert.Equal(8, lines.Count);
        Assert.All(lines, v => Assert.Equal(lo.Z, v.Z, tol));
    }
}
