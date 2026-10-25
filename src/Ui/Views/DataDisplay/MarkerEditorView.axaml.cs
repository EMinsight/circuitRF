using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class MarkerEditorView : UserControl
{
    public MarkerEditorView() => InitializeComponent();

    /// <summary>The card is a flyout's content wherever it is opened (the plot's glyph, the info box), so closing it
    /// is closing the popup that hosts it; the flyout follows its popup.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindLogicalAncestorOfType<Popup>() is { } popup) popup.Close();
        e.Handled = true;
    }

    private void OnFreqTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is MarkerEditorViewModel vm)
        {
            vm.CommitFrequency();
            e.Handled = true;
        }
    }

    private void OnVswrValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is MarkerEditorViewModel vm)
        {
            vm.CommitVswrValue();
            e.Handled = true;
        }
    }

    private void OnImpedanceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && DataContext is MarkerEditorViewModel vm)
        {
            vm.CommitImpedance();
            e.Handled = true;
        }
    }
}
