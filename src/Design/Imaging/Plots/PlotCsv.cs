// The raw digitised traces as CSV — brief-img-14-sparameter-plots-to-touchstone.md R-im14-11.
//
// ANY PLOT DIGITISES. The CSV is what was read off the picture and nothing more: one row per sample, in the plot's own
// units, never resampled, never filled. A plot of temperature against time digitises as readily as a dB(S21) plot — it
// just makes no Touchstone — so nothing here asks whether a trace is assigned, whether a chart trace has a frequency,
// or whether the x axis is frequency at all.
//
// ONE LONG TABLE, not one column pair per trace: traces do not share x values (each is sampled at its own pixel
// columns), so a wide table would be mostly empty cells. A rectangular trace fills x and y; a chart trace fills re and
// im, and x when its frequency is known. `exact` marks a sample that came from a marker's readout rather than pixels.
// `y_axis` says which y axis a rectangular trace was read against, `left` or `right` (brief-pdg-1-two-y-axes.md
// R-pdg1-5), empty for a chart trace. `y_fit` (brief-pdg-2-curve-fit.md R-pdg2-3) is a fitted trace's fit at each
// sample's x, empty where no fit is set — `y` stays exactly what was read. Each new column goes LAST, because column order
// is a format.
//
// A CURVE PLACED BY HAND (brief-pdg-4-digitise-by-hand.md R-pdg4-4) writes one row per point of its sampled grid — which is
// the one case the CSV is resampled, because the grid is what the user asked for — under the name the user gave it, its
// assignment `data` unless it was said to be an S-parameter quantity, and `exact` 0: a fixpoint is a click, not a readout.
// No column is added for it.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Imaging.Plots;

public static class PlotCsv
{
    /// <summary>The columns, in order.</summary>
    public static readonly IReadOnlyList<string> Columns = ["trace", "plot", "assignment", "x", "x_unit", "y", "y_unit", "re", "im", "exact", "y_axis", "y_fit"];

    /// <summary>The session's traces as CSV text, <c>\n</c> line ends. Pure.</summary>
    /// <param name="fits">The fits by trace name (<see cref="TraceFit.Session"/>, a Touchstone result's own); null fits none.</param>
    public static string Write(PlotSession session, IReadOnlyDictionary<string, TraceFitResult>? fits = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sb = new StringBuilder();
        foreach (var p in session.Pictures)
            sb.Append("# picture ").Append(p.Index + 1).Append(": ").Append(p.Name.Replace('\n', ' ')).Append(" sha256 ").Append(p.Sha256).Append('\n');
        sb.Append(string.Join(",", Columns)).Append('\n');
        foreach (var t in session.Traces)
        {
            var plot = session.Plots[t.Plot - 1];
            double xScale = 1;
            string xUnit = "";
            if (plot.Rect is { } r)
            {
                if (r.XIsFrequency) (xScale, xUnit) = (r.XUnit, FrequencyUnit(r.XUnit));
            }
            else if (t.Frequency is not null) (xScale, xUnit) = (1e9, "GHz");
            string assignment = t.Manual && !t.Assignment.Assigned && !t.Assignment.Ignored ? "data" : t.Assignment.ToString();
            string name = t.ManualName is { } given ? Cell(given) : t.Name;
            string side = t.Parametric ? "" : PlotReading.Side(t.Axis);
            var fit = !t.Parametric && fits is not null && fits.TryGetValue(t.Name, out var got) && got.Fitted ? got : null;
            foreach (var s in t.Samples)
            {
                sb.Append(name).Append(',').Append(t.Plot.ToString(CultureInfo.InvariantCulture)).Append(',').Append(assignment).Append(',');
                sb.Append(double.IsNaN(s.X) ? "" : Num(s.X / xScale)).Append(',').Append(double.IsNaN(s.X) ? "" : xUnit).Append(',');
                if (t.Parametric)
                    sb.Append(",,").Append(Num(s.Value.Real)).Append(',').Append(Num(s.Value.Imaginary));
                else
                    sb.Append(Num(s.Y)).Append(',').Append(Cell(t.ResolutionUnit)).Append(",,");
                sb.Append(',').Append(s.Exact ? '1' : '0').Append(',').Append(side).Append(',');
                if (fit is not null && TraceFit.EvaluateNear(fit, s.X, s.Y) is var y && double.IsFinite(y)) sb.Append(Num(y));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Writes <see cref="Write"/>'s text to <paramref name="path"/>, atomically.</summary>
    public static void WriteFile(string path, PlotSession session, IReadOnlyDictionary<string, TraceFitResult>? fits = null) =>
        Cells.AtomicFile.WriteAllText(path, Write(session, fits));

    private static string FrequencyUnit(double multiplier) => multiplier switch
    {
        >= 1e12 => "THz",
        >= 1e9 => "GHz",
        >= 1e6 => "MHz",
        >= 1e3 => "kHz",
        _ => "Hz",
    };

    /// <summary>A name or unit the user typed, as one cell: no comma, quote or line end can break the row.</summary>
    private static string Cell(string text) => string.Concat(text.Select(c => c is ',' or '"' or '\n' or '\r' ? ' ' : c)).Trim();

    private static string Num(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
