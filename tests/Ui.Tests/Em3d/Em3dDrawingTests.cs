// 3D vector copy and drawing export (2026-09-27) — the outline along any direction, its hidden edges, the drawing sheet,
// text as outlines, and Export Drawing…'s request.

using System.Text;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Ui.ThreeD;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class Em3dDrawingTests
{
    private const double Mm = 1e-3;

    private static Em3dSolid Box(string name, double x0, double y0, double z0, double x1, double y1, double z1, int order = 0)
        => new(name, "Copper", Em3dRole.Conductor, new Em3dBox(new Point3(x0 * Mm, y0 * Mm, z0 * Mm), new Point3(x1 * Mm, y1 * Mm, z1 * Mm)), order);

    private static Em3dProblem Problem(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new Point3(-50 * Mm, -50 * Mm, -50 * Mm), new Point3(50 * Mm, 50 * Mm, 50 * Mm), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
    }

    private static (double U0, double V0, double U1, double V1) Extent(Em3dScene s)
    {
        var (lo, hi) = Em3dDrawingSheet.Extent(s);
        return (lo.U / Mm, lo.V / Mm, hi.U / Mm, hi.V / Mm);
    }

    /// <summary>A 2 × 3 × 5 mm box seen from each Standard View is the rectangle of its two in-plane sides, oriented as
    /// the 3D view's camera orients it; with hidden edges removed, only the four edges facing the viewer remain.</summary>
    [Theory]
    [InlineData(Em3dStandardView.Top,     0, 0, 2, 3)]
    [InlineData(Em3dStandardView.Bottom,  0, -3, 2, 0)]
    [InlineData(Em3dStandardView.Front,   0, 0, 2, 5)]
    [InlineData(Em3dStandardView.Back,   -2, 0, 0, 5)]
    [InlineData(Em3dStandardView.Right,   0, 0, 3, 5)]
    [InlineData(Em3dStandardView.Left,   -3, 0, 0, 5)]
    public void AStandardView_OfABox_IsItsTwoSides(Em3dStandardView view, double u0, double v0, double u1, double v1)
    {
        var scene = Em3dSectionScene.Outline(Problem(Box("b", 0, 0, 0, 2, 3, 5)), Em3dProjection.Standard(view), new(), out var note);
        Assert.Null(note);
        var e = Extent(scene);
        Assert.Equal(u0, e.U0, 9); Assert.Equal(v0, e.V0, 9); Assert.Equal(u1, e.U1, 9); Assert.Equal(v1, e.V1, 9);
        Assert.Equal(4, scene.Lines.Count(l => (l.B.U - l.A.U) * (l.B.U - l.A.U) + (l.B.V - l.A.V) * (l.B.V - l.A.V) > 1e-18));
        Assert.All(scene.Lines, l => Assert.False(l.Hidden));
    }

    [Fact]
    public void TheIsometric_OfABox_ShowsNineEdges_TwelveAsAWireFrame_AndDashesTheThreeBehind()
    {
        var p = Problem(Box("b", 0, 0, 0, 2, 3, 5));
        var iso = Em3dProjection.Standard(Em3dStandardView.Isometric);
        Assert.Equal(9, Em3dSectionScene.Outline(p, iso, new() { Hidden = Em3dHiddenEdges.Removed }, out _).Lines.Count);
        Assert.Equal(12, Em3dSectionScene.Outline(p, iso, new() { Hidden = Em3dHiddenEdges.Shown }, out _).Lines.Count);
        var dashed = Em3dSectionScene.Outline(p, iso, new() { Hidden = Em3dHiddenEdges.Dashed }, out _);
        Assert.Equal(3, dashed.Lines.Count(l => l.Hidden));
        Assert.Equal(9, dashed.Lines.Count(l => !l.Hidden));
    }

    /// <summary>A small box behind a large one, seen from the front: gone when hidden edges are removed, all dashed when
    /// they are dashed, and whole from the top, where nothing covers it. A box reaching out past the large one's side is
    /// cut exactly at that side.</summary>
    [Fact]
    public void AnEdgeBehindASurface_IsRemovedOrDashed_AndCutWhereTheSurfaceEnds()
    {
        var front = Em3dProjection.Standard(Em3dStandardView.Front);
        var p = Problem(Box("wall", 0, 0, 0, 4, 1, 4), Box("behind", 1, 3, 1, 2, 4, 2, 1));
        Assert.DoesNotContain(Em3dSectionScene.Outline(p, front, new() { Hidden = Em3dHiddenEdges.Removed }, out _).Lines, l => l.Object == "behind");
        var dashed = Em3dSectionScene.Outline(p, front, new() { Hidden = Em3dHiddenEdges.Dashed }, out _).Lines.Where(l => l.Object == "behind").ToList();
        Assert.NotEmpty(dashed);
        Assert.All(dashed, l => Assert.True(l.Hidden));
        var top = Em3dSectionScene.Outline(p, Em3dProjection.Standard(Em3dStandardView.Top), new(), out _);
        Assert.Contains(top.Lines, l => l.Object == "behind");

        // Reaching past the wall's right side (x = 4): its top edge in the front view is visible from x = 4 to x = 6 only.
        var q = Problem(Box("wall", 0, 0, 0, 4, 1, 4), Box("long", 3, 3, 1, 6, 4, 2, 1));
        var visible = Em3dSectionScene.Outline(q, front, new(), out _).Lines
            .Where(l => l.Object == "long" && Math.Abs(l.A.V - 2 * Mm) < 1e-9 && Math.Abs(l.B.V - 2 * Mm) < 1e-9).ToList();
        var run = Assert.Single(visible);
        Assert.Equal(4.0, Math.Min(run.A.U, run.B.U) / Mm, 3);
        Assert.Equal(6.0, Math.Max(run.A.U, run.B.U) / Mm, 3);
    }

    [Fact]
    public void AModelOverTheTriangleBudget_DrawsItsHiddenEdges_AndSaysSo()
    {
        var scene = Em3dSectionScene.Outline(Problem(Box("b", 0, 0, 0, 1, 1, 1)), Em3dProjection.Standard(Em3dStandardView.Isometric),
                                             new() { Hidden = Em3dHiddenEdges.Removed, TriangleBudget = 4 }, out var note);
        Assert.Equal(12, scene.Lines.Count);
        Assert.Contains("12 triangles", note);
    }

    private static readonly Dictionary<string, SKColor> NoColours = new(StringComparer.Ordinal);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TextAsOutlines_LeavesNoTextElement(bool outlines, bool expectText)
    {
        var p = Problem(Box("b", 0, 0, 0, 2, 3, 5));
        var request = new Em3dDrawingRequest { Format = Em3dDrawingFormat.Svg, TextAsPaths = outlines, DocumentName = "box.c3d" };
        string svg = Encoding.UTF8.GetString(Em3dDrawingExport.Sheet(p, NoColours, ColorTheme.BuiltIn, request, out _, out _));
        Assert.Equal(expectText, svg.Contains("<text", StringComparison.Ordinal));
    }

    /// <summary>Five views and a section on one sheet: no two views overlap, every view's content fits its box at the
    /// ONE scale, and that scale is a round one.</summary>
    [Fact]
    public void TheSheet_LaysEveryViewOut_AtOneRoundScale_WithoutOverlap()
    {
        var p = Problem(Box("b", 0, 0, 0, 2, 3, 5), Box("c", 2, 0, 0, 6, 1, 1, 1));
        var request = new Em3dDrawingRequest
        {
            Views = [Em3dStandardView.Isometric, Em3dStandardView.Top, Em3dStandardView.Front, Em3dStandardView.Right, Em3dStandardView.Left],
            Sections = [new Em3dView(Em3dViewKind.SectionY, 0.5 * Mm)],
            DocumentName = "two.c3d",
        };
        var panels = Em3dDrawingExport.Panels(p, request, []);
        Assert.Equal(6, panels.Count);
        Assert.Equal("Section A–A · XZ at y = 500 µm", panels[5].Title);
        var style = new Em3dDrawingStyle(NoColours, ColorTheme.BuiltIn, ColorVariant.Light) { DocumentName = "two.c3d" };
        var (w, h) = request.PageSize();
        var layout = Em3dDrawingSheet.Layout(w, h, panels, style);

        for (int i = 0; i < layout.Views.Count; i++)
        {
            var (lo, hi) = Em3dDrawingSheet.Extent(panels[i].Scene);
            Assert.True((hi.U - lo.U) * layout.Scale <= layout.Views[i].Width + 1e-3, $"view {i} is wider than its box");
            Assert.True((hi.V - lo.V) * layout.Scale <= layout.Views[i].Height + 1e-3, $"view {i} is taller than its box");
            for (int j = i + 1; j < layout.Views.Count; j++)
                Assert.False(layout.Views[i].IntersectsWith(layout.Views[j]), $"views {i} and {j} overlap");
        }
        Assert.Matches(@"^(1|2|5)(0*):1$|^1:(1|2|5)0*$", layout.ScaleText);
        Assert.Equal(("5:1", 5 * Em3dDrawingSheet.PointsPerMetre), (Em3dDrawingSheet.RoundScale(7.3 * Em3dDrawingSheet.PointsPerMetre).Text,
                                                                     Em3dDrawingSheet.RoundScale(7.3 * Em3dDrawingSheet.PointsPerMetre).Scale));
        Assert.Equal("1:20", Em3dDrawingSheet.RoundScale(0.07 * Em3dDrawingSheet.PointsPerMetre).Text);
    }

    [Fact]
    public void TheDialog_BuildsTheRequest_AndRefusesABareNumber()
    {
        var vm = new Drawing3DDialogViewModel("pkg.c3d", new Point3(0, 0, 0), new Point3(10 * Mm, 8 * Mm, 1 * Mm), null)
        {
            Bottom = true, Right = false, HiddenIndex = 1, FormatIndex = 1, Landscape = false, PageIndex = 2,
        };
        vm.AddSection();
        Assert.Equal("4mm", vm.Sections[0].Position);           // XZ through the centre, y = 4 mm
        vm.Sections[0].Plane = 0;
        Assert.Equal("0.5mm", vm.Sections[0].Position);         // XY through the centre, z = 0.5 mm
        vm.Sections[0].Position = "250um";

        var r = vm.BuildRequest(new HashSet<string> { "lid" })!;
        Assert.Equal([Em3dStandardView.Top, Em3dStandardView.Isometric, Em3dStandardView.Front, Em3dStandardView.Bottom], r.Views);
        var s = Assert.Single(r.Sections);
        Assert.Equal(Em3dViewKind.SectionZ, s.Kind);
        Assert.Equal(250e-6, s.At, 12);
        Assert.Equal(Em3dHiddenEdges.Dashed, r.Hidden);
        Assert.Equal(Em3dDrawingFormat.Svg, r.Format);
        Assert.Equal((841.89f, 1190.55f), r.PageSize());
        Assert.Contains("lid", r.Omit!);
        Assert.Equal("svg", vm.Extension);

        vm.Sections[0].Position = "250";
        Assert.Null(vm.BuildRequest(null));
        Assert.Contains("unit", vm.Error);

        // What is remembered reads back as the same choices.
        var again = new Drawing3DDialogViewModel("pkg.c3d", new Point3(0, 0, 0), new Point3(1, 1, 1), vm.Choices(r));
        Assert.True(again.Bottom); Assert.False(again.Right);
        Assert.Equal(1, again.HiddenIndex); Assert.Equal(1, again.FormatIndex); Assert.Equal(2, again.PageIndex); Assert.False(again.Landscape);
    }
}
