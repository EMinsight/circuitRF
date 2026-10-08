using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Tuning;

/// <summary>The words a menu offers a complex value's parts by (overview D18).</summary>
public static class TunePartText
{
    /// <summary><c>Real</c>, <c>Imaginary</c>, <c>Magnitude</c>, <c>Phase (deg)</c> for a part key; the
    /// key itself for anything else.</summary>
    public static string Label(string key)
        => TunableKey.TryParse(key, out var k) && k.Part is { } part
            ? part switch
            {
                ComplexPart.Real => "Real",
                ComplexPart.Imag => "Imaginary",
                ComplexPart.Mag  => "Magnitude",
                _                => "Phase (deg)",
            }
            : key;
}
