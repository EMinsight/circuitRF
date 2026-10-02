// brief-em3d-98 R-em3d98-2 — how a run ENDED, kept beside its result, one file per solver leg (and the thermal run's own
// directory). Brief 87's record (document.c3d + inputs.json) says what a result was solved FROM; it was kept only on success,
// which left two errors: a cancelled re-run left the PREVIOUS record beside half-overwritten files, so an unchanged model read
// as "current"; and a Both run with Palace complete and openEMS cancelled kept no record for either.
//
// So a leg now opens its directory by REMOVING the previous record and writing `running` — before the solver touches the
// directory — and closes it with how it ended, on every exit path. Its inputs record is kept only when the leg completed
// (or did not converge: its result exists and is the model's). Written by the run services in src/Design, so the GUI's
// Simulate and `circuitrf em` keep the same record through the one door, as brief 87 requires.
//
// A directory left at `running` by a process that is gone was INTERRUPTED (a crash, a force-quit): partial. A directory with
// no status.json but a kept document is a run from before this brief, whose record was only ever kept on success: complete.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.ThreeD;

/// <summary>How a run leg ended (or that it has not yet).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<C3dRunState>))]
public enum C3dRunState
{
    [JsonStringEnumMemberName("running")]      Running,
    [JsonStringEnumMemberName("complete")]     Complete,
    [JsonStringEnumMemberName("cancelled")]    Cancelled,
    [JsonStringEnumMemberName("notConverged")] NotConverged,
    [JsonStringEnumMemberName("failed")]       Failed,
}

/// <summary>One leg's <c>status.json</c>.</summary>
/// <param name="State">How it ended, or <see cref="C3dRunState.Running"/>.</param>
/// <param name="Started">UTC.</param>
/// <param name="Finished">UTC; null while running.</param>
/// <param name="Detail">One sentence for a tooltip: why it did not converge, why it failed. Null for a plain completion.</param>
/// <param name="Pid">The process that wrote <c>running</c>, so a live run elsewhere is told apart from a crashed one.</param>
public sealed record C3dRunStatusRecord(C3dRunState State, DateTime Started, DateTime? Finished, string? Detail, int Pid)
{
    /// <summary>Left at <see cref="C3dRunState.Running"/> by a process that is no longer there.</summary>
    [JsonIgnore] public bool Interrupted => State == C3dRunState.Running && !C3dRunStatus.IsAlive(Pid);

    /// <summary>Still running, in a process that is alive (this one or another).</summary>
    [JsonIgnore] public bool Running => State == C3dRunState.Running && !Interrupted;

    /// <summary>The result is not the whole of what was asked: cancelled, not converged, failed, or interrupted.</summary>
    [JsonIgnore] public bool Partial => State is C3dRunState.Cancelled or C3dRunState.NotConverged or C3dRunState.Failed || Interrupted;

    /// <summary>A run from before this brief: no <c>status.json</c>, only a kept document (<see cref="C3dRunStatus.Read"/>).</summary>
    [JsonIgnore] public bool Legacy { get; init; }

    /// <summary>How long the run took, when it finished — unknown for a <see cref="Legacy"/> run.</summary>
    [JsonIgnore] public TimeSpan? Took => Finished is { } f && !Legacy ? f - Started : null;
}

public static class C3dRunStatus
{
    /// <summary>The file a run's directory keeps how it ended in.</summary>
    public const string FileName = "status.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathIn(string runDirectory) => Path.Combine(runDirectory, FileName);

    /// <summary>
    /// A leg is about to use <paramref name="runDirectory"/>: the previous run's record is removed (whatever this run leaves
    /// behind, it is not that run's whole result any more) and <c>running</c> written. Returns when it started, for
    /// <see cref="Finish"/>. A directory that cannot be written is left to the leg, which says why when it fails to use it.
    /// </summary>
    public static DateTime Begin(string runDirectory)
    {
        var started = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(runDirectory);
            Write(runDirectory, new C3dRunStatusRecord(C3dRunState.Running, started, null, null, Environment.ProcessId));
            foreach (string kept in new[] { C3dRunDocument.PathIn(runDirectory), C3dRunDocument.InputsPathIn(runDirectory) })
                if (File.Exists(kept)) File.Delete(kept);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return started;
    }

    /// <summary>The leg ended <paramref name="state"/>. Never throws: the run's own result is what the caller reports.</summary>
    public static void Finish(string runDirectory, DateTime started, C3dRunState state, string? detail = null)
    {
        try { Write(runDirectory, new C3dRunStatusRecord(state, started, DateTime.UtcNow, detail, Environment.ProcessId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// <paramref name="runDirectory"/>'s record: its <c>status.json</c>; for a run from before this brief (none, but a kept
    /// document), <see cref="C3dRunState.Complete"/> with no duration, since a document was only ever kept on success; else null.
    /// </summary>
    public static C3dRunStatusRecord? Read(string runDirectory)
    {
        string f = PathIn(runDirectory);
        try
        {
            if (File.Exists(f) && JsonSerializer.Deserialize<C3dRunStatusRecord>(File.ReadAllText(f), Json) is { } r) return r;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        string kept = C3dRunDocument.PathIn(runDirectory);
        if (!File.Exists(kept)) return null;
        var written = File.GetLastWriteTimeUtc(kept);
        return new C3dRunStatusRecord(C3dRunState.Complete, written, written, null, 0) { Legacy = true };
    }

    /// <summary>Writes <paramref name="record"/> (tests build run directories by hand through this).</summary>
    public static void Write(string runDirectory, C3dRunStatusRecord record)
        => Cells.AtomicFile.WriteAllText(PathIn(runDirectory), JsonSerializer.Serialize(record, Json));

    /// <summary>
    /// Whether process <paramref name="pid"/> is running. "Could not tell" reads as alive: calling a live run interrupted is
    /// the confident false statement this must not make (WorkspaceLock's rule).
    /// </summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (Exception) { return true; }
    }
}
