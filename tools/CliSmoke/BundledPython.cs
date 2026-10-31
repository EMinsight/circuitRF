// CliSmoke's sixth check, in a file of its own so tests/Ui.Tests can compile the SAME rule it runs
// (PackagingScriptTests) rather than a copy that would agree with itself.
//
// brief-gdf-5 R-gdf5-3 / gdsfactory series D10: no installer, archive or publish tree carries a Python
// environment. circuitRF launches the USER's interpreter and nothing else, which is what keeps
// gdsfactory's GPL layout-engine dependency entirely on the user's side. The rule names no package
// because it does not need to: an installed Python package always leaves one of these three behind.

namespace CliSmoke;

internal static class BundledPython
{
    /// <summary>Every path under <paramref name="root"/> that is a <c>site-packages</c> directory, a
    /// <c>*.dist-info</c> directory or a <c>*.whl</c> file, relative to it. Links are not followed.</summary>
    public static IReadOnlyList<string> Find(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("site-packages", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
                    found.Add(Path.GetRelativePath(root, sub));
                else if (new DirectoryInfo(sub).LinkTarget is null)
                    pending.Push(sub);
            }
            found.AddRange(Directory.EnumerateFiles(dir, "*.whl").Select(f => Path.GetRelativePath(root, f)));
        }
        found.Sort(StringComparer.Ordinal);
        return found;
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
