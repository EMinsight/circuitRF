// The gdstk worker as a process (brief-oasis-gdstk.md §5, D1): where it is, starting it, and one bounded
// request at a time. tools/gdstk-worker/README.md is the protocol; tools/gdstk-worker/RESOLVED.md is why
// every request here is bounded.
//
// EVERY REQUEST HAS A DEADLINE AND A MEMORY CAP, AND MISSING EITHER KILLS THE PROCESS. gdstk can crash on
// damaged input the worker's guards cannot see, can hang (the owner's Windows sessions saw an x86 hang
// where macOS crashed, on the same bytes), and a corrupt OASIS CBLOCK size can make it allocate without
// bound. A crash, a hang and a runaway all reach the caller the same way — a GdstkException with a
// sentence, and nothing created — because the only stop that always works is ending the process
// (G0 findings, "Recommendations for G1": 30 s + 1 s per MB, and a memory cap).
//
// The process and its frames are the geometry worker's (GeometryKernelProcessWorker, GeometryKernelFrame):
// the same framing on the same kind of pipe, reused rather than copied (§5). Nothing here goes through the
// device-worker transport — the gdstk worker is circuitRF's own program, like the geometry worker.

using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

/// <summary>Why a gdstk request produced nothing the caller can use.</summary>
public enum GdstkFailure
{
    /// <summary>No worker in this build (D6): the (gdstk) commands are disabled, and a request says so.</summary>
    Unavailable,
    /// <summary>The worker did not start, or its handshake failed.</summary>
    StartFailed,
    /// <summary>The worker answered with a refusal (<see cref="GdstkException.Code"/>): an ordinary reply.</summary>
    Refused,
    /// <summary>The worker ended mid-request.</summary>
    Crashed,
    /// <summary>The request missed its deadline and the worker was stopped.</summary>
    TimedOut,
    /// <summary>The worker passed its memory cap and was stopped.</summary>
    OutOfMemory,
    /// <summary>The worker's reply did not follow the protocol.</summary>
    Malformed,
}

/// <summary>A gdstk request that failed. <see cref="Diagnostic"/> is what a person reads (<see cref="GdstkDiagnostics"/>);
/// <see cref="Exception.Message"/> is its English rendering.</summary>
public sealed class GdstkException(GdstkFailure failure, Diagnostic diagnostic, string? code = null) : Exception(diagnostic.Render())
{
    public GdstkFailure Failure { get; } = failure;

    public Diagnostic Diagnostic { get; } = diagnostic;

    /// <summary>The worker's refusal code (<c>read.truncated</c>, <c>write.failed</c>, …), when it gave one.</summary>
    public string? Code { get; } = code;
}

/// <summary>Where discovery found the worker, or why it found none.</summary>
public sealed record GdstkWorkerLocation(string? Path, string How, string Reason)
{
    public bool Found => Path is not null;
}

/// <summary>What the worker said about itself in <c>hello</c>.</summary>
public sealed record GdstkIdentity(string Worker, string Gdstk, string Qhull, string Zlib, int Protocol, string Rid);

/// <summary>What a test or a caller changes about a worker. Every default is the product's.</summary>
public sealed record GdstkWorkerOptions
{
    /// <summary>The executable, when not <see cref="GdstkWorker.Locate()"/>'s answer.</summary>
    public string? Path { get; init; }

    /// <summary>Extra environment, for a test's <c>CRF_GDSTK_WORKER_TEST=1</c>.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>A request's deadline before the per-megabyte allowance (G0: 30 s).</summary>
    public TimeSpan BaseDeadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Added to the deadline per megabyte of the file read or the bytes sent (G0: 1 s).</summary>
    public TimeSpan DeadlinePerMegabyte { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The memory cap's floor (G0: 2 GB); the cap is the larger of this and 50 × the input's size.</summary>
    public long MemoryCapFloorBytes { get; init; } = 2L << 30;

    /// <summary>How often the memory watch reads the worker's working set.</summary>
    public TimeSpan MemoryPoll { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>One running gdstk worker. Requests are serialised: two threads writing to one pipe would
/// interleave frames.</summary>
public sealed class GdstkWorker : IDisposable
{
    public const int Protocol = 1;
    public const string EnvironmentVariable = "CIRCUITRF_GDSTK_WORKER";
    public const string Folder = "gdstk-kernel";

    /// <summary>The sentence a disabled (gdstk) command shows when there is no worker (brief §7a).</summary>
    public const string NotInstalledSentence = "The gdstk worker is not installed in this build.";

    public static string WorkerFileName(bool windows) => windows ? "gdstk-worker.exe" : "gdstk-worker";

    private readonly GeometryKernelProcessWorker _process;
    private readonly GdstkWorkerOptions _options;
    private readonly object _gate = new();
    private bool _lost;

    public GdstkIdentity Identity { get; }

    private GdstkWorker(GeometryKernelProcessWorker process, GdstkWorkerOptions options, GdstkIdentity identity)
    {
        _process = process;
        _options = options;
        Identity = identity;
    }

    // ── Discovery ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Discovery for this process, the geometry worker's rows (<see cref="GeometryKernelLocator"/>):
    /// <see cref="EnvironmentVariable"/> when set (then the only candidate); <c>&lt;base&gt;/gdstk-kernel/</c>,
    /// where a build and an installer stage it; and, only when that folder is absent, a source tree's own
    /// <c>tools/gdstk-worker/build/&lt;rid&gt;/gdstk-kernel/</c> — where <c>ensure-built</c> stages it, so a
    /// test project's output finds the worker its build tree holds. A named worker that does not exist is
    /// reported, never replaced.
    /// </summary>
    public static GdstkWorkerLocation Locate() => Locate(
        System.Environment.GetEnvironmentVariable(EnvironmentVariable), AppContext.BaseDirectory,
        GeometryKernelLocator.ProcessRid, OperatingSystem.IsWindows());

    public static GdstkWorkerLocation Locate(string? environmentValue, string baseDirectory, string rid, bool windows)
    {
        string exe = WorkerFileName(windows);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            string path = System.IO.Path.GetFullPath(environmentValue.Trim());
            return File.Exists(path)
                ? new(path, $"from {EnvironmentVariable}", "")
                : new(null, $"from {EnvironmentVariable}", $"{EnvironmentVariable} names {path}, which does not exist.");
        }

        string folder = System.IO.Path.Combine(baseDirectory, Folder);
        if (Directory.Exists(folder))
        {
            string path = System.IO.Path.Combine(folder, exe);
            return File.Exists(path)
                ? new(path, "included with circuitRF", "")
                : new(null, "included with circuitRF", $"The gdstk worker's folder {folder} has no {exe} in it.");
        }

        if (GeometryKernelLocator.SourceTreeRoot(baseDirectory) is { } root)
        {
            string built = System.IO.Path.Combine(root, "tools", "gdstk-worker", "build", rid, Folder, exe);
            return File.Exists(built)
                ? new(built, "built in this source tree", "")
                : new(null, "built in this source tree", $"{NotInstalledSentence} (looked for {built}; tools/gdstk-worker/build.sh builds it).");
        }
        return new(null, "included with circuitRF", NotInstalledSentence);
    }

    // ── Starting ───────────────────────────────────────────────────────────────

    /// <summary>Starts a worker and shakes hands with it. Throws <see cref="GdstkException"/>
    /// (<see cref="GdstkFailure.Unavailable"/> or <see cref="GdstkFailure.StartFailed"/>).</summary>
    public static GdstkWorker Start(GdstkWorkerOptions? options = null, CancellationToken token = default)
    {
        options ??= new GdstkWorkerOptions();
        string path = options.Path ?? Locate() switch
        {
            { Path: { } p } => p,
            var miss => throw new GdstkException(GdstkFailure.Unavailable, GdstkDiagnostics.NotFound(miss.Reason)),
        };

        GeometryKernelProcessWorker process;
        try
        {
            process = new GeometryKernelProcessWorker(path, options.Environment);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new GdstkException(GdstkFailure.StartFailed, GdstkDiagnostics.StartFailed(path, e.Message));
        }

        GeometryKernelMessage hello;
        using (var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, token))
        using (linked.Token.Register(process.Kill))
        {
            try
            {
                hello = process.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "hello", ["protocol"] = Protocol }));
            }
            catch (GeometryKernelWorkerExitedException e)
            {
                process.Dispose();
                token.ThrowIfCancellationRequested();
                string why = timer.IsCancellationRequested ? "it did not answer within 10 s" : $"it stopped during start-up (exit code {Spell(e.ExitCode)})";
                throw new GdstkException(GdstkFailure.StartFailed, GdstkDiagnostics.StartFailed(path, why));
            }
        }

        int protocol = hello.Json["protocol"] is JsonValue pv && pv.TryGetValue(out int pn) ? pn : -1;
        if (!hello.Ok || protocol != Protocol)
        {
            process.Dispose();
            throw new GdstkException(GdstkFailure.StartFailed, GdstkDiagnostics.WrongProtocol(path, protocol, Protocol));
        }
        var identity = new GdstkIdentity(hello.Text("worker") ?? "", hello.Text("gdstk") ?? "", hello.Text("qhull") ?? "",
                                         hello.Text("zlib") ?? "", protocol, hello.Text("rid") ?? "");
        return new GdstkWorker(process, options, identity);
    }

    // ── One bounded request ────────────────────────────────────────────────────

    /// <summary>The deadline and memory cap for a request whose input is <paramref name="inputBytes"/>
    /// long: the file read, or the bytes a write has sent.</summary>
    public (TimeSpan Deadline, long MemoryCap) BoundsFor(long inputBytes)
    {
        double mb = Math.Max(0, inputBytes) / (1024.0 * 1024.0);
        var deadline = _options.BaseDeadline + TimeSpan.FromTicks((long)(_options.DeadlinePerMegabyte.Ticks * mb));
        long cap = Math.Max(_options.MemoryCapFloorBytes, 50 * Math.Max(0, inputBytes));
        return (deadline, cap);
    }

    /// <summary>
    /// Sends one request and returns its reply — a refusal is returned, not thrown, so the caller words it.
    /// Throws <see cref="GdstkException"/> when the worker ends, misses <paramref name="deadline"/> or passes
    /// <paramref name="memoryCap"/> (it is killed in the last two cases), and <see cref="OperationCanceledException"/>
    /// when <paramref name="token"/> cancels (it is killed). <paramref name="doing"/> finishes the sentence
    /// "The gdstk worker stopped while …" — "reading /path/file.gds".
    /// </summary>
    public GeometryKernelMessage Send(GeometryKernelMessage request, TimeSpan deadline, long memoryCap, string doing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_lost)
                throw new GdstkException(GdstkFailure.Crashed, GdstkDiagnostics.AlreadyStopped(doing));

            bool overMemory = false;
            using var timer = new CancellationTokenSource(deadline);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, token);
            using var reg = linked.Token.Register(_process.Kill);
            using var watch = new Timer(_ =>
            {
                if (_process.WorkingSetBytes > memoryCap)
                {
                    Volatile.Write(ref overMemory, true);
                    _process.Kill();
                }
            }, null, _options.MemoryPoll, _options.MemoryPoll);

            try
            {
                var reply = _process.Exchange(request);
                if (token.IsCancellationRequested)
                {
                    Lose();
                    throw new OperationCanceledException(token);
                }
                return reply;
            }
            catch (GeometryKernelWorkerExitedException e)
            {
                Lose();
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (Volatile.Read(ref overMemory))
                    throw new GdstkException(GdstkFailure.OutOfMemory, GdstkDiagnostics.OutOfMemory(doing, memoryCap / (1024 * 1024)));
                if (timer.IsCancellationRequested)
                    throw new GdstkException(GdstkFailure.TimedOut, GdstkDiagnostics.TimedOut(doing, deadline.TotalSeconds));
                throw new GdstkException(GdstkFailure.Crashed, GdstkDiagnostics.Stopped(doing, Spell(e.ExitCode), LastLine(e.StderrTail)));
            }
        }
    }

    private void Lose()
    {
        _lost = true;
        _process.Kill();
    }

    private static string Spell(int? code) => code?.ToString() ?? "unknown";

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }

    /// <summary>Asks the worker to exit, then makes sure it has.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_lost)
            {
                try
                {
                    using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using (timer.Token.Register(_process.Kill))
                        _process.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "shutdown" }));
                }
                catch (GeometryKernelWorkerExitedException) { }
            }
            _lost = true;
            _process.Dispose();
        }
    }
}
