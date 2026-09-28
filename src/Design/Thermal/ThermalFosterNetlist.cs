// brief-em3d-80 R-em3d80-5 (D10) — a source's fitted self Z_th written as a .cnl subcircuit: Rᵢ ∥ Cᵢ stages in series between
// a thermal pin and a reference pin, in the electrical analogy (power ↔ current, temperature rise ↔ voltage). It is NOT
// attached to any device's thermal node (owner: later). The file also carries a one-port test bench — a port on the thermal
// pin and a logarithmic S-parameter sweep over the band the fit covers — so `circuitrf sparam` on it reads the network's
// Z_th back as Z = Z₀(1 + S11)/(1 − S11), and `check` passes on it as written.
//
// Values are written round-trip ("R"), so the network IS the fit to the last bit: the gate compares an AC analysis of this
// file against the fitted Z(jω) to 1e-9.

using System.Globalization;
using System.Text;
using CircuitRF.Thermal.Frequency;

namespace CircuitRF.Design.Thermal;

public static class ThermalFosterNetlist
{
    /// <summary>The thermal pin's name.</summary>
    public const string ThermalPin = "th";

    /// <summary>The reference pin's name.</summary>
    public const string ReferencePin = "ref";

    /// <summary>Where source <paramref name="source"/>'s network is written: <c>&lt;results&gt;/&lt;key&gt;.thermal.&lt;source&gt;.foster.cnl</c>.</summary>
    public static string PathFor(string resultsRoot, string resultKey, string source)
        => Path.Combine(resultsRoot, $"{resultKey}.{source}.foster.cnl");

    /// <summary>The subcircuit's cell name: <c>Foster_</c> and the source's name with anything but a letter, digit or
    /// underscore made an underscore.</summary>
    public static string CellName(string source)
        => "Foster_" + string.Concat(source.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_'));

    /// <summary>The file's text.</summary>
    public static string Write(string source, string setup, FosterNetwork network, double startHz, double stopHz, int points)
    {
        if (network.Terms.Count == 0) throw new ArgumentException("an empty network", nameof(network));
        var inv = CultureInfo.InvariantCulture;
        string cell = CellName(source);
        double z0 = double.Parse(network.Rth.ToString("G3", inv), inv);
        var b = new StringBuilder();
        b.AppendLine($"; circuitRF — the Foster thermal network of heat source '{source}', fitted by thermal setup '{setup}'.");
        b.AppendLine("; The electrical analogy: power (W) is current (A) and temperature rise (K) is voltage (V), so a resistance in Ohm is");
        b.AppendLine("; K/W and a capacitance in F is J/K. Pin 'th' takes the source's power; pin 'ref' is the reference (the heatsink).");
        b.AppendLine("; FOSTER form: the stages' internal nodes are NOT physical temperatures — only the voltage across the pins (the rise");
        b.AppendLine($"; of '{source}''s own place, its mean, per watt in it) is. Not attached to any device's thermal node.");
        b.AppendLine($"; Fitted to Z_th over {Num(startHz)} Hz – {Num(stopHz)} Hz: {network.Terms.Count} stage(s), largest relative error " +
                     $"{(100 * network.FitError).ToString("F3", inv)} %, ΣR = Rth = {Num(network.Rth)} K/W.");
        b.AppendLine();
        b.AppendLine($"define {cell} ( {ThermalPin} {ReferencePin} )");
        for (int i = 0; i < network.Terms.Count; i++)
        {
            var t = network.Terms[i];
            string a = i == 0 ? ThermalPin : $"n{i}";
            string c = i == network.Terms.Count - 1 ? ReferencePin : $"n{i + 1}";
            b.AppendLine($"  R:R{i + 1} {a} {c} R={Value(t.R)} Ohm");
            b.AppendLine($"  C:C{i + 1} {a} {c} C={Value(t.C)} F");
        }
        b.AppendLine($"end {cell}");
        b.AppendLine();
        b.AppendLine($"; A one-port bench: Z_th = Z0 (1 + S11) / (1 - S11) with Z0 = {Num(z0)} Ohm (K/W).");
        b.AppendLine($"Port:P1 {ThermalPin} 0 Num=1 Z={Value(z0)} Ohm");
        b.AppendLine($"{cell}:X1 {ThermalPin} 0");
        b.AppendLine($"analysis ZTH type=sparam start={Value(startHz)} stop={Value(stopHz)} npts={Math.Max(points, 2)} log Unit=Hz");
        return b.ToString();
    }

    private static string Value(double v) => v.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');

    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
