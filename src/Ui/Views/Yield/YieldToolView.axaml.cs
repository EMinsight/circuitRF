using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Tuning;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.Views.Yield;

/// <summary>
/// The Yield panel (brief-yield-10). Everything it DOES is on <see cref="YieldPanelViewModel"/>; this file opens the
/// correlation grid and the corner generator the view model asks for, commits a settings box or a spread on Enter and
/// when it loses focus, and routes Enter in the Add… list.
/// </summary>
public partial class YieldToolView : UserControl
{
    /// <summary>An active DOE effect in bold (brief-yield-14).</summary>
    public static readonly IValueConverter ActiveWeight = new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>A disabled goal is listed dimmed.</summary>
    public static readonly IValueConverter EnabledOpacity = new FuncValueConverter<bool, double>(on => on ? 1.0 : 0.45);

    /// <summary>A failing trial's pass/fail in red.</summary>
    public static readonly IValueConverter FailBrush =
        new FuncValueConverter<bool, IBrush?>(fail => fail ? new SolidColorBrush(Color.FromRgb(0xD9, 0x4F, 0x4F)) : null);

    private YieldPanelViewModel? _panel;

    private FlyoutBase AddFlyout => AddButton.Flyout!;

    public YieldToolView()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        EscapeCancelsEdit.Attach(this);

        AddFlyout.Opened += (_, _) => { if (_panel is { } p) p.Add.IsOpen = true; AddSearch.Focus(); };
        AddFlyout.Closed += (_, _) => { if (_panel is { } p) p.Add.IsOpen = false; };
        TuningAddPopup.Attach(AddSearch, AddList, () => _panel?.Add);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_panel is not null)
        {
            _panel.CorrelationsRequested -= OnCorrelationsRequested;
            _panel.GeneratorRequested    -= OnGeneratorRequested;
            _panel.Add.PropertyChanged   -= OnAddChanged;
        }
        _panel = (DataContext as YieldTool)?.Panel;
        if (_panel is not null)
        {
            _panel.CorrelationsRequested += OnCorrelationsRequested;
            _panel.GeneratorRequested    += OnGeneratorRequested;
            _panel.Add.PropertyChanged   += OnAddChanged;
        }
    }

    private void OnAddChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TuningAddViewModel.IsOpen) && _panel is { Add.IsOpen: false }) AddFlyout.Hide();
    }

    private async void OnCorrelationsRequested(object? sender, CorrelationsEditorViewModel editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new CorrelationsDialog(editor).ShowDialog(owner);
    }

    private async void OnGeneratorRequested(object? sender, CornerGeneratorViewModel editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new CornerGeneratorDialog(editor).ShowDialog(owner);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var source = e.Source as Control;
        if (source is TextBox { DataContext: YieldVariableRowViewModel row } spread && spread.Classes.Contains("spread"))
        {
            if (e.Key == Key.Enter)  { row.CommitSpread(spread.Text ?? ""); spread.Text = row.SpreadText; e.Handled = true; }
            if (e.Key == Key.Escape) { spread.Text = row.SpreadText; e.Handled = true; }
            return;
        }
        // A settings box commits on LostFocus, and on Enter.
        if (source is TextBox box && box.Classes.Contains("commit") && e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: YieldVariableRowViewModel row } box && box.Classes.Contains("spread")
            && box.Text != row.SpreadText)
        {
            row.CommitSpread(box.Text ?? "");
            box.Text = row.SpreadText;
        }
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/yield.html");
}
