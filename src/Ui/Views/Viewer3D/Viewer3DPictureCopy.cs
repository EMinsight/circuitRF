using Avalonia.Controls;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Views.Viewer3D;

/// <summary>
/// brief-em3d-29 R-em3d29-5 — the 3D context menu's Copy: the view drawn offscreen by the GPU at
/// <see cref="Viewer3DViewModel.CopyScale"/>× the pane's device pixels, read back (Export picture's own path), the
/// legend and caption painted on as the export options say, and put on the clipboard as an image. 3D editor bugs
/// round 4: ONE copy, shared by the read-only viewer and the .c3d editor — the editor's menu had been built with no
/// picture commands at all, so right-clicking its canvas offered nothing to copy.
/// </summary>
public static class Viewer3DPictureCopy
{
    /// <summary>The menu's word for it — plain "Copy", as every other editor's canvas says it; the multiple is the
    /// status line's to report, not the menu's.</summary>
    public const string Header = "Copy";

    /// <summary>The Copy item, copying <paramref name="pane"/>'s picture; what happened is handed to <paramref name="report"/>.</summary>
    public static MenuItem Item(Control pane, Func<Viewer3DViewModel?> vm, Action<string> report)
    {
        var copy = new MenuItem { Header = Header };
        copy.Click += async (_, _) => { if (vm() is { } v) await CopyAsync(pane, v, report); };
        return copy;
    }

    /// <summary>The pane's size in DEVICE pixels — what a picture's multiple is of.</summary>
    public static (int W, int H) PanePixels(Control pane)
    {
        double scale = TopLevel.GetTopLevel(pane)?.RenderScaling ?? 1;
        return ((int)Math.Ceiling(pane.Bounds.Width * scale), (int)Math.Ceiling(pane.Bounds.Height * scale));
    }

    /// <summary>
    /// The view at <paramref name="scale"/> × the pane: the GPU's pixels, with the overlay's chrome — the axis indicator and
    /// the scale bar as the toolbar has them, a measurement, the selection's highlight — painted at the picture's size to
    /// lay over them (3D editor bugs round 6: a picture had been the GPU's pixels alone). Copy and Export Picture… both.
    /// </summary>
    public static FieldPictureShot? Capture(Control pane, Viewer3DViewModel vm, int scale, out string? error, bool transparent = false)
    {
        var (w, h) = PanePixels(pane);
        var shot = vm.CapturePicture(w, h, scale, out error, transparent);
        return shot is null ? null : shot with { Layer = Viewer3DOverlay.PictureLayer(vm, pane.Bounds.Width, shot.Width, shot.Height) };
    }

    /// <summary>The composing runs off the UI thread; only the read-back and the overlay hold it.</summary>
    public static async Task CopyAsync(Control pane, Viewer3DViewModel vm, Action<string> report)
    {
        var shot = Capture(pane, vm, Viewer3DViewModel.CopyScale, out string? error);
        if (shot is null) { report("The picture could not be copied: " + error); return; }
        report("Copying the picture…");
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var pixels = shot.Compose();
                return Clipboard.ImageClipboard.FromSkia(pixels);
            });
            bool done = await Clipboard.ImageClipboard.SetAsync(pane, bitmap);
            report(done
                ? $"Copied the view at {shot.Width:N0} × {shot.Height:N0} pixels" +
                  (shot.Scale < Viewer3DViewModel.CopyScale ? $" ({shot.Scale:0.##}× the window: a picture's side is at most {FieldPicture.MaxSide:N0} pixels)." : ".")
                : "The picture could not be copied: this window has no clipboard.");
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            report("The picture could not be copied: " + ex.Message);
        }
    }
}
