using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Properties;

public partial class SymbolPrimitiveInspectorView : UserControl
{
    public SymbolPrimitiveInspectorView()
    {
        InitializeComponent();

        // The fill slider previews as it moves; its release — by pointer, a lost capture, or a key — is the one undo
        // step. handledEventsToo: the slider's thumb marks the pointer events handled.
        FillSlider.AddHandler(PointerReleasedEvent, (_, _) => Vm?.CommitFillSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        FillSlider.AddHandler(PointerCaptureLostEvent, (_, _) => Vm?.CommitFillSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        FillSlider.AddHandler(KeyUpEvent, (_, _) => Vm?.CommitFillSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private SymbolPrimitiveInspectorViewModel? Vm => DataContext as SymbolPrimitiveInspectorViewModel;

    private void OnFillTextKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.CommitFillText(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm?.RevertFillText(); e.Handled = true; }
    }

    private void OnFillTextLostFocus(object? sender, RoutedEventArgs e) => Vm?.CommitFillText();
}
