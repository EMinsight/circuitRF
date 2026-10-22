// brief-emi-5 R-emi5-1/2 — the EMerge process: `<env python> -I circuitrf_emerge.py problem.json solve`, in the run directory,
// started with an argument list (never a shell) through the process helper Palace's run uses, which logs both streams to
// emerge.log, samples the whole tree's memory and, on cancellation, kills the whole TREE: EMerge can start worker processes, and
// killing only the interpreter would leave them solving.

using System.Diagnostics;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d;

public static class EmergeRun
{
    /// <summary>The driver's mode that solves and writes the results.</summary>
    public const string SolveMode = "solve";

    /// <summary>
    /// The process to start. Its environment is the parent's less every <c>PYTHON*</c> variable and <c>VIRTUAL_ENV</c> (a
    /// user's Python settings must not reach circuitRF's interpreter, which <c>-I</c> also isolates), plus the thread count
    /// for every sparse solver (EMI-3 R-emi3-5), a plotting backend that draws to no window and an off-screen 3D view.
    /// Nothing in a run reaches a network: EMerge's update check is off unless asked for, and the driver never asks.
    /// </summary>
    public static ProcessStartInfo StartInfo(string python, string runDirectory, IReadOnlyDictionary<string, string> threads)
    {
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory       = runDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in new[] { "-I", EmergeScriptWriter.DriverFile, EmergeScriptWriter.ProblemFile, SolveMode }) psi.ArgumentList.Add(a);
        foreach (string key in psi.Environment.Keys.ToList())
            if (key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || key.Equals("VIRTUAL_ENV", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        foreach (var (k, v) in threads) psi.Environment[k] = v;
        psi.Environment["MPLBACKEND"] = "Agg";
        psi.Environment["PYVISTA_OFF_SCREEN"] = "true";
        psi.Environment["EMERGE_STD_LOGLEVEL"] = "INFO";
        return psi;
    }

    /// <summary>Runs <paramref name="psi"/> to its end, every line through <paramref name="tracker"/>, the log in
    /// <c>emerge.log</c> beside the input. Cancelled (or stopped by the tracker's memory check) kills the whole tree.</summary>
    internal static PalaceRun.ProcessRun Solve(ProcessStartInfo psi, RunControl? control, CancellationToken ct,
                                               EmergeStageTracker tracker, long physicalBytes)
    {
        tracker.Begin();
        return PalaceRun.RunProcess(psi, Path.Combine(psi.WorkingDirectory, EmergeResults.LogFile), Em3dProcessKind.Solver, control, ct,
                                    new ProcessWatch(tracker.Line, physicalBytes));
    }
}
