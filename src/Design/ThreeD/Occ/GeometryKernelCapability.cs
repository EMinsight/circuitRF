// brief-em3d-63 R-em3d63-1, -2, -3 — where the geometry worker is, whether it is the one this build was made
// with, and the ONE place its absence is worded.

using System.Runtime.InteropServices;

namespace CircuitRF.Design.ThreeD.Occ;

/// <summary>Why the geometry kernel is not available (R-em3d63-3a).</summary>
public enum GeometryKernelAbsence
{
    /// <summary>Running from a source tree whose worker has not been built.</summary>
    NotBuilt,
    /// <summary>This RID's installers do not ship the kernel (brief 62 D2).</summary>
    NotShippedOnThisPlatform,
    /// <summary>A publish tree without its <c>geometry-kernel/</c> folder, or with the worker gone.</summary>
    Missing,
    /// <summary>It is there and did not start: the handshake failed, it crashed at start, or it missed the deadline.</summary>
    Broken,
    /// <summary>It answered as a different protocol, OCCT or architecture from the one this build pins.</summary>
    WrongVersion,
}

/// <summary>How the worker was found (R-em3d63-1a's rows).</summary>
public enum GeometryKernelRoute
{
    /// <summary>Row 1: <c>CIRCUITRF_GEOMETRY_WORKER</c>.</summary>
    EnvironmentVariable,
    /// <summary>Row 2, the shipped route: <c>&lt;base&gt;/geometry-kernel/</c>.</summary>
    BesideTheApplication,
    /// <summary>Row 3: the source tree's own build of it.</summary>
    SourceTree,
}

/// <summary>
/// Is the geometry kernel here (R-em3d63-3a)? Available — or absent with a <see cref="Reason"/> (what is wrong)
/// and an <see cref="Action"/> (what fixes it), which every disabled command, refusal and report reads through
/// <see cref="GeometryKernel.NeedsKernel"/>.
/// </summary>
public sealed record GeometryKernelCapability(
    bool Available, GeometryKernelAbsence? Absence,
    string? WorkerPath, string? OcctVersion, string HowFound,
    string Reason,
    string Action)
{
    /// <summary>The worker's own version (circuitRF's), from its handshake; null when it never answered.</summary>
    public string? WorkerVersion { get; init; }

    /// <summary>The route that found it, when anything was found.</summary>
    public GeometryKernelRoute? Route { get; init; }
}

/// <summary>What a handshake established: the kernel's identity, which every cache key carries (R-em3d63-6a).</summary>
public sealed record GeometryKernelIdentity(int Protocol, string Occt, string Worker, string Rid)
{
    public string Key => $"protocol {Protocol}; occt {Occt}; worker {Worker}; rid {Rid}";
}

/// <summary>Where <see cref="GeometryKernelLocator.Locate"/> looked and what it found: a worker to start, or an absence.</summary>
public sealed record GeometryKernelLocation(
    string? WorkerPath, GeometryKernelRoute? Route, string HowFound,
    GeometryKernelAbsence? Absence, string Reason, string Action)
{
    public bool Found => WorkerPath is not null;
}

/// <summary>brief 62's recipe, read out of this assembly (R-em3d63-2b): the OCCT version pinned, and the RIDs that ship it.</summary>
public static class GeometryKernelRecipe
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Values = new(Load);

    /// <summary>The OCCT version every worker must report, exactly — not a range.</summary>
    public static string OcctVersion => Values.Value["OCCT_VERSION"];

    /// <summary>brief 62 D2: the RIDs whose installers ship the kernel.</summary>
    public static IReadOnlyList<string> ShippedRids =>
        [.. Values.Value["KERNEL_RIDS"].Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Parses the recipe's <c>KEY=value</c> lines, as the build scripts do: comments and blank lines skipped,
    /// nothing unquoted or expanded.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq]] = line[(eq + 1)..];
        }
        return d;
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var s = typeof(GeometryKernelRecipe).Assembly.GetManifestResourceStream("CircuitRF.Design.ThreeD.Occ.recipe.env")
            ?? throw new InvalidOperationException("The geometry kernel's recipe is not embedded in CircuitRF.Design (its csproj names tools/geometry-worker/occt/recipe.env).");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}

/// <summary>
/// R-em3d63-1 — discovery. Walks, in order, and stops at the first CANDIDATE, not the first that works:
/// <list type="number">
/// <item><c>CIRCUITRF_GEOMETRY_WORKER</c>, when set — and then it is the ONLY candidate;</item>
/// <item><c>&lt;base&gt;/geometry-kernel/geometry-worker[.exe]</c> — the shipped route;</item>
/// <item><c>tools/geometry-worker/build/&lt;rid&gt;/geometry-kernel/geometry-worker[.exe]</c> of the source tree
/// holding <c>circuitRF.slnx</c> above the base directory — consulted ONLY when row 2's folder is absent.</item>
/// </list>
/// <para><b>A named worker that does not work is reported, never replaced</b> (R-em3d63-1b): a publish tree whose
/// folder exists is Missing or Broken on its own terms, and never a cue to go looking for a source tree — the
/// installed app must never depend on the walk-up, the trap PCell artwork fell into.</para>
/// </summary>
public static class GeometryKernelLocator
{
    /// <summary>The folder a build or an installer puts the worker and its libraries in, beside the assemblies.</summary>
    public const string Folder = "geometry-kernel";

    public static string WorkerFileName(bool windows) => windows ? "geometry-worker.exe" : "geometry-worker";

    /// <summary>This process's RID, from its own architecture — what the worker's must match.</summary>
    public static string ProcessRid
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                var a => a.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>Discovery for this process.</summary>
    public static GeometryKernelLocation Locate() => Locate(
        Environment.GetEnvironmentVariable(GeometryKernel.EnvironmentVariable), AppContext.BaseDirectory,
        ProcessRid, GeometryKernelRecipe.ShippedRids, OperatingSystem.IsWindows());

    /// <summary>Discovery over explicit inputs (the tests' fake file trees).</summary>
    public static GeometryKernelLocation Locate(string? environmentValue, string baseDirectory, string rid,
                                                IReadOnlyList<string> shippedRids, bool windows)
    {
        string exe = WorkerFileName(windows);

        // Row 1 — the only candidate when set.
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            string path = Path.GetFullPath(environmentValue.Trim());
            string how = $"from {GeometryKernel.EnvironmentVariable}";
            return File.Exists(path)
                ? new(path, GeometryKernelRoute.EnvironmentVariable, how, null, "", "")
                : new(null, null, how, GeometryKernelAbsence.Missing,
                      $"{GeometryKernel.EnvironmentVariable} names {path}, which does not exist.",
                      GeometryKernel.ActionFor(GeometryKernelAbsence.Missing, GeometryKernelRoute.EnvironmentVariable));
        }

        bool shipped = shippedRids.Contains(rid, StringComparer.Ordinal);
        string folder = Path.Combine(baseDirectory, Folder);

        // Row 2 — the shipped route, and never looked at on a RID that does not ship it (R-em3d63-1d).
        if (shipped && Directory.Exists(folder))
        {
            string path = Path.Combine(folder, exe);
            return File.Exists(path)
                ? new(path, GeometryKernelRoute.BesideTheApplication, "included with circuitRF", null, "", "")
                : new(null, null, "included with circuitRF", GeometryKernelAbsence.Missing,
                      $"The geometry kernel's folder {folder} has no {exe} in it.",
                      GeometryKernel.ActionFor(GeometryKernelAbsence.Missing, GeometryKernelRoute.BesideTheApplication));
        }

        // Row 3 — a source tree's own build, only when row 2's folder is absent.
        if (SourceTreeRoot(baseDirectory) is { } root)
        {
            string built = Path.Combine(root, "tools", "geometry-worker", "build", rid, Folder, exe);
            if (File.Exists(built))
                return new(built, GeometryKernelRoute.SourceTree, "built in this source tree", null, "", "");
            if (!shipped)
                return NotShipped(rid);
            return new(null, null, "built in this source tree", GeometryKernelAbsence.NotBuilt,
                       $"The geometry kernel is not built in this source tree (looked for {built}).",
                       GeometryKernel.ActionFor(GeometryKernelAbsence.NotBuilt, GeometryKernelRoute.SourceTree));
        }

        return shipped
            ? new(null, null, "included with circuitRF", GeometryKernelAbsence.Missing,
                  $"The geometry kernel was not found at {folder}.",
                  GeometryKernel.ActionFor(GeometryKernelAbsence.Missing, GeometryKernelRoute.BesideTheApplication))
            : NotShipped(rid);
    }

    private static GeometryKernelLocation NotShipped(string rid) =>
        new(null, null, "", GeometryKernelAbsence.NotShippedOnThisPlatform,
            $"circuitRF for {rid} does not include the geometry kernel.",
            GeometryKernel.ActionFor(GeometryKernelAbsence.NotShippedOnThisPlatform, null));

    /// <summary>The directory holding <c>circuitRF.slnx</c> at or above <paramref name="baseDirectory"/>, or null.</summary>
    public static string? SourceTreeRoot(string baseDirectory)
    {
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) return dir.FullName;
        return null;
    }
}
