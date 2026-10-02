namespace CircuitRF.Design.Revision;

/// <summary>What a designer decided about one unexpectedly large file (§8.2). The choices are about
/// the file's ROLE, never about git mechanics.</summary>
public enum LargeFileChoice
{
    /// <summary>This is design input — the artwork the layout was built from. Kept: one copy now, one
    /// more only when it changes, which §2 measured as cheap.</summary>
    Include,

    /// <summary>Undecided; ask again. Nothing is written and the file stays where it is.</summary>
    LeaveOutThisTime,

    /// <summary>This is a by-product, not a design. A pattern is added to <c>.gitignore</c>, applying
    /// to a file that is <b>not yet kept</b>.</summary>
    NeverIncludeFilesLikeThis,
}

/// <summary>One file the guard is asking about.</summary>
/// <param name="RelativePath">Workspace-relative, forward slashes — what the dialog names.</param>
/// <param name="Bytes">Its size, for the dialog and for the by-size summary of a first recording.</param>
/// <param name="Pattern">The pattern "never include files like this" would add. Extension-shaped
/// where there is an extension, because that is the category a designer means.</param>
public sealed record LargeFile(string RelativePath, long Bytes, string Pattern);

/// <summary>
/// <b>Prevent, do not cure</b> (<c>docs/design/revision-control.md</c> §8, §8.1a, §8.2, §8.2a, §8.2b;
/// R-rc5-15 … R-rc5-17a).
///
/// <para>An RF designer unfamiliar with git will eventually put a very large file into a history —
/// an imported artwork file, a board's whole fabrication set, a results cube — then delete it, and be
/// surprised the history stayed large. That is predictable enough to design against directly, and
/// most of the prevention already shipped as the generated <c>.gitignore</c>.</para>
///
/// <para><b>There is no fourth choice, and "add it once, then ignore it" is not built</b> (R-rc5-17).
/// The reason is mechanical rather than a matter of taste: <c>.gitignore</c> has no effect on a file
/// that is already kept, so the option resolves to one of two implementations and both are worse than
/// either plain choice. Adding the pattern alone changes NOTHING while telling the user they turned
/// something off — §1.4's worst outcome, because the UI said something false about what is kept.
/// Adding the pattern and dropping the file leaves the history holding one stale version, the current
/// state missing it, and a copy taken by anyone else missing it entirely — design-IP loss wearing the
/// costume of a convenience. <b><see cref="Apply"/> refuses to write a pattern for a file that is
/// already kept</b>, which is what makes the absence of that path checkable rather than merely
/// intended.</para>
/// </summary>
public static class LargeFileGuard
{
    /// <summary>
    /// What counts as unexpectedly large, in bytes.
    ///
    /// <para><b>It is a surprise threshold, not a cost threshold.</b> §2 measured the cost of keeping
    /// a file that does not change as nothing per restore point, so the number is not chosen to
    /// protect the disk — it is chosen so that the question is asked about the files a designer would
    /// be surprised to find in a history and is never asked about an ordinary design document. Every
    /// document type circuitRF authors is orders of magnitude under this; an imported artwork file or
    /// a fabrication set is over it.</para>
    /// </summary>
    public static long ThresholdBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// What counts as an unexpectedly large FOLDER: the new files under it that are each below
    /// <see cref="ThresholdBytes"/>, added together.
    ///
    /// <para><b>A per-file threshold alone is defeated by any tool that splits its output.</b> A Palace
    /// solve writes its field data as one ParaView piece per process — 150 pieces of 10.7 MB for the 3D
    /// Connector example, 695 MB together, not one of them over 16 MB — and all of it went into the
    /// history at the next close. The run folders circuitRF knows about are in the generated
    /// <c>.gitignore</c> (<see cref="WorkspacePolicyFiles.RunFolderPatterns"/>); this is the net under
    /// the ones it does not.</para>
    /// </summary>
    public static long FolderThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Every file that would newly enter the history at this boundary and is over the threshold.
    ///
    /// <para><b>"Newly" is measured against the newest restore point's own state</b>, not against
    /// what git happens to have in an index: a file already in the history is not a surprise and asking
    /// about it again would be noise on every boundary forever. <b>Files <c>.gitignore</c> already
    /// excludes are never asked about</b>, which is what keeps the first recording of a four-year-old
    /// workspace short enough to read (§8.1a).</para>
    ///
    /// <para><b>A folder is returned, with a trailing <c>/</c>, when the new files under it that are each
    /// below <see cref="ThresholdBytes"/> add up to <see cref="FolderThresholdBytes"/></b> — the
    /// DEEPEST such folder, so as little as possible is left out, and <b>only a folder holding nothing
    /// already kept</b>: leaving a folder out leaves out everything in it, and a kept file left out of
    /// a restore point is a file that restore point no longer has.</para>
    /// </summary>
    /// <param name="previousTreeId">The newest restore point's state, or null when there is none —
    /// which is the first recording, where every large file is new by definition.</param>
    public static IReadOnlyList<LargeFile> Find(GitCommand git, string? previousTreeId)
    {
        var scan       = Scan(git.WorkspaceRoot);
        var candidates = scan.Large;
        bool heavy     = scan.Root.Children.Any(c => c.RawBytes() >= FolderThresholdBytes);
        if (candidates.Count == 0 && !heavy) return [];

        var already = previousTreeId is { Length: > 0 } ? PathsIn(git, previousTreeId) : [];
        candidates  = [.. candidates.Where(f => !already.Contains(f.RelativePath))];
        var small   = heavy ? scan.Root.SmallFiles().Where(f => !already.Contains(f.Path)).Select(f => f.Path).ToList() : [];
        if (candidates.Count == 0 && small.Count == 0) return [];

        var ignored = Ignored(git, candidates.Select(f => f.RelativePath).Concat(small));
        List<LargeFile> found = [.. candidates.Where(f => !ignored.Contains(f.RelativePath))];

        if (heavy)
            foreach (var child in scan.Root.Children)
                HeavyFolders(child, already, ignored, found);

        found.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return found;
    }

    /// <summary>The new, not-ignored bytes under <paramref name="folder"/> that no folder below it already
    /// accounts for, and whether anything under it is kept; a folder that qualifies is added and
    /// accounts for its own.</summary>
    private static (long Bytes, bool Kept) HeavyFolders(
        Folder folder, HashSet<string> already, HashSet<string> ignored, List<LargeFile> found)
    {
        // A nested repository is left out of every recording already (NestedRepositories).
        if (folder.Nested) return (0, false);

        long bytes = 0;
        bool kept  = false;
        foreach (var (path, length) in folder.Files)
        {
            if (already.Contains(path)) kept = true;
            else if (length < ThresholdBytes && !ignored.Contains(path)) bytes += length;
        }
        foreach (var child in folder.Children)
        {
            var (b, k) = HeavyFolders(child, already, ignored, found);
            bytes += b;
            kept  |= k;
        }

        if (kept || bytes < FolderThresholdBytes) return (bytes, kept);

        // A large file inside it was found on its own; the folder now says it for both.
        string relative = folder.Path + "/";
        found.RemoveAll(f => f.RelativePath.StartsWith(relative, StringComparison.Ordinal));
        found.Add(new LargeFile(relative, bytes, PatternFor(relative)));
        return (0, false);
    }

    /// <summary>
    /// The pathspecs that leave a set of files out of one recording — what
    /// <see cref="GitCheckpoint.Record"/> takes as its extra exclusions.
    /// </summary>
    public static IReadOnlyList<string> ExclusionsFor(IEnumerable<LargeFile> files)
        => [.. files.Select(f => f.RelativePath)];

    /// <summary>
    /// Carries out one decision.
    ///
    /// <para><b><see cref="LargeFileChoice.NeverIncludeFilesLikeThis"/> is refused for a file that is
    /// already kept</b> (R-rc5-17), and the refusal is the whole point: that is the shape the rejected
    /// fourth choice would have had, and a guard that merely intended not to take it would be
    /// indistinguishable from one that did.</para>
    /// </summary>
    /// <param name="alreadyKept">Whether the file is in the history already — <see cref="Find"/>
    /// never returns one that is, so this is the guard against a caller that built the request some
    /// other way.</param>
    /// <returns>True when something was written.</returns>
    public static bool Apply(string workspaceRoot, LargeFile file, LargeFileChoice choice, bool alreadyKept)
    {
        if (choice != LargeFileChoice.NeverIncludeFilesLikeThis) return false;
        if (alreadyKept) return false;

        return AppendIgnorePattern(workspaceRoot, file.Pattern);
    }

    /// <summary>
    /// Appends one pattern to circuitRF's <c>.gitignore</c>, if it is not already there.
    ///
    /// <para><b>Appends; never rewrites</b> (R-rc3-11b). Once written the file belongs to the
    /// workspace: a designer may edit it, and a policy file that is silently regenerated is a policy
    /// file whose user edits vanish — discovered when something they had excluded turns up in a
    /// copy.</para>
    /// </summary>
    public static bool AppendIgnorePattern(string workspaceRoot, string pattern)
    {
        string path = Path.Combine(workspaceRoot, WorkspacePolicyFiles.GitIgnoreName);

        try
        {
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";

            foreach (string line in existing.Split('\n'))
                if (line.Trim() == pattern) return false;

            string prefix = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
            File.WriteAllText(path, existing + prefix + pattern + "\n");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// R-rc5-17a. <b>The first recording into a workspace that already exists summarises by category
    /// and by size rather than naming every file.</b> A guard that names one unexpectedly large file
    /// is right; a workspace with four years of accumulated output has hundreds of them, and a dialog
    /// naming hundreds of files is not a dialog. The same three choices are then offered per PATTERN.
    /// </summary>
    public static IReadOnlyList<LargeFilePattern> SummariseByPattern(IEnumerable<LargeFile> files)
        => [.. files.GroupBy(f => f.Pattern, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new LargeFilePattern(g.Key, g.Count(), g.Sum(f => f.Bytes),
                                                      [.. g.Select(f => f.RelativePath).Order(StringComparer.Ordinal)]))
                    .OrderByDescending(p => p.TotalBytes)];

    // ── The scan ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One folder of the walk: its own files, workspace-relative. Only those below
    /// <see cref="ThresholdBytes"/> count towards it; a larger one is still a file it holds.</summary>
    private sealed class Folder(string path, bool nested)
    {
        public string Path { get; } = path;
        public bool Nested { get; } = nested;
        public List<(string Path, long Length)> Files { get; } = [];
        public List<Folder> Children { get; } = [];

        public long RawBytes() => Nested ? 0 : Small.Sum(f => f.Length) + Children.Sum(c => c.RawBytes());

        public IEnumerable<(string Path, long Length)> SmallFiles()
            => Nested ? [] : Small.Concat(Children.SelectMany(c => c.SmallFiles()));

        private IEnumerable<(string Path, long Length)> Small => Files.Where(f => f.Length < ThresholdBytes);
    }

    private static (List<LargeFile> Large, Folder Root) Scan(string root)
    {
        List<LargeFile> large = [];
        var top = new Folder("", nested: false);
        Walk(Path.GetFullPath(root), Path.GetFullPath(root), large, top, depth: 0);
        large.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return (large, top);
    }

    private static void Walk(string root, string dir, List<LargeFile> found, Folder folder, int depth)
    {
        if (depth > 64) return;

        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                long length;
                try { length = new FileInfo(file).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

                // circuitRF's own policy files are never asked about. The guard's question is "what
                // IS this file", and for these the answer is fixed: they are how the workspace says
                // what it keeps, so leaving one out would remove the answers a designer has already
                // given. Their size never reaches the shipping threshold, so this is a rule about
                // meaning rather than one that fires.
                if (relative is WorkspacePolicyFiles.GitIgnoreName or WorkspacePolicyFiles.GitAttributesName)
                    continue;

                folder.Files.Add((relative, length));
                if (length >= ThresholdBytes) found.Add(new LargeFile(relative, length, PatternFor(relative)));
            }

            foreach (string child in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(child);
                if (name is ".git") continue;
                if (string.Equals(name, WorkspacePolicyFiles.GeneratedCellsFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                string git  = Path.Combine(child, ".git");
                var    sub  = new Folder(Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/'),
                                         folder.Nested || Directory.Exists(git) || File.Exists(git));
                folder.Children.Add(sub);
                Walk(root, child, found, sub, depth + 1);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The pattern "never include files like this" would add — the file's KIND where it has
    /// one, because a kind is the category a designer means by "files like this".</summary>
    /// <remarks>A folder (a trailing <c>/</c>) is a folder pattern: <c>*.ext/</c> where its name has an
    /// extension, otherwise the folder itself, anchored at the workspace root.</remarks>
    public static string PatternFor(string relativePath)
    {
        if (relativePath.EndsWith('/'))
        {
            string folder = relativePath.TrimEnd('/');
            string ext    = Path.GetExtension(folder);
            return ext.Length > 1 ? "*" + ext + "/" : "/" + folder + "/";
        }
        string extension = Path.GetExtension(relativePath);
        return extension.Length > 1 ? "*" + extension : Path.GetFileName(relativePath);
    }

    private static HashSet<string> PathsIn(GitCommand git, string treeId)
    {
        var r = git.Run(["ls-tree", "-r", "--name-only", treeId], new GitRunOptions(ReadOnly: true));
        HashSet<string> paths = new(StringComparer.Ordinal);
        if (!r.Ok) return paths;

        foreach (string line in r.StdOut.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            paths.Add(line.Trim());
        return paths;
    }

    /// <summary>
    /// Which of these <c>.gitignore</c> already excludes. <c>check-ignore</c> exits 1 when nothing
    /// matched, which is an ANSWER and not a failure — so this reads the output rather than the exit
    /// code.
    /// </summary>
    private static HashSet<string> Ignored(GitCommand git, IEnumerable<string> relativePaths)
    {
        HashSet<string> ignored = new(StringComparer.Ordinal);

        string stdin = string.Join('\n', relativePaths) + "\n";
        var r = git.Run(["check-ignore", "--stdin"], new GitRunOptions(StandardInput: stdin, ReadOnly: true));
        if (!r.Started) return ignored;

        foreach (string line in r.StdOut.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            ignored.Add(line.Trim());
        return ignored;
    }
}

/// <summary>One row of a first recording's summary (§8.1a).</summary>
/// <param name="Pattern">What "never include files like this" would add for the whole group.</param>
/// <param name="Count">How many files fall under it.</param>
/// <param name="TotalBytes">Their size together — the figure that makes the summary readable.</param>
/// <param name="Examples">Their paths, so the row can be opened rather than only counted.</param>
public sealed record LargeFilePattern(
    string Pattern, int Count, long TotalBytes, IReadOnlyList<string> Examples);
