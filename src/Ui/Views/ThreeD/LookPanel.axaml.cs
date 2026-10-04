using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>brief-em3d-108 R-em3d108-3 — the Look panel's view: every rule is the view model's. A slider's release (pointer up, capture
/// lost, a key let go) writes its drag as one undo entry.</summary>
public partial class LookPanel : UserControl
{
    public LookPanel()
    {
        InitializeComponent();
        AddHandler(PointerReleasedEvent, (_, e) => Release(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, e) => Release(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => Release(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private C3dLookPanelViewModel? Vm => DataContext as C3dLookPanelViewModel;

    private void Release(object? source)
    {
        if ((source as Avalonia.Visual)?.FindAncestorOfType<Slider>(includeSelf: true) is not null) Vm?.CommitDrag();
    }

    private void OnNumberKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Control { Tag: string key }) return;
        Vm?.CommitText(key);
        e.Handled = true;
    }

    private void OnNumberLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string key }) Vm?.CommitText(key);
    }

    private void OnBackgroundColourChanged(object? sender, ColorChangedEventArgs e)
    {
        if (Vm is { BackgroundEditing: { } which } vm && e.OldColor != e.NewColor) vm.PreviewBackground(which == "top", e.NewColor);
    }

    private async void OnLoadHdr(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || Vm is not { } vm) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a Radiance .hdr environment",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Radiance HDR") { Patterns = ["*.hdr"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.LoadHdr(path);
    }
}
