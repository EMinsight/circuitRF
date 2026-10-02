// brief-em3d-99 — make a CompositionDrawingSurface update draw in the render that applies it, not the one after.
//
// Avalonia 12.0.3's ServerCompositor.RenderCore runs, in order: the global passes (apply batches, ..., the visuals' "own
// properties" pass, which is where a changed visual's dirty flags propagate to the root), THEN the server jobs, then each
// target's Update (dirty rects) and Render. A surface update is a server job: its Changed callback invalidates the surface
// visual's content and ENQUEUES it for the own-properties pass, which has already run. So the new image is drawn by the
// NEXT render, whichever thread runs it. During a live window resize on macOS that next render is Avalonia's render thread,
// between two synchronous paints: it blocked in Metal's BeginRenderingSession waiting for a drawable while holding the
// compositor lock the next paint needed — once per resize step, which was the stutter (src/Ui/RESOLVED.md, 2026-10-02).
//
// The fix posts one more server job straight after the update, in the same batch, that runs the own-properties pass again.
// Jobs run in order, so the visual's dirty flags reach the root before Update collects dirty rects, and the render that
// applied the image draws it. Nothing is left queued for another render. Both members are internal to Avalonia (checked
// unchanged in 12.1.0); if either is missing the flush is simply skipped and the update draws one render late, as before.

using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Rendering.Composition;

namespace CircuitRF.Ui.Viewer3D;

internal static class SurfaceUpdateFlush
{
    private static readonly MethodInfo? PostServerJob =
        typeof(Compositor).GetMethod("PostServerJob", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Action), typeof(bool)]);
    private static readonly PropertyInfo? Server =
        typeof(Compositor).GetProperty("Server", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? OwnPropertiesPass =
        Server?.PropertyType.GetMethod("VisualOwnPropertiesUpdatePass", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);

    private static readonly ConditionalWeakTable<Compositor, Action> Passes = new();

    /// <summary>True when this Avalonia exposes what the flush needs (a test holds it, so an upgrade that renames either
    /// member is noticed rather than silently bringing the stutter back).</summary>
    internal static bool Available => PostServerJob is not null && Server is not null && OwnPropertiesPass is not null;

    /// <summary>Call on the UI thread right after a surface update was posted to <paramref name="compositor"/>.</summary>
    public static void AfterUpdate(Compositor compositor)
    {
        if (!Available) return;
        var pass = Passes.GetValue(compositor, c => (Action)Delegate.CreateDelegate(typeof(Action), Server!.GetValue(c)!, OwnPropertiesPass!));
        PostServerJob!.Invoke(compositor, [pass, false]);
    }
}
