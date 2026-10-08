using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class PlotInspectorView : UserControl
{
    private ScrollViewer? _traceScrollViewer;
    private PlotInspectorViewModel? _vm;

    public PlotInspectorView()
    {
        InitializeComponent();
        _traceScrollViewer = this.FindControl<ScrollViewer>("TraceScrollViewer");
    }

    // Plot-type Help: open the Reference Guide's Plot Types chapter at the current type.
    private void OnPlotTypeHelp(object? sender, RoutedEventArgs e)
    {
        string? anchor = DataContext is PlotInspectorViewModel vm
            ? (vm.IsSmithPlot ? "smith" : vm.IsPolarPlot ? "polar" : vm.IsTablePlot ? "table" : "rectangular")
            : null;
        DocLauncher.OpenPlotType(anchor);
    }

    /// <summary>
    /// Subscribe to the new ViewModel's Traces collection whenever the DataContext changes,
    /// and unsubscribe from the old one to avoid memory leaks.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
            _vm.Traces.CollectionChanged -= OnTracesCollectionChanged;

        _vm = DataContext as PlotInspectorViewModel;

        if (_vm is not null)
            _vm.Traces.CollectionChanged += OnTracesCollectionChanged;
    }

    /// <summary>
    /// Scrolls to the end when a new trace card is added so the user immediately
    /// sees the newly created trace without having to scroll manually.
    /// </summary>
    private void OnTracesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEnd();
    }

    /// <summary>
    /// Scrolls the trace list so the card at <paramref name="traceIndex"/> is visible.
    /// Called from PlotControl after the flyout is shown, posted at Loaded priority
    /// so the ScrollViewer knows its extents before the offset is set.
    /// </summary>
    public void ScrollToTrace(int traceIndex)
    {
        if (traceIndex <= 0 || _traceScrollViewer is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            const double EstimatedCardHeight = 92.0;
            _traceScrollViewer.Offset = new Vector(0, traceIndex * EstimatedCardHeight);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scrolls the trace list to the bottom of the list.
    /// </summary>
    public void ScrollToEnd()
    {
        if (_traceScrollViewer is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            _traceScrollViewer?.Offset = new Vector(0, _traceScrollViewer!.Extent.Height);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Opens the inspector scrolled to the given trace and focuses its spec TextBox.
    /// Interim implementation of the Table trace-header double-click (brief §5):
    /// routes to the inline spec editor instead of just scrolling to the card.
    /// </summary>
    public void FocusSpecTextBox(int traceIndex)
    {
        if (traceIndex < 0 || _vm is null) return;
        ScrollToTrace(traceIndex);

        Dispatcher.UIThread.Post(() =>
        {
            var targetVm = _vm.Traces.ElementAtOrDefault(traceIndex);
            if (targetVm is null) return;
            foreach (var tb in this.GetVisualDescendants().OfType<TextBox>())
            {
                if (tb.IsVisible && ReferenceEquals(tb.DataContext, targetVm))
                {
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
            }
        }, DispatcherPriority.Render);
    }

    // ---- Spec editor event handlers (#4) ------------------------------------

    // The spec TextBox is OneWay-bound to SpecShorthand. A focused TextBox does NOT pick up a model
    // change made via the transform combo, so committing its (stale) text on LostFocus would overwrite
    // the combo's change. Track the value at focus time and commit ONLY if the user actually edited it;
    // otherwise re-sync the box to the current model (which the combo may have moved).
    private string _specPristine = "";

    /// <summary>The trace card's Statistics menu (brief-yield-8 R-ya8-2), built from the card's view model on each
    /// click — its entries depend on the trace and its source.</summary>
    private void OnStatisticsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || anchor.DataContext is not TraceRowViewModel vm) return;
        var entries = vm.StatisticsMenu();
        if (entries.Count == 0) return;
        var menu = new ContextMenu();
        foreach (var entry in entries) menu.Items.Add(MenuItemOf(entry));
        menu.Open(anchor);

        static MenuItem MenuItemOf(StatisticsMenuEntry entry)
        {
            var item = new MenuItem
            {
                Header = entry.Header,
                Icon   = entry.Checked
                    ? new Material.Icons.Avalonia.MaterialIcon { Kind = Material.Icons.MaterialIconKind.Check, Width = 12, Height = 12 }
                    : null,
            };
            if (entry.Children is { } children)
                foreach (var c in children) item.Items.Add(MenuItemOf(c));
            else if (entry.Run is { } run)
                item.Click += (_, _) => run();
            else
                item.IsEnabled = false;
            return item;
        }
    }

    private void OnSpecEditGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) _specPristine = tb.Text ?? "";
    }

    private void OnSpecEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TraceRowViewModel vm) return;
        if ((tb.Text ?? "") != _specPristine)
            vm.CommitSpec(tb.Text ?? "");        // user edited the text → commit it
        else
            tb.Text = vm.SpecShorthand;          // unedited → re-sync to model (the combo may have changed it)
    }

    private void OnSpecEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is TraceRowViewModel vm)
        {
            vm.CommitSpec(tb.Text ?? "");
            _specPristine = vm.SpecShorthand;    // committed — this is the new pristine baseline
            e.Handled = true;
        }
    }
}
