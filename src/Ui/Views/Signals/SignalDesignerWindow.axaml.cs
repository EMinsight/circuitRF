using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.Signals;

namespace CircuitRF.Ui.Views.Signals;

public partial class SignalDesignerWindow : Window
{
    /// <summary>One Designer per MOD_Source: two working copies of one signal would write it twice.</summary>
    private static readonly Dictionary<EditableComponent, SignalDesignerWindow> Open = new();

    public SignalDesignerWindow()
    {
        InitializeComponent();
        // The plots follow the window's light/dark variant, as the Match Designer's do — without this the
        // axes, ticks and labels render out of the light theme whatever the application theme is.
        DataContextChanged += (_, _) =>
        {
            SyncPlotTheme();
            if (Vm is { } vm) vm.PlotsUpdated += (_, _) => RepaintPlots();
        };
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        // A click anywhere on a carrier card - its fields included - selects that carrier for the EVM tab.
        // Tunnelling, so a field that handles the press still selects its card.
        AddHandler(PointerPressedEvent, OnCarrierCardPressed, RoutingStrategies.Tunnel);
        // the label follows the plot when the window is resized
        this.FindControl<PlotControl>("EnvelopePlotControl")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) PlaceGapLabel();
        };
    }

    private void OnCarrierCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual v) return;
        var card = v.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "carrier-card"));
        if (card?.DataContext is CarrierRowViewModel row) Vm?.SelectCarrierCommand.Execute(row);
    }

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        this.FindControl<PlotControl>("PsdPlotControl")?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
        this.FindControl<PlotControl>("CcdfPlotControl")?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
        this.FindControl<PlotControl>("ConstellationPlotControl")?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
        this.FindControl<PlotControl>("EnvelopePlotControl")?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    private SignalDesignerViewModel? Vm => DataContext as SignalDesignerViewModel;

    private static readonly string[] PlotNames = ["PsdPlotControl", "CcdfPlotControl", "ConstellationPlotControl", "EnvelopePlotControl"];

    /// <summary>
    /// The Designer edits its plots in place (same Plot, new traces), and a PlotControl repaints by itself only when
    /// its Plot is replaced or the user touches it - so every update repaints all four here, and re-places the
    /// peak-average label.
    /// </summary>
    private void RepaintPlots()
    {
        foreach (var name in PlotNames) this.FindControl<PlotControl>(name)?.InvalidateVisual();
        PlaceGapLabel();
    }

    /// <summary>Puts the peak-average value beside the arrow, at half its height, using the plot's own transform.</summary>
    private void PlaceGapLabel()
    {
        var pc = this.FindControl<PlotControl>("EnvelopePlotControl");
        var label = this.FindControl<Border>("GapLabel");
        var text = this.FindControl<TextBlock>("GapLabelText");
        if (pc?.Plot is null || label is null || text is null || Vm is null || !(Vm.GapDb > 0) || pc.Bounds.Width <= 0)
        {
            if (label is not null) label.IsVisible = false;
            return;
        }
        var tf = CircuitRF.Render.DataDisplay.PlotRenderer.BuildTransforms(pc.Plot, (pc.Bounds.Width, pc.Bounds.Height));
        var at = tf.ToCanvas(Vm.GapArrowX, Vm.GapDb / 2, false);
        text.Text = Vm.GapDb.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " dB";
        label.IsVisible = true;
        Canvas.SetLeft(label, at.X + 5);
        Canvas.SetTop(label, at.Y - 9);
    }

    public static SignalDesignerWindow Show(ViewModels.SchematicViewModel schematicVm, EditableComponent comp, Window? owner)
    {
        if (Open.TryGetValue(comp, out var existing))
        {
            existing.Activate();
            return existing;
        }
        var vm = new SignalDesignerViewModel();
        var window = new SignalDesignerWindow { DataContext = vm };
        vm.PickIqFileAsync = window.PickIqFileAsync;
        vm.PickExportPathAsync = window.PickExportPathAsync;
        vm.SetTarget(schematicVm, comp);
        Open.Add(comp, window);
        window.Closed += (_, _) => { Open.Remove(comp); vm.Dispose(); };
        if (owner is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show(owner);
        }
        else window.Show();
        return window;
    }

    private async Task<string?> PickIqFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "I/Q waveform (# sample_rate_hz header, I Q columns)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("I/Q text") { Patterns = ["*.txt", "*.csv", "*.iq"] }, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickExportPathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export signal - I/Q text (# sample_rate_hz header, I Q columns)",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("I/Q text") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
        });
        return file?.TryGetLocalPath();
    }

    private void OnApplyClose(object? sender, RoutedEventArgs e)
    {
        Vm?.ApplyCommand.Execute(null);
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
