// brief-em3d-46 R-em3d46-6c — the Measure card's copy buttons: one value as its lossless unit-bearing spelling, or the
// whole card as tab-separated text. The numbers themselves are the view model's (Viewer3DMeasureReadout).

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Views.Viewer3D;

public partial class Viewer3DMeasureCard : UserControl
{
    public Viewer3DMeasureCard() => InitializeComponent();

    private void OnCopyValue(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Viewer3DMeasureValue v) Copy(v.Copy);
    }

    private void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Viewer3DViewModel { MeasureReadout: { } r }) Copy(r.CopyAll());
    }

    private void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }
}
