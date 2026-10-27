using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// A synthetic vendor part library, generated at test time (overview §3 of the vendor-library series):
/// made-up part names, the header shapes a manufacturer's static SPICE edition uses, and nothing
/// downloaded. Three folders, six files, 300 parts — one file encrypted-shaped, one empty, and one
/// plain-text read-me that holds no part.
/// </summary>
internal static class VendorLibraryFixture
{
    public const int Parts = 300;

    /// <summary>Writes the library under <paramref name="root"/> and returns every part name in it.</summary>
    public static List<string> Write(string root)
    {
        var names = new List<string>();
        Directory.CreateDirectory(Path.Combine(root, "capacitors"));
        Directory.CreateDirectory(Path.Combine(root, "inductors"));
        Directory.CreateDirectory(Path.Combine(root, "general"));

        File.WriteAllText(Path.Combine(root, "capacitors", "series-a.lib"), Capacitors("A", 120, spaced: true, names));
        File.WriteAllText(Path.Combine(root, "general", "series-g.lib"), Capacitors("G", 100, spaced: false, names));
        File.WriteAllText(Path.Combine(root, "inductors", "series-l.lib"), Inductors(80, names));
        File.WriteAllText(Path.Combine(root, "capacitors", "readme.txt"),
            "Read me.\nThese models are small-signal, at 25 degC and zero bias.\n");
        File.WriteAllText(Path.Combine(root, "inductors", "empty.lib"), "");
        File.WriteAllText(Path.Combine(root, "general", "protected.lib"), EncodedBody("TSTENC0001"));
        return names;
    }

    /// <summary>A file whose one part's body is an encoded block — the shape of an encrypted edition.</summary>
    public static string EncodedBody(string part)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $".SUBCKT {part} Port1 Port2\n");
        var rng = new Random(7);
        for (int i = 0; i < 6; i++)
        {
            var bytes = new byte[48];
            rng.NextBytes(bytes);
            sb.Append(Convert.ToBase64String(bytes)).Append('\n');
        }
        sb.Append(CultureInfo.InvariantCulture, $".ENDS {part}\n");
        return sb.ToString();
    }

    private static readonly (string Metric, string Imperial)[] Sizes =
        [("0603M", "0201"), ("1005M", "0402"), ("1608M", "0603")];

    /// <summary>
    /// Capacitors with a Description header. <paramref name="spaced"/> picks between two editions'
    /// layouts: <c>* Property : C = …</c> with bracketed units, or <c>*Property:C=…</c> with bare ones.
    /// </summary>
    private static string Capacitors(string series, int count, bool spaced, List<string> names)
    {
        var sb = new StringBuilder("* Synthetic test library. Not a real product.\n\n");
        for (int i = 0; i < count; i++)
        {
            var (metric, imperial) = Sizes[i % Sizes.Length];
            double pf = 1.0 + i * 0.5;
            string name = $"TSTC{imperial}{series}{i:D4}";
            names.Add(name);
            string uf = (pf * 1e-6).ToString("0.00e+00", CultureInfo.InvariantCulture);

            sb.Append("*****************************************\n");
            if (spaced)
            {
                sb.Append(CultureInfo.InvariantCulture, $"* Description : {metric}({imperial})/C0G(-55~125degC)/{pf}[pF]/50[V]\n");
                sb.Append(CultureInfo.InvariantCulture, $"* Capacitor P/N : {name}\n");
                sb.Append(CultureInfo.InvariantCulture, $"* Property : C = {uf}[uF]\n");
                sb.Append("*   Frequency Range = 100[MHz]-30[GHz]\n");
                sb.Append("*   Temperature = 25 degC\n");
                sb.Append("*   DC Bias Voltage = 0V\n");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"*Description:{metric}({imperial})/X7R(-55~125degC)/{pf}pF/25V\n");
                sb.Append(CultureInfo.InvariantCulture, $"*P/N:{name}\n");
                sb.Append(CultureInfo.InvariantCulture, $"*Property:C={pf}pF\n");
                sb.Append("*Frequency Range=50MHz - 20GHz\n");
                sb.Append("*Temperature=25 degC\n");
            }
            sb.Append("*****************************************\n");
            sb.Append(CultureInfo.InvariantCulture, $".SUBCKT {name} Port1 Port2\n");
            sb.Append(CultureInfo.InvariantCulture, $"C1 Port1 n1 {pf}p\n");
            sb.Append("L1 n1 n2 0.3n\n");
            sb.Append("R1 n2 Port2 0.05\n");
            sb.Append(CultureInfo.InvariantCulture, $".ENDS {name}\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Inductors: no Description line, the other range spelling, a current-bias line.</summary>
    private static string Inductors(int count, List<string> names)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            double nh = 1.0 + i * 0.1;
            string name = $"TSTL0603B{i:D4}";
            names.Add(name);
            sb.Append(CultureInfo.InvariantCulture, $"* Inductor P/N : {name}\n");
            sb.Append(CultureInfo.InvariantCulture, $"* Property : L = {nh:0.0}[nH]\n");
            sb.Append("*   Frequency Range = 50MHz - 20GHz\n");
            sb.Append("*   Temperature = 25 degC\n");
            sb.Append("*   DC Bias Current = 0 A\n");
            sb.Append(CultureInfo.InvariantCulture, $".subckt {name} 1 2\n");
            sb.Append(CultureInfo.InvariantCulture, $"L1 1 n1 {nh:0.0}n Rser=0.1\n");
            sb.Append("C1 n1 2 0.05p\n");
            sb.Append(CultureInfo.InvariantCulture, $".ends {name}\n");
        }
        return sb.ToString();
    }
}
