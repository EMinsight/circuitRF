// brief-em3d-63 R-em3d63-2, -7 — one running geometry worker: the process, the frames, the handshake every
// start begins with, deadlines, cancellation, and what a crash costs.
//
// The geometry worker is circuitRF's OWN program, part of the installation as osdi-worker is. It is NOT
// started through the device-worker transport, so the kit consent gate (DeviceWorkerPolicy) — which exists
// for programs a KIT names beside itself — never applies to it (R-em3d63-1c, D15). A firewall-style source
// test pins that nothing under src/Design/ThreeD/Occ/ references that namespace.
//
// A CRASH COSTS ONE REQUEST (R-em3d63-7a). The pipe closing, or the process exiting mid-request, fails that
// request with a sentence; the next request starts a fresh worker. Nothing here touches a document.
//
// CANCELLATION KILLS (R-em3d63-7c). A fillet never polls a user break (brief 61 Q9), so the only interruption
// that always works is ending the process: a cancelled token or a missed deadline kills the worker, and the
// blocked read then returns at once.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Engine;

namespace CircuitRF.Design.ThreeD.Occ;

/// <summary>One running worker, or a stand-in for one. <see cref="Exchange"/> is called one request at a time.</summary>
public interface IGeometryKernelWorker : IDisposable
{
    /// <summary>Sends one request and blocks for its reply.</summary>
    /// <exception cref="GeometryKernelWorkerExitedException">The worker's pipe closed, or it was killed, before it replied.</exception>
    GeometryKernelMessage Exchange(GeometryKernelMessage request);

    /// <summary>Ends the process. Safe to call from another thread while <see cref="Exchange"/> blocks, which then throws.</summary>
    void Kill();

    /// <summary>The exit code once it has exited, else null.</summary>
    int? ExitCode { get; }

    /// <summary>The last lines the worker wrote to stderr.</summary>
    string StderrTail { get; }
}

/// <summary>The worker ended — crashed, was killed, or closed its pipe — before it replied.</summary>
public sealed class GeometryKernelWorkerExitedException(int? exitCode, string stderrTail, Exception? inner = null)
    : Exception($"The geometry worker ended (exit code {exitCode?.ToString() ?? "unknown"}).", inner)
{
    public int? ExitCode { get; } = exitCode;
    public string StderrTail { get; } = stderrTail;
}

/// <summary>The real worker: a child process speaking frames on its stdin and stdout.</summary>
public sealed class GeometryKernelProcessWorker : IGeometryKernelWorker
{
    private readonly Process _process;
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly Queue<string> _stderr = new();
    private readonly object _stderrGate = new();

    /// <param name="path">The worker executable.</param>
    /// <param name="environment">Extra variables for the worker's environment (a test's <c>CRF_GEOMETRY_WORKER_TEST=1</c>).</param>
    public GeometryKernelProcessWorker(string path, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"{path} did not start.");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderrGate)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > 20) _stderr.Dequeue();
            }
        };
        _process.BeginErrorReadLine();
        _in = _process.StandardInput.BaseStream;
        _out = _process.StandardOutput.BaseStream;
    }

    public int? ExitCode
    {
        get
        {
            try { return _process.HasExited ? _process.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public string StderrTail
    {
        get { lock (_stderrGate) return string.Join("\n", _stderr); }
    }

    public GeometryKernelMessage Exchange(GeometryKernelMessage request)
    {
        try
        {
            GeometryKernelFrame.Write(_in, request);
            if (GeometryKernelFrame.Read(_out) is { } reply) return reply;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or InvalidDataException)
        {
            throw Exited(e);
        }
        throw Exited(null);
    }

    private GeometryKernelWorkerExitedException Exited(Exception? inner)
    {
        // The pipe closing is usually the process ending; give it a moment so its exit code can be reported.
        try { _process.WaitForExit(2000); } catch (InvalidOperationException) { }
        return new GeometryKernelWorkerExitedException(ExitCode, StderrTail, inner);
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

/// <summary>Why a request did not produce a reply the caller can use.</summary>
public enum GeometryKernelFailure
{
    /// <summary>The worker refused it (a fillet too large, an invalid result): an ordinary reply.</summary>
    Refused,
    /// <summary>The worker ended mid-request, or faulted and exited.</summary>
    Crashed,
    /// <summary>The request missed its deadline and the worker was stopped.</summary>
    TimedOut,
    /// <summary>There is no kernel to ask (<see cref="GeometryKernel.Capability"/> is absent).</summary>
    Unavailable,
    /// <summary>The worker did not start, or its handshake failed.</summary>
    StartFailed,
}

/// <summary>A geometry-kernel request that failed. <see cref="Exception.Message"/> is the sentence a person reads.</summary>
public sealed class GeometryKernelException(GeometryKernelFailure failure, string sentence, string? code = null, string? obj = null)
    : Exception(sentence)
{
    public GeometryKernelFailure Failure { get; } = failure;

    /// <summary>The worker's refusal code (<c>build.failed</c>, <c>kernel.fault</c>, …), when it gave one.</summary>
    public string? Code { get; } = code;

    /// <summary>The object the request concerned.</summary>
    public string? Object { get; } = obj;

    /// <summary>brief-em3d-67 — the edge names a fillet or chamfer refusal concerns: the one that does not fit, the edges
    /// meeting at a corner that cannot be blended (<see cref="Corner"/>), or a name that resolves to nothing
    /// (<c>edge.missing</c>). Empty for any other refusal.</summary>
    public IReadOnlyList<string> Edges { get; init; } = [];

    /// <summary>True when <see cref="Edges"/> are the edges meeting at a corner.</summary>
    public bool Corner { get; init; }

    /// <summary>The narrower of the two faces beside the edge that does not fit, µm — what a radius must stay under.</summary>
    public double? WidthUm { get; init; }

    /// <summary>For <c>edge.missing</c>: the faces the edge name names that the target no longer has.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
}

/// <summary>
/// One session with the worker: the model session serves elaboration, the preview session serves dialogs
/// (R-em3d63-8a), so a preview can never delay a commit. Requests are serialised — one at a time per pipe,
/// the device worker's rule and its reason: two threads writing to one pipe interleave frames.
/// </summary>
internal sealed class GeometryKernelSession(GeometryKernel kernel, string role) : IDisposable
{
    private readonly object _gate = new();
    private IGeometryKernelWorker? _worker;
    private bool _lost;
    private readonly LinkedList<string> _held = new();
    private readonly Dictionary<string, LinkedListNode<string>> _heldIndex = new(StringComparer.Ordinal);

    /// <summary>Handles beyond this many are released oldest first, so a long session does not grow the worker without bound.</summary>
    internal const int MaxHeld = 512;

    public string Role { get; } = role;

    /// <summary>The identity the current worker's handshake reported.</summary>
    public GeometryKernelIdentity? Identity { get; private set; }

    /// <summary>Starts a worker if none is running, and shakes hands with it. Throws on any failure.</summary>
    public GeometryKernelIdentity EnsureStarted(string path, CancellationToken token)
    {
        lock (_gate) return StartLocked(path, token);
    }

    /// <summary>True when the running worker holds <paramref name="handle"/> (a restart forgets everything).</summary>
    public bool Holds(string handle)
    {
        lock (_gate) return _worker is not null && _heldIndex.ContainsKey(handle);
    }

    /// <summary>Kills the running worker, if any; the next request starts a fresh one. Not a crash: no restart is counted.</summary>
    public void Kill()
    {
        var w = Volatile.Read(ref _worker);
        if (w is null) return;
        Volatile.Write(ref _killedOnPurpose, true);
        w.Kill();
    }

    /// <summary>Set by <see cref="Kill"/>: the next lost worker was stopped on purpose (a superseded preview), which is a
    /// cancellation and says nothing about the tree — never a crash to cache as the tree's answer.</summary>
    private bool _killedOnPurpose;

    /// <summary>
    /// Sends one request and returns its reply — a refusal included, which the caller words. Throws
    /// <see cref="GeometryKernelException"/> for a crash, a timeout or a failed start, and
    /// <see cref="OperationCanceledException"/> when <paramref name="control"/>'s token cancels (the worker is killed).
    /// </summary>
    /// <param name="doing">What the request does, for a crash's sentence: "building", "tessellating".</param>
    /// <param name="obj">The object it concerns, for the same sentence.</param>
    public GeometryKernelMessage Send(string path, GeometryKernelMessage request, TimeSpan deadline, RunControl? control,
                                      string doing, string obj, string? holds = null)
    {
        var token = control?.Token ?? CancellationToken.None;
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // A worker killed on purpose while idle is dead; the next request starts a fresh one.
            if (_worker is { } idle && Volatile.Read(ref _killedOnPurpose))
            {
                Volatile.Write(ref _killedOnPurpose, false);
                Lose(idle);
            }
            StartLocked(path, token);
            var worker = _worker!;
            kernel.CountRequest();

            using var timer = new CancellationTokenSource(deadline);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, token);
            using var reg = linked.Token.Register(worker.Kill);
            GeometryKernelMessage reply;
            try
            {
                reply = worker.Exchange(request);
            }
            catch (GeometryKernelWorkerExitedException e)
            {
                Lose(worker);
                bool onPurpose = Volatile.Read(ref _killedOnPurpose);
                Volatile.Write(ref _killedOnPurpose, false);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (onPurpose) throw new OperationCanceledException("The request was stopped.");
                if (timer.IsCancellationRequested)
                    throw new GeometryKernelException(GeometryKernelFailure.TimedOut,
                        $"The geometry kernel took longer than {Spell(deadline)} {doing} '{obj}' and was stopped. Nothing was changed.",
                        null, obj);
                throw new GeometryKernelException(GeometryKernelFailure.Crashed,
                    $"The geometry kernel stopped while {doing} '{obj}' (exit code {e.ExitCode?.ToString() ?? "unknown"}). "
                    + "Nothing was changed; it restarts for the next operation.", null, obj);
            }
            // A reply that arrived as the token fired still counts as cancelled: the caller asked to stop.
            if (token.IsCancellationRequested)
            {
                Lose(worker);
                throw new OperationCanceledException(token);
            }
            if (!reply.Ok && reply.Text("code") == "kernel.fault")
            {
                // The worker answered and is exiting: a handler cannot vouch for a heap after a wild write.
                Lose(worker);
                throw new GeometryKernelException(GeometryKernelFailure.Crashed,
                    $"The geometry kernel faulted while {doing} '{obj}' ({reply.Text("detail")}). Nothing was changed; it restarts for the next operation.",
                    "kernel.fault", obj);
            }
            if (reply.Ok && holds is not null) Hold(holds);
            if (_held.Count > MaxHeld) ReleaseOldest(worker);
            return reply;
        }
    }

    private void Hold(string handle)
    {
        if (_heldIndex.Remove(handle, out var old)) _held.Remove(old);
        _heldIndex[handle] = _held.AddLast(handle);
    }

    private void ReleaseOldest(IGeometryKernelWorker worker)
    {
        var drop = new JsonArray();
        while (_held.Count > MaxHeld / 2)
        {
            string h = _held.First!.Value;
            _held.RemoveFirst();
            _heldIndex.Remove(h);
            drop.Add(h);
        }
        try
        {
            kernel.CountRequest();
            worker.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "release", ["shapes"] = drop }));
        }
        catch (GeometryKernelWorkerExitedException) { Lose(worker); }
    }

    private GeometryKernelIdentity StartLocked(string path, CancellationToken token)
    {
        if (_worker is not null && Identity is not null) return Identity;

        IGeometryKernelWorker worker;
        try
        {
            worker = kernel.StartWorker(path);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw kernel.StartFailed(path, $"it could not be started: {e.Message}", this);
        }
        kernel.CountStart(_lost);
        _lost = false;

        GeometryKernelMessage hello;
        using (var timer = new CancellationTokenSource(kernel.Deadlines.Handshake))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, token))
        using (linked.Token.Register(worker.Kill))
        {
            try
            {
                hello = worker.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "hello", ["protocol"] = GeometryKernel.Protocol }));
            }
            catch (GeometryKernelWorkerExitedException e)
            {
                worker.Dispose();
                _lost = true;
                token.ThrowIfCancellationRequested();
                string why = timer.IsCancellationRequested
                    ? $"it did not answer within {Spell(kernel.Deadlines.Handshake)}"
                    : $"it stopped during start-up (exit code {e.ExitCode?.ToString() ?? "unknown"})";
                string tail = LastLine(e.StderrTail);
                throw kernel.StartFailed(path, tail.Length > 0 ? $"{why}: {tail}" : why, this, e.StderrTail);
            }
        }

        var identity = kernel.CheckHandshake(path, hello, worker.StderrTail);
        if (identity is null)
        {
            worker.Dispose();
            throw kernel.LastStartFailure!;
        }
        kernel.StartSucceeded();
        _worker = worker;
        Identity = identity;
        _held.Clear();
        _heldIndex.Clear();
        return identity;
    }

    private void Lose(IGeometryKernelWorker worker)
    {
        worker.Dispose();
        if (ReferenceEquals(_worker, worker)) _worker = null;
        Identity = null;
        _lost = true;
        _held.Clear();
        _heldIndex.Clear();
    }

    /// <summary>Stops the worker politely if it is running, then kills it. Not counted as a loss.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_worker is null) return;
            try
            {
                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using (timer.Token.Register(_worker.Kill))
                    _worker.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "shutdown" }));
            }
            catch (GeometryKernelWorkerExitedException) { }
            _worker.Dispose();
            _worker = null;
            Identity = null;
            _held.Clear();
            _heldIndex.Clear();
        }
    }

    public void Dispose() => Shutdown();

    internal static string Spell(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.#} s" : $"{t.TotalMilliseconds:0} ms";

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
