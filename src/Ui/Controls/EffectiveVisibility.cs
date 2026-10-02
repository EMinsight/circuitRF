// brief-idle-power — tells a control when it starts or stops being effectively visible (itself and every ancestor visible).
//
// Avalonia has the event, Visual.IsEffectivelyVisibleChanged, but it is INTERNAL (Avalonia.Base 12.1.0). So: every
// Visual.IsVisibleProperty change in the application re-checks the watched visuals (its class notification is raised after
// Visual.OnPropertyChanged has updated the effective visibility of the whole subtree), and so does each watched visual's own
// attach and detach, which change it with no IsVisible changing. Each check is one property read per watched visual.
//
// It matters because Dock keeps every open document's view ATTACHED (DockDocumentControlCachedContentTemplate, in
// Styles/CircuitRfStyles.axaml) and hides the inactive ones with IsVisible: a background tab is not detached, only hidden.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Reactive;

namespace CircuitRF.Ui.Controls;

internal static class EffectiveVisibility
{
    private sealed class Watch(Action<bool> changed) { public bool Last = true; public readonly Action<bool> Changed = changed; }

    private static readonly ConditionalWeakTable<Visual, Watch> Watches = [];

    static EffectiveVisibility()
        => Visual.IsVisibleProperty.Changed.Subscribe(new AnonymousObserver<AvaloniaPropertyChangedEventArgs<bool>>(_ => CheckAll()));

    /// <summary>
    /// Calls <paramref name="changed"/> with <see cref="Visual.IsEffectivelyVisible"/> each time it changes, for as long as
    /// <paramref name="visual"/> lives. One watch per visual; a second call replaces nothing and is ignored.
    /// </summary>
    internal static void Observe(Visual visual, Action<bool> changed)
    {
        if (Watches.TryGetValue(visual, out _)) return;
        Watches.Add(visual, new Watch(changed) { Last = visual.IsEffectivelyVisible });
        visual.AttachedToVisualTree += (_, _) => Check(visual);
        visual.DetachedFromVisualTree += (_, _) => Check(visual);
    }

    private static void CheckAll()
    {
        List<Visual>? moved = null;
        foreach (var (v, w) in Watches)
            if (v.IsEffectivelyVisible != w.Last) (moved ??= []).Add(v);
        if (moved is not null) foreach (var v in moved) Check(v);   // not while enumerating: a callback may add a watch
    }

    private static void Check(Visual visual)
    {
        if (!Watches.TryGetValue(visual, out var w)) return;
        bool now = visual.IsEffectivelyVisible;
        if (now == w.Last) return;
        w.Last = now;
        w.Changed(now);
    }
}
