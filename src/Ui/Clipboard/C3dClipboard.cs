using Avalonia.Input.Platform;

namespace CircuitRF.Ui.Clipboard;

/// <summary>
/// brief-em3d-95 — the system clipboard for a 3D copy: the <c>C3dFragment</c> JSON as text, so it pastes between two
/// windows, two workspaces and two running instances. Nothing rides alongside as a picture (a picture of a 3D view is the
/// pane's own Copy). Clipboard traffic ONLY — what a copy carries and what a paste means are <c>C3dFragment</c>'s, in
/// src/Design, which this file never duplicates (the <see cref="LayoutClipboard"/> / <c>LayoutFragment</c> split).
/// </summary>
public static class C3dClipboard
{
    public static async Task WriteAsync(IClipboard? clipboard, string text)
    {
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(text); }
        catch { /* a clipboard the platform refuses leaves the copy in the view model, where this window can still paste it */ }
    }

    /// <summary>The clipboard's text, or null when it holds none (or cannot be read).</summary>
    public static async Task<string?> ReadAsync(IClipboard? clipboard)
    {
        if (clipboard is null) return null;
        try { return await clipboard.TryGetTextAsync(); }
        catch { return null; }
    }
}
