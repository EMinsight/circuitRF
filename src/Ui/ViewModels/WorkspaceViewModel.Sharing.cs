using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// <b>The window half of RC-9</b> — copying a workspace here, exchanging versions with the copy it
/// came from, and the pin (<c>docs/design/revision-control.md</c> §7, §7A.4, §9;
/// R-rc9-1 … R-rc9-17).
///
/// <para><b>This is what turns RC-2's read-only libraries from a restriction into a managed
/// collection.</b> The consumer's half is the pin: <i>which version of that library was this design
/// built and verified against</i>, recorded on the alias, moved only when somebody decides to move
/// it. The fixer's half is the copy: the way to change a library is to open it as a workspace of its
/// own, which on a shared one is a copy plus a send.</para>
///
/// <para><b>Nothing here happens by itself</b> (R-rc9-6). Every command on this partial is bound to a
/// menu item or a context-menu row. The one thing that runs on open reads the referenced workspaces'
/// repositories as they already are on this machine and reaches no network — which is what makes
/// <i>"a newer version is available"</i> a state rather than an event.</para>
///
/// <para><b>Kept in its own file for the same reason the revision partial is</b>: none of it is about
/// editing a design, and everything that decides anything lives below the firewall in
/// <see cref="WorkspaceSharingService"/> and the <c>src/Design</c> types under it — the same functions
/// <c>circuitrf history clone|pin|fetch|send</c> calls.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    private WorkspaceSharingService? _sharing;

    /// <summary>The service, built on first use so a workspace that references nothing costs
    /// nothing.</summary>
    private WorkspaceSharingService Sharing
    {
        get
        {
            if (_sharing is null)
            {
                _sharing = new WorkspaceSharingService(Messages);
                _sharing.PinsChanged += RefreshAfterReferenceChange;
            }
            return _sharing;
        }
    }

    // ── Copying a workspace here (R-rc9-1) ────────────────────────────────────────────────────────

    /// <summary>
    /// File ▸ <b>Clone Workspace…</b>
    ///
    /// <para>Below Reference Workspace… and separated from it, because the two answer the same question
    /// from opposite ends:
    /// one points at a workspace already on this machine, the other brings one onto it. It opens in a
    /// window of its own, exactly as opening any workspace does — <b>a copy is an ordinary workspace
    /// and there is deliberately nothing special about it afterwards.</b></para>
    ///
    /// <para><b>Offered only where git exists</b> (R-rc3-3). Absence is silent: a designer who does not
    /// want any of this never learns it is here.</para>
    /// </summary>
    [RelayCommand]
    private async Task CopyWorkspaceHere(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var dialog = new CopyWorkspaceDialog(ParentOfOpenWorkspace());
        if (await dialog.ShowDialog<bool?>(window) is not true) return;
        if (dialog.Source is not { Length: > 0 } source
         || dialog.Destination is not { Length: > 0 } destination) return;

        // RC-11 R-rc11-16. The titles about to be copied, listed first — and it is the SOURCE's titles,
        // not this window's, because a copy carries the history of the thing being copied.
        //
        // It shows on a source this machine can read, which is §7A's librarian scenario: one workspace
        // on this machine copied out of another one on it. A source behind an address cannot be
        // enumerated before it has been fetched, so there is nothing to compute and nothing is shown —
        // an empty list is the answer, and `ReviewWhatIsLeaving` goes straight on rather than
        // presenting a dialog with nothing in it.
        if (!await ReviewWhatIsLeaving(LeavingJourney.Copy, sourceRoot: source)) return;

        var result = await CloneWithProgress(source, destination);
        if (result is not { Ok: true, WorkspaceCwsPath: { } cws }) return;

        // R-rc9-5c. Nothing is carried across by hand — no management marker, no arming decision, no
        // identity. The copy reaches the ordinary arming path on its own terms in the window that
        // opens it, which is what stops one designer's decision becoming everybody's.
        App.OpenWorkspaceInNewWindow(cws, null);
    }

    /// <summary>
    /// Runs the clone off the UI thread, on a live Messages row with a Cancel. <b>A clone is a
    /// download</b> — a workspace carrying solved fields is hundreds of megabytes — and run on the UI
    /// thread it froze the window for the whole transfer with nothing saying why.
    ///
    /// <para>The bar is git's own progress, weighted so receiving fills most of it (see
    /// <see cref="CloneProgress.OverallPercent"/>). A Cancel kills git and removes the half-made
    /// folder, so the same destination can be used again straight away.</para>
    /// </summary>
    private async Task<CloneResult?> CloneWithProgress(string source, string destination)
    {
        string title = $"Cloning '{source}'";
        var live = Messages.BeginProgress(title);
        live.Update(title, "connecting", indeterminate: true);

        using var cts = new CancellationTokenSource();
        var cancellation = new RunCancellation("the clone", () =>
        {
            Messages.Info("Stopping the clone. The partly cloned folder is removed, and nothing is opened.");
            cts.Cancel();
        });
        live.BindCancellation(cancellation);

        try
        {
            var result = await Task.Run(() => Sharing.Copy(source, destination, cts.Token, p =>
            {
                // "Receiving objects 45% (48/106), 120.00 MiB | 10.00 MiB/s" — git's own words.
                string counter = string.Join(' ', new[] { p.Phase, p.PhasePercent is { } pct ? $"{pct:0}%" : "", p.Detail }
                                                      .Where(t => t.Length > 0));
                if (p.OverallPercent is { } overall) live.Update(title, counter, overall);
                else                                 live.Update(title, counter, indeterminate: true);
            }));

            if (result is null)                            live.Complete(MessageLevel.Info, $"{title} - did not start.");
            else if (result.Ok)                            live.Complete(MessageLevel.Info, $"{title} - done.");
            else if (cancellation.IsCancellationRequested) live.Complete(MessageLevel.Info, $"{title} - stopped.");
            else                                           live.Complete(MessageLevel.Info, $"{title} - did not complete.");
            return result;
        }
        catch (Exception ex)
        {
            live.Complete(MessageLevel.Error, $"{title}: {ex.Message}");
            return null;
        }
        finally
        {
            cancellation.Finish();
        }
    }

    /// <summary>Where the copy dialog's Browse opens: beside the workspace that is already open, which
    /// is where a sibling project belongs and is what the reference machinery expects.</summary>
    private string? ParentOfOpenWorkspace()
        => WorkspaceRootDir is { } root ? Path.GetDirectoryName(root) : null;

    // ── Exchanging with the other copy (R-rc9-6, R-rc9-7) ─────────────────────────────────────────

    /// <summary>Whether this workspace came from somewhere. Both exchange commands key on it, so a
    /// workspace that did not shows them greyed rather than failing when pressed.</summary>
    public bool CanExchangeWithOtherCopy => WorkspaceSharingService.HasOtherCopy(WorkspaceRootDir);

    /// <summary>
    /// File ▸ <b>Pull Changes</b>. Explicit, always (R-rc9-6) — an automatic fetch would silently
    /// change what a design resolves against.
    ///
    /// <para><b>It applies nothing to the workspace's files</b>, so no open document can be surprised
    /// by it: what arrives is LISTED, marked, at the top of the versions panel, and taking one is a
    /// separate act the designer performs. Choosing between two versions is RC-7's whole-file,
    /// pick-a-side decision, and nothing here makes it for anybody.</para>
    ///
    /// <para><b>The refresh below is what makes the fetch an operation with a result</b>, and until
    /// <c>HistoryBrowser.Incoming</c> existed it was not: a fetch updates the remote-tracking
    /// reference and moves nothing else, every surface read <c>HEAD</c>, and so this line refreshed a
    /// list that could not have changed. The versions were on the machine, complete, and invisible.
    /// Findings in <c>src/Ui/RESOLVED.md</c>.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExchangeWithOtherCopy))]
    private void BringInChanges()
    {
        Sharing.BringInChanges(WorkspaceRootDir);
        RefreshHistoryPanel();
    }

    /// <summary>
    /// File ▸ <b>Push Changes</b>.
    ///
    /// <para><b>The restore points do not go</b> (R-rc9-5a): they are one machine's safety net, ordered
    /// by a sequence that means nothing anywhere else. What goes is the narrative — the versions
    /// somebody deliberately kept.</para>
    ///
    /// <para>A refusal because the other side moved is RC-3's translated row (R-rc9-7), and its remedy
    /// is bring-in-then-choose, <b>never a merge</b>.</para>
    ///
    /// <para><b>The titles about to leave are listed first</b> (RC-11 R-rc11-16, §12 Q36). One dialog,
    /// in front of an operation that is already deliberate — and it is worth more than every
    /// correction mechanism §5.11 offers, because the expensive case is not a careless word but a
    /// customer's name in a title going to a different customer, and nobody can catch that from
    /// memory.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExchangeWithOtherCopy))]
    private async Task SendChanges()
    {
        if (!await ReviewWhatIsLeaving(LeavingJourney.Send)) return;

        Sharing.SendChanges(WorkspaceRootDir);
        RefreshHistoryPanel();
    }

    /// <summary>
    /// §5.11's review, for whichever of the three journeys is about to happen (R-rc11-16 … R-rc11-19).
    ///
    /// <para><b>One function and one dialog for all three</b>, over
    /// <see cref="Design.Revision.TitlesLeaving.For"/>'s one list. Three call sites each working out
    /// what leaves would be three chances for one of them to be wrong, and the wrong one would be the
    /// one nobody checked.</para>
    ///
    /// <para><b>Nothing to show is nothing to ask.</b> A workspace with no versions kept — the safety
    /// net without the narrative, which is a great many of them — goes straight on: a dialog listing
    /// nothing is a dialog that teaches people to click through this one.</para>
    ///
    /// <para>Returns whether to go on. Cancelling is a stop, not a failure, and reports nothing.</para>
    /// </summary>
    internal async Task<bool> ReviewWhatIsLeaving(LeavingJourney journey, string? fileWarning = null,
                                                 string? sourceRoot = null)
    {
        if (Views.WorkspaceLocator.WindowFor(this) is not { } owner) return true;

        string? root = sourceRoot ?? WorkspaceRootDir;

        var titles = History.TitlesLeaving(root, journey);
        if (titles.Count == 0) return true;

        // R-rc11-19. The correction is reachable FROM the list, and the list is re-read afterwards —
        // a review that went on showing the wording somebody had just fixed would be the one failure
        // this dialog exists to prevent, produced by the dialog itself.
        //
        // Offered only on this workspace's own history: on the copy journey the list is the SOURCE's,
        // which is another workspace circuitRF has not opened and whose history is not this window's
        // to write to (R-rc0-5 — circuitRF writes to exactly one repository).
        Views.Dialogs.TitlesLeavingDialog? review = null;

        Func<LeavingTitle, Task<IReadOnlyList<LeavingTitle>>>? correct =
            sourceRoot is null
                ? async title =>
                  {
                      await CorrectFromTheReview(root, title, review!);
                      return History.TitlesLeaving(root, journey);
                  }
                : null;

        review = new Views.Dialogs.TitlesLeavingDialog(journey, titles, fileWarning, correct);
        return await review.ShowDialog<bool?>(owner) is true;
    }

    /// <summary>
    /// One row of the review, corrected in place — <b>through the same two commands the History panel's
    /// own menu invokes</b>, so this surface holds no third spelling of §5.11.
    ///
    /// <para>The correction dialog is owned by the REVIEW rather than by the window. The review is
    /// already modal over the window, so a second dialog owned by the window would be a modal opened
    /// behind the one blocking it.</para>
    /// </summary>
    private async Task CorrectFromTheReview(string? root, LeavingTitle title, Window owner)
    {
        if (title.Sequence is { } sequence)
        {
            if (History.ListIncludingThinned(root).FirstOrDefault(p => p.Sequence == sequence) is { } point)
                await RenameRestorePoint(point, owner);
            return;
        }

        if (History.Versions(root).FirstOrDefault(
                v => string.Equals(v.CommitId, title.CommitId, StringComparison.Ordinal)) is { } version)
            await CorrectWhatYouWrote(version, owner);
    }

    // ── The pin (R-rc9-8 … R-rc9-16) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What every referenced workspace's pin says right now, keyed by alias. Rebuilt on open and
    /// whenever a pin changes; read by the Project panel's rows for their marks and menu labels.
    /// </summary>
    public IReadOnlyDictionary<string, PinState> PinStates { get; private set; } =
        new Dictionary<string, PinState>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// R-rc9-12 and R-rc9-16, on open: <b>a state is reported and nothing is asked.</b> A newer version
    /// is Info — the librarian publishes whenever they like and a signed-off design must not change
    /// because they did. A pin that cannot be honoured is an Error, because the cells behind that alias
    /// do not resolve and circuitRF has deliberately not fallen back to whatever is there now.
    /// </summary>
    private void OnWorkspaceOpenedForSharing()
    {
        RefreshPinStates(report: true);
        BringInChangesCommand.NotifyCanExecuteChanged();
        SendChangesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds <see cref="PinStates"/>. <b>Reads only, and reaches no network.</b></summary>
    private void RefreshPinStates(bool report = false)
    {
        var states = report
            ? Sharing.ReportPinsOnOpen(WorkspaceRootDir)
            : WorkspaceSharingService.Pins(WorkspaceRootDir);

        var map = new Dictionary<string, PinState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states) map[state.Alias] = state;
        PinStates = map;
    }

    /// <summary>The state one Project-panel row shows, or null when that alias has none.</summary>
    internal PinState? PinStateFor(string? alias)
        => alias is { Length: > 0 } && PinStates.TryGetValue(alias, out var state) ? state : null;

    /// <inheritdoc/>
    public PinState? PinStateFor(ProjectTreeNodeViewModel node)
        => node.Kind == NodeKind.ReferencedWorkspace ? PinStateFor(node.Name) : null;

    /// <inheritdoc/>
    public async Task TogglePinAsync(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return;
        if (node.Kind != NodeKind.ReferencedWorkspace) return;

        string root  = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string alias = node.Name;   // the alias IS the node's name (WorkspaceScanner)

        var state = WorkspaceSharingService.PinOf(root, alias);

        // R-rc9-15. THE PIN SURPRISES SOMEBODY, DELIBERATELY, AND THAT IS EXPLAINED RATHER THAN
        // DISCOVERED. With an unpinned reference, editing a cell in the library and coming back shows
        // the new cell; with a pinned one it deliberately does not. "I just edited it and my design
        // didn't change" is a thing to explain, and this is the moment somebody is choosing it.
        if (state is null || state.Status == PinStatus.Unpinned)
        {
            var window = ResolveOwner(null);
            if (window is null) return;

            var dlg = new SaveChangesDialog(
                $"Fix this design to the version of \"{alias}\" that is there now?\n\n"
              + "From then on this design uses that version and nothing else. If its owner changes a "
              + "cell, your design does NOT pick the change up — circuitRF tells you a newer version "
              + "is available instead, and you take it when you are ready.\n\n"
              + "That is what makes a result reproducible: it says which version of the library the "
              + "design was verified against, and a restore point brings that back along with your "
              + "own files.\n\n"
              + "Nothing is copied. If that workspace moves away or its history is rewritten, the "
              + "version cannot be reached and circuitRF says so rather than quietly using whatever "
              + "is there.",
                saveLabel:     "Use This Version",
                dontSaveLabel: null,
                cancelLabel:   "Cancel",
                title:         "Use a Fixed Version of This Library");
            await dlg.ShowDialog(window);
            if (dlg.Result != SaveChangesResult.Save) return;

            Sharing.Pin(root, alias);
        }
        else
        {
            // Going back to following the library restores the default and can lose nothing, so it
            // asks nothing — the same asymmetry RC-2's editability toggle already has.
            Sharing.Unpin(root, alias);
        }

        RefreshPinStates();
    }

    /// <inheritdoc/>
    public async Task TakeNewerVersionAsync(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return;
        if (node.Kind != NodeKind.ReferencedWorkspace) return;

        string root  = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string alias = node.Name;

        var window = ResolveOwner(null);
        if (window is null) return;

        // R-rc9-13. Moving a pin is a change to this workspace's own .cws, so it lands in the local
        // history with a date and an author — which is what later answers "when did this design start
        // using the new library?", usually the question being asked when something stopped working.
        var dlg = new SaveChangesDialog(
            $"Use the newest version of \"{alias}\" from now on?\n\n"
          + "Cells from it may have changed, so anything that places one is worth re-simulating.\n\n"
          + "This is a change to this workspace, so it goes into its history with today's date. If it "
          + "turns out to have broken something, going back to an earlier restore point brings the "
          + "old version back with your files.",
            saveLabel:     "Take the Newer Version",
            dontSaveLabel: null,
            cancelLabel:   "Cancel",
            title:         "Take a Newer Version of This Library");
        await dlg.ShowDialog(window);
        if (dlg.Result != SaveChangesResult.Save) return;

        Sharing.TakeNewerVersion(root, alias);
        RefreshPinStates();
    }
}
