using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// A color entry from <see cref="TraceProperties.ColorLUT"/> formatted for
/// display in a ComboBox color picker.
///
/// <para>The swatch is painted in the colour the canvas DRAWS the entry in, not the stored one:
/// on the dark theme <see cref="RenderTheme.TraceColor"/> lightens the dark half of the palette,
/// and a swatch showing black for a trace drawn white would be the one place the two disagree.
/// Both brushes are made once; <see cref="Brush"/> follows the application's theme variant.</para>
/// </summary>
public sealed class ColorItem : INotifyPropertyChanged
{
    public int     Index { get; }
    public SKColor Color { get; }
    public string  Name  { get; }
    public IBrush  Brush => IsDarkTheme ? _darkBrush : _lightBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IBrush _lightBrush;
    private readonly IBrush _darkBrush;

    // The LUT is SKColor since RND-4 moved TraceProperties below the UI firewall; the swatch this
    // item paints is still an Avalonia brush, so the conversion happens once, here, at the boundary.
    public ColorItem(int index, SKColor color, string name)
    {
        Index = index;
        Color = color;
        Name  = name;
        _lightBrush = ToBrush(RenderTheme.Light.TraceColor(color));
        _darkBrush  = ToBrush(RenderTheme.Dark.TraceColor(color));
    }

    /// <summary>Re-reads the theme variant: called by the picker's list when the application theme changes.</summary>
    internal void ThemeChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));

    // Same test DisplayWindowViewModel uses to pick RenderTheme.Light or .Dark for the canvas.
    private static bool IsDarkTheme => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static IBrush ToBrush(SKColor c) =>
        new SolidColorBrush(Avalonia.Media.Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

    public override string ToString() => Name;
}
