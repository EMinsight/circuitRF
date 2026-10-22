// brief-sar-5 R-sar5-6 (overview O3) — openEMS's sar_calc run on the same raw SAR dump as circuitRF's engine, as a
// cross-check. circuitRF's own engine produces the number; this only reports sar_calc's beside it, and their
// difference. It is off unless the setup's Sar.CrossCheck asks for it (the key arrives with SAR-6's Sar group; this
// is the step it switches on).
//
// sar_calc is GPL and runs as an external process (em-3d.md §5.3's contract), started through Em3dProcessLauncher like
// every other 3D program. ITS EXIT STATUS IS INVERTED: its main returns CalcFromHDF5's bool, so 1 is success and 0
// failure (docs/design/sar.md §2). It is judged by its output file instead, which is deleted before the run so a file
// from an earlier run is never read as this one's.
//
// What is said, and how loudly:
//   * not asked for                    nothing;
//   * asked, sar_calc not found        a NOTE — circuitRF's own number stands, it is just not cross-checked;
//   * asked, sar_calc failed           a WARNING naming its log;
//   * the peaks differ by over 0.5 %   a WARNING with both values (overview §4's tolerance).

using System.Diagnostics;
using System.Globalization;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Hdf5;

namespace CircuitRF.Design.Em3d;

/// <summary>One sar_calc run: what it wrote, or why there is nothing.</summary>
public sealed record SarCalcRun(bool Ok, string OutputPath, string LogPath, string? Failure, OpenEmsSarResult? Result);

/// <summary>One mass's cross-check: both peaks and their relative difference |ours − sar_calc| / sar_calc.</summary>
public sealed record SarCrossCheckRow(double MassGrams, double Peak, double? SarCalcPeak, double? RelativeDifference);

/// <summary>The cross-check's rows and what the run should say about it.</summary>
public sealed record SarCrossCheck(IReadOnlyList<SarCrossCheckRow> Rows, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes)
{
    public static SarCrossCheck None { get; } = new([], [], []);
}

public static class OpenEmsSarCalc
{
    /// <summary>A relative difference between the two peaks above this is a warning (overview §4: 0.5 %).</summary>
    public const double WarningDifference = 0.005;

    private static long _runs;

    /// <summary>How many times this process has started sar_calc.</summary>
    public static long Invocations => Interlocked.Read(ref _runs);

    /// <summary>sar_calc's method for circuitRF's: <see cref="SarMethod.Cube"/> is <c>IEEE_62704</c>, <see cref="SarMethod.Simple"/> <c>SIMPLE</c>.</summary>
    public static string MethodArgument(SarMethod method) => method == SarMethod.Simple ? "SIMPLE" : "IEEE_62704";

    /// <summary>The arguments of one run.</summary>
    public static IReadOnlyList<string> Arguments(string rawDump, string output, double massGrams, SarMethod method) =>
        ["-i", rawDump, "-o", output, "--method", MethodArgument(method), "--mass", massGrams.ToString("R", CultureInfo.InvariantCulture)];

    /// <summary>Runs <paramref name="sarCalc"/> on <paramref name="rawDump"/> for one mass, writing <paramref name="output"/>
    /// and its console output beside it as <c>&lt;output&gt;.log</c>.</summary>
    public static SarCalcRun Run(string sarCalc, string rawDump, double massGrams, SarMethod method, string output,
                                 CancellationToken cancel = default)
    {
        string log = output + ".log";
        if (File.Exists(output)) File.Delete(output);
        var info = new ProcessStartInfo(sarCalc)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(output)) ?? Environment.CurrentDirectory,
        };
        foreach (string a in Arguments(rawDump, output, massGrams, method)) info.ArgumentList.Add(a);

        // brief-emi-10 — through the one process loop, so it is held to the memory ceiling and the disk floor as openEMS is.
        var watch = PalaceRun.Watch(null, null, 0, info.WorkingDirectory) with { OnStarted = _ => Interlocked.Increment(ref _runs) };
        var run = PalaceRun.RunProcess(info, log, Em3dProcessKind.Solver, null, cancel, watch);
        if (run.Cancelled) throw new OperationCanceledException(cancel);
        if (run.StartFailure is { } why) return new SarCalcRun(false, output, log, $"sar_calc could not be started: {why}", null);
        if (run.Stopped is { } stopped)
            return new SarCalcRun(false, output, log, stopped.Sentence("sar_calc", ProcessLimits.MachineOwner, [], log), null);

        // Judged by its output, never its exit status (see the file header).
        if (!File.Exists(output))
            return new SarCalcRun(false, output, log, $"sar_calc wrote no result (its output is in {log})", null);
        try
        {
            return new SarCalcRun(true, output, log, null, OpenEmsHdf5.ReadSarResult(output));
        }
        catch (OpenEmsHdf5Exception e)
        {
            return new SarCalcRun(false, output, log, $"sar_calc's result could not be read: {e.Message}", null);
        }
    }

    /// <summary>
    /// The cross-check of <paramref name="ours"/>, computed from <paramref name="rawDump"/>: nothing when
    /// <paramref name="enabled"/> is false; otherwise sar_calc run beside <paramref name="openEms"/> once per averaged mass,
    /// its results written into <paramref name="workDirectory"/>, and both peaks compared.
    /// </summary>
    public static SarCrossCheck CrossCheck(bool enabled, SolverInstallation? openEms, string rawDump, SarResult ours,
                                           SarMethod method, string workDirectory, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(ours);
        if (!enabled || ours.Averages.Count == 0) return SarCrossCheck.None;
        string? sarCalc = openEms is null ? null : SolverDiscovery.SarCalcBeside(openEms);
        if (sarCalc is null)
        {
            string where = openEms is null ? "openEMS was not found" : $"there is no {SolverDiscovery.SarCalcFileName} beside {openEms.Where}";
            return new SarCrossCheck([], [], [$"SAR was not cross-checked against openEMS's sar_calc: {where}."]);
        }

        Directory.CreateDirectory(workDirectory);
        var rows = new List<SarCrossCheckRow>();
        var warnings = new List<string>();
        foreach (var average in ours.Averages)
        {
            string g = average.MassGrams.ToString("G4", CultureInfo.InvariantCulture);
            double peak = average.Peak?.Value ?? 0;
            var run = Run(sarCalc, rawDump, average.MassGrams, method, Path.Combine(workDirectory, $"sar_calc_{g}g.h5"), cancel);
            if (!run.Ok)
            {
                rows.Add(new SarCrossCheckRow(average.MassGrams, peak, null, null));
                warnings.Add($"The {g} g SAR was not cross-checked: {run.Failure}.");
                continue;
            }
            double theirs = run.Result!.MaxSar;
            double? difference = theirs > 0 ? Math.Abs(peak - theirs) / theirs : peak == 0 ? 0 : null;
            rows.Add(new SarCrossCheckRow(average.MassGrams, peak, theirs, difference));
            if (difference is not { } d || d > WarningDifference)
                warnings.Add($"circuitRF's {g} g peak SAR ({peak.ToString("G5", CultureInfo.InvariantCulture)} W/kg) differs from " +
                             $"openEMS's sar_calc ({theirs.ToString("G5", CultureInfo.InvariantCulture)} W/kg) by " +
                             (difference is { } dd ? $"{dd * 100:F2} %" : "an undefined amount") +
                             $", more than the {WarningDifference * 100:G2} % the two are expected to agree to.");
        }
        return new SarCrossCheck(rows, warnings, []);
    }
}
