using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.Optimization;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Optimization;

/// <summary>
/// The Optimizer panel (brief-tuneopt-10). Everything it DOES is on <see cref="OptimizerPanelViewModel"/>;
/// this file opens the goal editor the view model asks for, routes Enter/Esc in the range boxes and the
/// Add… list, edits a goal on double-click, and commits the ⚙ flyout when it closes.
/// </summary>
public partial class OptimizerToolView : UserControl
{
    private OptimizerPanelViewModel? _panel;

    private FlyoutBase AddFlyout => AddButton.Flyout!;

    public OptimizerToolView()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        GoalList.DoubleTapped += OnGoalDoubleTapped;

        AddFlyout.Opened += (_, _) => { if (_panel is { } p) p.Add.IsOpen = true; AddSearch.Focus(); };
        AddFlyout.Closed += (_, _) => { if (_panel is { } p) p.Add.IsOpen = false; };
        SettingsButton.Flyout!.Opened += (_, _) => _panel?.SettingsEditor.Reload();
        SettingsButton.Flyout!.Closed += (_, _) => _panel?.SettingsEditor.Commit();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_panel is not null)
        {
            _panel.GoalEditorRequested -= OnGoalEditorRequested;
            _panel.Add.PropertyChanged -= OnAddChanged;
        }
        _panel = (DataContext as OptimizerTool)?.Panel;
        if (_panel is not null)
        {
            _panel.GoalEditorRequested += OnGoalEditorRequested;
            _panel.Add.PropertyChanged += OnAddChanged;
        }
    }

    private void OnAddChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TuningAddViewModel.IsOpen) && _panel is { Add.IsOpen: false }) AddFlyout.Hide();
    }

    private async void OnGoalEditorRequested(object? sender, GoalEditorViewModel editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new GoalEditorDialog(editor).ShowDialog(owner);
    }

    private void OnGoalDoubleTapped(object? sender, TappedEventArgs e)
    {
        for (var v = e.Source as Avalonia.Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is CheckBox) return;
            if (v is Control { DataContext: OptimizerGoalRowViewModel row })
            {
                _panel?.EditGoalRow(row);
                e.Handled = true;
                return;
            }
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var source = e.Source as Control;
        if (_panel is not null && (ReferenceEquals(source, AddSearch) || IsInside(source, AddList)) && e.Key == Key.Enter)
        {
            _panel.Add.AddSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (source is TextBox { DataContext: OptimizerVariableRowViewModel row } box && box.Classes.Contains("optBound"))
        {
            if (e.Key == Key.Enter)  { CommitBound(row, box); e.Handled = true; }
            if (e.Key == Key.Escape) { box.Text = box.Tag as string == "min" ? row.MinText : row.MaxText; e.Handled = true; }
            return;
        }
        if (source is ListBox { Name: "GoalList" } && e.Key == Key.Enter)
        {
            _panel?.EditGoalCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: OptimizerVariableRowViewModel row } box && box.Classes.Contains("optBound"))
        {
            string shown = box.Tag as string == "min" ? row.MinText : row.MaxText;
            if (box.Text != shown) CommitBound(row, box);
        }
    }

    private static void CommitBound(OptimizerVariableRowViewModel row, TextBox box)
    {
        string text = box.Text ?? "";
        if (box.Tag as string == "min") row.CommitMin(text); else row.CommitMax(text);
        box.Text = box.Tag as string == "min" ? row.MinText : row.MaxText;
    }

    private static bool IsInside(Control? c, Avalonia.Visual container)
    {
        for (var v = c as Avalonia.Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, container)) return true;
        return false;
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.OpenAnalysis("optimizer");
}
