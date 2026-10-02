// brief-idle-power — an indeterminate ProgressBar that cannot be seen animates nothing.
//
// Measured 2026-10-02 on the owner's Mac (src/Ui/RESOLVED.md): a still window with a solved .c3d open cost ~9.6 % CPU and
// ~42 idle wake-ups a second, with no legend and no field plot. A heap dump found two live AnimationInstance<double>
// objects on their 831st iteration: the two containers of ONE 48-px indeterminate bar, the "Building the 3D view…" one in
// C3dEditorView. It had been shown once while the first scene was built, then hidden by its Border's IsVisible, and it
// animated, invisibly, from then on.
//
// Two facts of Avalonia 12 make that so (decompiled, Avalonia.Base 12.1.0):
//   * AnimationInstance pauses on invisibility only when its TARGET is a Visual. The Fluent theme's indeterminate animation
//     targets the template's TranslateTransform, which is not, so it never pauses (and never sees a visual detach).
//   * Even a paused instance stays subscribed to its clock, and MediaContext renders every frame while
//     MediaContextClock.HasSubscriptions is true. So pausing would not have made the window idle either.
// The only way to stop it is for the style that runs the animation to stop applying: `ProgressBar:indeterminate`.
//
// So while an indeterminate bar is not effectively visible, IsIndeterminate is held false at BindingPriority.Animation (above
// the XAML's local value and any binding), and released the moment it can be seen again. The bar's own value is never
// written: GetBaseValue still reads what the XAML or the view model says. The trigger is EffectiveVisibility (Avalonia's own
// event is internal).
//
// A MODULE INITIALIZER for the reason UiVerilogACacheInstaller gives: src/Ui has three Main methods (circuitRF, harmonicaRF,
// wBond) and this must hold in all three with no startup ordering to get wrong.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Reactive;

namespace CircuitRF.Ui.Controls;

internal static class IndeterminateBarGate
{
    /// <summary>Every bar that has ever been indeterminate (weakly), with the hold in force on it.</summary>
    private static readonly ConditionalWeakTable<ProgressBar, Hold> Bars = [];

    private sealed class Hold { public IDisposable? Off; }

    private static bool _evaluating;

    [ModuleInitializer]
    internal static void Install()
    {
        ProgressBar.IsIndeterminateProperty.Changed.Subscribe(new AnonymousObserver<AvaloniaPropertyChangedEventArgs<bool>>(args =>
        {
            if (args.Sender is ProgressBar bar) Evaluate(bar);
        }));
    }

    /// <summary>True while the gate is holding this bar's animation off (tests).</summary>
    internal static bool IsHeld(ProgressBar bar) => Bars.TryGetValue(bar, out var h) && h.Off is not null;

    private static void Evaluate(ProgressBar bar)
    {
        if (_evaluating) return;                       // the hold's own change of IsIndeterminate
        bool wanted = bar.GetBaseValue(ProgressBar.IsIndeterminateProperty) is { HasValue: true, Value: true };
        if (!Bars.TryGetValue(bar, out var hold))
        {
            if (!wanted) return;
            hold = new Hold();
            Bars.Add(bar, hold);
            EffectiveVisibility.Observe(bar, _ => Evaluate(bar));
        }
        bool off = wanted && !bar.IsEffectivelyVisible;
        if (off == (hold.Off is not null)) return;
        _evaluating = true;
        try
        {
            if (off) hold.Off = bar.SetValue(ProgressBar.IsIndeterminateProperty, false, BindingPriority.Animation);
            else { hold.Off!.Dispose(); hold.Off = null; }
        }
        finally { _evaluating = false; }
    }
}
