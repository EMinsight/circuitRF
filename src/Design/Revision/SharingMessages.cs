using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>
/// <b>Every sentence RC-9 puts in front of a designer</b> — cloning a workspace, fetching and sending,
/// and the pin (<c>docs/design/revision-control.md</c> §7, §7A.4, §9, §9.1; R-rc9-1 … R-rc9-17).
///
/// <para><b>Diagnostics, not strings</b> (R-rc3-6), for <see cref="HistoryMessages"/>' reasons: the
/// Messages panel and the CLI render one wording from one source, and the ids are what dedup and
/// filtering key on.</para>
///
/// <para><b>No git failure is invented here</b> (R-rc7-19). A remote that moved is
/// <see cref="GitFailures.NonFastForward"/>, a remote that wanted a sign-in is
/// <see cref="GitFailures.CredentialRequired"/>, a foreign-owned tree is
/// <see cref="GitFailures.ForeignOwnership"/>, and anything unrecognised is carried verbatim. What is
/// here is what a git failure is not: circuitRF's own refusals, and reports of what it did.</para>
///
/// <para><b>The vocabulary rule holds</b> (R-rc0-6). No branch, checkout, <c>HEAD</c>, detached, stash
/// or merge appears in any of this. A commit identity is named only where the designer has just made
/// or chosen one — the same exemption <see cref="HistoryMessages.VersionRecorded"/> takes, and for the
/// same reason: a pin IS an identity, and withholding it would leave the one fact that makes a result
/// reproducible unsayable.</para>
/// </summary>
public static class SharingMessages
{
    /// <summary>What a clone is called, in circuitRF's words, for <see cref="GitFailures.Translate"/>.</summary>
    public const string CopyingAWorkspace = "copying that workspace here";

    /// <summary>What a fetch is called, for the same reason. <b>Not "fetch"</b>: the designer asked to
    /// see whether the other copy has anything new, and that is the sentence.</summary>
    public const string BringingInChanges = "bringing in changes from the other copy";

    /// <summary>What a push is called, for the same reason.</summary>
    public const string SendingChanges = "sending your changes to the other copy";

    // ── Cloning (R-rc9-1 … R-rc9-5c) ──────────────────────────────────────────────────────────────

    /// <summary>Nothing was given to copy from.</summary>
    public static Diagnostic CloneNeedsASource() => new(
        "revision.clone.no-source",
        DiagnosticSeverity.Error,
        "circuitRF needs the address of the workspace to copy — the same address you would be given "
      + "by whoever maintains it.");

    /// <summary>
    /// The one thing not passed through to git (R-rc9-4). A source starting with <c>-</c> would be
    /// read as an option rather than as a repository, and git's own parser error names a flag the user
    /// never typed.
    /// </summary>
    public static Diagnostic CloneSourceLooksLikeAnOption(string source) => Diagnostic.Create(
        "revision.clone.source-looks-like-an-option",
        DiagnosticSeverity.Error,
        "'{source}' starts with a dash, so it would be read as a setting rather than as an address. "
      + "If that really is where the workspace is, give its full path instead.",
        ("source", source));

    /// <summary>The destination cannot be used at all — a bad path, or one that cannot be created.</summary>
    public static Diagnostic CloneDestinationUnusable(string path, string why) => Diagnostic.Create(
        "revision.clone.destination-unusable",
        DiagnosticSeverity.Error,
        "circuitRF could not create the folder '{path}': {why}",
        ("path", path), ("why", why));

    /// <summary>
    /// Something is already there. <b>Refused before git is started</b>, so the sentence is
    /// circuitRF's and names the one thing to do about it.
    /// </summary>
    public static Diagnostic CloneDestinationNotEmpty(string path) => Diagnostic.Create(
        "revision.clone.destination-not-empty",
        DiagnosticSeverity.Error,
        "'{path}' already has something in it, and a copy is only made into an empty folder. Choose a "
      + "folder that does not exist yet, or an empty one.",
        ("path", path));

    /// <summary>The designer stopped it. Nothing partial is left behind that circuitRF will open.</summary>
    public static Diagnostic CloneCancelled() => new(
        "revision.clone.cancelled",
        DiagnosticSeverity.Info,
        "Copying was stopped. Nothing was opened.");

    /// <summary>
    /// A stopped clone's half-made folder could not be removed. Said, because the next attempt at the
    /// same destination would otherwise be refused as "not empty" with no hint of why.
    /// </summary>
    public static Diagnostic ClonePartialCopyLeft(string path) => Diagnostic.Create(
        "revision.clone.partial-copy-left",
        DiagnosticSeverity.Warning,
        "The partly cloned folder '{path}' could not be removed. Delete it before cloning into the same place again.",
        ("path", path));

    /// <summary>
    /// The copy arrived and is not a circuitRF workspace. <b>Not a failure</b> — git did what was
    /// asked, and the folder is on disk exactly as it came down; there is simply nothing here to open.
    /// </summary>
    public static Diagnostic CloneIsNotAWorkspace(string path) => Diagnostic.Create(
        "revision.clone.not-a-workspace",
        DiagnosticSeverity.Warning,
        "The copy was made into '{path}', but it does not contain a circuitRF workspace, so there is "
      + "nothing to open. The files are there and untouched.",
        ("path", path));

    /// <summary>
    /// R-rc9-5a's row, said at the moment it becomes true.
    ///
    /// <para><b>The three ways a workspace leaves a machine disagree deliberately</b> — an archive
    /// carries restore points, a copy carries nothing, a clone carries the versions and not the safety
    /// net — <b>and a designer who is not told will assume the strongest of the three.</b> Saying it
    /// here is the difference between a documented property and a bug report.</para>
    /// </summary>
    public static Diagnostic CloneCarriesNoRestorePoints() => new(
        "revision.clone.no-restore-points",
        DiagnosticSeverity.Info,
        "The versions its author kept came with this copy. Their automatic restore points did not — "
      + "those belong to the machine they were taken on. circuitRF starts a safety net of your own "
      + "here the first time it has something to record.");

    // ── Fetch and send (R-rc9-6, R-rc9-7) ─────────────────────────────────────────────────────────

    /// <summary>
    /// There is no other copy recorded. A workspace circuitRF started a history for has no remote at
    /// all, which is the ordinary case and not an error.
    /// </summary>
    public static Diagnostic NoOtherCopy() => new(
        "revision.remote.none",
        DiagnosticSeverity.Warning,
        "This workspace was not copied from anywhere, so there is nothing to exchange changes with.");

    /// <summary>The other copy had nothing new.</summary>
    public static Diagnostic NothingNewToBringIn(string remote) => Diagnostic.Create(
        "revision.fetch.nothing-new",
        DiagnosticSeverity.Info,
        "'{remote}' has nothing that is not already here.",
        ("remote", remote));

    /// <summary>
    /// The other copy had something. <b>What arrived is not applied to the workspace's files</b> — it
    /// is brought in and reported, because applying it is the whole-file, pick-a-side decision RC-7
    /// owns and nothing here decides it for anybody.
    /// </summary>
    public static Diagnostic BroughtInChanges(string remote) => Diagnostic.Create(
        "revision.fetch.brought-in",
        DiagnosticSeverity.Info,
        "Brought in what is new on '{remote}'. Your own files are untouched — look at the versions "
      + "list to see what arrived.",
        ("remote", remote));

    /// <summary>Everything here was already there.</summary>
    public static Diagnostic NothingToSend(string remote) => Diagnostic.Create(
        "revision.push.nothing-to-send",
        DiagnosticSeverity.Info,
        "'{remote}' already has everything kept here, so nothing was sent.",
        ("remote", remote));

    /// <summary>It went.</summary>
    public static Diagnostic Sent(string remote) => Diagnostic.Create(
        "revision.push.sent",
        DiagnosticSeverity.Info,
        "Sent the versions kept here to '{remote}'. Your restore points stay on this machine.",
        ("remote", remote));

    // ── The pin (R-rc9-8 … R-rc9-16) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The referenced workspace keeps no history, so there is no identity to pin to. <b>Reported
    /// rather than approximated</b>: a pin invented from a folder's contents would be an identity that
    /// means nothing to anybody else, which is the opposite of what a pin is for.
    /// </summary>
    public static Diagnostic CannotPinWithoutAHistory(string alias) => Diagnostic.Create(
        "revision.pin.no-history-there",
        DiagnosticSeverity.Warning,
        "\"{alias}\" does not keep a history, so there is no particular version of it to fix this "
      + "design to. Whoever maintains it would need to keep versions of it first.",
        ("alias", alias));

    /// <summary>Recorded which version this design is built against.</summary>
    public static Diagnostic Pinned(string alias, string commitId) => Diagnostic.Create(
        "revision.pin.set",
        DiagnosticSeverity.Info,
        "This design now uses \"{alias}\" as it is at version {id}, and will go on using that version "
      + "until you take a newer one — even if its owner changes it.",
        ("alias", alias), ("id", Short(commitId)));

    /// <summary>Back to following whatever the library contains.</summary>
    public static Diagnostic Unpinned(string alias) => Diagnostic.Create(
        "revision.pin.cleared",
        DiagnosticSeverity.Info,
        "This design now follows \"{alias}\" as its owner changes it.",
        ("alias", alias));

    /// <summary>Nothing to do — the pin already names the version asked for.</summary>
    public static Diagnostic PinAlreadyCurrent(string alias) => Diagnostic.Create(
        "revision.pin.already-current",
        DiagnosticSeverity.Info,
        "This design is already using the newest version of \"{alias}\".",
        ("alias", alias));

    /// <summary>
    /// R-rc9-12. <b>A visible, per-reference state with an explicit action</b> — not an ambient event
    /// and not a prompt. The librarian publishes whenever they like and nothing in this workspace
    /// changes; this is how the designer finds out, when they look.
    /// </summary>
    public static Diagnostic NewerVersionAvailable(string alias) => Diagnostic.Create(
        "revision.pin.newer-available",
        DiagnosticSeverity.Info,
        "\"{alias}\" has a newer version than the one this design uses. Nothing has changed here, and "
      + "nothing will until you take it.",
        ("alias", alias));

    /// <summary>Moved, and it is a change to this workspace's own files, so it lands in the history
    /// with a date and an author (R-rc9-13).</summary>
    public static Diagnostic TookNewerVersion(string alias, string commitId) => Diagnostic.Create(
        "revision.pin.moved",
        DiagnosticSeverity.Info,
        "This design now uses \"{alias}\" at version {id}. That is a change to this workspace, so it "
      + "is in its history with today's date — which is what answers \"when did this design start "
      + "using the new library?\".",
        ("alias", alias), ("id", Short(commitId)));

    /// <summary>
    /// R-rc9-16, and gate 8. <b>Reported, never fallen back from.</b> Resolving to whatever is there
    /// now would defeat the entire feature — the design would silently be built against content it was
    /// never verified against, which is the failure the pin exists to prevent.
    /// </summary>
    public static Diagnostic PinCannotBeHonoured(string alias, string commitId, string why)
        => Diagnostic.Create(
            "revision.pin.cannot-be-honoured",
            DiagnosticSeverity.Error,
            "This design uses \"{alias}\" as it was at version {id}, and that version cannot be "
          + "reached: {why}. Cells from \"{alias}\" will not resolve until it can be, or until you "
          + "point this design at a version that is there. circuitRF does not fall back to whatever "
          + "\"{alias}\" contains now, because that is not what this design was built against.",
            ("alias", alias), ("id", Short(commitId)), ("why", why));

    /// <summary>The referenced workspace is not where the reference says it is.</summary>
    public const string BecauseTheWorkspaceIsNotThere =
        "the workspace it names is not where this design says it is";

    /// <summary>The referenced workspace is there and that version is not in its history.</summary>
    public const string BecauseThatVersionIsNotInItsHistory =
        "the workspace is there, but that version is not in its history any more";

    /// <summary>
    /// R-rc9-15. <b>The pin surprises someone, deliberately, and that must be explained rather than
    /// discovered.</b> Editing a cell in the library and returning to the design shows the new cell —
    /// unless the reference is pinned, when it deliberately does not. <i>"I just edited it and my
    /// design didn't change"</i> is a thing to explain, and this is where it is explained.
    /// </summary>
    public static Diagnostic EditedAPinnedLibrary(string alias) => Diagnostic.Create(
        "revision.pin.edit-not-shown",
        DiagnosticSeverity.Info,
        "This design uses a fixed version of \"{alias}\", so a change made there does not appear here. "
      + "That is what fixing a version means: the design goes on matching what it was verified "
      + "against. Take the newer version when you are ready for it.",
        ("alias", alias));

    /// <summary>Short form of a commit identity — enough to name one, short enough to read.</summary>
    private static string Short(string commitId)
        => commitId.Length > 12 ? commitId[..12] : commitId;
}
