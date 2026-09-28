// brief-em3d-75 R-em3d75-4e — the probe table's two styles: a measure in italics, a probe past its LimitC in the warning colour.

using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CircuitRF.Ui.ThreeD;

public static class C3dProbeTableStyle
{
    public static readonly IValueConverter Italic = new FuncValueConverter<bool, FontStyle>(b => b ? FontStyle.Italic : FontStyle.Normal);

    public static readonly IValueConverter Flag = new FuncValueConverter<bool, IBrush?>(b => b ? Brushes.OrangeRed : null);
}
