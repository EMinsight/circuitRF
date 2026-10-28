using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Tuning;

/// <summary>
/// The Tuning panel (brief-tuneopt-4). Everything a row DOES is on <see cref="TuningRowViewModel"/>;
/// this file only routes keys and pointer releases to it — Up/Down between rows, Left/Right, Shift and
/// Page keys on the value (overview §3, R-dfr26-6-3) wherever the keyboard is in the panel, Enter/Esc in the number
/// box, the min/max boxes that open in place — and paints
/// the status dot. A timer re-reads the session's lag while one runs, so "1.4 s behind" counts up.
/// </summary>
public partial class TuningToolView : UserControl
{
    private TuningPanelViewModel? _panel;

    private FlyoutBase AddFlyout => AddButton.Flyout!;
    private readonly DispatcherTimer _lagTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    static TuningToolView()
    {
        // An in-place box takes the caret the moment it appears, with its text selected.
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, _) =>
        {
            if (box.IsVisible && (box.Classes.Contains("tuningBound") || box.Classes.Contains("presetRename")))
                Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Background);
        });
    }

    public TuningToolView()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedAnywhere, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressedInRow, RoutingStrategies.Bubble);
        AddHandler(GotFocusEvent, (_, e) => { if (SliderOf(e.Source as Control) is { Tag: TuningRowViewModel r }) _currentRow = r; });
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        EscapeCancelsEdit.Attach(this);   // after the tunnel above, so a box's own Esc meaning runs first

        AddFlyout.Opened      += (_, _) => { if (_panel is { } p) p.Add.IsOpen = true; AddSearch.Focus(); };
        AddFlyout.Closed      += (_, _) => { if (_panel is { } p) p.Add.IsOpen = false; };
        TuningAddPopup.Attach(AddSearch, AddList, () => _panel?.Add);
        SettingsButton.Flyout!.Opened += (_, _) => _panel?.ScopeSettings.LoadPoints();

        _lagTimer.Tick += (_, _) => { if (_panel is { IsRunning: true } p) p.UpdateLag(); };
        AttachedToVisualTree   += (_, _) => _lagTimer.Start();
        DetachedFromVisualTree += (_, _) => _lagTimer.Stop();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_panel is not null)
        {
            _panel.PropertyChanged       -= OnPanelChanged;
            _panel.Add.PropertyChanged   -= OnAddChanged;
            _panel.PresetNamingRequested -= OnPresetNamingRequested;
            _panel.CopyText = null;
        }
        _panel = (DataContext as TuningTool)?.Panel;
        if (_panel is not null)
        {
            _panel.PropertyChanged       += OnPanelChanged;
            _panel.Add.PropertyChanged   += OnAddChanged;
            _panel.PresetNamingRequested += OnPresetNamingRequested;
            _panel.CopyText = text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
            };
        }
        PaintStatus();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TuningPanelViewModel.Status) or nameof(TuningPanelViewModel.LastEvalText)
                           or nameof(TuningPanelViewModel.BehindText))
            PaintStatus();
    }

    // Enter in the Add… list closes the flyout from the view model's side.
    private void OnAddChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TuningAddViewModel.IsOpen) && _panel is { Add.IsOpen: false }) AddFlyout.Hide();
    }

    // A preset was just locked in: open the drop-down with its name ready to type (R-to5-1).
    private void OnPresetNamingRequested(object? sender, EventArgs e) => PresetsButton.Flyout?.ShowAt(PresetsButton);

    /// <summary>Idle grey, running green, lagging amber — and beside it the last evaluation's time, or
    /// how far behind the displayed result is.</summary>
    private void PaintStatus()
    {
        var status = _panel?.Status ?? TuningStatus.Idle;
        StatusDot.Fill = status switch
        {
            TuningStatus.Running => new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x71)),
            TuningStatus.Lagging => new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00)),
            _                    => new SolidColorBrush(Color.FromArgb(0x80, 0x90, 0x90, 0x90)),
        };
        ToolTip.SetTip(StatusDot, status switch
        {
            TuningStatus.Running => "Tuning",
            TuningStatus.Lagging => "The display is behind the sliders",
            _                    => "Not tuning",
        });
        StatusTime.Text = status == TuningStatus.Lagging && _panel?.BehindText is { Length: > 0 } behind
            ? behind : _panel?.LastEvalText ?? "";
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var source = e.Source as Control;

        if (source is TextBox { DataContext: TuningPresetItemViewModel preset } rename && rename.Classes.Contains("presetRename"))
        {
            if (e.Key == Key.Enter)  { preset.CommitRename(); e.Handled = true; }
            if (e.Key == Key.Escape) { preset.CancelRename(); e.Handled = true; }
            return;
        }

        if (source is TextBox { Tag: TuningRowViewModel row } box && box.Classes.Contains("tuningValue"))
        {
            // Enter hands the keyboard back to the row's slider, so the arrows step on from the typed value.
            if (e.Key == Key.Enter)  { row.CommitValueText(); FocusSliderOf(row); e.Handled = true; }
            if (e.Key == Key.Escape) { row.RevertValueText(); e.Handled = true; }
            return;
        }

        if (source is TextBox { DataContext: TuningRowViewModel boundRow } bound && bound.Classes.Contains("tuningBound"))
        {
            if (e.Key == Key.Enter)  { CommitBound(boundRow, bound.Tag as string); e.Handled = true; }
            if (e.Key == Key.Escape) { boundRow.CancelRangeEdit(); e.Handled = true; }
            return;
        }

        // Anywhere else in a row — its slider, or its ⋮ button: Up/Down choose the parameter,
        // Left/Right change it (R-dfr26-6-3). A text box returned above, so it keeps its own arrows.
        if (_panel is null) return;
        if ((SliderOf(source)?.Tag ?? (source as Button)?.DataContext) is not TuningRowViewModel focused)
        {
            // Anywhere else in the panel — the panel itself after a click on a row's name or on blank space, after
            // Esc out of a box, or a toolbar button: the keys act on the last row used, else the first. They used
            // to act only from a slider, so the panel looked focused and the arrows did nothing (owner report,
            // 2026-10-25). A control with arrows of its own keeps them.
            if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown)) return;
            if (source is TextBox or SelectingItemsControl or NumericUpDown or MenuItem or Slider) return;
            if (CurrentRow() is not { } current) return;
            focused = current;
            FocusSliderOf(focused);
        }

        if (e.Key is Key.Up or Key.Down)
        {
            if (_panel.NeighbourRow(focused, e.Key == Key.Down ? +1 : -1) is { } next) FocusSliderOf(next);
            e.Handled = true;   // at an end too: the slider's own Up/Down would change the value
            return;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        (int dir, TuningNudge size)? nudge = e.Key switch
        {
            Key.Right    => (+1, shift ? TuningNudge.TenSteps : TuningNudge.Step),
            Key.Left     => (-1, shift ? TuningNudge.TenSteps : TuningNudge.Step),
            Key.PageUp   => (+1, TuningNudge.Page),
            Key.PageDown => (-1, TuningNudge.Page),
            _ => null,
        };
        if (nudge is { } n && focused.IsEnabled)
        {
            focused.Nudge(n.dir, n.size);
            e.Handled = true;
        }
    }

    /// <summary>The row the arrows act on when no row's slider has the keyboard: the last one used, while it is
    /// still listed and enabled, otherwise the first enabled row.</summary>
    private TuningRowViewModel? _currentRow;

    private TuningRowViewModel? CurrentRow()
        => _currentRow is { IsEnabled: true } r && _panel!.Rows.Contains(r) ? r
         : _panel!.Rows.FirstOrDefault(x => x.IsEnabled);

    /// <summary>
    /// A press on a row's name, units or blank space — nothing there takes the keyboard, so it went to the panel
    /// and the arrows did nothing: that row's slider takes it instead. A press a control handled (a slider, a box,
    /// a min/max opening its box) is left alone.
    /// </summary>
    private void OnPointerPressedInRow(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, RowsList); v = v.GetVisualParent())
        {
            if (v is InputElement { Focusable: true }) return;
            if (v is Border { DataContext: TuningRowViewModel row } border && border.Classes.Contains("row"))
            {
                _currentRow = row;
                FocusSliderOf(row);
                return;
            }
        }
    }

    /// <summary>Gives <paramref name="row"/>'s slider the keyboard and scrolls it into view.</summary>
    private void FocusSliderOf(TuningRowViewModel row)
    {
        if (RowsList.ContainerFromItem(row) is not { } container) return;
        foreach (var v in container.GetVisualDescendants())
            if (v is Slider { Tag: TuningRowViewModel r } slider && ReferenceEquals(r, row) && slider.Classes.Contains("tuning"))
            {
                slider.Focus(NavigationMethod.Directional);
                slider.BringIntoView();
                return;
            }
    }

    private static void CommitBound(TuningRowViewModel row, string? which)
    {
        switch (which)
        {
            case "min":  row.CommitMinText();  break;
            case "max":  row.CommitMaxText();  break;
            case "step": row.CommitStepText(); break;
            case "query": row.CommitQueryText(); break;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        switch (e.Source)
        {
            case TextBox { DataContext: TuningPresetItemViewModel preset } box when box.Classes.Contains("presetRename"):
                preset.CommitRename();
                break;
            case TextBox { Tag: TuningRowViewModel row } box when box.Classes.Contains("tuningValue"):
                row.RevertValueText();
                break;
            case TextBox { DataContext: TuningRowViewModel row } box when box.Classes.Contains("tuningBound"):
                string? which = box.Tag as string;
                bool open = which switch
                {
                    "min" => row.IsEditingMin, "max" => row.IsEditingMax, "query" => row.IsEditingQuery, _ => row.IsEditingStep,
                };
                if (open) CommitBound(row, which);
                break;
        }
    }

    /// <summary>A min or max under the slider was clicked: open its box.</summary>
    private void OnBoundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { DataContext: TuningRowViewModel row, Tag: string which }) return;
        if (which == "min") row.IsEditingMin = true; else row.IsEditingMax = true;
        e.Handled = true;
    }

    // ── The slider's release (D7: the final value) ──────────────────────────

    private void OnPointerReleasedAnywhere(object? sender, PointerReleasedEventArgs e)
    {
        if (SliderOf(e.Source as Control) is { Tag: TuningRowViewModel row }) row.Release();
    }

    private static Slider? SliderOf(Control? c)
    {
        for (var v = c as Visual; v is not null; v = v.GetVisualParent())
            if (v is Slider { Classes: var classes } s && classes.Contains("tuning")) return s;
        return null;
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/tuning.html");
}
