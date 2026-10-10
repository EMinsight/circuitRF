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

    /// <summary>brief-em3d-69 — a whole model's STEP file: every part built, cut by precedence and written in one request.</summary>
    public TimeSpan WriteStep { get; init; } = TimeSpan.FromSeconds(900);
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
public sealed record GeometryKernelFace(string Name, string Kind, double[] Box, double Area, double MinRadius)
{
    /// <summary>brief-em3d-68 R-em3d68-5c — the face's centroid, µm: half of the fingerprint Reload from Source matches by.</summary>
    public double[] Centroid { get; init; } = [];

    /// <summary>The outward unit normal at the face's point nearest its centroid; zeros where none is defined.</summary>
    public double[] Normal { get; init; } = [];
}

/// <summary>brief-em3d-130 — one boundary loop of a flat face: its vertices, three doubles each in µm, in the wire's order on the
/// face, whether every edge of it is a straight line, and the B-rep's own tolerance on it (µm): how far its vertices may sit
/// from its edges and its face.</summary>
public sealed record GeometryKernelLoop(double[] Points, bool Straight, double ToleranceUm = 0);

/// <summary>brief-em3d-130 — one face's loops, in <see cref="GeometryKernel.Faces"/>' order: whether it is a plane and, when it is,
/// the outer loop first and then each hole. A curved face has none.</summary>
public sealed record GeometryKernelFaceLoops(string Name, bool Plane, IReadOnlyList<GeometryKernelLoop> Loops);

/// <summary>One feature edge: its name (overview §1g), the two faces it separates, curve kind, length, smallest radius
/// (0 for a line) and a polyline for drawing and snapping, three doubles per point.</summary>
public sealed record GeometryKernelEdge(string Name, string FaceA, string FaceB, string Kind, double Length, double MinRadius, double[] Polyline)
{
    /// <summary>brief-em3d-67 — the edge's two ends (6 numbers, the polyline's way), µm; a closed edge's are one point.</summary>
    public double[] Ends { get; init; } = [];

    /// <summary>brief-em3d-67 R-em3d67-4 — the unit tangents at the two ends, along the polyline's direction (6 numbers).</summary>
    public double[] Tangents { get; init; } = [];

    /// <summary>A closed edge — a circle — has one vertex and no midpoint.</summary>
    public bool Closed { get; init; }

    /// <summary>brief-em3d-67 R-em3d67-3e — the point halfway along the curve, from the curve (never the polyline's
    /// middle); null for a closed edge.</summary>
    public double[]? Mid { get; init; }

    /// <summary>A circle's or an arc's centre, µm; null for any other curve.</summary>
    public double[]? Centre { get; init; }

    /// <summary>A circle's or an arc's radius, µm; 0 for any other curve.</summary>
    public double Radius { get; init; }
}

/// <summary>One part of a STEP file as the worker read it: its occurrence <see cref="Path"/> (<c>1/2</c>), product name,
/// colour (RGB 0–1, or null), its solids and its face count. <see cref="Handle"/> is empty: an import holds nothing in the
/// worker.</summary>
public sealed record GeometryKernelImportPart(string Handle, string Name, string Path, double[]? Colour,
                                              IReadOnlyList<GeometryKernelImportSolid> Solids, int Faces, bool Valid)
{
    /// <summary>brief-em3d-68 R-em3d68-4b — a closed solid after healing: what a Step object may be.</summary>
    public bool Closed { get; init; }

    /// <summary>Why it is not a closed solid, when it is not.</summary>
    public string Why { get; init; } = "";

    /// <summary>What shape healing changed, when it ran; empty for a part that was valid as read.</summary>
    public string Healing { get; init; } = "";

    /// <summary>The triangles the viewport would draw it with (R-em3d68-4c); 0 unless asked for.</summary>
    public long Triangles { get; init; }
}

/// <summary>brief-em3d-127 — one solid of a part, as <c>C3dStep.Solid = </c><see cref="Index"/> builds it: its own name when
/// the file gives one, its colour by overview D6 (null with <see cref="Mixed"/> when its faces disagree), its faces,
/// whether it is a closed solid and why not, its volume (µm³) and its box (µm, x0 y0 z0 x1 y1 z1, where the file places it).</summary>
public sealed record GeometryKernelImportSolid(int Index, string Name, double[]? Colour, bool Mixed, int Faces, bool Closed, string Why,
                                               double VolumeUm3, double[] BoxUm)
{
    /// <summary>The triangles the viewport would draw this solid with; 0 unless asked for.</summary>
    public long Triangles { get; init; }
}

/// <summary>What <see cref="GeometryKernel.ImportStep"/> read: the file's length units, its parts, and what healing did.</summary>
public sealed record GeometryKernelImport(IReadOnlyList<string> Units, IReadOnlyList<GeometryKernelImportPart> Parts, IReadOnlyList<string> Healing)
{
    /// <summary>Micrometres per unit, one per entry of <see cref="Units"/>, as the reader resolved it.</summary>
    public IReadOnlyList<double> UnitMicrons { get; init; } = [];

    /// <summary>How many dimensions, tolerances and datums the file carried; none is imported (brief-em3d-68 §10).</summary>
    public int Pmi { get; init; }
}

/// <summary>A preview: the build and its tessellation.</summary>
public sealed record GeometryKernelPreview(GeometryKernelBuild Build, GeometryKernelMesh Mesh);

/// <summary>One shape to export, with the name and colour a STEP file carries.</summary>
public sealed record GeometryKernelExportItem(GeometryKernelTree Tree, string? Name = null, double[]? Colour = null)
{
    /// <summary>In an assembly export, where this component sits: twelve numbers, a 3 × 4 matrix in rows, µm; null is
    /// identity. Two items of one tree are one part instanced twice.</summary>
    public double[]? Location { get; init; }
}

/// <summary>brief-em3d-69 — one part as the worker wrote it: its solid and face counts and volume after the precedence
/// cuts (µm³); <see cref="Empty"/> when the cuts left nothing, and the part was not written.</summary>
public sealed record GeometryKernelWrittenPart(string Name, bool Empty, int Solids, int Faces, double VolumeUm3);

/// <summary>brief-em3d-69 — a <c>write-step</c> reply: the file's bytes and each part's outcome, in request order.</summary>
public sealed record GeometryKernelStepFile(byte[] Data, IReadOnlyList<GeometryKernelWrittenPart> Parts);

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
        int results = hello.Json["results"] is JsonValue rv && rv.TryGetValue(out int rn) ? rn : 1;
        return new GeometryKernelIdentity(protocol, occt, worker, rid, results);
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
        => Faces(_model, tree, options ?? GeometryKernelBuildOptions.Default, control);

    private IReadOnlyList<GeometryKernelFace> Faces(GeometryKernelSession s, GeometryKernelTree tree, GeometryKernelBuildOptions options,
                                                    RunControl? control)
    {
        var (path, id) = Require($"Reading the faces of '{tree.Object}'");
        string key = Key("faces", HandleOf(tree, options), "", id);
        if (_cache.TryMemory(key, out IReadOnlyList<GeometryKernelFace> hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        var reply = Guard(key, () =>
        {
            string handle = EnsureHeld(s, path, id, tree, options, control);
            var r = s.Send(path, new GeometryKernelMessage(new JsonObject { ["op"] = "faces", ["shape"] = handle }),
                                Deadlines.Other, control, "reading the faces of", tree.Object);
            return r.Ok ? r : throw Refused(r, tree.Object);
        });
        List<GeometryKernelFace> faces = [];
        foreach (var f in reply.Json["faces"] as JsonArray ?? [])
        {
            if (f is null) continue;
            faces.Add(new GeometryKernelFace(f["name"]?.GetValue<string>() ?? "", f["kind"]?.GetValue<string>() ?? "other",
                [.. (f["box"] as JsonArray ?? []).Select(x => x?.GetValue<double>() ?? 0)],
                f["area"]?.GetValue<double>() ?? 0, f["min_radius"]?.GetValue<double>() ?? 0)
            {
                Centroid = Numbers(f["centroid"]) ?? [], Normal = Numbers(f["normal"]) ?? [],
            });
        }
        _cache.PutMemory(key, faces, faces.Count * 256L + 256);
        return faces;
    }

    /// <summary>brief-em3d-130 — each face's boundary loops (<see cref="GeometryKernelFaceLoops"/>), in the face table's order.</summary>
    public IReadOnlyList<GeometryKernelFaceLoops> Loops(GeometryKernelTree tree, GeometryKernelBuildOptions? options = null, RunControl? control = null)
    {
        var o = options ?? GeometryKernelBuildOptions.Default;
        var (path, id) = Require($"Reading the faces of '{tree.Object}'");
        string key = Key("loops", HandleOf(tree, o), "", id);
        if (_cache.TryMemory(key, out IReadOnlyList<GeometryKernelFaceLoops> hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        var reply = Guard(key, () =>
        {
            string handle = EnsureHeld(_model, path, id, tree, o, control);
            var r = _model.Send(path, new GeometryKernelMessage(new JsonObject { ["op"] = "loops", ["shape"] = handle }),
                                Deadlines.Other, control, "reading the faces of", tree.Object);
            return r.Ok ? r : throw Refused(r, tree.Object);
        });
        List<GeometryKernelFaceLoops> faces = [];
        long points = 0;
        foreach (var f in reply.Json["faces"] as JsonArray ?? [])
        {
            if (f is null) continue;
            var loops = (f["loops"] as JsonArray ?? []).Where(l => l is not null)
                .Select(l => new GeometryKernelLoop(Numbers(l!["points"]) ?? [], l!["straight"]?.GetValue<bool>() ?? false,
                                                    l!["tolerance"]?.GetValue<double>() ?? 0)).ToList();
            points += loops.Sum(l => l.Points.Length);
            faces.Add(new GeometryKernelFaceLoops(f["name"]?.GetValue<string>() ?? "", f["plane"]?.GetValue<bool>() ?? false, loops));
        }
        _cache.PutMemory(key, faces, points * 8L + faces.Count * 128L + 256);
        return faces;
    }

    /// <summary>The feature edges of <paramref name="tree"/>'s shape, each with a polyline at <paramref name="deflectionUm"/>.</summary>
    public IReadOnlyList<GeometryKernelEdge> Edges(GeometryKernelTree tree, double deflectionUm, GeometryKernelBuildOptions? options = null,
                                                   RunControl? control = null)
        => Edges(_model, tree, deflectionUm, options ?? GeometryKernelBuildOptions.Default, control);

    private IReadOnlyList<GeometryKernelEdge> Edges(GeometryKernelSession s, GeometryKernelTree tree, double deflectionUm,
                                                    GeometryKernelBuildOptions options, RunControl? control)
    {
        var (path, id) = Require($"Reading the edges of '{tree.Object}'");
        string key = Key("edges", HandleOf(tree, options), deflectionUm.ToString("R", CultureInfo.InvariantCulture), id);
        if (_cache.TryMemory(key, out IReadOnlyList<GeometryKernelEdge> hit))
        {
            Interlocked.Increment(ref _memoryHits);
            return hit;
        }
        var reply = Guard(key, () =>
        {
            string handle = EnsureHeld(s, path, id, tree, options, control);
            var r = s.Send(path, new GeometryKernelMessage(new JsonObject { ["op"] = "edges", ["shape"] = handle, ["deflection_um"] = deflectionUm }),
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
                poly.AsSpan(3 * at, 3 * n).ToArray())
            {
                Ends = Numbers(e["ends"]) ?? [], Tangents = Numbers(e["tangents"]) ?? [], Closed = e["closed"]?.GetValue<bool>() ?? false,
                Mid = Numbers(e["mid"]), Centre = Numbers(e["centre"]), Radius = e["radius"]?.GetValue<double>() ?? 0,
            });
            at += n;
        }
        _cache.PutMemory(key, edges, poly.Length * 8L + edges.Count * 256L + 256);
        return edges;
    }

    /// <summary>
    /// Writes <paramref name="items"/> as <paramref name="format"/> — <c>brep</c>, <c>step</c>, <c>ply</c> or <c>stl</c> — in
    /// <paramref name="units"/> (<c>um</c>, <c>mm</c>, <c>mil</c>, <c>in</c>, <c>m</c>). Not cached: a STEP file carries a timestamp.
    /// </summary>
    /// <param name="assembly">STEP only: one assembly whose components are the items, each at its <see cref="GeometryKernelExportItem.Location"/>.</param>
    public byte[] Export(IReadOnlyList<GeometryKernelExportItem> items, string format, string units = "um",
                         double linearUm = 1, double angularRad = 0.5, string? schema = null, RunControl? control = null, bool assembly = false)
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
        if (assembly)
        {
            request["assembly"] = true;
            request["locations"] = new JsonArray([.. items.Select(i => i.Location is { Length: 12 } m ? new JsonArray([.. m.Select(v => JsonValue.Create(v))]) : null)]);
        }
        var reply = _model.Send(path, new GeometryKernelMessage(request), Deadlines.Other, control, "exporting", what.Trim('\''));
        if (!reply.Ok) throw Refused(reply, what.Trim('\''));
        return reply.Blob("data")?.Data ?? [];
    }

    /// <summary>
    /// brief-em3d-69 — writes one STEP file from <paramref name="request"/> (the <c>write-step</c> request StepExport composes:
    /// parts, cuts, assemblies, header) and the B-rep <paramref name="blobs"/> it names. Holds nothing and is never cached: a
    /// STEP file carries a time-stamp. The kernel only carries the request; what is in the file is StepExport's decision.
    /// </summary>
    /// <exception cref="GeometryKernelException">Refused, crashed, timed out, or no kernel; the message is the sentence.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="control"/>'s token cancelled; the worker was killed.</exception>
    public GeometryKernelStepFile WriteStep(JsonObject request, IReadOnlyList<GeometryKernelBlob> blobs, RunControl? control = null)
    {
        var (path, _) = Require("Export STEP");
        request["op"] = "write-step";
        var reply = _model.Send(path, new GeometryKernelMessage(request, blobs), Deadlines.WriteStep, control, "exporting", "the STEP file");
        if (!reply.Ok) throw Refused(reply, "the STEP file");
        var parts = new List<GeometryKernelWrittenPart>();
        foreach (var p in reply.Json["parts"] as JsonArray ?? [])
            if (p is not null)
                parts.Add(new GeometryKernelWrittenPart(p["name"]?.GetValue<string>() ?? "", p["empty"]?.GetValue<bool>() ?? false,
                    p["solids"]?.GetValue<int>() ?? 0, p["faces"]?.GetValue<int>() ?? 0, p["volume_um3"]?.GetValue<double>() ?? 0));
        return new GeometryKernelStepFile(reply.Blob("data")?.Data ?? [], parts);
    }

    /// <summary>
    /// Reads a STEP file — its parts with names, colours, counts and whether each is a closed solid, its length units and
    /// the healing report. Holds nothing in the worker: what becomes an object is built later, from the copied file, by
    /// the ordinary build path. <paramref name="displayRelative"/> &gt; 0 also counts each part's display triangles at that
    /// fraction of its diagonal (the elaborator's display deflection). Never cached: the bytes are the question.
    /// </summary>
    /// <exception cref="GeometryKernelException">Refused (not STEP, or a unit it cannot resolve), crashed, or no kernel.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="control"/>'s token cancelled; the worker was killed.</exception>
    public GeometryKernelImport ImportStep(byte[] file, RunControl? control = null, double displayRelative = 0)
    {
        var (path, _) = Require("Importing a STEP file");
        string prefix = "step:" + Convert.ToHexStringLower(SHA256.HashData(file));
        var request = new JsonObject { ["op"] = "import-step", ["shape"] = prefix, ["hold"] = false };
        if (displayRelative > 0) request["display_rel"] = displayRelative;
        var reply = _model.Send(path, new GeometryKernelMessage(request, [GeometryKernelBlob.OfBytes("file", file)]),
                                Deadlines.ImportStep, control, "importing", "the STEP file");
        if (!reply.Ok) throw Refused(reply, "the STEP file");
        var parts = new List<GeometryKernelImportPart>();
        foreach (var p in reply.Json["parts"] as JsonArray ?? [])
        {
            if (p is null) continue;
            // Rounded to 12 places: OCCT holds a colour LINEAR and the worker re-encodes it to the file's sRGB, a round trip
            // that turns an exact 1 into 0.99999999999999989 — noise no colour carries, and a #rrggbb match must not see.
            static double[]? Colour(JsonNode? n) => n is JsonArray c ? [.. c.Select(x => Math.Round(x?.GetValue<double>() ?? 0, 12))] : null;
            var solids = new List<GeometryKernelImportSolid>();
            foreach (var s in p["solids"] as JsonArray ?? [])
                if (s is not null)
                    solids.Add(new GeometryKernelImportSolid(solids.Count + 1, s["name"]?.GetValue<string>() ?? "", Colour(s["colour"]),
                        s["mixed"]?.GetValue<bool>() ?? false, s["faces"]?.GetValue<int>() ?? 0, s["closed"]?.GetValue<bool>() ?? false,
                        s["why"]?.GetValue<string>() ?? "", s["volume_um3"]?.GetValue<double>() ?? 0, Numbers(s["box_um"]) ?? [])
                    { Triangles = s["triangles"]?.GetValue<long>() ?? 0 });
            parts.Add(new GeometryKernelImportPart(p["shape"]?.GetValue<string>() ?? "", p["name"]?.GetValue<string>() ?? "",
                p["path"]?.GetValue<string>() ?? "", Colour(p["colour"]), solids, p["faces"]?.GetValue<int>() ?? 0,
                p["valid"]?.GetValue<bool>() ?? false)
            {
                Closed = p["closed"]?.GetValue<bool>() ?? false, Why = p["why"]?.GetValue<string>() ?? "",
                Healing = p["healing"]?.GetValue<string>() ?? "", Triangles = p["triangles"]?.GetValue<long>() ?? 0,
            });
        }
        return new GeometryKernelImport(Strings(reply, "units"), parts, Strings(reply, "healing"))
        {
            UnitMicrons = Numbers(reply.Json["unit_um"]) ?? [], Pmi = Int(reply, "pmi"),
        };
    }

    private static double[]? Numbers(JsonNode? n) => n is JsonArray a ? [.. a.Select(x => x?.GetValue<double>() ?? 0)] : null;

    private static List<string> Strings(GeometryKernelMessage m, string key) =>
        m.Json[key] is JsonArray a ? [.. a.Select(x => x?.GetValue<string>() ?? "")] : [];

    // ── refusals, in circuitRF's voice (R-em3d63-4d) ─────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, Func<string, string>> RefusalSentences = new Dictionary<string, Func<string, string>>
    {
        ["tree.invalid"] = o => $"'{o}' is not a solid the geometry kernel can build",
        ["build.failed"] = o => $"The geometry kernel could not build '{o}'",
        ["build.empty"] = o => $"'{o}' is empty",
        ["edge.missing"] = o => $"The geometry kernel could not find an edge of '{o}'",
        ["build.invalid-result"] = o => $"The geometry kernel built '{o}', but the result is not a valid solid",
        ["shape.unknown"] = o => $"The geometry kernel no longer held '{o}'",
        ["export.failed"] = o => $"The geometry kernel could not export {o}",
        ["import.failed"] = _ => "The geometry kernel could not read the STEP file",
        ["import.units"] = _ => "The STEP file's length unit is not one circuitRF can import exactly",
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
        string sentence = OlderWorker(code, detail) is { } older ? older
            : RefusalSentences.TryGetValue(code, out var words)
            ? $"{words(o)}: {detail}"
            : $"The geometry kernel could not build '{o}': {detail} (an unrecognised refusal, '{code}')";
        return new GeometryKernelException(GeometryKernelFailure.Refused, sentence, code, o)
        {
            // brief-em3d-67 R-em3d67-5d — which edges a fillet or chamfer failed on, for the caller to say in the user's terms.
            Edges = Strings(reply, "edges"), Corner = reply.Json["corner"]?.GetValue<bool>() ?? false,
            WidthUm = reply.Json["width_um"]?.GetValue<double>(), Missing = Strings(reply, "missing"),
        };
    }

    /// <summary>brief-em3d-102 R-em3d102-3c — the kinds a worker built before them refuses by name, and what it then says.</summary>
    private static readonly IReadOnlyDictionary<string, string> NewerKinds = new Dictionary<string, string>
    {
        ["sphere"] = "spheres",
    };

    /// <summary>A worker older than a kind it was handed (<c>this worker cannot build a "sphere"</c>): what to do about it, rather
    /// than the generic failure. Null for any other refusal.</summary>
    internal static string? OlderWorker(string code, string detail)
    {
        const string Prefix = "this worker cannot build a \"";
        if (code != "tree.invalid" || !detail.StartsWith(Prefix, StringComparison.Ordinal) || !detail.EndsWith('"')) return null;
        return NewerKinds.TryGetValue(detail[Prefix.Length..^1], out var kinds)
            ? $"This geometry worker predates {kinds}; rebuild it (tools/geometry-worker/build.sh)."
            : null;
    }

    // ── previews (R-em3d63-8) ────────────────────────────────────────────────────────────────────

    private sealed class PreviewRequest(Func<GeometryKernelShapes, Func<bool>, object?> work)
    {
        public Func<GeometryKernelShapes, Func<bool>, object?> Work { get; } = work;
        public TaskCompletionSource<object?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        options ??= GeometryKernelBuildOptions.Default;
        return RequestPreview((shapes, superseded) =>
        {
            var build = shapes.Build(tree, options);
            return superseded() ? null : new GeometryKernelPreview(build, shapes.Tessellate(tree, linearUm, angularRad, options));
        });
    }

    /// <summary>
    /// brief-em3d-66 R-em3d66-2e — a preview that asks for whatever <paramref name="work"/> asks for, on the PREVIEW session,
    /// with <see cref="RequestPreview(GeometryKernelTree, double, double, GeometryKernelBuildOptions?)"/>'s supersession. Every
    /// answer is cached under the key the model session reads, so a commit asking the same questions of the same tree is
    /// answered from memory. <paramref name="work"/>'s second argument says whether a newer request has superseded this one.
    /// </summary>
    public async Task<T?> RequestPreview<T>(Func<GeometryKernelShapes, Func<bool>, T?> work) where T : class
    {
        var req = new PreviewRequest((shapes, superseded) => work(shapes, superseded));
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
        return (T?)await req.Done.Task.ConfigureAwait(false);
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
        var shapes = new GeometryKernelShapes(this, _previewSession);
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

            object? result = null;
            Exception? error = null;
            try
            {
                result = req.Work(shapes, () => IsSuperseded(req));
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

    /// <summary>The model session's questions, for code written once against either session.</summary>
    public GeometryKernelShapes Model => new(this, _model);

    // The session-bound halves GeometryKernelShapes forwards to.
    internal GeometryKernelBuild BuildOn(GeometryKernelSession s, GeometryKernelTree tree, GeometryKernelBuildOptions o) => Build(s, tree, o, null);
    internal GeometryKernelMesh TessellateOn(GeometryKernelSession s, GeometryKernelTree tree, double linearUm, double angularRad, GeometryKernelBuildOptions o)
        => Tessellate(s, tree, linearUm, angularRad, o, null);
    internal IReadOnlyList<GeometryKernelFace> FacesOn(GeometryKernelSession s, GeometryKernelTree tree, GeometryKernelBuildOptions o) => Faces(s, tree, o, null);
    internal IReadOnlyList<GeometryKernelEdge> EdgesOn(GeometryKernelSession s, GeometryKernelTree tree, double deflectionUm, GeometryKernelBuildOptions o)
        => Edges(s, tree, deflectionUm, o, null);

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

/// <summary>
/// brief-em3d-66 — the four questions a shape is asked (build, faces, tessellation, edges), bound to ONE of the kernel's
/// sessions: <see cref="GeometryKernel.Model"/>, or the preview session inside
/// <c>GeometryKernel.RequestPreview/// <see cref="GeometryKernel.RequestPreview{T}(Func{GeometryKernelShapes, Func{bool}, T})"/>. Both sessions share one cache, keyedlt;T/// <see cref="GeometryKernel.RequestPreview{T}(Func{GeometryKernelShapes, Func{bool}, T})"/>. Both sessions share one cache, keyedgt;</c>. Both sessions share one cache, keyed
/// by the question and the tree, so an answer the preview received is the model's answer too.
/// </summary>
public sealed class GeometryKernelShapes
{
    private readonly GeometryKernel _kernel;
    private readonly GeometryKernelSession _session;

    internal GeometryKernelShapes(GeometryKernel kernel, GeometryKernelSession session)
    {
        _kernel = kernel;
        _session = session;
    }

    public GeometryKernelBuild Build(GeometryKernelTree tree, GeometryKernelBuildOptions? options = null)
        => _kernel.BuildOn(_session, tree, options ?? GeometryKernelBuildOptions.Default);

    public GeometryKernelMesh Tessellate(GeometryKernelTree tree, double linearUm, double angularRad = 0.5, GeometryKernelBuildOptions? options = null)
        => _kernel.TessellateOn(_session, tree, linearUm, angularRad, options ?? GeometryKernelBuildOptions.Default);

    public IReadOnlyList<GeometryKernelFace> Faces(GeometryKernelTree tree, GeometryKernelBuildOptions? options = null)
        => _kernel.FacesOn(_session, tree, options ?? GeometryKernelBuildOptions.Default);

    public IReadOnlyList<GeometryKernelEdge> Edges(GeometryKernelTree tree, double deflectionUm, GeometryKernelBuildOptions? options = null)
        => _kernel.EdgesOn(_session, tree, deflectionUm, options ?? GeometryKernelBuildOptions.Default);
}
