using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Ui.Layout.Review;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Review;

/// <summary>
/// Code-behind for the Review panel. It tells the panel when it is on screen (markers draw only then), hands it
/// the list's selection, and puts into the list the rows the canvas's selection chose. Everything else is the
/// panel's view model (<c>ReviewPanelViewModel</c>) and the layout's.
/// </summary>
public partial class ReviewToolView : UserControl
{
    public ReviewToolView() => InitializeComponent();

    private ReviewTool? _shownTool;
    private bool _applyingCanvasSelection;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowFor(DataContext as ReviewTool);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ShowFor(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) ShowFor(DataContext as ReviewTool);
    }

    private void ShowFor(ReviewTool? tool)
    {
        if (ReferenceEquals(tool, _shownTool)) return;
        if (_shownTool is not null)
        {
            _shownTool.Panel.IsShown = false;
            _shownTool.Panel.SelectRowsRequested -= OnSelectRowsRequested;
        }
        _shownTool = tool;
        if (tool is not null)
        {
            tool.Panel.IsShown = true;
            tool.Panel.SelectRowsRequested += OnSelectRowsRequested;
        }
    }

    /// <summary>The list selects several rows so one reason can accept several findings; the panel is handed them all.</summary>
    private void OnRowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingCanvasSelection) return;
        if (sender is ListBox list && DataContext is ReviewTool { Panel: { } panel })
            panel.SetSelectedRows([.. (list.SelectedItems ?? Array.Empty<object>()).OfType<ReviewResultRow>()]);
    }

    /// <summary>R-rev6-5: copper selected on the canvas selects the findings its markers touch.</summary>
    private void OnSelectRowsRequested(IReadOnlyList<ReviewResultRow> rows)
    {
        if (RowsList.SelectedItems is not { } selected) return;
        _applyingCanvasSelection = true;
        try
        {
            selected.Clear();
            foreach (var row in rows) selected.Add(row);
        }
        finally { _applyingCanvasSelection = false; }
        if (rows.Count > 0) RowsList.ScrollIntoView(rows[0]);
    }
}
