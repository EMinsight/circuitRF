// brief-em3d-63 — the managed client of tools/geometry-worker. The rest of circuitRF asks THIS object three
// questions and nothing else ever starts the worker, reads its pipes or words its absence:
//
//   1. Is it here?   Capability — available, or absent with a reason and an action (R-em3d63-3).
//   2. Build this.   A resolved tree in; a B-rep, a tessellation, a face table and an edge table out — from
//                    the cache when the tree has been built before (R-em3d63-6).
//   3. Convert this. Export shapes as B-rep, STEP, PLY or STL; import a STEP file.
//
// THE ONE PLACE THE ABSENCE IS WORDED (R-em3d63-3b). Every disabled command's tooltip, the refusal on open
// (brief 64), `check`, `explain` and `em` read their sentence from NeedsKernel; a source test fails on the
// words "geometry kernel" anywhere else in src/Ui or src/Cli.
//
// OFF THE FRAME PATH (overview §1j). Cached by resolved input; previews asynchronous, the newest superseding;
// a crash costs one operation and never the document. Every gate is a COUNTER here, never a timing.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Engine;

namespace CircuitRF.Design.ThreeD.Occ;

/// <summary>Per-request deadlines (R-em3d63-2c, -7b). A miss kills the worker and refuses the request naming the deadline.</summary>
public sealed record GeometryKernelDeadlines
{
    /// <summary>A cold start loads ~25 shared libraries off a possibly slow disk; brief 61 measured 40 ms warm.</summary>
    public TimeSpan Handshake { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Build { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ImportStep { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan Other { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>How a <see cref="GeometryKernel"/> finds, starts and caches. The defaults are the application's.</summary>
public sealed record GeometryKernelOptions
{
    public Func<GeometryKernelLocation> Locate { get; init; } = GeometryKernelLocator.Locate;

    /// <summary>Starts the worker at a path. Tests substitute a stand-in, or add the test-request switch.</summary>
    public Func<string, IGeometryKernelWorker> Start { get; init; } = path => new GeometryKernelProcessWorker(path);

    /// <summary>The on-disk cache; null is <c>UserStateDirectory.SubDir("geometry-cache")</c>, resolved when used.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>False keeps the cache in memory only.</summary>
    public bool DiskCache { get; init; } = true;

    public string ExpectedOcct { get; init; } = GeometryKernelRecipe.OcctVersion;
    public string ExpectedRid { get; init; } = GeometryKernelLocator.ProcessRid;
    public GeometryKernelDeadlines Deadlines { get; init; } = new();
    public long MemoryBudgetBytes { get; init; } = 256L << 20;
    public long DiskBudgetBytes { get; init; } = 1L << 30;

    /// <summary>Where a failed start's stderr is written, for a report; null is the user state directory's.</summary>
    public string? LogPath { get; init; }
}

/// <summary>The worker's build switches (R-em3d63-4b). Fuzzy 0 is brief 61's finding: nothing needs one.</summary>
public sealed record GeometryKernelBuildOptions(double Fuzzy = 0, bool KeepTools = false)
{
    public static GeometryKernelBuildOptions Default { get; } = new();

    internal string Canonical => string.Create(CultureInfo.InvariantCulture, $"fuzzy={Fuzzy:R};keepTools={(KeepTools ? 1 : 0)}");
}

/// <summary>A built shape: what the worker holds under <see cref="Handle"/>, and its B-rep bytes.</summary>
public sealed record GeometryKernelBuild(string Handle, string Object, bool Valid, int Solids, int Faces, double VolumeUm3,
                                         IReadOnlyList<string> Notes, byte[] Brep)
{
    /// <summary>SHA-256 of the B-rep bytes — what brief 64's solid is content-hashed by.</summary>
    public string BrepHash { get; } = Convert.ToHexStringLower(SHA256.HashData(Brep));
}

/// <summary>A tessellation, micrometres: three doubles per vertex, three indices per triangle, and each triangle's
/// face as an index into <see cref="GeometryKernel.Faces"/>' list.</summary>
public sealed record GeometryKernelMesh(double[] Vertices, uint[] Triangles, uint[] TriangleFace);

/// <summary>One face of a built shape: its name, surface kind (plane, cylinder, cone, sphere, torus, bspline, other),
/// tight box [x0,y0,z0,x1,y1,z1] µm, area µm² and smallest radius of curvature µm (0 for a plane).</summary>
public sealed record GeometryKernelFace(string Name, string Kind, double[] Box, double Area, double MinRadius);

/// <summary>One feature edge: its name (overview §1g), the two faces it separates, curve kind, length, smallest radius
/// (0 for a line) and a polyline for drawing and snapping, three doubles per point.</summary>
public sealed record GeometryKernelEdge(string Name, string FaceA, string FaceB, string Kind, double Length, double MinRadius, double[] Polyline);

/// <summary>One part of an imported STEP file, held by the worker under <see cref="Handle"/>.</summary>
public sealed record GeometryKernelImportPart(string Handle, string Name, string Path, double[]? Colour, int Solids, int Faces, bool Valid);

/// <summary>What <see cref="GeometryKernel.ImportStep"/> read: the file's length units, its parts, and what healing did.</summary>
public sealed record GeometryKernelImport(IReadOnlyList<string> Units, IReadOnlyList<GeometryKernelImportPart> Parts, IReadOnlyList<string> Healing);

/// <summary>A preview: the build and its tessellation.</summary>
public sealed record GeometryKernelPreview(GeometryKernelBuild Build, GeometryKernelMesh Mesh);

/// <summary>One shape to export, with the name and colour a STEP file carries.</summary>
public sealed record GeometryKernelExportItem(GeometryKernelTree Tree, string? Name = null, double[]? Colour = null);

/// <summary>The geometry kernel, as circuitRF sees it: discovery, capability, and every request to the worker.</summary>
public sealed class GeometryKernel : IDisposable
{
    /// <summary>The protocol this build speaks; the worker must answer with exactly this.</summary>
    public const int Protocol = 1;

    /// <summary>Row 1 of discovery: a developer pointing at a build of their own, CI at the one it built.</summary>
    public const string EnvironmentVariable = "CIRCUITRF_GEOMETRY_WORKER";

    /// <summary>What a kernel command's tooltip says until the background probe answers (R-em3d63-3c).</summary>
    public const string Checking = "Checking for the geometry kernel…";

    /// <summary>The Settings row's title (R-em3d63-3d).</summary>
    public const string SettingsTitle = "Geometry kernel";

    private static readonly Lazy<GeometryKernel> _shared = new(() => new GeometryKernel(new GeometryKernelOptions()));

    /// <summary>The application's kernel: one per process.</summary>
    public static GeometryKernel Shared => _shared.Value;

    private readonly GeometryKernelOptions _options;
    private readonly GeometryKernelSession _model;
    private readonly GeometryKernelSession _previewSession;
    private readonly GeometryKernelCache _cache;
    private readonly object _probeGate = new();
    private volatile GeometryKernelCapability? _capability;
    private GeometryKernelIdentity? _identity;
    private int _consecutiveStartFailures;

    private long _requestsSent, _memoryHits, _diskHits, _workerStarts, _workerRestarts, _previewsDiscarded;

    public GeometryKernel(GeometryKernelOptions options)
    {
        _options = options;
        _model = new GeometryKernelSession(this, "model");
        _previewSession = new GeometryKernelSession(this, "preview");
        _cache = new GeometryKernelCache(options.MemoryBudgetBytes, options.DiskBudgetBytes,
            () => !options.DiskCache ? null : options.CacheDirectory ?? UserStateDirectory.SubDir("geometry-cache"));
    }

    // ── counters (R-em3d63-6d): every gate is one of these ───────────────────────────────────────

    /// <summary>Requests sent to a worker, the handshake excepted.</summary>
    public long RequestsSent => Interlocked.Read(ref _requestsSent);
    public long MemoryHits => Interlocked.Read(ref _memoryHits);
    public long DiskHits => Interlocked.Read(ref _diskHits);

    /// <summary>Every worker process started, by either session.</summary>
    public long WorkerStarts => Interlocked.Read(ref _workerStarts);

    /// <summary>Starts that replaced a worker lost to a crash, a timeout or a cancel.</summary>
    public long WorkerRestarts => Interlocked.Read(ref _workerRestarts);

    /// <summary>Preview replies that arrived after a newer preview superseded them.</summary>
    public long PreviewsDiscarded => Interlocked.Read(ref _previewsDiscarded);

    internal GeometryKernelDeadlines Deadlines => _options.Deadlines;

    internal void CountRequest() => Interlocked.Increment(ref _requestsSent);

    internal void CountStart(bool restart)
    {
        Interlocked.Increment(ref _workerStarts);
        if (restart) Interlocked.Increment(ref _workerRestarts);
    }

    internal IGeometryKernelWorker StartWorker(string path) => _options.Start(path);

    // ── capability (R-em3d63-3) ──────────────────────────────────────────────────────────────────

    /// <summary>Raised whenever a probe (or a failure that changes the answer) settles the capability.</summary>
    public event Action<GeometryKernelCapability>? CapabilityChanged;

    /// <summary>The capability if something has asked already, else null — what a GUI reads without waiting.</summary>
    public GeometryKernelCapability? Known => _capability;

    /// <summary>The capability, probing the first time it is asked — the CLI's lazy route (R-em3d63-3c).</summary>
    public GeometryKernelCapability Capability => _capability ?? Probe();

    /// <summary>Probes off the calling thread: what the GUI runs once after the main window is shown.</summary>
    public Task<GeometryKernelCapability> ProbeAsync() => Task.Run(Probe);

    /// <summary>
    /// Locates the worker again and shakes hands with it — the Settings row's <i>Check again</i>. Stops any
    /// running worker first, so a folder put back (or taken away) since the last probe is what answers.
    /// </summary>
    public GeometryKernelCapability Probe()
    {
        lock (_probeGate)
        {
            _model.Shutdown();
            _previewSession.Shutdown();
            _identity = null;
            Interlocked.Exchange(ref _consecutiveStartFailures, 0);
            _cache.ForgetFailures();

            var loc = _options.Locate();
            GeometryKernelCapability cap;
            if (!loc.Found)
            {
                cap = new GeometryKernelCapability(false, loc.Absence, null, null, loc.HowFound, loc.Reason, loc.Action) { Route = loc.Route };
            }
            else
            {
                _located = loc;
                _capability = null;
                try
                {
                    var id = _model.EnsureStarted(loc.WorkerPath!, CancellationToken.None);
                    _identity = id;
                    cap = new GeometryKernelCapability(true, null, loc.WorkerPath, id.Occt, loc.HowFound, "", "")
                    { WorkerVersion = id.Worker, Route = loc.Route };
                }
                catch (GeometryKernelException e)
                {
                    // A handshake that named a different kernel has already set WrongVersion; anything else is Broken.
                    cap = _capability is { Available: false } set ? set
                        : Absent(GeometryKernelAbsence.Broken, loc.WorkerPath!, loc, e.Message);
                }
            }
            Settle(cap);
            return cap;
        }
    }

    private GeometryKernelLocation? _located;

    private void Settle(GeometryKernelCapability cap)
    {
        _capability = cap;
        CapabilityChanged?.Invoke(cap);
    }

    private GeometryKernelCapability Absent(GeometryKernelAbsence absence, string path, GeometryKernelLocation loc, string reason) =>
        new(false, absence, path, null, loc.HowFound, reason, ActionFor(absence, loc.Route, LogPath)) { Route = loc.Route };

    /// <summary>The action sentence for an absence (R-em3d63-3a's table). A worker named by the environment variable is
    /// the developer's to fix; one in a source tree is rebuilt; one an installer laid down is reinstalled.</summary>
    public static string ActionFor(GeometryKernelAbsence absence, GeometryKernelRoute? route, string? logPath = null) => absence switch
    {
        GeometryKernelAbsence.NotBuilt =>
            "Build it with tools/geometry-worker/build.sh (build.cmd on Windows), then check again.",
        GeometryKernelAbsence.NotShippedOnThisPlatform =>
            "This platform's circuitRF does not include the geometry kernel. Booleans, fillets and STEP need a 64-bit build.",
        _ when route == GeometryKernelRoute.EnvironmentVariable =>
            $"Unset {EnvironmentVariable} or point it at a matching build.",
        _ when route == GeometryKernelRoute.SourceTree =>
            "Rebuild it with tools/geometry-worker/build.sh (build.cmd on Windows), then check again.",
        GeometryKernelAbsence.Broken =>
            $"Reinstall circuitRF. If it persists, report it with the log at {logPath ?? DefaultLogPath}.",
        _ => "Reinstall circuitRF to restore it.",
    };

    private string LogPath => _options.LogPath ?? DefaultLogPath;

    private static string DefaultLogPath => Path.Combine(UserStateDirectory.SubDir("geometry-kernel"), "start.log");

    /// <summary>
    /// The ONE sentence every surface uses for a kernel feature it cannot offer (R-em3d63-3b):
    /// <i>"&lt;What&gt; needs the geometry kernel, which this installation does not have: &lt;Reason&gt; &lt;Action&gt;"</i>.
    /// </summary>
    public static string NeedsKernel(string what, GeometryKernelCapability capability) => NeedsKernel(what, capability, plural: false);

    /// <summary>The same sentence with a plural subject — <i>"'lid' (a Boolean) and 'shell' (a Step part) need …"</i>, the
    /// refusal on open (brief 64 R-em3d64-5b).</summary>
    public static string NeedsKernel(string what, GeometryKernelCapability capability, bool plural) =>
        $"{what} {(plural ? "need" : "needs")} the geometry kernel, which this installation does not have: {capability.Reason} {capability.Action}";

    /// <summary><see cref="NeedsKernel(string, GeometryKernelCapability)"/> against the application's kernel, probing it if nothing has yet.</summary>
    public static string NeedsKernel(string what) => NeedsKernel(what, Shared.Capability);

    /// <summary>A kernel command's tooltip when it is disabled, or null when it is enabled: <see cref="Checking"/> until the
    /// probe answers, then <see cref="NeedsKernel(string, GeometryKernelCapability)"/>.</summary>
    public static string? DisabledReason(string what, GeometryKernelCapability? capability) =>
        capability is null ? Checking : capability.Available ? null : NeedsKernel(what, capability);

    /// <summary>The Settings row's status line (R-em3d63-3d).</summary>
    public static string SettingsStatus(GeometryKernelCapability? c) => c switch
    {
        null => Checking,
        { Available: true } => $"Open CASCADE Technology {c.OcctVersion}, {c.HowFound}",
        _ => $"{c.Reason} {c.Action}",
    };

    /// <summary>The About box's line — the prominent notice the Open CASCADE Exception asks for (brief 62). It names the
    /// version the worker REPORTED, and never a library that is not there.</summary>
    public static string AboutNotice(GeometryKernelCapability? c) => c switch
    {
        null => "Geometry kernel: checking…",
        { Available: true } => $"Uses Open CASCADE Technology {c.OcctVersion} (LGPL-2.1 with the Open CASCADE Exception).",
        { Absence: GeometryKernelAbsence.NotBuilt or GeometryKernelAbsence.NotShippedOnThisPlatform or GeometryKernelAbsence.Missing } =>
            "Geometry kernel: not included in this installation, so booleans, fillets and STEP are unavailable.",
        _ => $"Uses Open CASCADE Technology (LGPL-2.1 with the Open CASCADE Exception). Its geometry kernel did not start: {c.Reason}",
    };

    // ── handshake and start failures, called by a session ────────────────────────────────────────

    internal GeometryKernelException? LastStartFailure { get; private set; }

    /// <summary>Checks a handshake against the pinned recipe (R-em3d63-2b). Null, with <see cref="LastStartFailure"/> set,
    /// when it does not match — and a mismatch is WrongVersion at once: a different kernel does not become right by
    /// being asked again.</summary>
    internal GeometryKernelIdentity? CheckHandshake(string path, GeometryKernelMessage hello, string stderr)
    {
        var loc = _located ?? new GeometryKernelLocation(path, null, "", null, "", "");
        string? code = hello.Text("code");
        if (!hello.Ok)
        {
            string detail = hello.Text("detail") ?? hello.Text("error") ?? "no detail";
            if (code is "protocol.mismatch" or "kernel.mismatch")
                return WrongVersion(path, loc, $"The geometry kernel at {path} refused this build's handshake: {detail}.");
            LastStartFailure = StartFailed(path, $"its handshake was refused: {detail}", null, stderr);
            return null;
        }

        int protocol = hello.Json["protocol"] is JsonValue p && p.TryGetValue(out int n) ? n : -1;
        string occt = hello.Text("occt") ?? "an unreported version";
        string worker = hello.Text("worker") ?? "unknown";
        string rid = hello.Text("rid") ?? "an unreported platform";
        if (protocol != Protocol)
            return WrongVersion(path, loc, $"The geometry kernel at {path} speaks protocol {protocol}; this build of circuitRF speaks {Protocol}.");
        if (occt != _options.ExpectedOcct)
            return WrongVersion(path, loc, $"The geometry kernel at {path} is Open CASCADE Technology {occt}; this build of circuitRF was made with {_options.ExpectedOcct}.");
        if (rid != _options.ExpectedRid)
            return WrongVersion(path, loc, $"The geometry kernel at {path} was built for {rid}; this circuitRF runs as {_options.ExpectedRid}.");
        return new GeometryKernelIdentity(protocol, occt, worker, rid);
    }

    private GeometryKernelIdentity? WrongVersion(string path, GeometryKernelLocation loc, string reason)
    {
        var cap = new GeometryKernelCapability(false, GeometryKernelAbsence.WrongVersion, path, null, loc.HowFound, reason,
                                               ActionFor(GeometryKernelAbsence.WrongVersion, loc.Route, LogPath)) { Route = loc.Route };
        Settle(cap);
        LastStartFailure = new GeometryKernelException(GeometryKernelFailure.StartFailed, NeedsKernel("This operation", cap));
        return null;
    }

    /// <summary>A start that failed: logged, counted, and after three in a row the capability is Broken for the
    /// process (R-em3d63-7a) — until <see cref="Probe"/> is asked again.</summary>
    internal GeometryKernelException StartFailed(string path, string why, GeometryKernelSession? session, string? stderr = null)
    {
        string reason = $"The geometry kernel at {path} did not start: {why}.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {session?.Role ?? "probe"}: {reason}\n{stderr}\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (Interlocked.Increment(ref _consecutiveStartFailures) >= 3 && _located is { } loc)
            Settle(Absent(GeometryKernelAbsence.Broken, path, loc, reason));
        return LastStartFailure = new GeometryKernelException(GeometryKernelFailure.StartFailed, reason);
    }

    internal void StartSucceeded() => Interlocked.Exchange(ref _consecutiveStartFailures, 0);

    // ── the requests ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The worker to start and the identity cache keys carry — or the refusal naming what is missing.</summary>
    private (string Path, GeometryKernelIdentity Id) Require(string what)
    {
        var cap = Capability;
        if (!cap.Available || cap.WorkerPath is null || _identity is null)
            throw new GeometryKernelException(GeometryKernelFailure.Unavailable, NeedsKernel(what, cap));
        return (cap.WorkerPath, _identity);
    }

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>The handle a tree is held under: the tree's hash, or its hash with the options when they are not the defaults.</summary>
    public static string HandleOf(GeometryKernelTree tree, GeometryKernelBuildOptions? options = null) =>
        options is null || options == GeometryKernelBuildOptions.Default ? tree.Hash : Sha(tree.Hash + "\n" + options.Canonical);

    private static string Key(string op, string handle, string parameters, GeometryKernelIdentity id) =>
        Sha($"{op}\n{handle}\n{parameters}\n{id.Key}");

    /// <summary>
    /// Builds <paramref name="tree"/> — from the cache when it has been built before by this kernel (memory, then disk).
    /// </summary>
    /// <exception cref="GeometryKernelException">Refused, crashed, timed out, or no kernel; the message is the sentence.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="control"/>'s token cancelled; the worker was killed.</exception>
    public GeometryKernelBuild Build(GeometryKernelTree tree, GeometryKernelBuildOptions? options = null, RunControl? control = null)
        => Build(_model, tree, options ?? GeometryKernelBuildOptions.Default, control);

    private GeometryKernelBuild Build(GeometryKernelSession s, GeometryKernelTree tree, GeometryKernelBuildOptions options, RunControl? control)
    {
        var (path, id) = Require($"Building '{tree.Object}'");
        string handle = HandleOf(tree, options);
        string key = Key("build", handle, "", id);
        if (_cache.Failed(key) is { } failed) throw failed;
        if (_cache.TryMemory(key, out GeometryKernelBuild hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        if (_cache.ReadDisk(key, "build") is { } disk && TryParseBuild(disk, handle, tree.Object) is { } fromDisk)
        {
            Interlocked.Increment(ref _diskHits);
            _cache.PutMemory(key, fromDisk, fromDisk.Brep.Length + 512);
            return fromDisk;
        }
        var reply = Guard(key, () => SendBuild(s, path, tree, options, handle, control));
        var built = TryParseBuild(reply, handle, tree.Object)
                    ?? throw new GeometryKernelException(GeometryKernelFailure.Refused, $"The geometry kernel's reply for '{tree.Object}' was not understood.");
        _cache.PutMemory(key, built, built.Brep.Length + 512);
        _cache.WriteDisk(key, "build", reply);
        return built;
    }

    private GeometryKernelMessage SendBuild(GeometryKernelSession s, string path, GeometryKernelTree tree, GeometryKernelBuildOptions options,
                                            string handle, RunControl? control)
    {
        var request = new GeometryKernelMessage(new JsonObject
        {
            ["op"] = "build",
            ["shape"] = handle,
            ["tree"] = JsonNode.Parse(tree.Json),
            ["options"] = new JsonObject { ["fuzzy"] = options.Fuzzy, ["keepTools"] = options.KeepTools },
        });
        var reply = s.Send(path, request, Deadlines.Build, control, "building", tree.Object, holds: handle);
        return reply.Ok ? reply : throw Refused(reply, tree.Object);
    }

    /// <summary>Runs a request, recording a refusal or a crash as this key's answer for the process (R-em3d63-6c).
    /// A timeout and a cancel are not recorded: neither says anything about the tree.</summary>
    private T Guard<T>(string key, Func<T> send)
    {
        try
        {
            return send();
        }
        catch (GeometryKernelException e) when (e.Failure is GeometryKernelFailure.Refused or GeometryKernelFailure.Crashed)
        {
            _cache.RecordFailure(key, e);
            throw;
        }
    }

    private static GeometryKernelBuild? TryParseBuild(GeometryKernelMessage m, string handle, string obj)
    {
        if (!m.Ok || m.Blob("brep") is not { } brep) return null;
        var notes = m.Json["notes"] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToList() : [];
        return new GeometryKernelBuild(handle, m.Text("object") is { Length: > 0 } o ? o : obj,
            m.Json["valid"]?.GetValue<bool>() ?? false, Int(m, "solids"), Int(m, "faces"),
            m.Json["volume_um3"]?.GetValue<double>() ?? 0, notes, brep.Data);
    }

    private static int Int(GeometryKernelMessage m, string key) => m.Json[key] is JsonValue v && v.TryGetValue(out int n) ? n : 0;

    /// <summary>Makes sure the session's worker holds <paramref name="tree"/>'s shape, building it there if it does not
    /// (a restarted worker holds nothing, and a cache hit never reached a worker at all).</summary>
    private string EnsureHeld(GeometryKernelSession s, string path, GeometryKernelIdentity id, GeometryKernelTree tree,
                              GeometryKernelBuildOptions options, RunControl? control)
    {
        string handle = HandleOf(tree, options);
        if (s.Holds(handle)) return handle;
        string key = Key("build", handle, "", id);
        if (_cache.Failed(key) is { } failed) throw failed;
        Guard(key, () => SendBuild(s, path, tree, options, handle, control));
        return handle;
    }

    /// <summary>Tessellates <paramref name="tree"/> at a linear deflection (µm) and an angular one (radians) — cached like a build.</summary>
    public GeometryKernelMesh Tessellate(GeometryKernelTree tree, double linearUm, double angularRad = 0.5,
                                        GeometryKernelBuildOptions? options = null, RunControl? control = null)
        => Tessellate(_model, tree, linearUm, angularRad, options ?? GeometryKernelBuildOptions.Default, control);

    private GeometryKernelMesh Tessellate(GeometryKernelSession s, GeometryKernelTree tree, double linearUm, double angularRad,
                                          GeometryKernelBuildOptions options, RunControl? control)
    {
        var (path, id) = Require($"Drawing '{tree.Object}'");
        string key = Key("tessellate", HandleOf(tree, options), string.Create(CultureInfo.InvariantCulture, $"{linearUm:R};{angularRad:R}"), id);
        if (_cache.Failed(key) is { } failed) throw failed;
        if (_cache.TryMemory(key, out GeometryKernelMesh hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        if (_cache.ReadDisk(key, "mesh") is { } disk && ParseMesh(disk) is { } fromDisk)
        {
            Interlocked.Increment(ref _diskHits);
            _cache.PutMemory(key, fromDisk, SizeOf(fromDisk));
            return fromDisk;
        }
        var reply = Guard(key, () =>
        {
            string handle = EnsureHeld(s, path, id, tree, options, control);
            var r = s.Send(path, new GeometryKernelMessage(new JsonObject
            {
                ["op"] = "tessellate", ["shape"] = handle, ["linear_um"] = linearUm, ["angular_rad"] = angularRad,
            }), Deadlines.Other, control, "tessellating", tree.Object);
            return r.Ok ? r : throw Refused(r, tree.Object);
        });
        var mesh = ParseMesh(reply) ?? throw new GeometryKernelException(GeometryKernelFailure.Refused, $"The geometry kernel's tessellation of '{tree.Object}' was not understood.");
        _cache.PutMemory(key, mesh, SizeOf(mesh));
        _cache.WriteDisk(key, "mesh", reply);
        return mesh;
    }

    private static long SizeOf(GeometryKernelMesh m) => m.Vertices.Length * 8L + m.Triangles.Length * 4L + m.TriangleFace.Length * 4L + 256;

    private static GeometryKernelMesh? ParseMesh(GeometryKernelMessage m) =>
        m.Ok && m.Blob("vertices") is { } v && m.Blob("tris") is { } t && m.Blob("face") is { } f
            ? new GeometryKernelMesh(v.Doubles(), t.UInts(), f.UInts())
            : null;

    /// <summary>The face table of <paramref name="tree"/>'s shape, in the order a tessellation's face indices use.</summary>
    public IReadOnlyList<GeometryKernelFace> Faces(GeometryKernelTree tree, GeometryKernelBuildOptions? options = null, RunControl? control = null)
    {
        options ??= GeometryKernelBuildOptions.Default;
        var (path, id) = Require($"Reading the faces of '{tree.Object}'");
        string key = Key("faces", HandleOf(tree, options), "", id);
        if (_cache.TryMemory(key, out IReadOnlyList<GeometryKernelFace> hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        var reply = Guard(key, () =>
        {
            string handle = EnsureHeld(_model, path, id, tree, options, control);
            var r = _model.Send(path, new GeometryKernelMessage(new JsonObject { ["op"] = "faces", ["shape"] = handle }),
                                Deadlines.Other, control, "reading the faces of", tree.Object);
            return r.Ok ? r : throw Refused(r, tree.Object);
        });
        List<GeometryKernelFace> faces = [];
        foreach (var f in reply.Json["faces"] as JsonArray ?? [])
        {
            if (f is null) continue;
            faces.Add(new GeometryKernelFace(f["name"]?.GetValue<string>() ?? "", f["kind"]?.GetValue<string>() ?? "other",
                [.. (f["box"] as JsonArray ?? []).Select(x => x?.GetValue<double>() ?? 0)],
                f["area"]?.GetValue<double>() ?? 0, f["min_radius"]?.GetValue<double>() ?? 0));
        }
        _cache.PutMemory(key, faces, faces.Count * 256L + 256);
        return faces;
    }

    /// <summary>The feature edges of <paramref name="tree"/>'s shape, each with a polyline at <paramref name="deflectionUm"/>.</summary>
    public IReadOnlyList<GeometryKernelEdge> Edges(GeometryKernelTree tree, double deflectionUm, GeometryKernelBuildOptions? options = null,
                                                   RunControl? control = null)
    {
        options ??= GeometryKernelBuildOptions.Default;
        var (path, id) = Require($"Reading the edges of '{tree.Object}'");
        string key = Key("edges", HandleOf(tree, options), deflectionUm.ToString("R", CultureInfo.InvariantCulture), id);
        if (_cache.TryMemory(key, out IReadOnlyList<GeometryKernelEdge> hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        var reply = Guard(key, () =>
        {
            string handle = EnsureHeld(_model, path, id, tree, options, control);
            var r = _model.Send(path, new GeometryKernelMessage(new JsonObject { ["op"] = "edges", ["shape"] = handle, ["deflection_um"] = deflectionUm }),
                                Deadlines.Other, control, "reading the edges of", tree.Object);
            return r.Ok ? r : throw Refused(r, tree.Object);
        });
        double[] poly = reply.Blob("polylines")?.Doubles() ?? [];
        List<GeometryKernelEdge> edges = [];
        int at = 0;
        foreach (var e in reply.Json["edges"] as JsonArray ?? [])
        {
            if (e is null) continue;
            int n = e["points"]?.GetValue<int>() ?? 0;
            var faces = e["faces"] as JsonArray;
            edges.Add(new GeometryKernelEdge(e["name"]?.GetValue<string>() ?? "",
                faces?[0]?.GetValue<string>() ?? "", faces?[1]?.GetValue<string>() ?? "",
                e["kind"]?.GetValue<string>() ?? "other", e["length"]?.GetValue<double>() ?? 0, e["min_radius"]?.GetValue<double>() ?? 0,
                poly.AsSpan(3 * at, 3 * n).ToArray()));
            at += n;
        }
        _cache.PutMemory(key, edges, poly.Length * 8L + edges.Count * 256L + 256);
        return edges;
    }

    /// <summary>
    /// Writes <paramref name="items"/> as <paramref name="format"/> — <c>brep</c>, <c>step</c>, <c>ply</c> or <c>stl</c> — in
    /// <paramref name="units"/> (<c>um</c>, <c>mm</c>, <c>mil</c>, <c>in</c>, <c>m</c>). Not cached: a STEP file carries a timestamp.
    /// </summary>
    public byte[] Export(IReadOnlyList<GeometryKernelExportItem> items, string format, string units = "um",
                         double linearUm = 1, double angularRad = 0.5, string? schema = null, RunControl? control = null)
    {
        string what = items.Count == 1 ? $"'{items[0].Tree.Object}'" : $"{items.Count} objects";
        var (path, id) = Require($"Exporting {what}");
        var handles = new JsonArray();
        var names = new JsonArray();
        var colours = new JsonArray();
        foreach (var item in items)
        {
            handles.Add(EnsureHeld(_model, path, id, item.Tree, GeometryKernelBuildOptions.Default, control));
            names.Add(item.Name ?? item.Tree.Object);
            colours.Add(item.Colour is { Length: 3 } c ? new JsonArray(c[0], c[1], c[2]) : null);
        }
        var request = new JsonObject
        {
            ["op"] = "export", ["shapes"] = handles, ["format"] = format, ["units"] = units, ["names"] = names, ["colours"] = colours,
            ["linear_um"] = linearUm, ["angular_rad"] = angularRad,
        };
        if (schema is not null) request["schema"] = schema;
        var reply = _model.Send(path, new GeometryKernelMessage(request), Deadlines.Other, control, "exporting", what.Trim('\''));
        if (!reply.Ok) throw Refused(reply, what.Trim('\''));
        return reply.Blob("data")?.Data ?? [];
    }

    /// <summary>Reads a STEP file: one held shape per part, with names, colours, units and the healing report.</summary>
    public GeometryKernelImport ImportStep(byte[] file, RunControl? control = null)
    {
        var (path, _) = Require("Importing a STEP file");
        string prefix = "step:" + Convert.ToHexStringLower(SHA256.HashData(file));
        var reply = _model.Send(path, new GeometryKernelMessage(new JsonObject { ["op"] = "import-step", ["shape"] = prefix },
                                                                [GeometryKernelBlob.OfBytes("file", file)]),
                                Deadlines.ImportStep, control, "importing", "the STEP file");
        if (!reply.Ok) throw Refused(reply, "the STEP file");
        var parts = new List<GeometryKernelImportPart>();
        foreach (var p in reply.Json["parts"] as JsonArray ?? [])
        {
            if (p is null) continue;
            double[]? colour = p["colour"] is JsonArray c ? [.. c.Select(x => x?.GetValue<double>() ?? 0)] : null;
            parts.Add(new GeometryKernelImportPart(p["shape"]?.GetValue<string>() ?? "", p["name"]?.GetValue<string>() ?? "",
                p["path"]?.GetValue<string>() ?? "", colour, p["solids"]?.GetValue<int>() ?? 0, p["faces"]?.GetValue<int>() ?? 0,
                p["valid"]?.GetValue<bool>() ?? false));
        }
        return new GeometryKernelImport(Strings(reply, "units"), parts, Strings(reply, "healing"));
    }

    private static List<string> Strings(GeometryKernelMessage m, string key) =>
        m.Json[key] is JsonArray a ? [.. a.Select(x => x?.GetValue<string>() ?? "")] : [];

    // ── refusals, in circuitRF's voice (R-em3d63-4d) ─────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, Func<string, string>> RefusalSentences = new Dictionary<string, Func<string, string>>
    {
        ["tree.invalid"] = o => $"'{o}' is not a solid the geometry kernel can build",
        ["build.failed"] = o => $"The geometry kernel could not build '{o}'",
        ["build.invalid-result"] = o => $"The geometry kernel built '{o}', but the result is not a valid solid",
        ["shape.unknown"] = o => $"The geometry kernel no longer held '{o}'",
        ["export.failed"] = o => $"The geometry kernel could not export {o}",
        ["import.failed"] = _ => "The geometry kernel could not read the STEP file",
        ["kernel.exception"] = o => $"The geometry kernel failed on '{o}'",
        ["request.malformed"] = o => $"The geometry kernel did not understand circuitRF's request for '{o}'",
    };

    /// <summary>A refusal's sentence: the code mapped to circuitRF's words naming the object, then the worker's own
    /// detail verbatim — the upstream line is the value. An unmapped code is said to be unrecognised, never guessed at.</summary>
    public static GeometryKernelException Refused(GeometryKernelMessage reply, string obj)
    {
        string code = reply.Text("code") ?? "";
        string detail = reply.Text("detail") ?? reply.Text("error") ?? "";
        string o = reply.Text("object") is { Length: > 0 } named ? named : obj;
        string sentence = RefusalSentences.TryGetValue(code, out var words)
            ? $"{words(o)}: {detail}"
            : $"The geometry kernel could not build '{o}': {detail} (an unrecognised refusal, '{code}')";
        return new GeometryKernelException(GeometryKernelFailure.Refused, sentence, code, o);
    }

    // ── previews (R-em3d63-8) ────────────────────────────────────────────────────────────────────

    private sealed class PreviewRequest(GeometryKernelTree tree, GeometryKernelBuildOptions options, double linearUm, double angularRad)
    {
        public GeometryKernelTree Tree { get; } = tree;
        public GeometryKernelBuildOptions Options { get; } = options;
        public double LinearUm { get; } = linearUm;
        public double AngularRad { get; } = angularRad;
        public TaskCompletionSource<GeometryKernelPreview?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Superseded { get; set; }
    }

    private readonly object _previewGate = new();
    private PreviewRequest? _pending, _inFlight;
    private bool _previewLoop;

    /// <summary>
    /// Asks for a preview on the PREVIEW session, so it can never delay a commit. A newer request supersedes any pending
    /// one (dropped unsent; its task completes null) and any in-flight one (its reply is discarded when it arrives, and
    /// <see cref="PreviewsDiscarded"/> counts it). Only the latest is ever delivered.
    /// </summary>
    public Task<GeometryKernelPreview?> RequestPreview(GeometryKernelTree tree, double linearUm, double angularRad = 0.5,
                                                       GeometryKernelBuildOptions? options = null)
    {
        var req = new PreviewRequest(tree, options ?? GeometryKernelBuildOptions.Default, linearUm, angularRad);
        lock (_previewGate)
        {
            _pending?.Done.TrySetResult(null);
            _pending = req;
            if (_inFlight is not null) _inFlight.Superseded = true;
            if (!_previewLoop)
            {
                _previewLoop = true;
                _ = Task.Run(PreviewLoop);
            }
        }
        return req.Done.Task;
    }

    /// <summary>Closing a dialog, or Esc: the pending preview is dropped and the in-flight one stopped — which kills the
    /// preview session's worker; it starts again lazily. The model session is untouched.</summary>
    public void CancelPreview()
    {
        lock (_previewGate)
        {
            _pending?.Done.TrySetResult(null);
            _pending = null;
            if (_inFlight is null) return;
            _inFlight.Superseded = true;
        }
        _previewSession.Kill();
    }

    private void PreviewLoop()
    {
        while (true)
        {
            PreviewRequest req;
            lock (_previewGate)
            {
                if (_pending is null)
                {
                    _previewLoop = false;
                    return;
                }
                req = _pending;
                _pending = null;
                _inFlight = req;
            }

            GeometryKernelPreview? result = null;
            Exception? error = null;
            try
            {
                var build = Build(_previewSession, req.Tree, req.Options, null);
                if (!IsSuperseded(req))
                    result = new GeometryKernelPreview(build, Tessellate(_previewSession, req.Tree, req.LinearUm, req.AngularRad, req.Options, null));
            }
            catch (Exception e) when (e is GeometryKernelException or OperationCanceledException)
            {
                error = e;
            }

            lock (_previewGate)
            {
                _inFlight = null;
                if (req.Superseded)
                {
                    Interlocked.Increment(ref _previewsDiscarded);
                    req.Done.TrySetResult(null);
                }
                else if (error is not null) req.Done.TrySetException(error);
                else req.Done.TrySetResult(result);
            }
        }
    }

    private bool IsSuperseded(PreviewRequest req)
    {
        lock (_previewGate) return req.Superseded;
    }

    // ── the cache, for the Settings row ──────────────────────────────────────────────────────────

    /// <summary>Drops the in-memory cache (the disk cache and the failures stay).</summary>
    public void ClearMemoryCache() => _cache.ClearMemory();

    /// <summary>Deletes the on-disk cache — the Settings row's <i>Clear</i>.</summary>
    public void ClearDiskCache() => _cache.ClearDisk();

    /// <summary>The on-disk cache's size in bytes.</summary>
    public long DiskCacheBytes() => _cache.DiskBytes();

    public void Dispose()
    {
        CancelPreview();
        _model.Dispose();
        _previewSession.Dispose();
    }
}
