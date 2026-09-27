using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Impedance;

/// <summary>
/// Code-behind for the Impedance panel. Two gestures only — ask where to write the PDF, and bring the
/// selected row on screen. Everything else, from validating a field to holding the report, is the
/// panel's view model (<c>ImpedancePanelViewModel</c>) and the layout's.
/// </summary>
public partial class ImpedanceToolView : UserControl
{
    public ImpedanceToolView() => InitializeComponent();

    // The width survey runs only while the panel is on screen (ImpedancePanelViewModel.IsShown).
    private ImpedanceTool? _shownTool;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowFor(DataContext as ImpedanceTool);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ShowFor(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) ShowFor(DataContext as ImpedanceTool);
    }

    private void ShowFor(ImpedanceTool? tool)
    {
        if (ReferenceEquals(tool, _shownTool)) return;
        if (_shownTool is not null) _shownTool.Panel.IsShown = false;
        _shownTool = tool;
        if (tool is not null) tool.Panel.IsShown = true;
    }

    private async void OnExportPdfClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImpedanceTool { Panel: { CanExport: true } panel }) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Impedance Analysis",
            SuggestedFileName = panel.SuggestedPdfName,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF report") { Patterns = ["*.pdf"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await panel.ExportPdfAsync(path);
    }

    /// <summary>The list selects several rows so one reason can accept several findings (R-imp5-3a); the
    /// panel is handed them all. <c>SelectedItem</c> stays bound for the one the canvas emphasises.</summary>
    private void OnRowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is ImpedanceTool { Panel: { } panel })
            panel.SetSelectedRows([.. (list.SelectedItems ?? Array.Empty<object>()).OfType<CircuitRF.Ui.Layout.Impedance.ImpedanceResultRow>()]);
    }

    /// <summary>
    /// R-imp3-2d's zoom. Double-click rather than single, as in the DRC panel: a single click selects
    /// the row (which emphasises it on the canvas), and yanking the viewport on every arrow-key walk
    /// down the list would make the list unusable.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImpedanceTool { Panel: { } panel }) panel.ZoomToSelectedCommand.Execute(null);
    }
}
