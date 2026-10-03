// ================================================================
//  Misc.cs  —  Plot-specific enums and TraceProperties
//
//  Ported from splotRF/src/Models/Misc.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay; font/color seams are in the Renderers layer.
// ================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    // ============================================================
    //  PlotDetail
    // ============================================================

    public enum PlotDetail { Full, Medium, Quick }

    public static class PlotDetailExtensions
    {
        public static (bool MinorTicks, bool MajorTicks, bool Labels)
            Properties(this PlotDetail d) => d switch
        {
            PlotDetail.Full   => (true,  true,  true),
            PlotDetail.Medium => (false, true,  true),
            PlotDetail.Quick  => (false, false, false),
            _                 => (false, false, false)
        };
    }

    // ============================================================
    //  MarkerType / LineType
    // ============================================================

    /// <summary>
    /// The per-point symbol a trace draws.
    ///
    /// <para><b>The member NAME is what a `.cdd` persists</b> (the config carries a
    /// <c>JsonStringEnumConverter</c>), so <c>Circle</c> and <c>Square</c> keep their spelling and
    /// every file written before the rest existed still loads. Order is display order in the trace
    /// card's picker; the numeric values are not written anywhere.</para>
    ///
    /// <para>Geometry for all of them lives in one place — <see cref="MarkerGlyph"/> — which both
    /// the renderer and the picker read.</para>
    /// </summary>
    public enum MarkerType
    {
        Circle, Square, Diamond,
        TriangleUp, TriangleDown, TriangleLeft, TriangleRight,
        Plus, Cross, Star,
        Pentagon, Hexagon,
        Bowtie, Hourglass
    }

    /// <summary>How a trace's line is stroked. <b>Append only</b>: a <c>.cdd</c> stores the member,
    /// and a reading written by an older build must mean what it meant.</summary>
    public enum LineType   { Solid,  Dashed,  Dotted }

    // ============================================================
    //  PrecisionFormat
    // ============================================================

    /// <summary>
    /// How a number is written in a table cell or a marker readout.
    ///
    /// <para><c>S</c> is APPENDED — the member name is what a `.cdd` persists, so the three that
    /// were here keep their spelling and a file written before it existed still loads.</para>
    /// </summary>
    public enum PrecisionFormat { G, F, E, S }

    public static class PrecisionFormatExtensions
    {
        /// <summary>
        /// Format <paramref name="value"/> in this precision.
        ///
        /// <para><b>Every consumer must come through here.</b> The three original members happen to
        /// be .NET standard numeric format specifiers, so the display built the format string by
        /// concatenation — <c>$"{FormatString}{MaximumFractionDigits}"</c> — in eight places.
        /// <c>S</c> is not a standard specifier: <c>ToString("S3")</c> throws
        /// <see cref="FormatException"/>, so every one of those eight sites would have crashed on a
        /// marker or a table cell the moment Engineering was selected. Concatenating a format string
        /// from an enum only ever worked by coincidence; this is the seam that makes it safe.</para>
        /// </summary>
        public static string Format(this PrecisionFormat f, double value, int digits) =>
            f == PrecisionFormat.S
                ? EngineeringFormat.Value(value, digits)
                : value.ToString($"{f}{digits}");

        public static string Description(this PrecisionFormat f) => f switch
        {
            PrecisionFormat.G => "Auto",
            PrecisionFormat.F => "Fixed",
            PrecisionFormat.E => "Scientific",
            // The only format that can write a capacitance. "0.000" is what Fixed makes of 100 pF.
            PrecisionFormat.S => "Engineering",
            _                 => f.ToString()
        };
    }

    // ============================================================
    //  TraceProperties  —  per-trace visual state
    // ============================================================

    /// <summary>
    /// Stores line, marker, and fill visual properties for one trace.
    /// Colours are stored as <see cref="SKColor"/>; <see cref="RenderTheme.ToSKColor"/>
    /// applies the per-trace opacity on the way to a paint.
    /// </summary>
    public class TraceProperties
    {
        // ---- Color look-up table ----------------------------------------

        // The seventeen colours the trace-colour combo offers, as explicit ARGB.
        //
        // These were Avalonia's `Colors.*` constants until RND-4 moved this model below the UI
        // firewall. They are written out rather than swapped for `SKColors.*` because the two
        // libraries DISAGREE on one of them: Avalonia's Transparent is #00FFFFFF and Skia's is
        // #00000000. That is invisible while the colour is transparent — and not invisible at all
        // once `RenderTheme.ToSKColor` overrides the alpha with the trace's own opacity, where the
        // same stored choice renders white on one and black on the other. Every other name here is
        // the standard CSS value both libraries already agree on; Transparent is the reason none of
        // them is taken on trust.
        public static readonly Dictionary<int, SKColor> ColorLUT = new()
        {
            {  0, new SKColor(0xFF000000) },   // Black
            {  1, new SKColor(0xFF0000FF) },   // Blue
            {  2, new SKColor(0xFFA52A2A) },   // Brown
            {  3, new SKColor(0x00FFFFFF) },   // Transparent (Avalonia's, deliberately)
            {  4, new SKColor(0xFF00FFFF) },   // Cyan
            {  5, new SKColor(0xFF808080) },   // Gray
            {  6, new SKColor(0xFF008000) },   // Green
            {  7, new SKColor(0xFF4B0082) },   // Indigo
            {  8, new SKColor(0xFF3EB489) },   // mint
            {  9, new SKColor(0xFFFFA500) },   // Orange
            { 10, new SKColor(0xFFFFC0CB) },   // Pink
            { 11, new SKColor(0xFF800080) },   // Purple
            { 12, new SKColor(0xFFFF0000) },   // Red
            { 13, new SKColor(0xFF008080) },   // Teal
            { 14, new SKColor(0xFFFFFFFF) },   // White
            { 15, new SKColor(0xFFFFFF00) },   // Yellow
            { 16, new SKColor(0xFF6495ED) }    // CornflowerBlue (accent)
        };

        public static readonly int[]        LineColorOrder  = { 12, 1, 9, 6, 11, 2, 10, 4, 5, 0, 7 };
        public static readonly double[]     LineWidthOrder  = { 1.0, 0.75 };
        public static readonly MarkerType[] MarkerTypeOrder = Enum.GetValues<MarkerType>();

        public static SKColor DefaultLineColor   = ColorLUT[12];   // Red
        public static SKColor DefaultMarkerColor = ColorLUT[12];   // Red
        public static SKColor DefaultFillColor   = ColorLUT[16];   // CornflowerBlue

        // ---- Identity ---------------------------------------------------

        public string Id { get; set; } = string.Empty;

        // ---- Enabled flag -----------------------------------------------

        public bool Enabled { get; set; } = false;

        // ---- Line -------------------------------------------------------

        private int    _lineColorIndex = LineColorOrder[0];
        private SKColor? _lineColorStorage;
        private double _lineWidth   = LineWidthOrder[0];
        private double _lineOpacity = 1.0;
        private bool   _lineEnabled = true;
        private LineType _lineType  = LineType.Solid;

        public int LineColorIndex
        {
            get => _lineColorIndex;
            set { _lineColorIndex = value; Custom = true; }
        }
        public SKColor? LineColorStorage
        {
            get => _lineColorStorage;
            set { _lineColorStorage = value; Custom = true; }
        }
        public double LineWidth
        {
            get => _lineWidth;
            set { _lineWidth = value; Custom = true; }
        }
        public double LineOpacity
        {
            get => _lineOpacity;
            set { _lineOpacity = value; Custom = true; }
        }
        public bool LineEnabled
        {
            get => _lineEnabled;
            set { _lineEnabled = value; Custom = true; }
        }
        public LineType LineType
        {
            get => _lineType;
            set { _lineType = value; Custom = true; }
        }

        // ---- Marker -----------------------------------------------------

        private int       _markerColorIndex = LineColorOrder[0];
        private SKColor?    _markerColorStorage;
        private double    _markerSize    = 1.5;
        private double    _markerOpacity = 1.0;
        private bool      _markerEnabled = false;
        private MarkerType _markerType   = MarkerType.Circle;

        public int MarkerColorIndex
        {
            get => _markerColorIndex;
            set { _markerColorIndex = value; Custom = true; }
        }
        public SKColor? MarkerColorStorage
        {
            get => _markerColorStorage;
            set { _markerColorStorage = value; Custom = true; }
        }
        public double MarkerSize
        {
            get => _markerSize;
            set { _markerSize = value; Custom = true; }
        }
        public double MarkerOpacity
        {
            get => _markerOpacity;
            set { _markerOpacity = value; Custom = true; }
        }
        public bool MarkerEnabled
        {
            get => _markerEnabled;
            set { _markerEnabled = value; Custom = true; }
        }
        public MarkerType MarkerType
        {
            get => _markerType;
            set { _markerType = value; Custom = true; }
        }

        // ---- Fill -------------------------------------------------------

        private int    _fillColorIndex = LineColorOrder[0];
        private SKColor? _fillColorStorage;
        private double _fillOpacity    = 0.0;

        public int FillColorIndex
        {
            get => _fillColorIndex;
            set { _fillColorIndex = value; Custom = true; }
        }
        public SKColor? FillColorStorage
        {
            get => _fillColorStorage;
            set { _fillColorStorage = value; Custom = true; }
        }
        public double FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = value; Custom = true; }
        }

        // ---- Resolved colors --------------------------------------------

        public SKColor LineColor =>
            LineColorStorage
            ?? (ColorLUT.TryGetValue(LineColorIndex, out var c) ? c : DefaultLineColor);

        public SKColor MarkerColor =>
            MarkerColorStorage
            ?? (ColorLUT.TryGetValue(MarkerColorIndex, out var c) ? c : DefaultMarkerColor);

        public SKColor FillColor =>
            FillColorStorage
            ?? (ColorLUT.TryGetValue(FillColorIndex, out var c) ? c : DefaultFillColor);

        // ---- Custom flag ------------------------------------------------

        public bool Custom { get; set; } = false;

        // ---- Constructors -----------------------------------------------

        public TraceProperties() { }

        public TraceProperties(string fileName) { Id = fileName; }

        /// <summary>Copy constructor; optionally cycles the color forward.</summary>
        public TraceProperties(TraceProperties src, int incrementColorBy = 0)
        {
            Id      = src.Id;
            Enabled = src.Enabled;
            CopyProperties(src);

            if (incrementColorBy > 0)
            {
                int ci = Array.IndexOf(LineColorOrder, LineColorIndex);
                if (ci >= 0)
                {
                    int next = ci + incrementColorBy;
                    int idx  = next < LineColorOrder.Length
                        ? LineColorOrder[next]
                        : LineColorOrder[0];
                    _lineColorIndex   = idx;
                    _markerColorIndex = idx;
                    _lineColorStorage   = null;
                    _markerColorStorage = null;
                    Custom = false;
                }
            }
        }

        /// <summary>Creates a secondary-axis–styled properties object.</summary>
        public static TraceProperties ForSecondary(string fileName = "")
        {
            return new TraceProperties(fileName)
            {
                _lineColorIndex   = LineColorOrder[1],
                _markerColorIndex = LineColorOrder[1],
                _lineWidth        = LineWidthOrder[1]
            };
        }

        // ---- Copy / comparison ------------------------------------------

        public void CopyProperties(TraceProperties src)
        {
            _lineColorIndex    = src._lineColorIndex;
            _lineColorStorage  = src._lineColorStorage;
            _lineWidth         = src._lineWidth;
            _lineOpacity       = src._lineOpacity;
            _lineEnabled       = src._lineEnabled;
            _lineType          = src._lineType;

            _markerColorIndex   = src._markerColorIndex;
            _markerColorStorage = src._markerColorStorage;
            _markerSize         = src._markerSize;
            _markerOpacity      = src._markerOpacity;
            _markerEnabled      = src._markerEnabled;
            _markerType         = src._markerType;

            _fillColorStorage = src._fillColorStorage;
            _fillColorIndex   = src._fillColorIndex;
            _fillOpacity      = src._fillOpacity;
            Custom            = src.Custom;
        }

        public bool HasSameProperties(TraceProperties rhs)
        {
            return _lineWidth        == rhs._lineWidth
                && _lineOpacity      == rhs._lineOpacity
                && Custom            == rhs.Custom
                && _lineColorStorage == rhs._lineColorStorage
                && _lineEnabled      == rhs._lineEnabled
                && _lineType         == rhs._lineType
                && _markerSize       == rhs._markerSize
                && _markerOpacity    == rhs._markerOpacity
                && _markerColorStorage == rhs._markerColorStorage
                && _markerEnabled    == rhs._markerEnabled
                && _markerType       == rhs._markerType;
        }

        public override bool Equals(object? obj) =>
            obj is TraceProperties rhs && Id == rhs.Id;

        public override int GetHashCode() => Id.GetHashCode();
    }
}
