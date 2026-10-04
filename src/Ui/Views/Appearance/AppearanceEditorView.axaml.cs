using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Ui.Appearance;

namespace CircuitRF.Ui.Views.Appearance;

/// <summary>brief-em3d-108 — the appearance editor's view: every rule is the view model's. A slider's release (pointer up, capture
/// lost, a key let go) commits its drag, as the Inspector's Transparency slider does.</summary>
public partial class AppearanceEditorView : UserControl
{
    /// <summary>A colour field's swatch brush.</summary>
    public static readonly IValueConverter ToBrush = new FuncValueConverter<Color, IBrush>(c => new SolidColorBrush(c));

    /// <summary>R-em3d108-1b — the swatch's RGBA8 rows (AppearanceSwatch.Rgba, opaque) as a bitmap.</summary>
    public static readonly IValueConverter SwatchBitmap = new FuncValueConverter<byte[]?, Avalonia.Media.Imaging.Bitmap?>(rgba =>
    {
        int side = CircuitRF.Render.Scene3D.Look.AppearanceSwatch.Size;
        if (rgba is null || rgba.Length != side * side * 4) return null;
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(side, side), new Avalonia.Vector(96, 96),
                                                                 Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        for (int row = 0; row < side; row++)
            System.Runtime.InteropServices.Marshal.Copy(rgba, row * side * 4, fb.Address + row * fb.RowBytes, side * 4);
        return bitmap;
    });

    public AppearanceEditorView()
    {
        InitializeComponent();
        AddHandler(PointerReleasedEvent, (s, e) => Release(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (s, e) => Release(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, (s, e) => Release(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The event came from inside a field's slider: its drag is written.</summary>
    private static void Release(object? source)
    {
        if ((source as Avalonia.Visual)?.FindAncestorOfType<Slider>(includeSelf: true) is { DataContext: AppearanceFieldViewModel field })
            field.CommitSlider();
    }

    private void OnFieldKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Control { DataContext: AppearanceFieldViewModel field }) return;
        field.CommitText();
        e.Handled = true;
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AppearanceFieldViewModel field }) field.CommitText();
    }

    // The field and flyout of the colour being picked, held from Opened to Closed. Avalonia detaches a flyout's popup from its
    // button BEFORE it raises Closed, so by then the content's inherited DataContext is gone: reading the field from it there found
    // nothing, the colour was never written, and the preview stayed on screen with no undo entry behind it (owner-reported).
    private AppearanceFieldViewModel? _colourField;
    private Flyout? _colourFlyout;

    private void OnColourFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not Flyout flyout) return;
        var field = flyout.Target?.DataContext as AppearanceFieldViewModel ?? (flyout.Content as Control)?.DataContext as AppearanceFieldViewModel;
        if (field is null) return;
        _colourField = field;
        _colourFlyout = flyout;
        field.BeginColour();
    }

    private void OnColourFlyoutClosed(object? sender, EventArgs e)
    {
        var field = _colourField;
        _colourField = null;
        _colourFlyout = null;
        field?.CommitColour();
    }

    /// <summary>Done: closes the picker, which keeps the colour (as clicking away does).</summary>
    private void OnColourDone(object? sender, RoutedEventArgs e) => _colourFlyout?.Hide();

    private void OnColourChanged(object? sender, ColorChangedEventArgs e)
    {
        if (sender is Control { DataContext: AppearanceFieldViewModel field }) field.PreviewColour(e.NewColor);
    }
}
