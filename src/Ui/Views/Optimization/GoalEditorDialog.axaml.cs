using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Optimization;

namespace CircuitRF.Ui.Views.Optimization;

/// <summary>
/// The goal editor (brief-tuneopt-10 R-to10-4). Everything it does is on
/// <see cref="GoalEditorViewModel"/>; this file passes a template click on, and closes on OK only when
/// the view model accepts the fields.
/// </summary>
public partial class GoalEditorDialog : Window
{
    private readonly GoalEditorViewModel? _vm;
    private bool _selecting;

    public GoalEditorDialog() => InitializeComponent();

    public GoalEditorDialog(GoalEditorViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    // One template list per section: picking in one clears the others.
    private void OnTemplateSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_selecting || _vm is null || sender is not ListBox list || list.SelectedItem is not GoalTemplate t) return;
        _selecting = true;
        foreach (var other in this.GetLogicalDescendants().OfType<ListBox>())
            if (!ReferenceEquals(other, list)) other.SelectedItem = null;
        _selecting = false;
        _vm.SelectedTemplate = t;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_vm?.TryCommit() == true) Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
