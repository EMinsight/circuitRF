using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace CircuitRF.Ui;

/// <summary>
/// "Copy Path" and "Copy Full Path" — the document tab's menu and the project tree's, stated once.
///
/// <para><b>Copy Path is the ENCLOSING folder</b>: the full path with its last part removed, whether
/// that part is a file name or a folder's own name. A cell row therefore copies the folder the cell
/// sits in, which is the same answer a file row gives — the place to go to find it. Copy Full Path is
/// the path itself.</para>
///
/// <para>The clipboard is reached through whichever window is active rather than one handed in: the
/// tab menu is one shared instance whose command is static, and a torn-off document's window is a
/// different TopLevel from the shell's. The clipboard is the platform's, so any of them writes the
/// same text.</para>
/// </summary>
public static class PathClipboard
{
    /// <summary>The text Copy Path puts on the clipboard — the folder holding <paramref name="path"/>,
    /// or the path itself when it has no parent (a volume root).</summary>
    public static string EnclosingFolder(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetDirectoryName(trimmed) is { Length: > 0 } parent ? parent : trimmed;
    }

    /// <summary>Puts the path — or, when <paramref name="full"/> is false, its enclosing folder — on
    /// the clipboard. A null or empty path does nothing.</summary>
    public static Task CopyAsync(string? path, bool full)
        => string.IsNullOrEmpty(path) ? Task.CompletedTask : SetTextAsync(full ? path : EnclosingFolder(path));

    private static async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var window = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        if (window is null || TopLevel.GetTopLevel(window)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch (Exception) { /* a clipboard the platform refused is not worth an error dialog */ }
    }
}
