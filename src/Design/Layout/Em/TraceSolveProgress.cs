// brief-impedance-8 R-imp8-4: how far a layer's solve has got and how long it has left. One sentence,
// shared by the CLI's stderr line and the Impedance panel's progress line, so the two never disagree.

using System.Globalization;

namespace CircuitRF.Design.Layout.Em;

public static class TraceSolveProgress
{
    /// <summary>The CLI prints a progress line at most this often within one stage.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    /// <summary>The time left, extrapolated from the rate so far — or null until enough is done for
    /// the rate to mean anything (a few seconds and a few percent of the stage).</summary>
    public static TimeSpan? Remaining(long done, long total, TimeSpan elapsed)
    {
        if (total <= 0 || done <= 0 || done >= total) return null;
        if (elapsed < TimeSpan.FromSeconds(2) || done < Math.Max(10, total / 50)) return null;
        return TimeSpan.FromSeconds(elapsed.TotalSeconds * (total - done) / done);
    }

    /// <summary>"1,234 of 6,368 cross-sections, 42 s elapsed, about 1 min 10 s left".</summary>
    public static string Text(long done, long total, string unit, TimeSpan elapsed)
    {
        var inv = CultureInfo.InvariantCulture;
        string text = $"{done.ToString("N0", inv)} of {total.ToString("N0", inv)} {unit}, {Duration(elapsed)} elapsed";
        return Remaining(done, total, elapsed) is { } left ? $"{text}, about {Duration(left)} left" : text;
    }

    /// <summary>"8 s", "1 min 10 s", "1 h 4 min".</summary>
    public static string Duration(TimeSpan t)
    {
        long s = (long)Math.Round(Math.Max(0, t.TotalSeconds));
        return s < 60 ? $"{s} s"
             : s < 3600 ? (s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s")
             : $"{s / 3600} h {s % 3600 / 60} min";
    }
}
