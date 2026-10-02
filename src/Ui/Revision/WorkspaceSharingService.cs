using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Revision;

/// <summary>
/// <b>The window's half of RC-9</b> — copying a workspace here, exchanging versions with the copy it
/// came from, and the pin (<c>docs/design/revision-control.md</c> §7, §7A.4, §9, §9.1; R-rc9-1 …
/// R-rc9-17).
///
/// <para><b>It owns no revision-control logic.</b> Every decision is made below the firewall by
/// <see cref="WorkspaceClone"/>, <see cref="WorkspaceRemotes"/> and <see cref="WorkspacePins"/> — the
/// same functions <c>circuitrf history clone|fetch|send|pin</c> call, which is what stops the two
/// spellings drifting and is what gate 14 asserts byte for byte. What is here is what only a window
/// has: the Messages sink, and the knowledge of which workspace is open.</para>
///
/// <para><b>Nothing on this type is ever called automatically</b> (R-rc9-6). Every method reaches a
/// network or changes what a design resolves against, and both belong to a gesture somebody made. An
/// automatic fetch would silently change what a design resolves against, which is the failure §7A.4 is
/// written to prevent; gate 10 source-scans the open and checkpoint paths for a call into it.</para>
///
/// <para><b>The pin survey is the one thing that runs on open</b>, and it reaches no network: it reads
/// the referenced workspace's repository as it already is on this machine. That is what makes
/// <i>"a newer version is available"</i> a visible per-reference STATE rather than an ambient event or
/// a prompt (R-rc9-12) — the librarian publishes whenever they like, and nothing here changes until
/// the designer says so.</para>
/// </summary>
public sealed class WorkspaceSharingService
{
    private readonly IMessageSink _messages;

    public WorkspaceSharingService(IMessageSink messages) => _messages = messages;

    /// <summary>Raised when a pin changed, so the tree and any open document can be rebuilt against
    /// what the references now resolve to.</summary>
    public event Action? PinsChanged;

    // ── Copying a workspace here (R-rc9-1) ────────────────────────────────────────────────────────

    /// <summary>
    /// Copies a workspace from wherever git can reach it, and hands back its <c>.cws</c> when what
    /// arrived is one.
    ///
    /// <para><b>It never prompts for a credential and never blocks on one</b> (§9.1). circuitRF holds
    /// none and asks for none: it uses whatever git is already configured to use — a credential helper,
    /// an SSH agent, whatever IT set up — and an operation that would have asked <b>refuses</b>, with a
    /// sentence naming what the remote wanted. <b>A hang is the worst failure mode available</b>, and a
    /// window that never comes back from a copy is exactly it.</para>
    /// </summary>
    /// <para><b>Safe to call off the UI thread</b>, and the window does: a clone of a workspace
    /// carrying solved fields is hundreds of megabytes. <paramref name="progress"/> is called on the
    /// thread git's output is read on.</para>
    public CloneResult? Copy(string source, string destination, CancellationToken ct = default,
                             Action<CloneProgress>? progress = null)
    {
        if (GitDiscovery.Find(out _) is not { } installation)
        {
            // R-rc3-3's silence does not apply: the designer opened a dialog and pressed a button, so
            // they asked. Absence is only silent on the paths nobody asked for.
            _messages.Error(
                "circuitRF could not find a git to use on this machine, so it cannot copy a workspace "
              + "from one. Install one, or name it in Settings ▸ Revision Control.");
            return null;
        }

        var result = WorkspaceClone.Clone(installation, source, destination, ct, progress);
        Post(result.Diagnostics);
        return result;
    }

    /// <summary>Whether the copy affordances are offered at all (R-rc3-3: absent is silent, so they are
    /// not there rather than disabled).</summary>
    public static bool Available => GitDiscovery.IsAvailable;

    // ── Exchanging with the other copy (R-rc9-6, R-rc9-7) ─────────────────────────────────────────

    /// <summary>
    /// Whether this workspace came from somewhere and can exchange with it.
    ///
    /// <para><b>The directory check comes first and is not tidiness</b> — this is read on every
    /// workspace open, and everything after it starts a git process. The overwhelming majority of
    /// workspaces keep no history at all, and asking git three questions about each one of them on
    /// open would be a cost paid by everybody for a menu item almost nobody can use. A <c>.git</c>
    /// that is a FILE counts, because that is how a worktree points at its real repository.</para>
    /// </summary>
    public static bool HasOtherCopy(string? workspaceRoot)
    {
        if (workspaceRoot is not { Length: > 0 }) return false;

        try
        {
            string dotGit = Path.Combine(workspaceRoot, ".git");
            if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        return GitCommand.For(workspaceRoot) is { } git
            && git.IsRepositoryRoot()
            && WorkspaceRemotes.HasOtherCopy(git);
    }

    /// <summary>Brings in what is new on the other copy. <b>Applies nothing to the working tree</b>,
    /// so no open document can be surprised by it.</summary>
    public ExchangeResult? BringInChanges(string? workspaceRoot, CancellationToken ct = default)
        => Exchange(workspaceRoot, send: false, ct);

    /// <summary>Sends the versions kept here. The restore points stay on this machine (R-rc9-5a).</summary>
    public ExchangeResult? SendChanges(string? workspaceRoot, CancellationToken ct = default)
        => Exchange(workspaceRoot, send: true, ct);

    private ExchangeResult? Exchange(string? workspaceRoot, bool send, CancellationToken ct)
    {
        if (workspaceRoot is not { Length: > 0 }) return null;
        if (GitCommand.For(workspaceRoot) is not { } git || !git.IsRepositoryRoot())
        {
            _messages.Warning("This workspace does not keep a history, so there is nothing to exchange.");
            return null;
        }

        var result = send ? WorkspaceRemotes.Push(git, ct) : WorkspaceRemotes.Fetch(git, ct);
        Post(result.Diagnostics);
        return result;
    }

    // ── The pin (R-rc9-8 … R-rc9-16) ──────────────────────────────────────────────────────────────

    /// <summary>Every referenced workspace's pin state. Reads only; reaches no network.</summary>
    public static IReadOnlyList<PinState> Pins(string? workspaceRoot)
        => workspaceRoot is { Length: > 0 } ? WorkspacePins.Survey(workspaceRoot) : [];

    /// <summary>One reference's state, for a menu item's label and for the row's mark.</summary>
    public static PinState? PinOf(string? workspaceRoot, string alias)
        => workspaceRoot is { Length: > 0 } ? WorkspacePins.StateOf(workspaceRoot, alias) : null;

    /// <summary>
    /// R-rc9-12 and R-rc9-16, on open. <b>Two things are said and nothing is asked.</b>
    ///
    /// <para>A newer version is reported once, as Info: it is a state with an explicit action, not an
    /// event and not a prompt, and a design that was signed off must not change because somebody
    /// published. <b>A pin that cannot be honoured is an Error</b>, because the cells behind that alias
    /// do not resolve and the designer is about to find out the hard way — and circuitRF deliberately
    /// has not fallen back to whatever is there now.</para>
    /// </summary>
    public IReadOnlyList<PinState> ReportPinsOnOpen(string? workspaceRoot)
    {
        var states = Pins(workspaceRoot);
        foreach (var state in states)
            if (state.Diagnostic is { } d) Post([d]);
        return states;
    }

    /// <summary>
    /// Fixes this design to a version of one referenced workspace, or moves it to a newer one.
    ///
    /// <para><b>The write lands in this workspace's own history</b> (R-rc9-13), which is what later
    /// answers <i>"when did my design start using the new library?"</i> — usually the question being
    /// asked when something stopped working.</para>
    /// </summary>
    public PinChange? Pin(string? workspaceRoot, string alias, string? commitId = null)
        => Apply(workspaceRoot, () => WorkspacePins.Pin(workspaceRoot!, alias, commitId));

    /// <inheritdoc cref="Pin"/>
    public PinChange? TakeNewerVersion(string? workspaceRoot, string alias)
        => Apply(workspaceRoot, () => WorkspacePins.TakeNewerVersion(workspaceRoot!, alias));

    /// <summary>Back to following the referenced workspace as its owner changes it.</summary>
    public PinChange? Unpin(string? workspaceRoot, string alias)
        => Apply(workspaceRoot, () => WorkspacePins.Unpin(workspaceRoot!, alias));

    private PinChange? Apply(string? workspaceRoot, Func<PinChange> change)
    {
        if (workspaceRoot is not { Length: > 0 }) return null;

        var result = change();
        Post([result.Diagnostic]);

        // Only a WRITE changes what anything resolves to. "It already says that" is a success with
        // nothing to rebuild, and rebuilding anyway would drop every render model for no reason.
        if (result.Written)
        {
            WorkspaceRootFinder.InvalidateCache();
            PinsChanged?.Invoke();
        }
        return result;
    }

    /// <summary>Through <c>PostDiagnostic</c>, exactly as <see cref="WorkspaceHistoryService"/> does:
    /// the severity and the id travel with the sentence, so the panel and the CLI render one wording
    /// from one source and nothing is flattened into English on the way out (R-rc3-6).</summary>
    private void Post(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics) _messages.PostDiagnostic(d);
    }
}
