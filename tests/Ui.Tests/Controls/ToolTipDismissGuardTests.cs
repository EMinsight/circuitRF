using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests.Controls;

/// <summary>
/// A click closes the tip it would leave behind a dialog, and holds that control's tooltip service off — for every
/// button in the application, not the one toolbar button that used to carry its own handler (3D editor round 6, then
/// the 3D Properties inspector's material Edit… button). Restoring it needs a pointer and a window, which no test here
/// has; that half was not exercised.
/// </summary>
public sealed class ToolTipDismissGuardTests
{
    [Fact]
    public void AClick_HoldsOffTheButtonsTip_OrItsNearestAncestorsWhenItHasNone_AndTouchesNothingWithoutATip()
    {
        ToolTipDismissGuard.Install();

        var own = new Button();
        ToolTip.SetTip(own, "Edit this material");
        own.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(ToolTip.GetServiceEnabled(own));

        var bare = new Button();
        var row = new StackPanel { Children = { bare } };
        ToolTip.SetTip(row, "The row's tip");
        bare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(ToolTip.GetServiceEnabled(row));

        var withoutAny = new Button();
        withoutAny.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(ToolTip.GetServiceEnabled(withoutAny));
    }
}
