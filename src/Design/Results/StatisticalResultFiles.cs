using CircuitRF.Design.Statistics;

namespace CircuitRF.Design.Results;

/// <summary>
/// The result files a Monte Carlo, yield, corner or design-of-experiments run writes — BESIDE its design
/// (<c>&lt;design&gt;.yield.npy</c>, <c>.corners.npy</c>, <c>.doe.npy</c>), not under the workspace's flat
/// <c>results/</c> folder, which Simulate owns. A Data Display lists <c>results/</c> by scanning it; these need
/// finding, or the only display that can ever show one is the one the Yield panel opens for it.
/// </summary>
public static class StatisticalResultFiles
{
    /// <summary>The name endings, each taken from its run's own result-path rule so the two cannot drift.</summary>
    public static IReadOnlyList<string> Suffixes { get; } =
    [
        Suffix(StatisticalRun.ResultPathFor("d.csch")),
        Suffix(CornerRun.ResultPathFor("d.csch")),
        Suffix(DoeRun.ResultPathFor("d.csch")),
    ];

    /// <summary>Whether <paramref name="path"/> is named as one of these results.</summary>
    public static bool IsStatisticalResult(string path)
        => Suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every such result in a schematic view folder of the workspace at <paramref name="workspaceDir"/>, newest first.
    /// Only <c>schematic/</c> folders are opened — that is where a schematic's run writes — and the walk skips hidden
    /// folders (the PCell cache, a session's bookkeeping) and links, and stops <paramref name="maxDepth"/> levels down, so
    /// a workspace holding large 3D run folders costs a directory listing, never a file scan.
    /// </summary>
    public static IReadOnlyList<string> In(string workspaceDir, int maxDepth = 8)
    {
        if (!Directory.Exists(workspaceDir)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth     = maxDepth,
            IgnoreInaccessible    = true,
            AttributesToSkip      = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var found = new List<(string Path, DateTime Written)>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(workspaceDir, "schematic", options))
            foreach (string npy in Directory.EnumerateFiles(dir, "*.npy"))
                if (IsStatisticalResult(npy))
                    found.Add((npy, File.GetLastWriteTimeUtc(npy)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return [.. found.OrderByDescending(f => f.Written).Select(f => f.Path)];
    }

    private static string Suffix(string resultPath)
    {
        string name = Path.GetFileName(resultPath);
        return name[name.IndexOf('.')..];
    }
}
