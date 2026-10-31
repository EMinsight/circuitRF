// One short-lived child process with a deadline that actually holds: both pipes drained concurrently,
// the wait bounded, and the whole tree killed when it runs over.
//
// The shape it replaces — ReadToEnd() on stdout, THEN WaitForExit(timeout) — never times out: ReadToEnd
// returns only when the child closes stdout, so a child that hangs hangs the caller, and the kill after
// the wait is unreachable. Leaving stderr redirected and unread is a second hang: once the child has
// written a pipe's worth of it, the child blocks on the write and stdout never closes.

using System.Diagnostics;

namespace CircuitRF.Design.Layout.PCells.Wire;

internal static class BoundedProcess
{
    /// <summary>What the child said and how it ended. <see cref="TimedOut"/> means it was killed and
    /// the two texts are whatever it had written by then (possibly nothing).</summary>
    internal readonly record struct Outcome(string Stdout, string Stderr, int ExitCode, bool TimedOut);

    /// <summary>How long the pipes may stay open after the child itself has exited — a grandchild that
    /// inherited them can keep them open indefinitely, and that is not worth waiting on.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts <paramref name="info"/> with every stream redirected and stdin closed at once, and waits at
    /// most <paramref name="timeout"/>. Null when the process would not start. Throws only what
    /// <see cref="Process.Start(ProcessStartInfo)"/> throws (a missing executable), which every caller
    /// already catches.
    /// </summary>
    internal static Outcome? Run(ProcessStartInfo info, TimeSpan timeout)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError  = true;
        info.RedirectStandardInput  = true;
        info.UseShellExecute        = false;
        info.CreateNoWindow         = true;

        using var process = Process.Start(info);
        if (process is null) return null;
        try { process.StandardInput.Close(); } catch (IOException) { /* already gone */ }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        bool exited = process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { process.WaitForExit(2000); } catch { /* best effort */ }
        }

        try { Task.WaitAll([stdout, stderr], DrainGrace); } catch (AggregateException) { /* read failed: what was read stands */ }
        string Text(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : "";
        return new Outcome(Text(stdout), Text(stderr), exited ? process.ExitCode : -1, !exited);
    }
}
