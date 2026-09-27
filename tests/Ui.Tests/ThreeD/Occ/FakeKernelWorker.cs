using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>
/// A stand-in worker (R-em3d63-9b): everything that can be tested without OpenCASCADE is — the cache, the preview
/// rule, the capability's wording and the handshake's checks — against this. It answers the handshake as the
/// recipe's kernel unless told otherwise, records every request, and can hold a request until released or killed.
/// </summary>
internal sealed class FakeKernelWorker : IGeometryKernelWorker
{
    private readonly FakeKernel _owner;
    private readonly CancellationTokenSource _killed = new();

    public FakeKernelWorker(FakeKernel owner) => _owner = owner;

    public int? ExitCode { get; private set; }

    public string StderrTail => "";

    public GeometryKernelMessage Exchange(GeometryKernelMessage request)
    {
        if (_killed.IsCancellationRequested) throw new GeometryKernelWorkerExitedException(137, "");
        string op = request.Text("op") ?? "";
        lock (_owner.Requests) _owner.Requests.Add((JsonObject)request.Json.DeepClone());
        if (op != "hello" && _owner.Hold is { } hold)
        {
            try { hold.Wait(_killed.Token); }
            catch (OperationCanceledException) { throw new GeometryKernelWorkerExitedException(ExitCode = 137, ""); }
        }
        if (_owner.CrashOn?.Invoke(request) == true)
        {
            ExitCode = 70;
            throw new GeometryKernelWorkerExitedException(70, "a test crash");
        }
        return op switch
        {
            "hello" => new(new JsonObject
            {
                ["ok"] = true, ["protocol"] = _owner.HelloProtocol, ["worker"] = _owner.WorkerVersion,
                ["occt"] = _owner.HelloOcct, ["rid"] = _owner.HelloRid,
            }),
            "build" => _owner.RefuseBuild is { } code
                ? new(new JsonObject { ["ok"] = false, ["code"] = code, ["object"] = "", ["detail"] = "the kernel's own text" })
                : new(new JsonObject
                {
                    ["ok"] = true, ["shape"] = request.Text("shape"), ["object"] = request.Json["tree"]?["root"]?["name"]?.GetValue<string>(),
                    ["valid"] = true, ["solids"] = 1, ["faces"] = 6, ["volume_um3"] = 1.0, ["notes"] = new JsonArray(),
                }, [GeometryKernelBlob.OfBytes("brep", Encoding.UTF8.GetBytes("brep of " + request.Text("shape")))]),
            "tessellate" => new(new JsonObject { ["ok"] = true },
            [
                GeometryKernelBlob.OfDoubles("vertices", [0, 0, 0, 1, 0, 0, 0, 1, 0]),
                GeometryKernelBlob.OfUInts("tris", [0, 1, 2]),
                GeometryKernelBlob.OfUInts("face", [0]),
            ]),
            _ => new(new JsonObject { ["ok"] = true }),
        };
    }

    public void Kill() => _killed.Cancel();

    public void Dispose() => Kill();
}

/// <summary>A <see cref="GeometryKernel"/> whose workers are <see cref="FakeKernelWorker"/>s.</summary>
internal sealed class FakeKernel
{
    public const string WorkerPath = "/fake/geometry-kernel/geometry-worker";

    public List<JsonObject> Requests { get; } = [];
    public List<FakeKernelWorker> Workers { get; } = [];

    public int HelloProtocol { get; set; } = GeometryKernel.Protocol;
    public string HelloOcct { get; set; } = GeometryKernelRecipe.OcctVersion;
    public string HelloRid { get; set; } = GeometryKernelLocator.ProcessRid;
    public string WorkerVersion { get; set; } = "1.0.0-test";

    /// <summary>When set, every request but the handshake waits for it (or for a kill).</summary>
    public SemaphoreSlim? Hold { get; set; }

    /// <summary>When it returns true for a request, the worker "crashes" on it.</summary>
    public Func<GeometryKernelMessage, bool>? CrashOn { get; set; }

    /// <summary>When set, every build is refused with this code.</summary>
    public string? RefuseBuild { get; set; }

    public GeometryKernel Create(string? cacheDir = null) => new(new GeometryKernelOptions
    {
        Locate = () => new GeometryKernelLocation(WorkerPath, GeometryKernelRoute.BesideTheApplication, "included with circuitRF", null, "", ""),
        Start = _ =>
        {
            var w = new FakeKernelWorker(this);
            lock (Workers) Workers.Add(w);
            return w;
        },
        DiskCache = cacheDir is not null,
        CacheDirectory = cacheDir,
        LogPath = Path.Combine(Path.GetTempPath(), "crf-geometry-kernel-tests.log"),
    });

    /// <summary>The requests of one kind that were sent, in order.</summary>
    public List<JsonObject> Sent(string op)
    {
        lock (Requests) return [.. Requests.Where(r => r["op"]?.GetValue<string>() == op)];
    }
}
