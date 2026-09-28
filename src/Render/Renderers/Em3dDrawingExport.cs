// 3D vector copy and drawing export (2026-09-27) — from a request to bytes: which views, which sections, how hidden
// edges are drawn, on what page. Below the firewall, so the 3D editor's Export Drawing… and anything headless make the
// same file from the same problem.

using CircuitRF.Engine.Em3d;
using SkiaSharp;

namespace CircuitRF.Render;

public enum Em3dDrawingFormat { Svg, Pdf }

/// <summary>The sheet sizes a drawing offers, portrait, in points.</summary>
public enum Em3dPageSize { A4, Letter, A3, Tabloid }

/// <summary>What Export Drawing… makes.</summary>
public sealed record Em3dDrawingRequest
{
    /// <summary>The outline views, in the order they are laid out (left to right, then down). Top first, so that on two
    /// columns Top sits over Front and Right beside it, as a third-angle drawing places them.</summary>
    public IReadOnlyList<Em3dStandardView> Views { get; init; } =
        [Em3dStandardView.Top, Em3dStandardView.Isometric, Em3dStandardView.Front, Em3dStandardView.Right];

    /// <summary>The sections (SectionX/Y/Z at a position, metres), after the views, lettered A, B, C … in order.</summary>
    public IReadOnlyList<Em3dView> Sections { get; init; } = [];

    public Em3dHiddenEdges Hidden { get; init; } = Em3dHiddenEdges.Removed;
    public bool Legend { get; init; } = true;
    public bool TextAsPaths { get; init; } = true;
    public Em3dPageSize Page { get; init; } = Em3dPageSize.A4;
    public bool Landscape { get; init; } = true;
    public Em3dDrawingFormat Format { get; init; } = Em3dDrawingFormat.Pdf;

    /// <summary>The title block's name — the document's file name, normally.</summary>
    public string DocumentName { get; init; } = "";

    /// <summary>Solid and sheet names left out of every view — what the 3D view hides.</summary>
    public IReadOnlySet<string>? Omit { get; init; }

    /// <summary>The page, in points, oriented.</summary>
    public (float W, float H) PageSize()
    {
        var (w, h) = Page switch
        {
            Em3dPageSize.Letter  => (612f, 792f),
            Em3dPageSize.A3      => (841.89f, 1190.55f),
            Em3dPageSize.Tabloid => (792f, 1224f),
            _                    => (595.28f, 841.89f),
        };
        return Landscape ? (h, w) : (w, h);
    }
}

/// <summary>
/// 3D editor bugs round 6 — the part of the picture plane a vector picture shows: the 3D view's window, its centre and
/// half-extents in metres. Without one a picture is framed on everything it draws, which is what Copy as Vector did at
/// every zoom — so a close-up could not be copied.
/// </summary>
public readonly record struct Em3dPictureWindow(Uv Centre, double HalfWidth, double HalfHeight);

/// <summary>
/// 3D editor bugs round 6 — what a vector picture of the 3D view carries beside the model, as the view's toolbar has it:
/// the axis indicator (bottom left) and the scale bar (bottom right). Sized in the view's DIPs times
/// <see cref="PagePerDip"/>, so each is the size on the page it is on the screen, relative to the view.
/// </summary>
public sealed record Em3dPictureChrome
{
    /// <summary>Page units per DIP of the view the picture was made from.</summary>
    public double PagePerDip { get; init; } = 1;
    public bool AxisIndicator { get; init; }
    /// <summary>The scale bar: its length in metres and its label, or null for none.</summary>
    public (double Metres, string Label)? ScaleBar { get; init; }
}

/// <summary>One view as a picture, ready to encode: Copy as Vector and Export as Vector.</summary>
public sealed record Em3dVectorPicture(Em3dScene Scene, float Width, float Height, double Scale, Em3dDrawingStyle Style, string? Note)
{
    /// <summary>The picture-plane point at the page's centre; null centres the page on what the scene draws.</summary>
    public Uv? Centre { get; init; }
    /// <summary>The direction it looks along, for the axis indicator.</summary>
    public Em3dProjection? Projection { get; init; }
    public Em3dPictureChrome? Chrome { get; init; }

    public void Draw(SKCanvas canvas)
    {
        Em3dDrawingSheet.DrawPicture(canvas, Width, Height, Scale, Scene, Style, Centre);
        if (Chrome is { } c) Em3dDrawingSheet.DrawChrome(canvas, Width, Height, Scale, Projection, c, Style.TextAsPaths);
    }
    public string Svg() => Em3dDrawingSheet.Svg(Width, Height, Draw);
    public byte[] Pdf() => Em3dDrawingSheet.Pdf(Width, Height, Draw);
    public byte[] Png(float pixelsPerUnit) => Em3dDrawingSheet.Png(Width, Height, pixelsPerUnit, Draw);
}

public static class Em3dDrawingExport
{
    /// <summary><paramref name="problem"/> without the objects <paramref name="omit"/> names.</summary>
    public static Em3dProblem Without(Em3dProblem problem, IReadOnlySet<string>? omit)
        => omit is not { Count: > 0 } ? problem
           : problem with { Solids = [.. problem.Solids.Where(s => !omit.Contains(s.Name))],
                            Sheets = [.. problem.Sheets.Where(s => !omit.Contains(s.Name))] };

    /// <summary>The views and sections <paramref name="request"/> names, each titled; a view's note (hidden edges not
    /// worked out) goes to <paramref name="notes"/>.</summary>
    public static IReadOnlyList<Em3dDrawingPanel> Panels(Em3dProblem problem, Em3dDrawingRequest request, List<string> notes)
    {
        var shown = Without(problem, request.Omit);
        var panels = new List<Em3dDrawingPanel>();
        var options = new Em3dOutlineOptions { Hidden = request.Hidden };
        foreach (var v in request.Views)
        {
            var p = Em3dProjection.Standard(v);
            var scene = Em3dSectionScene.Outline(shown, p, options, out string? note);
            if (note is not null && !notes.Contains(note)) notes.Add(note);
            panels.Add(new Em3dDrawingPanel(p.Name, scene));
        }
        for (int k = 0; k < request.Sections.Count; k++)
        {
            var scene = Em3dSectionScene.Build(shown, request.Sections[k]);
            panels.Add(new Em3dDrawingPanel(SectionTitle(k, scene), scene));
        }
        return panels;
    }

    /// <summary>"Section A–A · XZ at y = 1.2 mm" — lettered in order, as a drawing letters its cuts.</summary>
    public static string SectionTitle(int index, Em3dScene scene)
    {
        string letter = index < 26 ? ((char)('A' + index)).ToString() : "S" + (index + 1);
        return $"Section {letter}–{letter} · {scene.View.Plane.ToUpperInvariant()} at {scene.View.Axis} = " +
               Em3dSectionScene.FormatLength(scene.At);
    }

    /// <summary>The drawing sheet as bytes (SVG text as UTF-8, or PDF).</summary>
    public static byte[] Sheet(Em3dProblem problem, IReadOnlyDictionary<string, SKColor> colours, ColorTheme theme,
                               Em3dDrawingRequest request, out IReadOnlyList<string> notes, out Em3dSheetLayout layout)
    {
        var said = new List<string>();
        var panels = Panels(problem, request, said);
        var style = new Em3dDrawingStyle(colours, theme, ColorVariant.Light)
        {
            Legend = request.Legend, TextAsPaths = request.TextAsPaths, DocumentName = request.DocumentName,
            Remark = request.Views.Count == 0 ? null : request.Hidden switch
            {
                Em3dHiddenEdges.Dashed  => "hidden edges dashed",
                Em3dHiddenEdges.Shown   => "hidden edges shown",
                _                       => "hidden edges removed",
            },
        };
        var (w, h) = request.PageSize();
        var l = Em3dDrawingSheet.Layout(w, h, panels, style);
        layout = l;
        notes = said;
        void Draw(SKCanvas c) => Em3dDrawingSheet.Draw(c, l, panels, style);
        return request.Format == Em3dDrawingFormat.Svg
            ? System.Text.Encoding.UTF8.GetBytes(Em3dDrawingSheet.Svg(w, h, Draw))
            : Em3dDrawingSheet.Pdf(w, h, Draw);
    }

    /// <summary>One outline along <paramref name="projection"/>: what Copy as Vector copies. Framed on
    /// <paramref name="window"/> — the 3D view's, so the picture is what the camera sees at its zoom — or, without one, on
    /// its own content. No legend, no title, labels as outlines, a transparent page; <paramref name="chrome"/>'s axis
    /// indicator and scale bar when given.</summary>
    public static Em3dVectorPicture Picture(Em3dProblem problem, Em3dProjection projection, IReadOnlyDictionary<string, SKColor> colours,
                                            ColorTheme theme, IReadOnlySet<string>? omit, Em3dHiddenEdges hidden = Em3dHiddenEdges.Removed,
                                            float maxSide = 720f, Em3dPictureWindow? window = null, Em3dPictureChrome? chrome = null)
    {
        var scene = Em3dSectionScene.Outline(Without(problem, omit), projection, new Em3dOutlineOptions { Hidden = hidden }, out string? note);
        var style = new Em3dDrawingStyle(colours, theme, ColorVariant.Light) { Legend = false, Transparent = true };
        if (window is not { HalfWidth: > 0, HalfHeight: > 0 } win)
        {
            var (w, h, scale) = Em3dDrawingSheet.PictureSize(scene, maxSide);
            return new Em3dVectorPicture(scene, w, h, scale, style, note) { Projection = projection, Chrome = chrome };
        }
        double aspect = win.HalfWidth / win.HalfHeight;
        float pw = aspect >= 1 ? maxSide : (float)(maxSide * aspect), ph = aspect >= 1 ? (float)(maxSide / aspect) : maxSide;
        return new Em3dVectorPicture(scene, pw, ph, pw / (2 * win.HalfWidth), style, note)
        {
            Centre = win.Centre, Projection = projection, Chrome = chrome,
        };
    }
}
