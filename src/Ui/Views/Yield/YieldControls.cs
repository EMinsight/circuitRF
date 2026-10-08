using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Yield;

/// <summary>
/// A fraction drawn on a 0…1 track (brief-yield-10 R-ya10-6): the interval <see cref="Lower"/>…<see cref="Upper"/> as
/// a band, the <see cref="Value"/> as a tick, and the <see cref="Target"/> as a marker. A share bar is the same control
/// with only <see cref="Upper"/> set. It paints from its properties and nothing else, so a run's per-batch update is a
/// property set and a redraw.
/// </summary>
public sealed class FractionBar : Control
{
    public static readonly StyledProperty<double> LowerProperty = AvaloniaProperty.Register<FractionBar, double>(nameof(Lower));
    public static readonly StyledProperty<double> UpperProperty = AvaloniaProperty.Register<FractionBar, double>(nameof(Upper));
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<FractionBar, double>(nameof(Value), double.NaN);
    public static readonly StyledProperty<double?> TargetProperty = AvaloniaProperty.Register<FractionBar, double?>(nameof(Target));
    public static readonly StyledProperty<bool> IsBadProperty = AvaloniaProperty.Register<FractionBar, bool>(nameof(IsBad));

    static FractionBar() => AffectsRender<FractionBar>(LowerProperty, UpperProperty, ValueProperty, TargetProperty, IsBadProperty);

    public double Lower { get => GetValue(LowerProperty); set => SetValue(LowerProperty, value); }
    public double Upper { get => GetValue(UpperProperty); set => SetValue(UpperProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public bool IsBad { get => GetValue(IsBadProperty); set => SetValue(IsBadProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double y = (b.Height - 6) / 2, w = b.Width;
        static double C(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), null, new Rect(0, y, w, 6), 2, 2);
        var fill = IsBad ? Color.FromRgb(0xD9, 0x4F, 0x4F) : Color.FromRgb(0x3C, 0x8D, 0xD9);
        double lo = C(Lower), hi = C(Upper);
        if (hi > lo) ctx.DrawRectangle(new SolidColorBrush(fill, 0.75), null, new Rect(w * lo, y, Math.Max(1, w * (hi - lo)), 6), 2, 2);
        if (double.IsFinite(Value))
            ctx.DrawRectangle(new SolidColorBrush(fill), null, new Rect(Math.Clamp(w * C(Value) - 1, 0, w - 2), y - 2, 2, 10));
        if (Target is { } t && double.IsFinite(t))
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00)), 1.5);
            double x = Math.Clamp(w * C(t), 1, w - 1);
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, b.Height));
        }
    }
}
