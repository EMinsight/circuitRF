// brief-em3d-98 — "is a window being resized or moved right now?", so background work that ends in a layout change (the
// "solved" checks and the glyphs they redraw) holds off until the window has been still for a moment. A live resize lays the
// whole window out on every frame, and anything else the UI thread does then shows as a stutter. Owner report, 2026-10-02:
// resizing a window with a .c3d open became chunky once the solved checks existed; no check is needed mid-resize.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Controls;

public static class WindowMotion
{
    /// <summary>How long a window must have been still before the held-back work goes ahead.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    private static long _last;          // 0: no window has moved since the machine started, which reads as long quiet
    private static int _installed;

    /// <summary>Once per process: every window's size and position changes are noted.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        TopLevel.ClientSizeProperty.Changed.AddClassHandler<Window>((_, _) => Mark());
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => w.PositionChanged += (_, _) => Mark());
    }

    public static void Mark() => Interlocked.Exchange(ref _last, Environment.TickCount64);

    /// <summary>Time since a window last changed size or moved.</summary>
    public static TimeSpan SinceLast => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _last));

    /// <summary>Returns once no window has resized or moved for <see cref="Quiet"/>.</summary>
    public static async Task WaitForQuietAsync(CancellationToken ct)
    {
        for (var since = SinceLast; since < Quiet; since = SinceLast)
            await Task.Delay(Quiet - since + TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
    }

    /// <summary>The same, blocking: for a worker thread of its own.</summary>
    public static void WaitForQuiet(CancellationToken ct)
    {
        for (var since = SinceLast; since < Quiet; since = SinceLast)
            if (ct.WaitHandle.WaitOne(Quiet - since + TimeSpan.FromMilliseconds(10))) ct.ThrowIfCancellationRequested();
    }
}
