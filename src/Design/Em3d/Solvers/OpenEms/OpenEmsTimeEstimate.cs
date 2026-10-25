// brief-dfr27-5 R-dfr27-5-2 — how long an openEMS run will take, said before it starts.
//
// openEMS's cost is arithmetic on the grid: every cell is updated once per time step, once per port (openEMS excites one port
// per simulation). The grid generator already knows the cells and the steps (FdtdGrid.Build), so the only thing this file adds
// is a throughput, and that is a CONSERVATIVE constant per core rather than a measurement: the runs recorded in
// testdata/sar/openems-ref/README.md and docs/design/sar.md went at 142 to 900 million cell-updates a second, 288 on four
// threads (72 a thread), and the engine stops scaling where memory bandwidth runs out. A designer's 8 GB laptop that swaps
// goes slower than any of them, which is the machine this check exists for.

using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class OpenEmsTimeEstimate
{
    /// <summary>Cell updates a second one core is credited with — a third of the slowest recorded per-thread rate.</summary>
    public const double CellUpdatesPerSecondPerCore = 25e6;

    /// <summary>The cores the estimate credits at most: past these openEMS waits on memory, not arithmetic.</summary>
    public const int MaxUsefulCores = 8;

    /// <summary>Above this a run is asked about before it starts; below it the estimate is a note.</summary>
    public const double AskAboveSeconds = 3600;

    /// <summary>Above this the question also names what makes the run long: the smallest cell and what set it.</summary>
    public const double CauseAboveSeconds = 86_400;

    /// <summary>The cores a run uses: the user's cap when there is one, else this machine's.</summary>
    public static int Cores(int? maxCores) => Math.Max(1, maxCores ?? Environment.ProcessorCount);

    /// <summary>Cell updates a second credited to <paramref name="cores"/> cores.</summary>
    public static double Throughput(int cores) => CellUpdatesPerSecondPerCore * Math.Clamp(cores, 1, MaxUsefulCores);

    /// <summary>Seconds for every port's run: cells × steps × ports over <see cref="Throughput"/>.</summary>
    public static double Seconds(FdtdGridResult grid, int ports, int cores)
        => (double)grid.Cells * grid.Steps * Math.Max(1, ports) / Throughput(cores);

    /// <summary>A duration as a reader takes it in: seconds, minutes, hours or days.</summary>
    public static string Format(double seconds)
    {
        var c = CultureInfo.InvariantCulture;
        return seconds switch
        {
            < 90         => $"{Math.Max(1, Math.Round(seconds)).ToString("0", c)} s",
            < 90 * 60    => $"{(seconds / 60).ToString("0", c)} min",
            < 36 * 3600  => $"{(seconds / 3600).ToString("0.#", c)} hours",
            _            => $"{(seconds / 86_400).ToString("0.#", c)} days",
        };
    }

    /// <summary>
    /// The estimate as a sentence: the total, what it is made of, and — past <see cref="CauseAboveSeconds"/> — the smallest
    /// cell, the features that set it and the setting that would relax it.
    /// </summary>
    public static string Sentence(FdtdGridResult grid, int ports, int cores)
    {
        var c = CultureInfo.InvariantCulture;
        double s = Seconds(grid, ports, cores);
        int credited = Math.Clamp(cores, 1, MaxUsefulCores);
        string text =
            $"openEMS should take about {Format(s)}: {grid.Cells.ToString("N0", c)} cells × {grid.Steps.ToString("N0", c)} time " +
            $"steps × {Math.Max(1, ports)} run{(ports == 1 ? "" : "s")} (one per port), at about " +
            $"{(Throughput(cores) / 1e6).ToString("0", c)} million cell updates a second on {credited} core{(credited == 1 ? "" : "s")} " +
            "— a conservative figure; each run stops when its ports' signals have decayed, which can be sooner.";
        if (s <= CauseAboveSeconds) return text;
        var a = grid.Smallest;
        string at = FdtdGrid.FormatLength(a.SmallestCellM);
        return text + $" Most of that is the time step, set by the smallest cell: {at} on {FdtdGrid.AxisName(a.Axis)}, set by " +
               string.Join(" and ", a.SmallestCellFeatures.Select(f => f.Describe(a.Axis))) +
               $". Setting the openEMS section's MinCellUm above {at} merges the lines that make it.";
    }

    /// <summary>
    /// The question a run asks before it starts, as the memory check's: past <see cref="AskAboveSeconds"/> a
    /// <see cref="Em3dMemoryLevel.Severe"/> verdict (the panel asks; the CLI says so and runs), otherwise a verdict that fits
    /// and carries the sentence as a note.
    /// </summary>
    public static Em3dMemoryVerdict Verdict(FdtdGridResult grid, int ports, int cores)
        => new(Seconds(grid, ports, cores) > AskAboveSeconds ? Em3dMemoryLevel.Severe : Em3dMemoryLevel.Fits, null, 0,
               Sentence(grid, ports, cores)) { Concern = Em3dRunConcern.Time };
}
