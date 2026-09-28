// ================================================================
//  EditorRound6PictureTests.cs — the 3D editor's sixth round of owner feedback: a vector picture is framed on the view's
//  window (Copy as Vector always zoomed out to the extents) and carries the axis indicator and the scale bar the toolbar
//  shows; the selection outline a raster picture paints keeps only what the clip plane keeps; and a VAR in use says why
//  it cannot be deleted before Delete is pressed.
// ================================================================

using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Ui.Viewer3D;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class EditorRound6PictureTests
{
    private const double Mm = 1e-3;

    private static Em3dSolid Box(string name, double x0, double y0, double x1, double y1)
        => new(name, "Copper", Em3dRole.Conductor, new Em3dBox(new Point3(x0 * Mm, y0 * Mm, 0), new Point3(x1 * Mm, y1 * Mm, 1 * Mm)), 0);

    private static Em3dProblem Problem(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new Point3(-50 * Mm, -50 * Mm, -50 * Mm), new Point3(50 * Mm, 50 * Mm, 50 * Mm), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
    }

    private static Em3dVectorPicture Picture(Em3dProblem p, Em3dPictureWindow? window, Em3dPictureChrome? chrome = null)
        => Em3dDrawingExport.Picture(p, Em3dProjection.Standard(Em3dStandardView.Top), new Dictionary<string, SKColor>(),
                                     ColorTheme.BuiltIn, null, window: window, chrome: chrome);

    /// <summary>What an SVG draws: its path, line, rect and circle elements.</summary>
    private static int Marks(string svg) => Regex.Matches(svg, "<(path|line|rect|circle|polyline|polygon)\\b").Count;

    /// <summary>Two boxes 20 mm apart, seen from the top through a 4 × 2 mm window on the first: the page takes the window's
    /// aspect at the window's scale, and the second box is not in the file at all.</summary>
    [Fact]
    public void AVectorPicture_IsFramedOnTheViewsWindow_AndLeavesOutWhatIsOutsideIt()
    {
        var p = Problem(Box("near", 0, 0, 1, 1), Box("far", 20, 0, 21, 1));
        var window = new Em3dPictureWindow(new Uv(0.5 * Mm, 0.5 * Mm), 2 * Mm, 1 * Mm);
        var framed = Picture(p, window);
        Assert.Equal(720f, framed.Width);
        Assert.Equal(360f, framed.Height);
        Assert.Equal(720 / (4 * Mm), framed.Scale, 6);

        int both = Marks(Picture(p, null).Svg());
        int near = Marks(framed.Svg());
        int none = Marks(Picture(p, new Em3dPictureWindow(new Uv(10 * Mm, 30 * Mm), 2 * Mm, 1 * Mm)).Svg());
        Assert.True(near > none, $"the near box draws ({near} marks against {none} for an empty window)");
        Assert.True(near < both, $"the far box is left out ({near} marks against {both} for the whole model)");
    }

    /// <summary>The axis indicator and the scale bar are drawn when the chrome asks for them, and not otherwise.</summary>
    [Fact]
    public void AVectorPicture_CarriesTheAxisIndicatorAndScaleBar_OnlyWhenAsked()
    {
        var p = Problem(Box("b", 0, 0, 1, 1));
        var window = new Em3dPictureWindow(new Uv(0.5 * Mm, 0.5 * Mm), 2 * Mm, 1 * Mm);
        int bare = Marks(Picture(p, window, new Em3dPictureChrome()).Svg());
        int axis = Marks(Picture(p, window, new Em3dPictureChrome { AxisIndicator = true }).Svg());
        int bar = Marks(Picture(p, window, new Em3dPictureChrome { ScaleBar = (1 * Mm, "1 mm") }).Svg());
        Assert.True(axis > bare);
        Assert.True(bar > bare);
    }

    /// <summary>The outline painted for a picture keeps what fs_edge keeps: the part of an edge on the plane's kept side.</summary>
    [Fact]
    public void TheSelectionOutline_IsCutWhereTheClipPlaneCutsIt()
    {
        var plane = new Vector4(1, 0, 0, -1);                        // discards x > 1
        Vector3 a = new(0, 0, 0), b = new(2, 0, 0);
        Assert.True(Viewer3DOverlay.ClipSegment(plane, ref a, ref b));
        Assert.Equal(1f, b.X, 5);
        Vector3 c = new(1.5f, 0, 0), d = new(3, 0, 0);
        Assert.False(Viewer3DOverlay.ClipSegment(plane, ref c, ref d));
    }

    /// <summary>A VAR a field uses reports why Delete would refuse — the sentence Delete itself returns — before the
    /// gesture; one nothing uses reports nothing and deletes.</summary>
    [Fact]
    public void AVarInUse_SaysWhyItCannotBeDeleted_BeforeDeleteIsPressed()
    {
        var doc = new C3dDocument
        {
            Variables = [new C3dVariable { Name = "cav_w", Expression = "10", Unit = "Mil" }, new C3dVariable { Name = "spare", Expression = "1" }],
        };
        var floor = new C3dBox { Name = "floor", Material = "Gold", Size = new C3dPoint3(1000, 1000, 1000) };
        C3dBindings.SetExpr(floor, C3dBindings.SpecOf(floor.GetType(), "Size")!, 0, new C3dExpr("cav_w", "Mil"));
        doc.Objects.Add(floor);

        string? why = C3dVariableEdits.DeleteRefusal(doc, "cav_w");
        Assert.NotNull(why);
        Assert.Contains("'floor'", why);
        Assert.Equal(why, C3dVariableEdits.Delete(doc, "cav_w"));
        Assert.Null(C3dVariableEdits.DeleteRefusal(doc, "spare"));
        Assert.Null(C3dVariableEdits.Delete(doc, "spare"));
        Assert.Equal(["cav_w"], doc.Variables.Select(v => v.Name));
    }
}
