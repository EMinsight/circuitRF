// The analysis range a recognised circuit is written with — overview D15; brief-artsch-7 R-as7-4 (the CLI's
// --start/--stop/--npts) and brief-artsch-8 R-as8-2 (the dialog's Frequency fields).
//
// ONE composition for both surfaces (overview D2): each field the caller leaves alone keeps the basis's — the
// layout's EM setup's sweep, else 100 MHz – 6 GHz in 201 points — and nothing stated at all is null, the run's own
// default. A frequency is a number WITH its unit, spelled as an analysis line spells it; a bare number is not one.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>A frequency as typed: its number text, its unit, and its value in Hz.</summary>
public readonly record struct RecognitionFrequency(string Text, string Unit, double Hz);

/// <summary>The sweep a recognition is emitted with.</summary>
public static class RecognitionSweep
{
    private static readonly Regex FrequencyText =
        new(@"^\s*([0-9]*\.?[0-9]+(?:[eE][+-]?[0-9]+)?)\s*(Hz|kHz|MHz|GHz|THz)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A frequency with its unit — <c>100 MHz</c>, <c>6GHz</c> — or null. A bare number is not one.</summary>
    public static RecognitionFrequency? Parse(string? text)
    {
        var m = FrequencyText.Match(text ?? "");
        if (!m.Success) return null;
        string unit = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "hz" => "Hz", "khz" => "kHz", "mhz" => "MHz", "ghz" => "GHz", _ => "THz",
        };
        double scale = unit switch { "Hz" => 1, "kHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, _ => 1e12 };
        return new RecognitionFrequency(m.Groups[1].Value, unit,
                                        double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * scale);
    }

    /// <summary>The sweep the emit starts from: the EM setup's, else <see cref="RecognitionEmitOptions.DefaultSweep"/>.</summary>
    public static FrequencySpec Basis(EmSetup? setup) => setup?.Frequency ?? RecognitionEmitOptions.DefaultSweep;

    /// <summary>
    /// The sweep with each stated field replacing the basis's — null when nothing is stated, which is the run's own
    /// default. <paramref name="npts"/> must be at least 2 where stated.
    /// </summary>
    public static FrequencySpec? Compose(EmSetup? setup, RecognitionFrequency? start, RecognitionFrequency? stop, int? npts)
    {
        if (start is null && stop is null && npts is null) return null;
        var basis = Basis(setup);
        var (startText, startUnit) = start is { } s ? (s.Text, s.Unit) : (basis.StartExpr, basis.StartUnit);
        var (stopText, stopUnit)   = stop  is { } e ? (e.Text, e.Unit) : (basis.StopExpr, basis.StopUnit);
        return new FrequencySpec(startText, stopText,
                                 npts ?? basis.NumPoints ?? RecognitionEmitOptions.DefaultSweep.NumPoints!.Value,
                                 basis.Kind, startUnit, stopUnit);
    }
}
