// Create Layout from Image — correcting the reading by hand before Create. brief-dfr29-1-picture-to-layout-by-hand.md
// R-dfr29-1-2, R-dfr29-1-3, R-dfr29-1-5.
//
// Every choice here is one ImageCorrection (Design) — the record the CLI's --via/--hole/--not-via/--copper/--clear/
// --outline flags build — kept on the picture and laid over every re-read, as a schematic picture's crossing overrides
// are. Each choice is one undo step (Cmd/Ctrl+Z on the canvas). The dialog states no stackup: the picture is read with the
// chosen technology as it is (owner decision, 2026-10-27).
//
// IT DECIDES NOTHING: what a correction does, and whether it lands on anything, is ImageTrace.Build's.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;

namespace CircuitRF.Ui.Recognition;

/// <summary>The canvas's drawing tools on a layout picture (R-dfr29-1-3).</summary>
public enum PictureDrawTool { None, Via, Copper, Outline }

public sealed partial class ImageSourceViewModel
{
    /// <summary>A hole whose radius is within this share of the one chosen is "one like it" (R-dfr29-1-2).</summary>
    public const double LikeHoleShare = 0.10;

    /// <summary>The corrections made on a layout picture, in its pixels, in the order made.</summary>
    public ObservableCollection<ImageCorrection> Corrections { get; } = [];

    private readonly List<ImageCorrection[]> _correctionUndo = [];

    /// <summary>Any correction is held — of a schematic picture's crossings or a layout picture's pieces.</summary>
    public bool HasCorrections => Overrides.Count > 0 || Corrections.Count > 0;

    /// <summary>The corrections, one undo step: <paramref name="change"/> applied to a copy, and kept only when it differs.</summary>
    private bool Correct(Func<List<ImageCorrection>, string?> change)
    {
        var before = Corrections.ToArray();
        var list = before.ToList();
        string? said = change(list);
        if (list.SequenceEqual(before)) { if (said is not null) Status = said; return false; }
        _correctionUndo.Add(before);
        Corrections.Clear();
        foreach (var c in list) Corrections.Add(c);
        Status = said ?? "";
        ScheduleRecognition();
        return true;
    }

    /// <summary>Cmd/Ctrl+Z on the canvas: the last correction step undone.</summary>
    public bool UndoCorrection()
    {
        if (_correctionUndo.Count == 0) return false;
        var back = _correctionUndo[^1];
        _correctionUndo.RemoveAt(_correctionUndo.Count - 1);
        Corrections.Clear();
        foreach (var c in back) Corrections.Add(c);
        ScheduleRecognition();
        return true;
    }

    private void ForgetCorrections()
    {
        Corrections.Clear();
        _correctionUndo.Clear();
        DrawPoints.Clear();
        DrawTool = PictureDrawTool.None;
    }

    // ── a hole or a copper region clicked: what it is (R-dfr29-1-2) ─────────────────────────────────

    /// <summary>The hole the selection is — a drill, or a round hole kept open or made non-plated — or null.</summary>
    public (double X, double Y, double R)? SelectedHole =>
        IsLayoutPicture && SelectedItem is { Class: ImageOverlayClass.Drill, Circle: { } c } item
        && !item.Id.StartsWith(ImageOverlay.CorrectionPrefix, StringComparison.Ordinal) ? c : null;

    /// <summary>The copper region the selection is, or null.</summary>
    public ImageOverlayItem? SelectedCopper =>
        IsLayoutPicture && SelectedItem is { Class: ImageOverlayClass.Copper } item && item.Id.StartsWith("copper:", StringComparison.Ordinal) ? item : null;

    public bool HolePickerShown => SelectedHole is not null;
    public bool CopperPickerShown => SelectedCopper is not null;

    /// <summary>The hole picker's caption: the hole's size and what it is now.</summary>
    public string HolePickerCaption => SelectedHole is not { } h ? ""
        : $"Hole ⌀{(Overlay.Frame is { } f ? ImageScale.FormatLength(2 * h.R * 0.5 * (f.ScaleX + f.ScaleY) / (TraceDbuPerMicron * 1e6)) : $"{2 * h.R:0.#} px")}" +
          $" — {KindOf(h) switch { ImageHoleKind.Via => "a via", ImageHoleKind.NonPlated => "not plated", _ => "an opening" }}";

    /// <summary><i>And every one like it</i>: the choice applies to every hole of the same diameter within 10 %.</summary>
    [ObservableProperty] private bool _everyOneLikeIt = true;

    /// <summary>The conductor a copper region is said to be on — a picker of the technology's conductors, never typed.</summary>
    [ObservableProperty] private string? _pickerLayer;

    /// <summary>The technology's conductors, top first.</summary>
    public IReadOnlyList<string> ConductorChoices => _technology is { } t ? ImageTrace.ConductorLayerNames(t) : [];

    private int TraceDbuPerMicron => _trace?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;

    private ImageHoleKind KindOf((double X, double Y, double R) h) =>
        _trace?.Holes.FirstOrDefault(t => Math.Abs(t.Circle.Cx - h.X) <= 0.5 && Math.Abs(t.Circle.Cy - h.Y) <= 0.5)?.Kind ?? ImageHoleKind.Via;

    private void OnSelectionForPickers()
    {
        OnPropertyChanged(nameof(SelectedHole));
        OnPropertyChanged(nameof(SelectedCopper));
        OnPropertyChanged(nameof(HolePickerShown));
        OnPropertyChanged(nameof(CopperPickerShown));
        OnPropertyChanged(nameof(HolePickerCaption));
        OnPropertyChanged(nameof(ConductorChoices));
        if (SelectedCopper is { } copper)
        {
            string? own = copper.Id.Split(':') is [_, var layer, _] ? layer : null;
            PickerLayer = ConductorChoices.Contains(own) ? own : ConductorChoices.FirstOrDefault();
        }
    }

    [RelayCommand] private void HoleIsVia() => ChooseHole(ImageCorrectionKind.Via);
    [RelayCommand] private void HoleIsNotPlated() => ChooseHole(ImageCorrectionKind.Hole);
    [RelayCommand] private void HoleIsOpening() => ChooseHole(ImageCorrectionKind.NotVia);

    /// <summary>The selected hole — and, with <see cref="EveryOneLikeIt"/>, every hole of its diameter — said to be
    /// <paramref name="kind"/>: one correction per hole, one undo step for them all.</summary>
    public void ChooseHole(ImageCorrectionKind kind)
    {
        if (SelectedHole is not { } chosen) return;
        var want = kind switch { ImageCorrectionKind.Via => ImageHoleKind.Via, ImageCorrectionKind.Hole => ImageHoleKind.NonPlated, _ => ImageHoleKind.Opening };
        var targets = EveryOneLikeIt && _trace is { } t
            ? [.. t.Holes.Select(h => (h.Circle.Cx, h.Circle.Cy, h.Circle.R)).Where(h => Math.Abs(h.R - chosen.R) <= LikeHoleShare * chosen.R)]
            : new List<(double Cx, double Cy, double R)> { chosen };
        Correct(list =>
        {
            int changed = 0;
            foreach (var (x, y, r) in targets)
            {
                int had = list.RemoveAll(c => c.IsPoint && Math.Abs(c.At.X - x) <= r && Math.Abs(c.At.Y - y) <= r);
                if (had == 0 && KindOf((x, y, r)) == want) continue;
                list.Add(new ImageCorrection(kind, [new PixelPoint(Math.Round(x, 2), Math.Round(y, 2))]));
                changed++;
            }
            string what = want switch { ImageHoleKind.Via => "vias", ImageHoleKind.NonPlated => "non-plated holes", _ => "openings" };
            return changed == 0 ? $"Every one of those is already one of the {what}." : null;
        });
    }

    [RelayCommand]
    private void RegionIsCopper()
    {
        if (SelectedCopper is not { Paths: [{ Count: >= 3 } outer, ..] } || PickerLayer is not { } layer) return;
        Correct(list => { list.Add(ImageCorrection.Copper(Polygon(outer), layer)); return null; });
    }

    [RelayCommand]
    private void RegionIsNotCopper()
    {
        if (SelectedCopper is not { Paths: [{ Count: >= 3 } outer, ..] }) return;
        Correct(list => { list.Add(ImageCorrection.Clear(Polygon(outer))); return null; });
        SelectedItem = null;
    }

    private static IReadOnlyList<PixelPoint> Polygon(IReadOnlyList<(double X, double Y)> ring) =>
        [.. ring.Select(p => new PixelPoint(Math.Round(p.X, 2), Math.Round(p.Y, 2)))];

    // ── drawing on the picture: via, copper, outline (R-dfr29-1-3) ─────────────────────────────────

    [ObservableProperty] private PictureDrawTool _drawTool;

    /// <summary>The Copper tool's layer — the technology's conductors, top copper first.</summary>
    [ObservableProperty] private string? _drawLayer;

    /// <summary>The corners clicked so far of a polygon being drawn, picture pixels; a double-click closes it.</summary>
    public List<PixelPoint> DrawPoints { get; } = [];

    public bool ViaArmed
    {
        get => DrawTool == PictureDrawTool.Via;
        set => DrawTool = value ? PictureDrawTool.Via : DrawTool == PictureDrawTool.Via ? PictureDrawTool.None : DrawTool;
    }

    public bool CopperArmed
    {
        get => DrawTool == PictureDrawTool.Copper;
        set => DrawTool = value ? PictureDrawTool.Copper : DrawTool == PictureDrawTool.Copper ? PictureDrawTool.None : DrawTool;
    }

    public bool OutlineArmed
    {
        get => DrawTool == PictureDrawTool.Outline;
        set => DrawTool = value ? PictureDrawTool.Outline : DrawTool == PictureDrawTool.Outline ? PictureDrawTool.None : DrawTool;
    }

    /// <summary>The tool strip is offered once the picture is read as a layout with a scale.</summary>
    public bool DrawToolsShown => IsLayoutPicture && !HasFailure && Overlay.Frame is not null;

    partial void OnDrawToolChanged(PictureDrawTool value)
    {
        DrawPoints.Clear();
        OnPropertyChanged(nameof(ViaArmed));
        OnPropertyChanged(nameof(CopperArmed));
        OnPropertyChanged(nameof(OutlineArmed));
        if (value == PictureDrawTool.Copper && (DrawLayer is null || !ConductorChoices.Contains(DrawLayer))) DrawLayer = ConductorChoices.FirstOrDefault();
        RaiseCanvasPrompt();
    }

    partial void OnDrawLayerChanged(string? value) => RaiseCanvasPrompt();

    /// <summary>The tool's instruction on the canvas, or null.</summary>
    private string? DrawPrompt => DrawTool switch
    {
        PictureDrawTool.Via => "Click to place a via; a click on a hole places it at the hole's centre. Escape stops.",
        PictureDrawTool.Copper => $"Drag a rectangle, or click corners and double-click to close, to fill copper on {DrawLayer}. Escape stops.",
        PictureDrawTool.Outline => "Drag a rectangle, or click corners and double-click to close: the board's outline. Escape stops.",
        _ => null,
    };

    /// <summary>A click with the Via tool armed: a via there, at the centre of the hole under it when there is one.</summary>
    public void PlaceVia(PixelPoint p)
    {
        var hole = _trace?.Holes.Select(h => h.Circle)
                         .Where(c => (c.Cx - p.X) * (c.Cx - p.X) + (c.Cy - p.Y) * (c.Cy - p.Y) <= c.R * c.R)
                         .OrderBy(c => c.R).Cast<CircleFit?>().FirstOrDefault();
        var at = hole is { } c ? new PixelPoint(Math.Round(c.Cx, 2), Math.Round(c.Cy, 2)) : new PixelPoint(Math.Round(p.X, 2), Math.Round(p.Y, 2));
        double reach = hole?.R ?? 1;
        Correct(list =>
        {
            list.RemoveAll(c => c.IsPoint && Math.Abs(c.At.X - at.X) <= reach && Math.Abs(c.At.Y - at.Y) <= reach);
            list.Add(ImageCorrection.Via(at.X, at.Y));
            return null;
        });
    }

    /// <summary>A click with the Copper or Outline tool armed: one more corner of the polygon being drawn.</summary>
    public void AddDrawPoint(PixelPoint p)
    {
        if (DrawTool is not (PictureDrawTool.Copper or PictureDrawTool.Outline)) return;
        DrawPoints.Add(new PixelPoint(Math.Round(p.X, 2), Math.Round(p.Y, 2)));
    }

    /// <summary>A double-click: the polygon being drawn closed and kept, when it has three corners.</summary>
    public bool ClosePolygon()
    {
        if (DrawPoints.Count < 3) return false;
        var polygon = DrawPoints.ToArray();
        DrawPoints.Clear();
        return DrawShape(polygon);
    }

    /// <summary>A rectangle dragged with the Copper or Outline tool armed.</summary>
    public bool DrawRectangle(PixelPoint a, PixelPoint b) =>
        DrawShape(ImageCorrection.Rectangle(Math.Round(a.X, 2), Math.Round(a.Y, 2), Math.Round(b.X, 2), Math.Round(b.Y, 2)));

    private bool DrawShape(IReadOnlyList<PixelPoint> polygon) => DrawTool switch
    {
        PictureDrawTool.Copper when DrawLayer is { } layer => Correct(list => { list.Add(ImageCorrection.Copper(polygon, layer)); return null; }),
        // One outline: drawing another replaces it.
        PictureDrawTool.Outline => Correct(list =>
        {
            list.RemoveAll(c => c.Kind == ImageCorrectionKind.Outline);
            list.Add(ImageCorrection.Outline(polygon));
            return null;
        }),
        _ => false,
    };
}
