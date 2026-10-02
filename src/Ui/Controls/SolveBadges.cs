// brief-em3d-98 R-em3d98-4 — the "solved" glyphs: ◆ FEM (Palace), ▲ FDTD (openEMS), ● thermal, after a .c3d's name on its tab,
// on its workspace tree row and in the docs' legend.
//
// DRAWN AS VECTOR PATHS, NEVER AS CHARACTERS. Font fallback for ◆▲● differs between Windows, macOS and Linux in size, baseline
// and, now and then, emoji presentation; a path is the same shape everywhere. The '*' after a partial result is a path too
// (it was text): a tab header is laid out on every frame of a window resize, and shaping text there is work a path is not.
//
// Colour carries no meaning (shape and fill do), so the two brushes are a theme's, not literals: SolveBadgeBrush and
// SolveBadgeFadedBrush, in both variants of CircuitRfResources.axaml.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Controls;

/// <summary>The glyphs <see cref="SolveBadgeRules.Pick"/> chooses for <see cref="Badges"/>, one child per glyph, each with its
/// own tooltip. Empty (and zero-width) when there is nothing to show.</summary>
public sealed class SolveBadges : StackPanel
{
    public static readonly StyledProperty<SolveBadgeSet?> BadgesProperty =
        AvaloniaProperty.Register<SolveBadges, SolveBadgeSet?>(nameof(Badges));

    /// <summary>The glyph's height in device-independent pixels — noticeably bigger than the tab's <c>•</c> unsaved mark, so
    /// the two circles are not confused.</summary>
    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<SolveBadges, double>(nameof(GlyphSize), 10);

    public SolveBadgeSet? Badges
    {
        get => GetValue(BadgesProperty);
        set => SetValue(BadgesProperty, value);
    }

    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public SolveBadges()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 3;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BadgesProperty || change.Property == GlyphSizeProperty) Rebuild();
    }

    private IReadOnlyList<SolveBadge> _shown = [];
    private double _shownSize;

    /// <summary>Children only when the glyphs themselves changed: a status check that answers the same thing (most of them)
    /// touches nothing, so it never invalidates the tab strip's layout.</summary>
    private void Rebuild()
    {
        var glyphs = Badges?.Glyphs ?? [];
        if (_shownSize == GlyphSize && glyphs.SequenceEqual(_shown)) return;
        _shown = glyphs;
        _shownSize = GlyphSize;
        Children.Clear();
        foreach (var g in glyphs)
        {
            var glyph = new SolveGlyph(g.Kind, g.Look, g.Partial, GlyphSize);
            ToolTip.SetTip(glyph, g.Tooltip);
            Children.Add(glyph);
        }
    }
}

/// <summary>
/// One glyph: its shape (by solver kind), filled or hollow, solid or faded, and an optional <c>*</c>. Everything is decided
/// when it is made — a fixed size, the geometry built once, the brush resolved once (again on a theme change) — so a layout
/// pass or a redraw during a window resize does no work here beyond drawing two cached geometries. The <c>*</c> is a vector
/// asterisk too: shaping text in a tab header was the one costly thing this control did.
/// </summary>
public sealed class SolveGlyph : Control
{
    public SolverKind Kind { get; }
    public SolveBadgeLook Look { get; }
    public bool Partial { get; }
    public double GlyphSize { get; }

    private readonly Size _size;
    private readonly Geometry _shape;
    private readonly Geometry? _star;
    private IBrush? _brush;
    private Pen? _pen, _starPen;

    public SolveGlyph(SolverKind kind, SolveBadgeLook look, bool partial, double glyphSize)
    {
        (Kind, Look, Partial, GlyphSize) = (kind, look, partial, glyphSize);
        double s = glyphSize, starSize = Math.Round(s * 0.6);
        _size = new Size(s + (partial ? starSize + 1.5 : 0), s);
        _shape = Shape(kind, new Rect(0.75, 0.75, s - 1.5, s - 1.5));
        _star = partial ? Asterisk(new Point(s + 1 + starSize / 2, starSize / 2 + 0.5), starSize / 2) : null;
        ActualThemeVariantChanged += (_, _) => { _brush = null; InvalidateVisual(); };
    }

    protected override Size MeasureOverride(Size availableSize) => _size;

    public override void Render(DrawingContext context)
    {
        if (_brush is null)
        {
            string key = Look == SolveBadgeLook.Faded ? "SolveBadgeFadedBrush" : "SolveBadgeBrush";
            _brush = this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Gray;
            _pen = new Pen(_brush, 1.4);
            _starPen = new Pen(_brush, 1.1, lineCap: PenLineCap.Round);
        }
        using (context.PushTransform(Matrix.CreateTranslation(0, Math.Round((Bounds.Height - GlyphSize) / 2))))
        {
            if (Look == SolveBadgeLook.Hollow) context.DrawGeometry(null, _pen, _shape);
            else context.DrawGeometry(_brush, null, _shape);
            if (_star is not null) context.DrawGeometry(null, _starPen, _star);
        }
    }

    /// <summary>A six-armed asterisk centred on <paramref name="c"/>.</summary>
    private static Geometry Asterisk(Point c, double r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            for (int i = 0; i < 3; i++)
            {
                double a = Math.PI / 2 + i * Math.PI / 3, dx = r * Math.Cos(a), dy = r * Math.Sin(a);
                ctx.BeginFigure(new Point(c.X - dx, c.Y - dy), false);
                ctx.LineTo(new Point(c.X + dx, c.Y + dy));
                ctx.EndFigure(false);
            }
        return g;
    }

    /// <summary>◆ for FEM, ▲ for FDTD, ● for thermal, inscribed in <paramref name="r"/>.</summary>
    public static Geometry Shape(SolverKind kind, Rect r)
    {
        if (kind == SolverKind.Thermal) return new EllipseGeometry(r.Deflate(r.Width * 0.06));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (kind == SolverKind.Fem)
            {
                c.BeginFigure(new Point(r.Center.X, r.Top), true);
                c.LineTo(new Point(r.Right, r.Center.Y));
                c.LineTo(new Point(r.Center.X, r.Bottom));
                c.LineTo(new Point(r.Left, r.Center.Y));
            }
            else
            {
                c.BeginFigure(new Point(r.Center.X, r.Top), true);
                c.LineTo(new Point(r.Right, r.Bottom));
                c.LineTo(new Point(r.Left, r.Bottom));
            }
            c.EndFigure(true);
        }
        return g;
    }
}
