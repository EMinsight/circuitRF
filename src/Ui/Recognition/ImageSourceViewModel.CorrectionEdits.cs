// Create Layout from Image — the corrections as objects, and a via's drill. brief-dfr30-1-picture-to-layout-round-2.md
// R-dfr30-1-2, R-dfr30-1-3, R-dfr30-1-4, R-dfr30-1-6, R-dfr30-1-7.
//
// Every correction is a row of the Corrections list: selected from its row or on the canvas, deleted, a drawn polygon's
// corners moved, added or removed, a hand via moved or given another drill, a copper fill moved to another conductor.
// EDITING REPLACES A RECORD in Corrections — it never adds a kind of record — so the CLI's flags and the dialog still
// build the same ImageCorrection list and write the same bytes. Each edit is one undo step, through Correct.
//
// The Remove menu clears one kind at once, each one undo step: the vias read are answered with a NotVia each (the holes
// stay openings), the copper read on a conductor with a Clear covering the picture on that conductor alone.
//
// IT DECIDES NOTHING: what a correction does is still ImageTrace.Build's.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;

namespace CircuitRF.Ui.Recognition;

/// <summary>Where the Via tool's drill comes from (R-dfr30-1-2).</summary>
public enum ViaDrillSource { FromHole, Read, Technology, Other }

/// <summary>One row of the Via tool's Drill picker.</summary>
/// <param name="Metres">The drill, for a size read or the technology's; null for From the hole and Other….</param>
public sealed record ViaDrillChoice(string Label, ViaDrillSource Source, double? Metres)
{
    public override string ToString() => Label;
}

/// <summary>One row of the Corrections list (R-dfr30-1-3).</summary>
public sealed partial class PictureCorrectionRow : ObservableObject
{
    private readonly ImageSourceViewModel _owner;

    internal PictureCorrectionRow(ImageSourceViewModel owner, int index, ImageCorrection correction, string text, string detail,
                                  IReadOnlyList<string> layers)
    {
        _owner = owner;
        Index = index;
        Correction = correction;
        Text = text;
        Detail = detail;
        Layers = layers;
        _layer = correction.Layer;
    }

    public int Index { get; }
    public ImageCorrection Correction { get; }

    /// <summary>What it is: a via, a copper fill, a clear, the outline.</summary>
    public string Text { get; }

    /// <summary>Its drill for a via, or what a clear clears.</summary>
    public string Detail { get; }

    /// <summary>A copper fill's or a painted area's conductor is chosen on its row.</summary>
    public bool IsCopper => Correction.Kind is ImageCorrectionKind.Copper or ImageCorrectionKind.PaintFill;

    public IReadOnlyList<string> Layers { get; }

    /// <summary>A copper fill's conductor; another one chosen moves the fill there (one undo step).</summary>
    public string? Layer
    {
        get => _layer;
        set
        {
            // A picker clears its selection while its items are replaced: never a choice of the user's.
            if (value is null || value == _layer) return;
            _layer = value;
            OnPropertyChanged();
            _owner.SetCorrectionLayer(Index, value);
        }
    }

    private string? _layer;

    [RelayCommand] private void Delete() => _owner.DeleteCorrection(Index);
}

public sealed partial class ImageSourceViewModel
{
    // ── the Corrections list (R-dfr30-1-3) ────────────────────────────────────────────────────────────

    public ObservableCollection<PictureCorrectionRow> CorrectionRows { get; } = [];

    public bool HasCorrectionRows => Corrections.Count > 0;

    private int _selectedCorrection = -1;
    private bool _rebuildingRows;

    /// <summary>The correction selected — on its row or on the canvas — by its place in <see cref="Corrections"/>; -1 for none.</summary>
    public int SelectedCorrection
    {
        get => _selectedCorrection;
        set
        {
            if (_rebuildingRows) return;   // the list clears its selection while its rows are replaced
            if (value < 0 || value >= Corrections.Count) value = -1;
            if (value == _selectedCorrection) return;
            _selectedCorrection = value;
            OnPropertyChanged();
            OnCorrectionSelected();
        }
    }

    /// <summary>The selected correction, or null.</summary>
    public ImageCorrection? SelectedCorrectionRecord =>
        _selectedCorrection >= 0 && _selectedCorrection < Corrections.Count ? Corrections[_selectedCorrection] : null;

    /// <summary>A shape keeps at least this many corners.</summary>
    public const int MinCorners = 3;

    public const string MinCornersRefusal = "A shape keeps at least three corners.";

    private void OnCorrectionSelected()
    {
        OnPropertyChanged(nameof(SelectedCorrectionRecord));
        OnPropertyChanged(nameof(DrillPickerShown));
        OnPropertyChanged(nameof(OtherDrillShown));
        // The Drill picker shows the selected hand via's drill, without that being a change to it.
        if (SelectedCorrectionRecord is { Kind: ImageCorrectionKind.Via } via)
        {
            _showingSelection = true;
            try { ShowDrill(via.DrillMetres); }
            finally { _showingSelection = false; }
        }
    }

    /// <summary>The rows rebuilt from <see cref="Corrections"/>, the selection kept where it still is one.</summary>
    private void CorrectionsChanged()
    {
        _rebuildingRows = true;
        try
        {
            CorrectionRows.Clear();
            var layers = ConductorChoices;
            for (int i = 0; i < Corrections.Count; i++)
            {
                var (text, detail) = Describe(Corrections[i]);
                CorrectionRows.Add(new PictureCorrectionRow(this, i, Corrections[i], text, detail, layers));
            }
        }
        finally { _rebuildingRows = false; }
        if (_selectedCorrection >= Corrections.Count) _selectedCorrection = -1;
        OnPropertyChanged(nameof(SelectedCorrection));
        OnPropertyChanged(nameof(HasCorrectionRows));
        OnCorrectionSelected();
        RaiseRemoveCounts();
    }

    private (string Text, string Detail) Describe(ImageCorrection c) => c.Kind switch
    {
        ImageCorrectionKind.Via => ("Via", c.DrillMetres is { } m ? "⌀" + FormatDrill(m) : "drilled from the hole"),
        ImageCorrectionKind.Hole => ("Hole, not plated", ""),
        ImageCorrectionKind.NotVia => ("Opening", ""),
        ImageCorrectionKind.Copper => ("Copper fill on", ""),
        ImageCorrectionKind.PaintFill => ("Paint Fill on", ""),
        ImageCorrectionKind.Clear => c.Layer is { } l ? ("Copper read removed", $"on {l}") : ("Not copper", "on every layer"),
        _ => ("Board outline", $"{c.Points.Count} corners"),
    };

    [RelayCommand]
    private void DeleteSelectedCorrection()
    {
        if (_selectedCorrection >= 0) DeleteCorrection(_selectedCorrection);
    }

    /// <summary>The correction at <paramref name="index"/> removed: one undo step.</summary>
    public bool DeleteCorrection(int index)
    {
        if (index < 0 || index >= Corrections.Count) return false;
        _selectedCorrection = -1;
        return Correct(list => { list.RemoveAt(index); return null; });
    }

    /// <summary>One corner of a drawn polygon moved (R-dfr30-1-3): one undo step per drag.</summary>
    public bool MoveCorrectionVertex(int index, int vertex, PixelPoint to) =>
        EditShape(index, points => { if (vertex < 0 || vertex >= points.Count) return null; points[vertex] = Round(to); return null; });

    /// <summary>A corner added on the edge after <paramref name="after"/> — a double-click on the edge.</summary>
    public bool InsertCorrectionVertex(int index, int after, PixelPoint at) =>
        EditShape(index, points => { if (after < 0 || after >= points.Count) return null; points.Insert(after + 1, Round(at)); return null; });

    /// <summary>A corner removed — refused below three.</summary>
    public bool RemoveCorrectionVertex(int index, int vertex) =>
        EditShape(index, points =>
        {
            if (vertex < 0 || vertex >= points.Count) return null;
            if (points.Count <= MinCorners) return MinCornersRefusal;
            points.RemoveAt(vertex);
            return null;
        });

    private bool EditShape(int index, Func<List<PixelPoint>, string?> edit)
    {
        if (index < 0 || index >= Corrections.Count || Corrections[index].IsPoint) return false;
        return Correct(list =>
        {
            var points = list[index].Points.ToList();
            string? said = edit(points);
            if (said is null) list[index] = list[index] with { Points = points };
            return said;
        });
    }

    /// <summary>A hand via, or a painted area's point, dragged to a new place (R-dfr30-1-3).</summary>
    public bool MoveCorrection(int index, PixelPoint to)
    {
        if (index < 0 || index >= Corrections.Count || Corrections[index].Kind is not (ImageCorrectionKind.Via or ImageCorrectionKind.PaintFill)) return false;
        return Correct(list => { list[index] = list[index] with { Points = [Round(to)] }; return null; });
    }

    /// <summary>A copper fill moved to another conductor, from its row.</summary>
    public bool SetCorrectionLayer(int index, string layer)
    {
        if (index < 0 || index >= Corrections.Count || Corrections[index].Kind is not (ImageCorrectionKind.Copper or ImageCorrectionKind.PaintFill)
            || !ConductorChoices.Contains(layer)) return false;
        return Correct(list => { list[index] = list[index] with { Layer = layer }; return null; });
    }

    private static PixelPoint Round(PixelPoint p) => new(Math.Round(p.X, 2), Math.Round(p.Y, 2));

    // ── where a click lands (the canvas asks; R-dfr30-1-3) ─────────────────────────────────────────────

    /// <summary>The correction under <paramref name="p"/>, the last made first: a point within its hole (or a few pixels),
    /// an outline near its line, a fill or a clear inside it. -1 for none. The copper read removed on a whole conductor
    /// covers the picture and is selected from its row only.</summary>
    public int CorrectionAt(PixelPoint p, double tolerancePx)
    {
        for (int i = Corrections.Count - 1; i >= 0; i--)
        {
            var c = Corrections[i];
            if (c.IsPoint || c.Kind == ImageCorrectionKind.PaintFill)
            {
                double r = (c.IsPoint ? HoleRadiusAt(c.At) : 4) + tolerancePx;
                if ((p.X - c.At.X) * (p.X - c.At.X) + (p.Y - c.At.Y) * (p.Y - c.At.Y) <= r * r) return i;
            }
        }
        for (int i = Corrections.Count - 1; i >= 0; i--)
        {
            var c = Corrections[i];
            if (c.IsPoint || c.Kind == ImageCorrectionKind.PaintFill || c is { Kind: ImageCorrectionKind.Clear, Layer: not null } || c.Points.Count < 3) continue;
            if (EdgeAt(i, p, tolerancePx) >= 0) return i;
            if (c.Kind != ImageCorrectionKind.Outline && Inside(c.Points, p)) return i;
        }
        return -1;
    }

    /// <summary>The corner of shape correction <paramref name="index"/> under <paramref name="p"/>, or -1.</summary>
    public int VertexAt(int index, PixelPoint p, double tolerancePx)
    {
        if (index < 0 || index >= Corrections.Count || Corrections[index].IsPoint) return -1;
        var pts = Corrections[index].Points;
        for (int k = 0; k < pts.Count; k++)
            if (Math.Abs(pts[k].X - p.X) <= tolerancePx && Math.Abs(pts[k].Y - p.Y) <= tolerancePx) return k;
        return -1;
    }

    /// <summary>The edge of shape correction <paramref name="index"/> under <paramref name="p"/> — by its first corner — or -1.</summary>
    public int EdgeAt(int index, PixelPoint p, double tolerancePx)
    {
        if (index < 0 || index >= Corrections.Count || Corrections[index].IsPoint) return -1;
        var pts = Corrections[index].Points;
        for (int k = 0; k < pts.Count; k++)
        {
            var (a, b) = (pts[k], pts[(k + 1) % pts.Count]);
            double vx = b.X - a.X, vy = b.Y - a.Y, len2 = vx * vx + vy * vy;
            double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
            double dx = a.X + t * vx - p.X, dy = a.Y + t * vy - p.Y;
            if (dx * dx + dy * dy <= tolerancePx * tolerancePx) return k;
        }
        return -1;
    }

    /// <summary>The radius of the hole a point correction sits on, else a few pixels — what it is drawn and hit as.</summary>
    internal double HoleRadiusAt(PixelPoint p) =>
        _trace?.Holes.Select(h => h.Circle).Where(c => Near(p, c)).Select(c => c.R).DefaultIfEmpty(4).First() ?? 4;

    private static bool Inside(IReadOnlyList<PixelPoint> ring, PixelPoint p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y)
                && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    // ── a via's drill (R-dfr30-1-2) ──────────────────────────────────────────────────────────────────

    public const string OtherDrillRefusal = "Type the drill with its unit — 0.3 mm, 12 mil.";

    private IReadOnlyList<ViaDrillChoice> _viaDrillChoices = [FromHoleChoice];
    private ViaDrillChoice _viaDrill = FromHoleChoice;
    private bool _showingSelection;

    private static readonly ViaDrillChoice FromHoleChoice = new("From the hole", ViaDrillSource.FromHole, null);
    private static readonly ViaDrillChoice OtherChoice = new("Other…", ViaDrillSource.Other, null);

    /// <summary>From the hole; every drill the trace read, with its count; the technology's via; Other….</summary>
    public IReadOnlyList<ViaDrillChoice> ViaDrillChoices => _viaDrillChoices;

    /// <summary>The Drill picker's choice, kept for the following clicks — and, with a hand via selected, that via's drill.</summary>
    public ViaDrillChoice? ViaDrill
    {
        get => _viaDrill;
        set
        {
            // A picker clears its selection while its items are replaced: never a choice of the user's.
            if (value is null || Equals(value, _viaDrill)) return;
            _viaDrill = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OtherDrillShown));
            RaiseCanvasPrompt();
            if (!_showingSelection && value.Source != ViaDrillSource.Other) ResizeSelectedVia(value.Metres);
        }
    }

    partial void OnOtherDrillTextChanged(string value) => RaiseCanvasPrompt();

    /// <summary>Other…'s typed size, with its unit.</summary>
    [ObservableProperty] private string _otherDrillText = "";

    /// <summary>The Drill picker: with the Via tool armed, or a hand via selected while no tool with options of its own is
    /// armed — the two share one slot under the tools (brief-dfr30-3 R-dfr30-3-1).</summary>
    public bool DrillPickerShown => ViaArmed || (!DrawLayerShown && SelectedCorrectionRecord is { Kind: ImageCorrectionKind.Via });
    public bool OtherDrillShown => DrillPickerShown && _viaDrill.Source == ViaDrillSource.Other;

    /// <summary>The drill the next via is placed with, metres; null takes the hole's (or the technology's off a hole).</summary>
    public double? ChosenDrillMetres => _viaDrill.Source switch
    {
        ViaDrillSource.Read or ViaDrillSource.Technology => _viaDrill.Metres,
        ViaDrillSource.Other => ParseDrill(OtherDrillText),
        _ => null,
    };

    /// <summary>A typed drill in metres, or null when it has no unit or is not a size.</summary>
    public static double? ParseDrill(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || !char.IsLetter(t[^1]) || t.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return null;
        return LayoutUnits.TryParse(t, LayoutUnit.Um, 1000, out long nm) && nm > 0 ? nm * 1e-9 : null;
    }

    /// <summary>Other…'s size applied to the selected hand via (Enter, or Set).</summary>
    [RelayCommand]
    private void ApplyOtherDrill()
    {
        if (ParseDrill(OtherDrillText) is not { } m) { Status = OtherDrillRefusal; return; }
        ResizeSelectedVia(m);
    }

    /// <summary>The selected hand via — and, with <see cref="EveryOneLikeIt"/>, every hand via drilled as it is — given
    /// <paramref name="metres"/>: one undo step.</summary>
    private void ResizeSelectedVia(double? metres)
    {
        if (SelectedCorrectionRecord is not { Kind: ImageCorrectionKind.Via } chosen) return;
        int at = _selectedCorrection;
        Correct(list =>
        {
            for (int i = 0; i < list.Count; i++)
                if (i == at || (EveryOneLikeIt && list[i].Kind == ImageCorrectionKind.Via && SameDrill(list[i].DrillMetres, chosen.DrillMetres)))
                    list[i] = list[i] with { DrillMetres = metres };
            return null;
        });
    }

    private static bool SameDrill(double? a, double? b) => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 1e-9;

    /// <summary>The picker showing <paramref name="metres"/>: its read or technology row, else Other… with the size typed.</summary>
    private void ShowDrill(double? metres)
    {
        if (metres is not { } m) { ViaDrill = FromHoleChoice; return; }
        var row = _viaDrillChoices.FirstOrDefault(c => c.Metres is { } cm && Math.Abs(cm - m) < 1e-9);
        if (row is null) OtherDrillText = FormatDrill(m);
        ViaDrill = row ?? OtherChoice;
    }

    /// <summary>The picker's rows from the trace just read: the choice kept where the new rows have it.</summary>
    private void RefreshDrillChoices()
    {
        var rows = new List<ViaDrillChoice> { FromHoleChoice };
        if (_trace is { } t)
        {
            double perMetre = t.DbuPerMicron * 1e6;
            foreach (var g in t.Shapes.OfType<ViaShape>().GroupBy(v => v.DrillSize).OrderBy(g => g.Key))
                rows.Add(new ViaDrillChoice($"{FormatDrill(g.Key / perMetre)} · {g.Count()}", ViaDrillSource.Read, g.Key / perMetre));
        }
        if (_technology is { DefaultViaDrillDbu: > 0 } tech)
        {
            double m = tech.DefaultViaDrillDbu / (LayoutUnits.DefaultDbuPerMicron * 1e6);
            rows.Add(new ViaDrillChoice($"{tech.Name}'s via · {FormatDrill(m)}", ViaDrillSource.Technology, m));
        }
        rows.Add(OtherChoice);
        if (rows.SequenceEqual(_viaDrillChoices)) return;
        var was = _viaDrill;
        _viaDrillChoices = rows;
        OnPropertyChanged(nameof(ViaDrillChoices));
        // The same choice in the new rows — a size read keeps its place when its count changes; one no longer read is
        // still the size chosen, as Other….
        var again = rows.FirstOrDefault(r => r.Source == was.Source && SameDrill(r.Metres, was.Metres))
                    ?? (was.Source == ViaDrillSource.Read && was.Metres is { } m0 ? null : FromHoleChoice);
        _showingSelection = true;
        try
        {
            if (again is null) { OtherDrillText = FormatDrill(was.Metres!.Value); again = OtherChoice; }
            _viaDrill = again;
            OnPropertyChanged(nameof(ViaDrill));
            OnPropertyChanged(nameof(OtherDrillShown));
        }
        finally { _showingSelection = false; }
    }

    /// <summary>A drill as the technology states one: mil on an inch board, mm to two places otherwise.</summary>
    internal string FormatDrill(double metres) =>
        _technology?.DefaultDisplayUnit is LayoutUnit.Mil or LayoutUnit.Inch
            ? $"{(metres / 25.4e-6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} mil"
            : $"{(metres * 1e3).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} mm";

    // ── Remove ▾ (R-dfr30-1-4) ───────────────────────────────────────────────────────────────────────

    /// <summary>The vias the reading made — a drill circle, or a round hole in the copper — not a via placed by hand.</summary>
    private IReadOnlyList<CircleFit> AutoVias() =>
        _trace is not { } t ? []
        : [.. t.Holes.Where(h => h.Kind == ImageHoleKind.Via).Select(h => h.Circle)
                     .Where(c => !Corrections.Any(f => f.Kind == ImageCorrectionKind.Via && Near(f.At, c)))];

    private static bool Near(PixelPoint p, CircleFit c) =>
        (p.X - c.Cx) * (p.X - c.Cx) + (p.Y - c.Cy) * (p.Y - c.Cy) <= (c.R + ImageTraceHoleReach) * (c.R + ImageTraceHoleReach);

    private const double ImageTraceHoleReach = 1.0;

    public int AutoViaCount => AutoVias().Count;
    public int HandViaCount => Corrections.Count(c => c.Kind == ImageCorrectionKind.Via);
    public int CopperFillCount => Corrections.Count(c => c.Kind == ImageCorrectionKind.Copper);
    public int PaintedCount => Corrections.Count(c => c.Kind == ImageCorrectionKind.PaintFill);
    public int ClearCount => Corrections.Count(c => c.Kind == ImageCorrectionKind.Clear && c.Layer is null);
    public int OutlineCount => Corrections.Count(c => c.Kind == ImageCorrectionKind.Outline);

    /// <summary>The regions read on <paramref name="layer"/>; none once its copper read is removed.</summary>
    public int CopperReadCount(string layer) =>
        Corrections.Any(c => c.Kind == ImageCorrectionKind.Clear && c.Layer == layer) ? 0
        : _trace?.Layers.FirstOrDefault(l => l.Role == ImageLayerRole.Layer && l.Name == layer)?.PixelRegions.Count ?? 0;

    private void RaiseRemoveCounts()
    {
        foreach (string p in new[] { nameof(AutoViaCount), nameof(HandViaCount), nameof(CopperFillCount), nameof(PaintedCount), nameof(ClearCount), nameof(OutlineCount) })
            OnPropertyChanged(p);
    }

    /// <summary>A NotVia for every via the reading made: the holes stay openings. One undo step.</summary>
    public void RemoveAutoVias()
    {
        var vias = AutoVias();
        if (vias.Count == 0) return;
        Correct(list =>
        {
            foreach (var c in vias) list.Add(ImageCorrection.NotVia(Math.Round(c.Cx, 2), Math.Round(c.Cy, 2)));
            return $"{vias.Count} {(vias.Count == 1 ? "via" : "vias")} read from the picture removed; the holes stay openings.";
        });
    }

    public void RemoveHandVias() => RemoveKind(ImageCorrectionKind.Via, null, "via placed by hand", "vias placed by hand");
    public void RemoveCopperFills() => RemoveKind(ImageCorrectionKind.Copper, null, "copper fill", "copper fills");
    public void RemovePainted() => RemoveKind(ImageCorrectionKind.PaintFill, null, "paint fill", "paint fills");
    public void RemoveClears() => RemoveKind(ImageCorrectionKind.Clear, c => c.Layer is null, "clear", "clears");
    public void RemoveOutline() => RemoveKind(ImageCorrectionKind.Outline, null, "outline", "outlines");

    private void RemoveKind(ImageCorrectionKind kind, Func<ImageCorrection, bool>? also, string one, string many)
    {
        bool Match(ImageCorrection c) => c.Kind == kind && (also?.Invoke(c) ?? true);
        int n = Corrections.Count(Match);
        if (n == 0) return;
        _selectedCorrection = -1;
        Correct(list => { list.RemoveAll(Match); return $"{n} {(n == 1 ? one : many)} removed."; });
    }

    /// <summary>The copper read on <paramref name="layer"/> removed: a Clear covering the picture on that conductor
    /// alone, so only fills drawn there remain. One undo step.</summary>
    public void RemoveCopperRead(string layer)
    {
        if (Raster is not { } raster || CopperReadCount(layer) == 0) return;
        Correct(list =>
        {
            list.Add(ImageCorrection.Clear(ImageCorrection.Rectangle(0, 0, raster.Width, raster.Height), layer));
            return $"The copper read on {layer} removed; only copper drawn there remains.";
        });
    }

    // ── the outline (R-dfr30-1-6) and a line drawing (R-dfr30-1-7) ──────────────────────────────────────

    /// <summary>The board outline in the picture's pixels: the one drawn (or the corners'), else the one read; null.</summary>
    public IReadOnlyList<PixelPoint>? OutlinePx
    {
        get
        {
            if (_trace is not { } t) return null;
            if (t.Corrections.LastOrDefault(o => o.Applied && o.Correction.Kind == ImageCorrectionKind.Outline) is { } drawn)
                return drawn.Correction.Points;
            if (t.Frame is not { } frame) return null;
            var read = t.Layers.Where(l => l.Role == ImageLayerRole.BoardOutline).SelectMany(l => l.Shapes).OfType<PathShape>()
                        .OrderByDescending(p => p.Xy.Length).FirstOrDefault();
            if (read is null || read.Xy.Length < 6) return null;
            return [.. Enumerable.Range(0, read.Xy.Length / 2).Select(i => frame.ToPixel(read.Xy[2 * i], read.Xy[2 * i + 1]))
                                 .Select(p => new PixelPoint(p.X, p.Y))];
        }
    }

    /// <summary>The line drawing's one note, or "" — with what the dialog offers for one: crop to the board, paint its copper in.</summary>
    public string LineArtNote => IsLayoutPicture && _trace is { LineArt: true } ? ImageTrace.LineArtNote + " " + LineArtFillHint : "";

    public const string LineArtFillHint =
        "Crop to the board first if the picture holds more than the board, and draw its Outline so its size can be checked.";

    /// <summary>The copper picker's one line on reshaping a traced region (R-dfr30-1-5, D1 (a)).</summary>
    public const string ReshapeHint =
        "To reshape it: Not copper, then draw a Copper fill — or reshape it after Create in the layout editor.";

    /// <summary>Called when a trace lands: the picker's rows, the counts, the outline and the note.</summary>
    private void OnTraceLandedForCorrections()
    {
        RefreshDrillChoices();
        RaiseRemoveCounts();
        OnPropertyChanged(nameof(OutlinePx));
        OnPropertyChanged(nameof(LineArtNote));
        if (CorrectionRows.Count != Corrections.Count || CorrectionRows.FirstOrDefault()?.Layers.SequenceEqual(ConductorChoices) == false)
            CorrectionsChanged();
    }
}
