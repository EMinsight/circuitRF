using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>What a clone produced.</summary>
/// <param name="Ok">Whether git brought the repository down.</param>
/// <param name="Destination">The folder that was created, whether or not it holds a workspace.</param>
/// <param name="WorkspaceCwsPath">
/// The <c>.cws</c> at the destination's root, or null when the cloned repository is not a workspace —
/// which is not a failure, merely a folder circuitRF has nothing to open (R-rc9-1).
/// </param>
/// <param name="Diagnostics">Everything worth saying, in order. Empty on a plain success.</param>
public sealed record CloneResult(
    bool Ok,
    string Destination,
    string? WorkspaceCwsPath,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>True when the destination holds a workspace circuitRF can open.</summary>
    public bool IsWorkspace => WorkspaceCwsPath is not null;
}

/// <summary>How far a clone has got, as git reports it.</summary>
/// <param name="Phase">Git's own name for what it is doing — "Receiving objects", "Updating files".</param>
/// <param name="PhasePercent">How far through that phase, 0-100, or null when git gave no figure.</param>
/// <param name="OverallPercent">
/// How far through the whole clone, 0-100, or null while nothing countable is happening (the remote
/// counting and compressing on its side). <b>The phases are weighted, not equal</b>: receiving is the
/// transfer and nearly all the wait, so it fills most of the bar, and a bar that ran from empty to
/// full three times would read as a clone that kept starting over.
/// </param>
/// <param name="Detail">The rest of git's line — the counts, the size received and the rate.</param>
public sealed record CloneProgress(string Phase, double? PhasePercent, double? OverallPercent, string Detail);

/// <summary>
/// RC-9 §1: <b>cloning a repository as a workspace</b> (<c>revision-control.md</c> §9, R-rc9-1 …
/// R-rc9-5c).
///
/// <para><b>It is nearly free and it is the strongest product argument in the architecture</b>: a
/// public library of reference designs, opened in one action, from any git host — with fetch and push
/// for those who want them, and clone-and-read for a manager who only wants to look. It is also the
/// mechanism RC-2's refusal points at: the way to edit a library is to open it as a workspace of its
/// own, which on a shared library is a clone plus a push, or a request to the librarian. <b>Either
/// way it is a deliberate act with a record, which is what was missing.</b></para>
///
/// <para><b>Nothing here is host-specific</b> (R-rc9-4). The design binds to git, not to any hosting
/// service: no provider API, no account, no URL parsing beyond what git itself accepts. Whatever the
/// user pastes is handed to git after the one check below that it is not an OPTION.</para>
///
/// <para><b>The refspec is git's own and is never widened</b> (R-rc9-5a, §5.2a). A default fetch takes
/// branches and tags and nothing under a private namespace, so RC-5's per-checkpoint references do not
/// travel — <b>a clone arrives with no restore points, and that is correct</b>. The safety net is a
/// property of one machine and one designer's sessions: ordered by a monotonic sequence that means
/// nothing anywhere else and thinned by that machine's retention preference. What clones is the
/// narrative; the recipient starts a safety net of their own on their first boundary. It looks like a
/// bug and is not, which is why it is a documented row rather than an unstated fact.</para>
///
/// <para><b>A clone carries no management marker either</b> (R-rc9-5c, R-rc0-15). Git does not clone a
/// repository's config, so <see cref="GitRepositoryConfig.MarkerManagedKey"/> does not travel and the
/// clone reaches R-rc0-13's ordinary arming path on its own terms. <b>Do not carry it across by
/// hand</b>: a marker that travelled would make one designer's management decision everybody's, which
/// is the reason it is config rather than a reference. The <c>.cws</c>'s own
/// <see cref="CwsFile.RevisionControl"/> flag is the one thing that DOES travel, because it is
/// versioned — a workspace its owner switched off clones as off.</para>
///
/// <para><b>circuitRF has no credentials and asks for none</b> (§9.1, R-rc9-7a). It supplies whatever
/// git is already configured to supply and adds nothing of its own; what makes that safe rather than
/// merely small is <see cref="GitEnvironment"/>'s <c>GIT_TERMINAL_PROMPT=0</c>, which turns an
/// operation that would have asked into a refusal with a sentence instead of a subprocess blocked on
/// an invisible prompt. <b>A hang is the worst failure mode available</b> — no message, no exit code,
/// no end — and it is the one this operation is most likely to produce by accident. The bound is
/// INACTIVITY rather than wall-clock: a slow clone of a large library is not a hang.</para>
/// </summary>
public static class WorkspaceClone
{
    /// <summary>
    /// Clones <paramref name="source"/> into <paramref name="destination"/>, and reports whether what
    /// arrived is a workspace.
    ///
    /// <para><paramref name="destination"/> must not already exist as a non-empty folder: git refuses
    /// that itself, and circuitRF refuses it first so the sentence is circuitRF's.</para>
    /// </summary>
    /// <param name="progress">
    /// Called on a background thread as git reports progress, or null for none. <b>Asking for it is
    /// what asks git for <c>--progress</c></b>: git draws progress only to a terminal, and the process
    /// here has none, so without the flag a clone is silent until it ends.
    /// </param>
    public static CloneResult Clone(
        GitInstallation installation, string source, string destination, CancellationToken ct = default,
        Action<CloneProgress>? progress = null)
    {
        List<Diagnostic> notes = [];

        string url = (source ?? "").Trim();
        if (url.Length == 0)
            return Failed(destination, notes, SharingMessages.CloneNeedsASource());

        // The ONE thing that is not passed through (R-rc9-4). A source beginning with '-' would be
        // read by git as an option rather than as a repository, which is how an argument becomes a
        // flag. `--` does not help: `git clone -- --upload-pack=…` still refuses, and refusing here
        // says what is wrong instead of handing back git's parser error.
        if (url.StartsWith('-'))
            return Failed(destination, notes, SharingMessages.CloneSourceLooksLikeAnOption(url));

        string dest;
        try { dest = Path.GetFullPath(destination); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Failed(destination, notes, SharingMessages.CloneDestinationUnusable(destination, e.Message));
        }

        try
        {
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
                return Failed(dest, notes, SharingMessages.CloneDestinationNotEmpty(dest));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(dest, notes, SharingMessages.CloneDestinationUnusable(dest, e.Message));
        }

        // Whether the folder was there before, so a stopped clone removes only what IT created.
        bool destinationExisted = Directory.Exists(dest);

        string parent = Path.GetDirectoryName(dest) ?? dest;
        try { Directory.CreateDirectory(parent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(dest, notes, SharingMessages.CloneDestinationUnusable(parent, e.Message));
        }

        // The working directory is the destination's PARENT: the destination does not exist yet, and a
        // process cannot start in a folder that is not there.
        var git = new GitCommand(installation, parent);

        // R-rc9-5b. A clone touches two trees and git's ownership check applies to each: the parent it
        // writes into, and — when the source is a local path, which is the librarian scenario §7A is
        // written for — the repository it reads. Named individually, never `*`.
        List<string> safe = [parent];
        if (LocalSourcePath(url) is { } localSource) safe.Add(localSource);

        var result = git.Run(
            // No --branch, no --depth, no --single-branch, and NO --refmap or +refs/*:refs/* of any
            // kind. Git's default refspec is what leaves the checkpoint namespace behind (R-rc9-5a),
            // and gate 11 source-scans this file for one that reaches it.
            progress is null ? ["clone", "--", url, dest] : ["clone", "--progress", "--", url, dest],
            new GitRunOptions(Network: true, SafeDirectories: safe,
                              StdErrSegment: progress is null ? null : seg =>
                              {
                                  if (ParseProgress(seg) is { } p) progress(p);
                              }),
            ct);

        if (result.Cancelled || result.TimedOut)
        {
            // Git is KILLED, not asked, so it gets no chance to remove the half-made folder the way it
            // does when it fails on its own — and that folder is not empty, so the next attempt at the
            // same destination would be refused for a reason the designer did not create.
            if (!RemovePartialCopy(dest, destinationExisted))
                notes.Add(SharingMessages.ClonePartialCopyLeft(dest));
            if (result.Cancelled)
                return Failed(dest, notes, SharingMessages.CloneCancelled());
        }

        if (!result.Ok)
        {
            // Progress lines are noise in a failure's sentence, and asking for them put them there.
            var quiet = progress is null ? result : result with { StdErr = WithoutProgress(result.StdErr) };
            return Failed(dest, notes, GitFailures.Translate(quiet, SharingMessages.CopyingAWorkspace, parent));
        }

        progress?.Invoke(new CloneProgress("Finishing", null, 100, ""));

        string cws = Path.Combine(dest, WorkspacePersistence.FileName);
        bool isWorkspace;
        try { isWorkspace = File.Exists(cws); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { isWorkspace = false; }

        // RC-11 R-rc11-13. §5.11's corrections are attached to the versions this clone just took, and
        // `refs/notes/` is not in git's default clone refspec — so a copy would otherwise arrive
        // showing the wording its author had already corrected, which is the one outcome that feature
        // exists to prevent. Best effort in its own invocation, for the reason
        // `WorkspaceRemotes.BringInCorrections` states in full: a non-wildcard refspec naming a
        // reference the source does not have is a fatal error, and a source with no corrections in it
        // is the ordinary case.
        new GitCommand(installation, dest)
            .Run(["fetch", "--", "origin", VersionCorrections.NotesRefspec],
                 new GitRunOptions(Network: true, SafeDirectories: safe), ct);

        if (!isWorkspace)
        {
            // Not a failure. Git did what was asked; the folder simply is not a circuitRF workspace,
            // which is a thing a caller has to be told rather than a thing that went wrong.
            notes.Add(SharingMessages.CloneIsNotAWorkspace(dest));
            return new CloneResult(true, dest, null, notes);
        }

        // R-rc9-5a's row, said at the moment it is true rather than left to be discovered. The three
        // journeys disagree deliberately and a designer who is not told will assume the strongest of
        // the three.
        notes.Add(SharingMessages.CloneCarriesNoRestorePoints());

        return new CloneResult(true, dest, cws, notes);
    }

    // Git's progress phases, in the order a clone passes through them, and the share of the bar
    // each one fills. The locale is pinned to C (GitEnvironment), so these names are stable.
    private static readonly (string Phase, double From, double To)[] Weights =
    [
        ("Receiving objects", 0,  85),
        ("Resolving deltas",  85, 90),
        ("Updating files",    90, 100),
    ];

    private static readonly System.Text.RegularExpressions.Regex ProgressLine = new(
        @"^(?:remote:\s*)?(?<phase>[A-Z][a-z]+(?: [a-z]+)*):\s+(?<pct>\d{1,3})%(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// One segment of git's progress output as a <see cref="CloneProgress"/>, or null when it is not a
    /// progress line. A phase the weights do not name — the remote's own counting and compressing —
    /// is reported with no overall figure, which a bar draws as busy rather than as a number.
    /// </summary>
    public static CloneProgress? ParseProgress(string segment)
    {
        var m = ProgressLine.Match((segment ?? "").Trim());
        if (!m.Success) return null;

        string phase = m.Groups["phase"].Value;
        double pct   = Math.Clamp(double.Parse(m.Groups["pct"].Value, System.Globalization.CultureInfo.InvariantCulture), 0, 100);

        string detail = m.Groups["rest"].Value.Trim().TrimStart(',').Trim();
        if (detail.EndsWith(", done.", StringComparison.Ordinal)) detail = detail[..^", done.".Length];
        else if (detail.EndsWith("done.", StringComparison.Ordinal)) detail = detail[..^"done.".Length];
        detail = detail.Trim().TrimEnd(',').Trim();

        double? overall = null;
        foreach (var (name, from, to) in Weights)
            if (name == phase) overall = from + (to - from) * pct / 100;

        return new CloneProgress(phase, pct, overall, detail);
    }

    /// <summary>Standard error with git's progress lines taken out, for a failure's sentence.</summary>
    internal static string WithoutProgress(string stderr)
        => string.Join('\n', (stderr ?? "").Split('\r', '\n')
                                            .Where(l => l.Length > 0 && ParseProgress(l) is null));

    /// <summary>
    /// Removes what a stopped clone left. <b>Only what this clone created</b>: a folder that did not
    /// exist goes entirely, and a folder that existed (it was checked empty) is emptied and kept.
    ///
    /// <para><b>Git marks its object files read-only</b>, and on Windows a recursive delete stops at
    /// the first one, so the attribute is cleared first.</para>
    /// </summary>
    /// <returns>True when nothing is left behind.</returns>
    private static bool RemovePartialCopy(string dest, bool destinationExisted)
    {
        try
        {
            if (!Directory.Exists(dest)) return true;

            var all = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip      = FileAttributes.ReparsePoint,
                IgnoreInaccessible    = true,
            };
            foreach (var f in new DirectoryInfo(dest).EnumerateFiles("*", all))
                if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;

            if (!destinationExisted)
            {
                Directory.Delete(dest, recursive: true);
                return true;
            }

            var dir = new DirectoryInfo(dest);
            foreach (var d in dir.EnumerateDirectories()) d.Delete(recursive: true);
            foreach (var f in dir.EnumerateFiles())       f.Delete();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The local directory a source names, or null when it is a URL. <b>Not host-specific parsing</b>
    /// (R-rc9-4) — it asks the filesystem whether the string is a folder that exists, which is the
    /// only question <c>safe.directory</c> needs answered.
    /// </summary>
    private static string? LocalSourcePath(string url)
    {
        if (url.Contains("://", StringComparison.Ordinal)) return null;
        try { return Directory.Exists(url) ? Path.GetFullPath(url) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                   or PathTooLongException or NotSupportedException)
        { return null; }
    }

    private static CloneResult Failed(string destination, List<Diagnostic> notes, Diagnostic d)
    {
        notes.Add(d);
        return new CloneResult(false, destination, null, notes);
    }
}
