using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using SkiaSharp;

namespace CircuitRF.Ui.Views.Viewer3D;

/// <summary>
/// 3D vector copy and drawing export (2026-09-27) — the 3D view's picture in VECTOR form, three ways: Copy as Vector (the
/// current view to the clipboard), Export as Vector… (the same picture to an SVG or PDF) and Export Drawing… (several views
/// and sections on a sheet). Shared by the .c3d editor and a setup's read-only 3D view, and by File ▸ Export ▸ Drawing….
///
/// <para><b>What the picture is.</b> Not the GPU's pixels: the problem the pane draws (<see cref="Scene3DModel.Problem"/>)
/// as an OUTLINE along the camera's direction (Em3dSectionScene.Outline), hidden edges removed — what a shaded view shows,
/// in lines. A perspective camera's picture is in perspective, from the camera's own eye, at the scale the view has at its
/// orbit centre; an orthographic camera's is orthographic. What the view hides is left out.</para>
///
/// <para><b>The clipboard is the one every other vector copy here writes</b> (WBondClipboardWriter's recipe): on Windows one
/// P/Invoke session with CF_ENHMETAFILE first, built from the SVG by WindowsClipboard's proven SVG-to-EMF route; elsewhere one
/// DataTransfer carrying the native PDF and SVG types and a PNG. Labels are outlines, so the EMF's and the SVG's text cannot
/// come out in a substitute font.</para>
/// </summary>
public static class Viewer3DVectorExport
{
    public const string CopyHeader = "Copy as Vector";
    public const string ExportHeader = "Export as Vector…";
    public const string DrawingHeader = "Export Drawing…";

    /// <summary>The problem the pane draws, or why there is none.</summary>
    internal static (Em3dProblem? Problem, string? Why) ProblemOf(Viewer3DViewModel vm)
        => vm.Scene.Problem is { } p && p.Solids.Count + p.Sheets.Count > 0
            ? (p, null) : (null, "the 3D view has nothing drawn yet.");

    /// <summary>The names of what the view does not show: hidden objects, and a pushed-in view's context. A name any shown
    /// object also carries is kept.</summary>
    internal static IReadOnlySet<string> HiddenNames(Viewer3DViewModel vm)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var visible = vm.View.Visible;
        foreach (var o in vm.Scene.Objects)
        {
            bool on = o.Id >= 1 && o.Id <= visible.Length && visible[o.Id - 1];
            (o.Context || !on ? hidden : shown).Add(o.Name);
        }
        hidden.ExceptWith(shown);
        return hidden;
    }

    /// <summary>Each object's colour as the view draws it.</summary>
    internal static IReadOnlyDictionary<string, SKColor> Colours(Scene3DModel scene)
    {
        var map = new Dictionary<string, SKColor>(StringComparer.Ordinal);
        foreach (var o in scene.Objects)
            map.TryAdd(o.Name, new SKColor((byte)(o.Rgba & 0xFF), (byte)((o.Rgba >> 8) & 0xFF), (byte)((o.Rgba >> 16) & 0xFF)));
        return map;
    }

    /// <summary>The camera as a projection: its direction, and for a perspective camera its eye — so the picture has the
    /// view's own foreshortening, not an orthographic view along the same direction.</summary>
    internal static Em3dProjection CameraProjection(Viewer3DViewModel vm)
    {
        var c = vm.View.Camera;
        var (b, r, u) = (c.Back, c.Right, c.Up);
        var projection = Em3dProjection.FromVectors(new Point3(b.X, b.Y, b.Z), new Point3(r.X, r.Y, r.Z), new Point3(u.X, u.Y, u.Z), "View");
        if (c.Projection != Projection3D.Perspective || !(c.Distance > 0)) return projection;
        var (x, y, z) = vm.Scene.ToWorld(c.Eye);
        return projection.WithEye(new Point3(x, y, z), c.Distance);
    }

    /// <summary>
    /// 3D editor bugs round 6 — what the camera sees, in the picture plane: the window about its target, as tall as the
    /// orthographic projection's (Camera3D.ProjectionMatrix) and as wide as the pane's aspect makes it. For a perspective
    /// camera it is the window at the orbit centre — the plane the scale bar is measured in.
    /// </summary>
    internal static Em3dPictureWindow Window(Viewer3DViewModel vm, double aspect)
    {
        var c = vm.View.Camera;
        var (x, y, z) = vm.Scene.ToWorld(c.Target);
        double half = c.Distance * Math.Tan((c.FovY > 0 && c.FovY < MathF.PI ? c.FovY : Camera3D.DefaultFovY) * 0.5);
        return new Em3dPictureWindow(CameraProjection(vm).Project(new Point3(x, y, z)), half * aspect, half);
    }

    /// <summary>The page's longer side, in points (≈10 in, the size a pasted picture lands at).</summary>
    private const float MaxSide = 720f;

    /// <summary>The current view — what the pane (<paramref name="paneW"/> × <paramref name="paneH"/> DIPs) shows, at its
    /// zoom, with the axis indicator and the scale bar as the toolbar has them — as a vector picture, or null with
    /// <paramref name="why"/>.</summary>
    public static Em3dVectorPicture? Picture(Viewer3DViewModel vm, double paneW, double paneH, out string? why)
    {
        var (problem, refusal) = ProblemOf(vm);
        why = refusal;
        if (problem is null) return null;
        var theme = ThemeService.Active;
        paneW = Math.Max(1, paneW); paneH = Math.Max(1, paneH);
        var window = Window(vm, paneW / paneH);
        double metresPerDip = 2 * window.HalfHeight / paneH;
        double bar = Viewer3DOverlay.ScaleBarLength(Viewer3DOverlay.ScaleBarTarget * metresPerDip, vm.MeasureUnit, vm.MeasureDbuPerMicron);
        var chrome = new Em3dPictureChrome
        {
            PagePerDip = MaxSide / Math.Max(paneW, paneH),
            AxisIndicator = vm.ShowAxisIndicator,
            ScaleBar = vm.ShowScaleLegend && bar > 0 ? (bar, vm.FormatLength(bar)) : null,
        };
        return Em3dDrawingExport.Picture(problem, CameraProjection(vm), Colours(vm.Scene), theme, HiddenNames(vm), maxSide: MaxSide,
                                         window: window, chrome: chrome);
    }

    public static MenuItem CopyItem(Control pane, Func<Viewer3DViewModel?> vm, Action<string> report)
    {
        var item = new MenuItem { Header = CopyHeader };
        ToolTip.SetTip(item, "The view as lines — silhouettes and sharp edges, hidden edges removed — framed as the view is, for a document or a slide. On Windows it pastes as a metafile.");
        item.Click += async (_, _) => { if (vm() is { } v) await CopyAsync(pane, v, report); };
        return item;
    }

    /// <summary>Copy as Vector.</summary>
    public static async Task CopyAsync(Control pane, Viewer3DViewModel vm, Action<string> report)
    {
        report("Copying the view as vector…");
        Em3dVectorPicture? picture = null;
        string? svg = null; byte[]? pdf = null; Bitmap? bitmap = null; string? why = null;
        try
        {
            picture = Picture(vm, pane.Bounds.Width, pane.Bounds.Height, out why);
            if (picture is not null)
            {
                var p = picture;
                (svg, pdf, bitmap) = await Task.Run(() =>
                {
                    string s = p.Svg();
                    byte[] d = p.Pdf();
                    using var ms = new MemoryStream(p.Png(2f));
                    return (s, d, new Bitmap(ms));
                });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or OutOfMemoryException)
        {
            why = ex.Message;
        }
        if (picture is null || svg is null) { report("The view could not be copied as vector: " + why); return; }

        if (OperatingSystem.IsWindows())
        {
            const float maxSide = 720f;   // ≈10 in at 72 pt/in, the size WBondClipboardWriter pastes at
            float scale = MathF.Min(1f, maxSide / MathF.Max(picture.Width, picture.Height));
            IntPtr hwnd = TopLevel.GetTopLevel(pane)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WindowsClipboard.SetClipboard(hwnd, pdf, svg, null, bitmap, picture.Width * scale, picture.Height * scale);
        }
        else
        {
            if (TopLevel.GetTopLevel(pane)?.Clipboard is not { } clipboard) { report("The view could not be copied: this window has no clipboard."); return; }
            var item = new DataTransferItem();
            if (pdf is not null) item.Set(ClipboardFormats.PdfNativeMacFormat, pdf);
            item.Set(ClipboardFormats.SvgNativeFormat, Encoding.UTF8.GetBytes(svg));
            if (bitmap is not null) item.Set(DataFormat.Bitmap, bitmap);
            var transfer = new DataTransfer();
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
        }
        report($"Copied the view as vector ({picture.Scene.Lines.Count:N0} lines, " +
               (vm.View.Camera.Projection == Projection3D.Perspective ? "perspective" : "orthographic") + ", hidden edges removed)." +
               (picture.Note is { } note ? " " + note : ""));
    }

    public static MenuItem ExportItem(Func<Window?> owner, Control pane, Func<Viewer3DViewModel?> vm, Func<string> documentPath, Action<string> report)
    {
        var item = new MenuItem { Header = ExportHeader };
        ToolTip.SetTip(item, "The view as lines, to an SVG or PDF file.");
        item.Click += async (_, _) => { if (owner() is { } w && vm() is { } v) await ExportAsync(w, pane, v, documentPath(), report); };
        return item;
    }

    /// <summary>Export as Vector….</summary>
    public static async Task ExportAsync(Window owner, Control pane, Viewer3DViewModel vm, string documentPath, Action<string> report)
    {
        var picture = Picture(vm, pane.Bounds.Width, pane.Bounds.Height, out string? why);
        if (picture is null) { report("The view could not be exported: " + why); return; }
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export as Vector",
            // NO extension on the suggested name: the storage provider appends DefaultExtension itself (SuggestedFileNameGateTests).
            SuggestedFileName = Path.GetFileNameWithoutExtension(documentPath) + "-view",
            DefaultExtension = "svg",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("SVG") { Patterns = ["*.svg"] },
                new FilePickerFileType("PDF") { Patterns = ["*.pdf"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } target) return;
        bool pdf = string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase);
        byte[] bytes = await Task.Run(() => pdf ? picture.Pdf() : Encoding.UTF8.GetBytes(picture.Svg()));
        await File.WriteAllBytesAsync(target, bytes);
        report($"Exported the view as {Path.GetFileName(target)}." + (picture.Note is { } note ? " " + note : ""));
    }

    public static MenuItem DrawingItem(Func<Window?> owner, Func<Viewer3DViewModel?> vm, Func<string> documentPath, Action<string> report)
    {
        var item = new MenuItem { Header = DrawingHeader };
        ToolTip.SetTip(item, "A drawing sheet: isometric, top, front … views and sections at one scale, as SVG or PDF.");
        item.Click += async (_, _) => { if (owner() is { } w && vm() is { } v) await ExportDrawingAsync(w, v, documentPath(), report); };
        return item;
    }

    /// <summary>Export Drawing…: the dialog, where to write it, then the sheet. <paramref name="report"/> hears what was
    /// written, or why nothing was.</summary>
    public static async Task ExportDrawingAsync(Window owner, Viewer3DViewModel vm, string documentPath, Action<string> report)
    {
        var (problem, why) = ProblemOf(vm);
        if (problem is null) { report("No drawing: " + why); return; }
        var (min, max) = Em3dSectionScene.Frame(problem, 0);
        var dialogVm = new Drawing3DDialogViewModel(Path.GetFileName(documentPath), min, max, Drawing3DPreference.Preferred)
        {
            HiddenInView = HiddenNames(vm),
        };
        bool ok = await new CircuitRF.Ui.Views.ThreeD.Drawing3DDialog(dialogVm).ShowDialog<bool>(owner);
        if (!ok || dialogVm.Request is not { } request) return;
        Drawing3DPreference.Preferred = dialogVm.Choices(request);

        string ext = request.Format == Em3dDrawingFormat.Svg ? "svg" : "pdf";
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Drawing",
            SuggestedFileName = Path.GetFileNameWithoutExtension(documentPath) + "-drawing",
            DefaultExtension = ext,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = ["*." + ext] }],
        });
        if (file?.TryGetLocalPath() is not { } target) return;

        var colours = Colours(vm.Scene);
        var theme = ThemeService.Active;
        try
        {
            var (bytes, notes, layout) = await Task.Run(() =>
            {
                var b = Em3dDrawingExport.Sheet(problem, colours, theme, request, out var n, out var l);
                return (b, n, l);
            });
            await File.WriteAllBytesAsync(target, bytes);
            int views = request.Views.Count + request.Sections.Count;
            report($"Exported the drawing as {Path.GetFileName(target)}: {views} view{(views == 1 ? "" : "s")} at {layout.ScaleText}." +
                   (notes.Count > 0 ? " " + string.Join(" ", notes) : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            report("The drawing could not be written: " + ex.Message);
        }
    }
}
