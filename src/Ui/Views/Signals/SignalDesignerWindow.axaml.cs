using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
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
            if (Vm is { } vm)
            {
                vm.PlotsUpdated += (_, _) => RepaintPlots();
                WireMarkers(vm);
            }
        };
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        // A click anywhere on a carrier card - its fields included - selects that carrier for the EVM tab.
        // Tunnelling, so a field that handles the press still selects its card.
        AddHandler(PointerPressedEvent, OnCarrierCardPressed, RoutingStrategies.Tunnel);
        // A press anywhere but on an info box clears the marker selection, as the Data Display's canvas does.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        // the label follows the plot when the window is resized
        this.FindControl<PlotControl>("EnvelopePlotControl")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) PlaceGapLabel();
        };
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/signal-designer.html");

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

    // ── Markers ─────────────────────────────────────────────────────────────
    //
    // The plots are bare PlotControls, so this window is their host, as the Match Designer is for its two: it
    // hands each one its container's marker providers and events, and gives the info boxes somewhere to be
    // drawn. None of that was wired, so a marker was added with no info box, and could not be selected (the
    // selection lives on its box) or deleted. Each tab has its own layer holding that tab's boxes, and a
    // container's rectangle is kept equal to its plot's in that layer's coordinates.

    private readonly System.Collections.ObjectModel.ObservableCollection<MarkerInfoBoxViewModel> _spectrumBoxes = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<MarkerInfoBoxViewModel> _evmBoxes = [];
    private (PlotControl Plot, PlotContainerViewModel Container, ItemsControl Layer)[] _hosted = [];

    private void WireMarkers(SignalDesignerViewModel vm)
    {
        var spectrum = this.FindControl<ItemsControl>("SpectrumInfoBoxLayer")!;
        var evm = this.FindControl<ItemsControl>("EvmInfoBoxLayer")!;
        spectrum.ItemsSource = _spectrumBoxes;
        evm.ItemsSource = _evmBoxes;
        _hosted =
        [
            (this.FindControl<PlotControl>("PsdPlotControl")!, vm.PsdContainer, spectrum),
            (this.FindControl<PlotControl>("CcdfPlotControl")!, vm.CcdfContainer, spectrum),
            (this.FindControl<PlotControl>("ConstellationPlotControl")!, vm.ConstellationContainer, evm),
            (this.FindControl<PlotControl>("EnvelopePlotControl")!, vm.EnvelopeContainer, evm),
        ];
        foreach (var (plot, container, _) in _hosted)
        {
            plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
            plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
            plot.ContainerProvider           = () => container;
            plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
            plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;
            plot.DoubleTapped += (_, args) => { plot.HandleDoubleTapAt(args.GetPosition(plot)); args.Handled = true; };
            plot.PlotChanged += container.OnPlotChanged;
            plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
            plot.MarkerAdded += container.OnMarkerAdded;
            container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();
        }
        vm.PlotHost.MarkerInfoBoxes.CollectionChanged += (_, _) => SortBoxesByTab();
        SortBoxesByTab();
        // LayoutUpdated rather than SizeChanged: a plot can move without resizing, and its boxes must follow.
        LayoutUpdated += (_, _) => SyncPlotContainers();
    }

    private void SortBoxesByTab()
    {
        if (Vm is null) return;
        Refill(_spectrumBoxes, Vm.PsdContainer, Vm.CcdfContainer);
        Refill(_evmBoxes, Vm.ConstellationContainer, Vm.EnvelopeContainer);

        void Refill(System.Collections.ObjectModel.ObservableCollection<MarkerInfoBoxViewModel> into, params PlotContainerViewModel[] mine)
        {
            into.Clear();
            foreach (var box in Vm.PlotHost.MarkerInfoBoxes)
                if (mine.Contains(box.Container)) into.Add(box);
        }
    }

    private void SyncPlotContainers()
    {
        foreach (var (plot, container, layer) in _hosted)
        {
            // A plot on the hidden tab is not laid out; its boxes are hidden with the tab, so leave it be.
            if (plot.Bounds.Width < 1 || plot.Bounds.Height < 1) continue;
            if (plot.TranslatePoint(default, layer) is not { } origin) continue;
            if (Math.Abs(container.Left - origin.X) < 0.5 && Math.Abs(container.Top - origin.Y) < 0.5
                && Math.Abs(container.Width - plot.Bounds.Width) < 0.5 && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
                continue;   // LayoutUpdated fires constantly; only a real move is worth notifying
            container.Left = origin.X;
            container.Top = origin.Y;
            container.Width = plot.Bounds.Width;
            container.Height = plot.Bounds.Height;
            container.NotifyViewProperties();
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        if (e.Source is Avalonia.Visual v
            && v.FindAncestorOfType<Views.DataDisplay.MarkerInfoBoxView>(includeSelf: true) is not null)
            return;
        Vm.PlotHost.SelectOnly((MarkerInfoBoxViewModel?)null);
    }

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
