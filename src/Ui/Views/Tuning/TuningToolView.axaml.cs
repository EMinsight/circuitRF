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
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Tuning;

/// <summary>
/// The Tuning panel (brief-tuneopt-4). Everything a row DOES is on <see cref="TuningRowViewModel"/>;
/// this file only routes keys and pointer releases to it — the slider's arrows, Shift+arrows and Page
/// keys (overview §3), Enter/Esc in the number box, the min/max boxes that open in place — and paints
/// the status dot. A timer re-reads the session's lag while one runs, so "1.4 s behind" counts up.
/// </summary>
public partial class TuningToolView : UserControl
{
    private TuningPanelViewModel? _panel;

    private FlyoutBase AddFlyout => AddButton.Flyout!;
    private readonly DispatcherTimer _lagTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

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
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);

        AddFlyout.Opened      += (_, _) => { if (_panel is { } p) p.Add.IsOpen = true; AddSearch.Focus(); };
        AddFlyout.Closed      += (_, _) => { if (_panel is { } p) p.Add.IsOpen = false; };
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

        // The Add… list: Enter adds, from the search box or the list.
        if (_panel is not null && (ReferenceEquals(source, AddSearch) || IsInside(source, AddList)) && e.Key == Key.Enter)
        {
            _panel.Add.AddSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (source is TextBox { DataContext: TuningPresetItemViewModel preset } rename && rename.Classes.Contains("presetRename"))
        {
            if (e.Key == Key.Enter)  { preset.CommitRename(); e.Handled = true; }
            if (e.Key == Key.Escape) { preset.CancelRename(); e.Handled = true; }
            return;
        }

        if (source is TextBox { Tag: TuningRowViewModel row } box && box.Classes.Contains("tuningValue"))
        {
            if (e.Key == Key.Enter)  { row.CommitValueText(); e.Handled = true; }
            if (e.Key == Key.Escape) { row.RevertValueText(); e.Handled = true; }
            return;
        }

        if (source is TextBox { DataContext: TuningRowViewModel boundRow } bound && bound.Classes.Contains("tuningBound"))
        {
            if (e.Key == Key.Enter)  { CommitBound(boundRow, bound.Tag as string); e.Handled = true; }
            if (e.Key == Key.Escape) { boundRow.CancelRangeEdit(); e.Handled = true; }
            return;
        }

        if (SliderOf(source) is { Tag: TuningRowViewModel sliderRow })
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            (int dir, TuningNudge size)? nudge = e.Key switch
            {
                Key.Right or Key.Up => (+1, shift ? TuningNudge.TenSteps : TuningNudge.Step),
                Key.Left or Key.Down => (-1, shift ? TuningNudge.TenSteps : TuningNudge.Step),
                Key.PageUp   => (+1, TuningNudge.Page),
                Key.PageDown => (-1, TuningNudge.Page),
                _ => null,
            };
            if (nudge is { } n)
            {
                sliderRow.Nudge(n.dir, n.size);
                e.Handled = true;
            }
        }
    }

    private static void CommitBound(TuningRowViewModel row, string? which)
    {
        switch (which)
        {
            case "min":  row.CommitMinText();  break;
            case "max":  row.CommitMaxText();  break;
            case "step": row.CommitStepText(); break;
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
                bool open = which switch { "min" => row.IsEditingMin, "max" => row.IsEditingMax, _ => row.IsEditingStep };
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

    private static bool IsInside(Control? c, Visual container)
    {
        for (var v = c as Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, container)) return true;
        return false;
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/tuning.html");
}
