// ================================================================
//  ComboItems.cs  —  lightweight wrapper types for typed ComboBoxes
//
//  YAxisItem    — DependentVarFormat entry with a proper label and
//                 IsEnabled flag (Complex is disabled on Rect plots).
//
//  MarkerTypeItem — MarkerType entry paired with the glyph GEOMETRY the
//                   renderer draws, so the picker shows the real shape.
// ================================================================

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

// ---- YAxisItem -------------------------------------------------------------

public sealed class YAxisItem
{
    public DependentVarFormat Format    { get; }
    public string             Label     { get; }
    public bool               IsEnabled { get; }

    public YAxisItem(DependentVarFormat format, bool enabled = true)
    {
        Format    = format;
        IsEnabled = enabled;

        // DependentVarFormat.Db is the compiler name; show it as "dB".
        Label = format switch
        {
            DependentVarFormat.Db        => "dB",
            DependentVarFormat.Mag       => "Mag",
            DependentVarFormat.Phase     => "Phase°",
            DependentVarFormat.Real      => "Real",
            DependentVarFormat.Imaginary => "Imag",
            DependentVarFormat.Complex   => "Complex",
            _                            => format.ToString()
        };
    }
}

// ---- PrecisionFormatConverter -----------------------------------------------
//  Used in MarkerEditorView to show human-readable names for PrecisionFormat enum values.

public sealed class PrecisionFormatConverter : IValueConverter
{
    public static readonly PrecisionFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PrecisionFormat f ? f.Description() : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// ---- MarkerStyleConverter ---------------------------------------------------
//  Used in MarkerEditorView to show compact S/M/L/XL labels in the Size ComboBox.

public sealed class MarkerStyleConverter : IValueConverter
{
    public static readonly MarkerStyleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MarkerStyle s ? s.ShortDescription() : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// ---- MatrixFormatConverter --------------------------------------------------
//  Used in MarkerEditorView's Format ComboBox to show the conventional display
//  casing (mA / RI / dB) rather than the raw enum names (MA / RI / DB).

public sealed class MatrixFormatConverter : IValueConverter
{
    public static readonly MatrixFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MatrixFormat f
            ? f switch
              {
                  MatrixFormat.MA => "mA",
                  MatrixFormat.RI => "RI",
                  MatrixFormat.DB => "dB",
                  _               => f.ToString(),
              }
            : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// ---- MarkerTypeItem --------------------------------------------------------
//  A MarkerType paired with the GEOMETRY the plot draws for it, parsed once from
//  MarkerGlyph's own outline. The picker used to carry a hand-picked MaterialIcon
//  per shape; with fourteen shapes that is fourteen chances for the icon and the
//  plot to disagree, and the icon set has no bowtie or hourglass at all.

public sealed class MarkerTypeItem
{
    /// <summary>Glyph radius, in px, for the picker's icons.</summary>
    public const double IconRadius = 5.0;

    private Geometry? _glyph;

    public MarkerType Value     { get; }
    public string     GlyphData { get; }
    public string     Label     { get; }

    /// <summary>
    /// <b>Parsed on first bind, never in the static list that builds these.</b>
    /// <c>Geometry.Parse</c> reaches for <c>IPlatformRenderInterface</c>, so parsing eagerly gives
    /// the option lists a dependency on Avalonia being up — and a static initializer that throws
    /// takes the whole inspector with it rather than one icon.
    /// </summary>
    public Geometry Glyph => _glyph ??= Geometry.Parse(GlyphData);

    public MarkerTypeItem(MarkerType value)
    {
        Value     = value;
        GlyphData = MarkerGlyph.SvgPath(value, IconRadius);
        Label     = MarkerGlyph.Description(value);
    }
}

// ---- LineModeItem ----------------------------------------------------------
// Merges Line on/off + LineType into a single icon-pick option (§C of brief 7.1d-1-r2).
// IsOff=true → line disabled; IsOff=false → line enabled with the given Type.

public sealed class LineModeItem
{
    public bool     IsOff   { get; }
    public LineType Type    { get; }
    public double   Opacity => IsOff ? 0.25 : 1.0;

    public LineModeItem(bool isOff, LineType type) { IsOff = isOff; Type = type; }
}

// ---- CubeTransformItem -----------------------------------------------------
// Wraps a CubeTransform enum value with a human-readable label.
// Enabled=false marks cube-only entries that are shown greyed for network traces.

public sealed class CubeTransformItem
{
    public CubeTransform Transform { get; }
    public string        Label     { get; }
    public bool          Enabled   { get; }

    public CubeTransformItem(CubeTransform transform, bool enabled = true)
    {
        Transform = transform;
        Enabled   = enabled;
        Label = transform switch
        {
            CubeTransform.None  => "None",
            CubeTransform.dB20  => "dB20",
            CubeTransform.dB10  => "dB10",
            CubeTransform.dB    => "dB",
            CubeTransform.Mag   => "Mag",
            CubeTransform.Phase => "Phase°",
            CubeTransform.Real  => "Real",
            CubeTransform.Imag  => "Imag",
            CubeTransform.Conj  => "Conj",
            CubeTransform.PhaseUnwrapped => "Unwrap°",
            _                   => transform.ToString()
        };
    }
}

// ---- SymbolModeItem --------------------------------------------------------
// Merges Marker on/off + MarkerType into a single icon-pick option.

public sealed class SymbolModeItem
{
    private Geometry? _glyph;

    public bool       IsOff     { get; }
    public MarkerType Shape     { get; }
    public string?    GlyphData { get; }
    public string     Label     { get; }
    public double     Opacity   => IsOff ? 0.35 : 1.0;

    /// <summary>The "Off" row draws no glyph — the template shows its own struck-through icon.</summary>
    public bool IsOn => !IsOff;

    /// <summary>Parsed on first bind — see <see cref="MarkerTypeItem.Glyph"/> for why not sooner.</summary>
    public Geometry? Glyph =>
        GlyphData is null ? null : _glyph ??= Geometry.Parse(GlyphData);

    public SymbolModeItem(bool isOff, MarkerType shape)
    {
        IsOff     = isOff;
        Shape     = shape;
        GlyphData = isOff ? null : MarkerGlyph.SvgPath(shape, MarkerTypeItem.IconRadius);
        Label     = isOff ? "Off" : MarkerGlyph.Description(shape);
    }
}
