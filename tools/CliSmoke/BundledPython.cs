// CliSmoke's sixth check, in a file of its own so tests/Ui.Tests can compile the SAME rule it runs
// (PackagingScriptTests) rather than a copy that would agree with itself.
//
// brief-gdf-5 R-gdf5-3 / gdsfactory series D10: no installer, archive or publish tree carries a Python
// environment. circuitRF launches the USER's interpreter and nothing else, which is what keeps
// gdsfactory's GPL layout-engine dependency entirely on the user's side. The rule names no package
// because it does not need to: installing or bundling Python leaves one of the things Find lists behind.

namespace CliSmoke;

internal static class BundledPython
{
    /// <summary>
    /// Every path under <paramref name="root"/> that a Python environment or an installed package leaves behind,
    /// relative to it. Links are not followed. Named by what installing or bundling DOES, never by a package:
    /// <list type="bullet">
    /// <item>an installation's directories — <c>site-packages</c>, <c>dist-packages</c> (a Debian interpreter's),
    /// <c>*.dist-info</c>, <c>*.egg-info</c>, <c>*.egg</c> — and its files: <c>*.whl</c>, <c>*.egg</c>, <c>*.pth</c>;</item>
    /// <item>a package copied in WITHOUT its metadata, which leaves none of those: its compiled extension modules
    /// (<c>*.pyd</c>, <c>*.cpython-*.so</c>, <c>*.abi3.so</c>), which any package with native parts carries;</item>
    /// <item>an interpreter itself — <c>python3*.dll</c>, <c>libpython3*</c>, a <c>python3*.zip</c> standard
    /// library, a <c>Python.framework</c>.</item>
    /// </list>
    /// A pure-Python package copied in with no metadata at all leaves nothing a rule naming no package can tell from
    /// circuitRF's own <c>pcell-python</c> sources; that case is held by review, not by this scan.
    /// </summary>
    public static IReadOnlyList<string> Find(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (IsEnvironmentDirectory(Path.GetFileName(sub)))
                    found.Add(Path.GetRelativePath(root, sub));
                else if (new DirectoryInfo(sub).LinkTarget is null)
                    pending.Push(sub);
            }
            found.AddRange(Directory.EnumerateFiles(dir).Where(f => IsEnvironmentFile(Path.GetFileName(f)))
                                    .Select(f => Path.GetRelativePath(root, f)));
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool IsEnvironmentDirectory(string name) =>
        name.Equals("site-packages", StringComparison.OrdinalIgnoreCase)
        || name.Equals("dist-packages", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Python.framework", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".egg", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnvironmentFile(string name)
    {
        string n = name.ToLowerInvariant();
        return n.EndsWith(".whl", StringComparison.Ordinal) || n.EndsWith(".egg", StringComparison.Ordinal)
            || n.EndsWith(".pth", StringComparison.Ordinal) || n.EndsWith(".pyd", StringComparison.Ordinal)
            || n.EndsWith(".abi3.so", StringComparison.Ordinal)
            || (n.Contains(".cpython-", StringComparison.Ordinal) && n.EndsWith(".so", StringComparison.Ordinal))
            || n.StartsWith("libpython3", StringComparison.Ordinal)
            || (n.StartsWith("python3", StringComparison.Ordinal) && (n.EndsWith(".dll", StringComparison.Ordinal)
                                                                     || n.EndsWith(".zip", StringComparison.Ordinal)));
    }

    /// <summary>The tree that ships around <paramref name="exe"/>: the whole <c>.app</c> for
    /// <c>…/X.app/Contents/MacOS/x</c>, otherwise the executable's own directory (a Linux publish
    /// tree, or on Windows the launcher stub's directory holding the version directory it runs).</summary>
    public static string TreeOf(string exe)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var contents = Directory.GetParent(dir);
        return Path.GetFileName(dir) == "MacOS" && contents is { Name: "Contents" }
               && contents.Parent is { } app && app.Name.EndsWith(".app", StringComparison.Ordinal)
            ? app.FullName
            : dir;
    }
}
