// brief-dfr32-4 R-dfr32-4-2 — the power each driven port loses, said in the run summary.
//
// Driving port j with every other port matched, the power that does not come back out of some port is 1 − Σᵢ|Sᵢⱼ|². It is
// what a lossless circuit model cannot show and what |S21| hides behind mismatch, so a solve that loses power where the
// bench does not is visible in the run's own lines rather than only after a comparison. Read at the band's two ends,
// which is enough to tell a frequency-flat loss from one that grows with frequency (dielectric, radiation).
//
// A port is flagged when it loses several times the median port's, and more than a floor, so a structure whose every
// port is nearly lossless does not flag 0.3 % against 0.1 %.

using System.Globalization;
using System.Numerics;

namespace CircuitRF.Design.Em3d;

public static class Em3dPortLoss
{
    /// <summary>A port is flagged when its loss is more than this many times the median port's.</summary>
    public const double OutlierFactor = 3;

    /// <summary>…and more than this fraction of its power, so near-lossless ports never flag one another.</summary>
    public const double OutlierFloor = 0.01;

    /// <summary>1 − Σᵢ|Sᵢⱼ|² for each column j of <paramref name="s"/>: the fraction of the power driven into port j that
    /// leaves the model other than through a port.</summary>
    public static double[] Lost(Complex[,] s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int n = s.GetLength(0);
        var lost = new double[s.GetLength(1)];
        for (int j = 0; j < lost.Length; j++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += s[i, j].Real * s[i, j].Real + s[i, j].Imaginary * s[i, j].Imaginary;
            lost[j] = 1 - sum;
        }
        return lost;
    }

    /// <summary>The indices of <paramref name="lost"/> that are outliers: more than <see cref="OutlierFactor"/> times the
    /// median and more than <see cref="OutlierFloor"/>.</summary>
    public static IReadOnlyList<int> Outliers(IReadOnlyList<double> lost)
    {
        if (lost.Count < 2) return [];
        var sorted = lost.OrderBy(v => v).ToArray();
        int m = sorted.Length / 2;
        double median = sorted.Length % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
        double bar = Math.Max(OutlierFactor * Math.Max(median, 0), OutlierFloor);
        return [.. Enumerable.Range(0, lost.Count).Where(k => lost[k] > bar)];
    }

    /// <summary>
    /// The run-summary line: each port's loss at the band's lowest and highest frequency, and the ports that lose several
    /// times the median, or null when there is no frequency. <paramref name="s"/>[k] is the matrix at
    /// <paramref name="frequenciesHz"/>[k]; <paramref name="portNumbers"/>[j] is column j's port number.
    /// </summary>
    public static string? Line(IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex[,]> s, IReadOnlyList<int> portNumbers)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(portNumbers);
        if (frequenciesHz.Count == 0 || s.Count != frequenciesHz.Count || portNumbers.Count == 0) return null;
        int lo = 0, hi = 0;
        for (int k = 1; k < frequenciesHz.Count; k++)
        {
            if (frequenciesHz[k] < frequenciesHz[lo]) lo = k;
            if (frequenciesHz[k] > frequenciesHz[hi]) hi = k;
        }
        var ends = lo == hi ? new[] { lo } : [lo, hi];
        var at = new List<string>();
        var flagged = new SortedSet<int>();
        foreach (int k in ends)
        {
            var lost = Lost(s[k]);
            at.Add($"at {Ghz(frequenciesHz[k])} " + string.Join(", ",
                Enumerable.Range(0, Math.Min(lost.Length, portNumbers.Count)).Select(j => $"port {portNumbers[j]} {Pct(lost[j])}")));
            foreach (int j in Outliers(lost)) if (j < portNumbers.Count) flagged.Add(portNumbers[j]);
        }
        string line = "Power lost per driven port, the others matched (1 − Σᵢ|Sᵢⱼ|²): " + string.Join("; ", at) + ".";
        if (flagged.Count > 0)
            line += $" Port{(flagged.Count == 1 ? "" : "s")} {string.Join(", ", flagged)} lose{(flagged.Count == 1 ? "s" : "")} " +
                    $"several times the median port's: power is leaving the model on {(flagged.Count == 1 ? "that port's" : "those ports'")} " +
                    "path and not through a port — look at what its field reaches, such as an absorbing face close to it.";
        return line;
    }

    private static string Pct(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private static string Ghz(double hz) => (hz / 1e9).ToString("G4", CultureInfo.InvariantCulture) + " GHz";
}
