using Avalonia.Controls;
using Avalonia.Data;
using CircuitRF.Ui.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests.Controls;

/// <summary>
/// brief-idle-power — an indeterminate bar that cannot be seen is not <c>:indeterminate</c>, so the theme's animation (which
/// keeps Avalonia rendering every frame however hidden the bar is) is not running; seen again, it is. The bar's own value —
/// the XAML's, or a binding's — is never written. The gate is a module initializer: it is installed by touching the assembly.
/// </summary>
public sealed class IndeterminateBarGateTests
{
    [Fact]
    public void AHiddenAncestor_StopsTheBarsAnimation_AndShowingItAgainRestartsIt_WithoutTouchingTheBarsOwnValue()
    {
        var bar = new ProgressBar { IsIndeterminate = true };
        var row = new Border { Child = bar };
        var panel = new StackPanel { Children = { row } };
        Assert.True(bar.Classes.Contains(":indeterminate"));
        Assert.False(IndeterminateBarGate.IsHeld(bar));

        row.IsVisible = false;
        Assert.False(bar.IsIndeterminate);
        Assert.False(bar.Classes.Contains(":indeterminate"));
        Assert.True(bar.GetBaseValue(ProgressBar.IsIndeterminateProperty).GetValueOrDefault());

        panel.IsVisible = false;                                  // a second hidden ancestor changes nothing
        row.IsVisible = true;
        Assert.False(bar.Classes.Contains(":indeterminate"));

        panel.IsVisible = true;
        Assert.True(bar.IsIndeterminate);
        Assert.True(bar.Classes.Contains(":indeterminate"));
        Assert.False(IndeterminateBarGate.IsHeld(bar));
    }

    [Fact]
    public void ABoundValue_ChangedWhileHidden_IsWhatShowsWhenTheBarIsSeenAgain()
    {
        var bar = new ProgressBar();
        var row = new Border { Child = bar, IsVisible = false };
        bar.IsIndeterminate = true;                               // indeterminate while already hidden: never animates
        Assert.False(bar.Classes.Contains(":indeterminate"));

        bar.SetValue(ProgressBar.IsIndeterminateProperty, false, BindingPriority.LocalValue);
        row.IsVisible = true;
        Assert.False(bar.IsIndeterminate);                        // the run finished meanwhile: no animation comes back
        Assert.False(IndeterminateBarGate.IsHeld(bar));
    }
}

/// <summary>brief-idle-power — Dock hides a background document with IsVisible on an ANCESTOR; the watcher sees it.</summary>
public sealed class EffectiveVisibilityTests
{
    [Fact]
    public void AnAncestorsIsVisible_ReachesTheWatcher_OncePerChange()
    {
        var pane = new Border();
        var tab = new Border { Child = new Border { Child = pane } };
        var seen = new System.Collections.Generic.List<bool>();
        EffectiveVisibility.Observe(pane, seen.Add);

        tab.IsVisible = false;
        tab.IsVisible = false;
        tab.IsVisible = true;
        Assert.Equal([false, true], seen);
    }
}
