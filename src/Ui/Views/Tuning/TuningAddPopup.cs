// ================================================================
//  TuningAddPopup.cs  —  Enter and double-click in the Add… popup
//
//  The Tuning, Optimizer and Yield panels each host the popup in a
//  Button.Flyout. A flyout's content lives under its own popup root, so
//  a key pressed inside it never routes through the panel: a tunnel
//  handler on the panel never runs, and the list (Multiple,Toggle)
//  takes Enter as "toggle this row". The handlers therefore go on the
//  search box and the list themselves.
// ================================================================

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Views.Tuning;

internal static class TuningAddPopup
{
    /// <summary>Enter (in the search box or the list) adds the selected rows; a double-click adds that
    /// row along with any already selected.</summary>
    public static void Attach(TextBox search, ListBox list, Func<TuningAddViewModel?> add)
    {
        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || add() is not { } vm) return;
            vm.AddSelectedCommand.Execute(null);
            e.Handled = true;
        }
        search.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        list.DoubleTapped += (_, e) =>
        {
            if (add() is not { } vm) return;
            var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is TuningAddRow row) { vm.AddWith(row); e.Handled = true; }
        };
    }
}
