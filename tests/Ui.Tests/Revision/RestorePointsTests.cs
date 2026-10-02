using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Revision;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-5's gates — <c>docs/sonnet-briefs/brief-revision-control-5-checkpoints.md</c> §8.
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b> A workspace name
/// from a real machine must not reach this repository.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> for the reason RC-3's own gates are: the identity
/// path redirects the per-user state directory, and the git-environment isolation these tests install
/// is process-wide, so two collections that both touch a process global still clobber each other.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class RestorePointsTests
{
    // ── Gate 1: the reference is outside the branch ───────────────────────────────────────────────

    /// <summary>
    /// R-rc5-1. <b>After ten restore points, the history a designer would browse has no new entries
    /// on it.</b> The two histories share a repository and nothing else.
    /// </summary>
    [GitFact]
    public void TenRestorePointsPutNothingOnTheDesignersOwnHistory()
    {
        using var ws = Armed();

        for (int i = 0; i < 10; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Assert.True(Take(ws, CheckpointOrigin.SavePoint, $"edit {i}").Recorded);
        }

        var heads = ws.Raw("for-each-ref", "refs/heads/");
        Assert.Equal("", heads.Out.Trim());

        // And HEAD names nothing, because nothing ever committed to a branch.
        Assert.NotEqual(0, ws.Raw("rev-parse", "--verify", "--quiet", "HEAD").Code);
    }

    // ── Gate 14: one reference per restore point, and deleting one leaves the rest ────────────────

    /// <summary>
    /// R-rc5-1. <b>Retention cannot be built without this</b>, so it is gated here rather than there:
    /// a single reference walking a chain can only be truncated from the oldest end, and truncation
    /// leaves everything before the cut still reachable — so it thins nothing at all.
    /// </summary>
    [GitFact]
    public void EachRestorePointHasItsOwnReferenceAndDroppingOneLeavesTheOthers()
    {
        using var ws = Armed();
        var git = ws.Git();

        for (int i = 0; i < 10; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Take(ws, CheckpointOrigin.SavePoint, $"edit {i}");
        }

        var all = CheckpointReferences.List(git);
        Assert.Equal(10, all.Count);

        var fifth = all.Single(r => r.Sequence == 5);
        Assert.Equal(0, ws.Raw("update-ref", "-d", fifth.Reference).Code);

        var left = CheckpointReferences.List(git);
        Assert.Equal(9, left.Count);
        Assert.DoesNotContain(left, r => r.Sequence == 5);

        // Every survivor still resolves to a state a restore could write.
        foreach (var (_, _, sequence) in left)
        {
            var point = RestorePoints.List(git).Single(p => p.Sequence == sequence);
            Assert.NotEqual("", point.TreeId);
        }
    }

    // ── Gate 14a: deleting a reference makes its state unreachable ────────────────────────────────

    /// <summary>
    /// R-rc5-1b. <b>Gate 14 passes on a parent chain; this one does not.</b> If each entry named the
    /// one before it as its parent, dropping a reference would free nothing, retention would thin
    /// nothing, and a gate that asserted the OTHERS survive would have passed anyway.
    ///
    /// <para>Run in a scratch copy with an immediate expiry, because the repository's own
    /// configuration forbids a routine pack from destroying anything (§4.5's
    /// <c>gc.pruneExpire=never</c>) — which is the behaviour under test everywhere else and would make
    /// this measurement impossible in place.</para>
    /// </summary>
    [GitFact]
    public void DroppingAReferenceMakesThatStateUnreachableAndLeavesTheRest()
    {
        using var ws = Armed();
        var git = ws.Git();

        for (int i = 0; i < 10; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Take(ws, CheckpointOrigin.SavePoint, $"edit {i}");
        }

        var points = RestorePoints.List(git).OrderBy(p => p.Sequence).ToList();
        Assert.Equal(10, points.Count);

        // Parentless by construction — the property everything below rests on.
        foreach (var p in points)
            Assert.Equal("1", ws.Raw("rev-list", "--count", p.CommitId).Out.Trim());

        string fifth = points[4].CommitId;
        Assert.Equal(0, ws.Raw("update-ref", "-d", points[4].Reference).Code);

        string scratch = Path.Combine(ws.HomeDir, "scratch-copy");
        CopyTree(Path.Combine(ws.Root, ".git"), Path.Combine(scratch, ".git"));

        RawIn(scratch, "config", "gc.pruneExpire", "now");
        RawIn(scratch, "reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all");
        RawIn(scratch, "prune", "--expire=now");

        Assert.NotEqual(0, RawIn(scratch, "cat-file", "-e", fifth + "^{commit}").Code);

        foreach (var p in points.Where(p => p.CommitId != fifth))
            Assert.Equal(0, RawIn(scratch, "cat-file", "-e", p.CommitId + "^{commit}").Code);
    }

    // ── Gate 14b: the shared index and HEAD are untouched ─────────────────────────────────────────

    /// <summary>
    /// R-rc5-1c. Every entry is built through a temporary index, so the designer's own staged state
    /// from a shell in the same folder survives and <c>HEAD</c> never moves.
    /// </summary>
    [GitFact]
    public void TheSharedIndexAndHeadAreUntouched()
    {
        using var ws = Armed();

        ws.Write("cells/a/thing.csch", "one");
        ws.Raw("add", "cells/a/thing.csch");
        ws.Raw("commit", "-m", "the designer's own");

        string head   = ws.Raw("rev-parse", "HEAD").Out.Trim();
        ws.Write("cells/a/staged.csch", "staged by hand");
        ws.Raw("add", "cells/a/staged.csch");
        string staged = ws.Raw("write-tree").Out.Trim();

        ws.Write("cells/a/thing.csch", "two");
        Assert.True(Take(ws, CheckpointOrigin.SavePoint, "after").Recorded);

        Assert.Equal(head,   ws.Raw("rev-parse", "HEAD").Out.Trim());
        Assert.Equal(staged, ws.Raw("write-tree").Out.Trim());
    }

    // ── Gate 3 / 4: the three boundaries, and the two that are deliberately not ───────────────────

    /// <summary>
    /// R-rc5-4, R-rc5-5. <b>Asserted directly, because these are the two most likely to be re-added
    /// by someone who did not read the reasons.</b> A run is frequent, often unchanged from the last
    /// one, and a sweep would generate dozens of near-identical entries; an idle timeout produces an
    /// entry the user cannot predict, at a moment meaningful to nobody.
    /// </summary>
    [Fact]
    public void OnlyThreeBoundariesExistAndNeitherARunNorATimerIsOne()
    {
        // The origins ARE the boundaries — a new automatic one would have to be a new member here, so
        // this is a structural statement rather than a search through prose.
        var origins = Enum.GetValues<CheckpointOrigin>();
        Assert.Equal(6, origins.Length);
        Assert.Contains(CheckpointOrigin.SavePoint, origins);
        Assert.Contains(CheckpointOrigin.WorkspaceClosed, origins);
        Assert.Contains(CheckpointOrigin.BeforeBatch, origins);

        // The fourth is a restore's own pre-state, which is not a boundary a session reaches on its
        // own — it exists only because R-rc5-12a makes going back symmetric.
        Assert.Contains(CheckpointOrigin.BeforeRestore, origins);

        // The fifth and sixth arrived with RC-6 and are not session boundaries either: each is taken
        // exactly once, by an explicit request to stop or start recording (R-rc6-14a). They are the
        // two ends of an off period, which is what lets it render as a GAP rather than as a stretch in
        // which nothing happened to be worth keeping. No session reaches either on its own, and
        // nothing takes one on a timer.
        Assert.Contains(CheckpointOrigin.RecordingOff, origins);
        Assert.Contains(CheckpointOrigin.RecordingOn,  origins);

        string source = ReadSource("src/Design/Revision/WorkspaceCheckpoints.cs")
                      + ReadSource("src/Design/Revision/WorkspaceArming.cs")
                      + ReadSource("src/Ui/Revision/WorkspaceHistoryService.cs");
        source = StripComments(source);

        foreach (string forbidden in new[] { "Timer", "Idle", "OnRunCompleted", "AfterRun" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-rc5-4. Three boundaries take three entries; <b>a sequence of file saves between two of them
    /// takes none</b>, because a save is not a boundary — one logical design action writes a layout,
    /// the workspace file and a schematic.
    /// </summary>
    [GitFact]
    public void EachBoundaryTakesOneAndSavesBetweenThemTakeNone()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "one");
        Assert.True(Take(ws, CheckpointOrigin.SavePoint, "first").Recorded);

        // Six file writes — the shape of one logical edit saved several times.
        for (int i = 0; i < 6; i++) ws.Write("cells/a/thing.csch", $"between {i}");
        Assert.Single(CheckpointReferences.List(git));

        Assert.True(Take(ws, CheckpointOrigin.BeforeBatch, "widen the output match").Recorded);
        ws.Write("cells/a/thing.csch", "after the batch");
        Assert.True(Take(ws, CheckpointOrigin.WorkspaceClosed, null).Recorded);

        Assert.Equal(3, CheckpointReferences.List(git).Count);
    }

    // ── Gate 5: what a restore point spans ────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-7, R-rc5-1d. <b>The fourth file is the one a literal <c>commit -a</c> misses</b> — a new
    /// cell folder an agent just created is exactly the file §1.2 exists to capture.
    /// </summary>
    [GitFact]
    public void ARestorePointSpansFilesNobodyOpenedIncludingOnesThatDidNotExist()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "a");
        ws.Write("cells/b/thing.csch", "b");
        ws.Write("cells/c/thing.csch", "c");
        Take(ws, CheckpointOrigin.SavePoint, "before");

        ws.Write("cells/a/thing.csch", "a2");
        ws.Write("cells/b/thing.csch", "b2");
        ws.Write("cells/c/thing.csch", "c2");
        ws.Write("cells/d/brand-new.csch", "d");   // never seen by any earlier entry

        var taken = Take(ws, CheckpointOrigin.SavePoint, "after");
        Assert.True(taken.Recorded);

        var names = ws.Raw("ls-tree", "-r", "--name-only", taken.Point!.TreeId).Out;
        foreach (string expected in new[]
                 { "cells/a/thing.csch", "cells/b/thing.csch", "cells/c/thing.csch", "cells/d/brand-new.csch" })
            Assert.Contains(expected, names, StringComparison.Ordinal);

        Assert.Equal("a2", ws.Raw("show", taken.Point.TreeId + ":cells/a/thing.csch").Out.Trim());
    }

    // ── Gate 5a: an unchanged tree records nothing, and a label is a label ────────────────────────

    /// <summary>
    /// R-rc5-5a. <b>The test is the tree, not the clock.</b> A close after a session that only
    /// looked, and a save-point pressed twice, would otherwise add an entry indistinguishable from
    /// the one before it — the unreadable log R-rc5-4 rejects.
    /// </summary>
    [GitFact]
    public void ABoundaryWithAnUnchangedTreeRecordsNothing()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "one");
        Assert.True(Take(ws, CheckpointOrigin.SavePoint, "first").Recorded);
        Assert.Single(CheckpointReferences.List(git));

        Assert.False(Take(ws, CheckpointOrigin.SavePoint, "second").Recorded);
        Assert.Single(CheckpointReferences.List(git));

        Assert.False(Take(ws, CheckpointOrigin.WorkspaceClosed, null).Recorded);
        Assert.Single(CheckpointReferences.List(git));
    }

    /// <summary>
    /// R-rc5-5a. <b>A save-point's label is its message, and a bare time is never the label</b> — a
    /// time is what every entry already has, so an entry labelled only with one says nothing.
    /// </summary>
    [GitFact]
    public void ASavePointsLabelIsItsMessageAndSavePointStandsInWhenThereIsNone()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "one");
        Take(ws, CheckpointOrigin.SavePoint, "Widen the output match");

        ws.Write("cells/a/thing.csch", "two");
        Take(ws, CheckpointOrigin.SavePoint, null);

        var points = RestorePoints.List(git);
        Assert.Equal(CheckpointMessage.UnnamedSavePoint, points[0].Label);
        Assert.Equal("Widen the output match", points[1].Label);

        // The trailer, not a rendering of it.
        string raw = ws.Raw("cat-file", "-p", points[1].CommitId).Out;
        Assert.Contains(CheckpointMessage.IntentKey + ": Widen the output match", raw, StringComparison.Ordinal);
        Assert.Contains(CheckpointMessage.OriginKey + ": save-point", raw, StringComparison.Ordinal);

        // And nothing anywhere in it is a bare time.
        Assert.DoesNotMatch(new Regex(@"^\s*\d{1,2}:\d{2}\s*$", RegexOptions.Multiline), points[0].Label);
    }

    // ── Gate 7: ordering survives a clock change ─────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-8. <b>The wall clock supplies the label a human reads; it never decides what is
    /// oldest.</b> This is the gate retention depends on: a machine whose clock jumps forward would
    /// otherwise make every entry expired on the next sweep, and one set backwards would stamp new
    /// entries as older than the ones they follow.
    /// </summary>
    [GitFact]
    public void OrderingSurvivesAClockSetBackwards()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "one");
        Take(ws, CheckpointOrigin.SavePoint, "first");

        // The fixture's clock, moved backwards a year between two entries. git takes its stamp from
        // these, so the second entry is TIMESTAMPED older than the first.
        ws.SetClock(DateTimeOffset.UtcNow.AddYears(-1));

        ws.Write("cells/a/thing.csch", "two");
        Take(ws, CheckpointOrigin.SavePoint, "second");

        var points = RestorePoints.List(git);
        Assert.Equal(2, points.Count);

        // Newest first, by SEQUENCE.
        Assert.Equal("second", points[0].Label);
        Assert.Equal(2, points[0].Sequence);
        Assert.Equal(1, points[1].Sequence);

        // And the clock genuinely disagrees, so the assertion above is measuring something.
        Assert.True(points[0].TakenUtc < points[1].TakenUtc);
    }

    // ── Gate 8: no identity means no arming ──────────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-9. On a fresh machine nobody can be named, and a restore point is not user-initiated so
    /// it has no dialog to fail into. <b>The workspace does not arm, nothing is created, and the
    /// refusal names the Settings tab rather than a git command.</b>
    /// </summary>
    [GitFact]
    public void WithNobodyToRecordAsNothingArmsAndTheRefusalNamesTheSettingsTab()
    {
        using var ws = new GitWorkspace();
        using var appData = new AppDataRootScope();          // no preferences file, so no identity
        File.WriteAllText(ws.GlobalConfig, "");              // and no global git identity either

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint,
                                        RevisionArming.KeepHistoryDefault, null,
                                        circuitRfWroteAFileThisSession: true);

        Assert.False(armed.Armed);
        Assert.NotNull(armed.Refusal);
        Assert.Equal("revision.identity.missing", armed.Refusal!.Id);
        Assert.Contains("Settings", armed.Refusal.Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("user.email", armed.Refusal.Render(), StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(ws.Root, ".git")));
    }

    // ── Gate 18a: arming happens at a boundary, not at open ──────────────────────────────────────

    /// <summary>
    /// R-rc5-4a. <b>The share case is the one that matters</b>: creating a repository because a
    /// colleague glanced at the folder is R-rc0-5's ambush pointed at a directory instead of a
    /// history.
    /// </summary>
    [GitFact]
    public void OpeningAWorkspaceAndChangingNothingCreatesNothing()
    {
        using var ws = new GitWorkspace();
        using var identity = Identity(ws);

        // A close after a session in which circuitRF wrote nothing. The preference is ON.
        var glance = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.WorkspaceClosed,
                                         keepHistoryPreference: true, workspaceSetting: null,
                                         circuitRfWroteAFileThisSession: false);

        Assert.False(glance.Armed);
        Assert.False(Directory.Exists(Path.Combine(ws.Root, ".git")));
        Assert.False(File.Exists(Path.Combine(ws.Root, ".gitignore")));

        // Then a boundary that WOULD record something.
        var real = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint,
                                       keepHistoryPreference: true, workspaceSetting: null,
                                       circuitRfWroteAFileThisSession: false);

        Assert.True(real.Armed);
        Assert.True(Directory.Exists(Path.Combine(ws.Root, ".git")));
    }

    /// <summary>R-rc5-4a. A close DOES arm once circuitRF has written into the workspace itself.</summary>
    [GitFact]
    public void ACloseArmsOnceCircuitRfHasWrittenAFile()
    {
        using var ws = new GitWorkspace();
        using var identity = Identity(ws);

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.WorkspaceClosed,
                                        keepHistoryPreference: true, workspaceSetting: null,
                                        circuitRfWroteAFileThisSession: true);

        Assert.True(armed.Armed);
        Assert.True(Directory.Exists(Path.Combine(ws.Root, ".git")));
    }

    // ── Gate 18b: the announcement fires once ────────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-4b. §1.4's rule is that a state must be VISIBLE, not that it must be absent, and this is
    /// the cheap half of it: one entry, saying what was created, where the setting is, and that
    /// removing it later cannot harm the design.
    /// </summary>
    [GitFact]
    public void TheFirstArmingAnnouncesItselfExactlyOnce()
    {
        using var ws = new GitWorkspace();
        using var identity = Identity(ws);

        var first = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.True(first.RepositoryJustCreated);
        Assert.NotNull(first.Announcement);
        Assert.Equal("revision.history.started", first.Announcement!.Id);
        Assert.Contains("Settings", first.Announcement.Render(), StringComparison.Ordinal);
        Assert.Contains("cannot harm the design", first.Announcement.Render(), StringComparison.Ordinal);

        var second = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.False(second.RepositoryJustCreated);
        Assert.Null(second.Announcement);
    }

    // ── Gate 18c: both Stage 2 affordances exist ─────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-4c. <b>A boundary with a test and no affordance is the gap this requirement was written
    /// for.</b> The restore-point list is a dockable tool panel, and the explicit save-point is an
    /// invokable command on the File menu.
    /// </summary>
    [Fact]
    public void BothStageTwoAffordancesExist()
    {
        // The panel has a home in the dock schema — which is what makes it captured and restored with
        // every other panel rather than a window that appears once.
        // RE-POINTED at RC-10's merged panel (§5.10). What is asserted is unchanged: the list has a
        // home in the dock schema, which is what makes it captured and restored with every other panel.
        Assert.Contains(DockPanelIds.History, DockPanelIds.All);
        Assert.Equal(DockPanelIds.History, new HistoryTool().Id);

        // The save-point is a command, and it reaches the boundary.
        var command = typeof(WorkspaceViewModel).GetProperty("KeepThisStateCommand");
        Assert.NotNull(command);

        string source = StripComments(ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));
        Assert.Contains("TakeSavePoint", source, StringComparison.Ordinal);

        // And both are reachable from the menus a user reads.
        string menu = ReadSource("src/Ui/Views/WorkspaceWindow.axaml");
        Assert.Contains("KeepThisStateCommand", menu, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"History\"", menu, StringComparison.Ordinal);
    }

    // ── Gate 25: a kept restore point is marked ──────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-1f. A save-point carries the mark because the user's judgement about what matters beats
    /// any heuristic, and the keep action adds it to any other entry — §10B.3's "make it permanent",
    /// in Stage 2, before there is a Commit to turn anything into.
    /// </summary>
    [GitFact]
    public void ASavePointIsKeptAndTheKeepActionMarksAnythingElse()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "one");
        Take(ws, CheckpointOrigin.SavePoint, "worth keeping");

        ws.Write("cells/a/thing.csch", "two");
        Take(ws, CheckpointOrigin.WorkspaceClosed, null);

        var points = RestorePoints.List(git);
        var closed = points.Single(p => p.Origin == CheckpointOrigin.WorkspaceClosed);
        var saved  = points.Single(p => p.Origin == CheckpointOrigin.SavePoint);

        Assert.True(saved.Kept);
        Assert.False(closed.Kept);

        Assert.True(RestorePoints.MarkKept(git, closed).Ok);

        var after = RestorePoints.List(git).Single(p => p.Sequence == closed.Sequence);
        Assert.True(after.Kept);
        Assert.Equal(closed.TreeId, after.TreeId);          // the state itself is untouched
        Assert.Equal(closed.Label, after.Label);            // and so is the line the list shows
        Assert.Contains(CheckpointMessage.KeptKey, ws.Raw("cat-file", "-p", after.CommitId).Out,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// R-rc5-1f, and the trap the <c>Intent</c> field exists for. A batch's LABEL is
    /// "before: <i>intent</i>", so marking one kept — which rewrites the message — must start from the
    /// intent. Rebuilt from the label it reads "before: before: …", which is a well-formed entry with
    /// a slightly odd line in it: exactly the kind of wrong nobody notices.
    /// </summary>
    [GitFact]
    public void MarkingABatchEntryKeptDoesNotDoubleItsPrefix()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "one");
        Take(ws, CheckpointOrigin.BeforeBatch, "widen the output match", attended: false);

        var before = RestorePoints.List(git).Single();
        Assert.Equal("before: widen the output match", before.Label);
        Assert.Equal("widen the output match", before.Intent);

        Assert.True(RestorePoints.MarkKept(git, before).Ok);

        var after = RestorePoints.List(git).Single();
        Assert.True(after.Kept);
        Assert.Equal("before: widen the output match", after.Label);
    }

    // ── Gate 19e: a nested repository is excluded by pathspec ────────────────────────────────────

    /// <summary>
    /// R-rc5-7d. Handing a directory that contains a repository of its own to <c>git add</c> records
    /// it as an embedded pointer — precisely the "committed as something by the enclosing workspace"
    /// §7A.5 forbids, with a warning nobody reads.
    /// </summary>
    [GitFact]
    public void ANestedRepositoryIsInNeitherTheTreeNorAsAPointer()
    {
        using var ws = Armed();

        ws.Write("cells/a/thing.csch", "a");
        ws.Write("cells/vendor/part.csch", "someone else's");
        RawIn(Path.Combine(ws.Root, "cells", "vendor"), "init", "--quiet");

        var taken = Take(ws, CheckpointOrigin.SavePoint, "with a nested one");
        Assert.True(taken.Recorded);

        string names = ws.Raw("ls-tree", "-r", "--name-only", taken.Point!.TreeId).Out;
        Assert.DoesNotContain("cells/vendor", names, StringComparison.Ordinal);

        // Not as a gitlink either — a non-recursive listing is where one would show.
        string top = ws.Raw("ls-tree", "-r", "-t", taken.Point.TreeId).Out;
        Assert.DoesNotContain("commit", top, StringComparison.Ordinal);
    }

    // ── Gate 19f: an unattended boundary leaves a large file out and says so ─────────────────────

    /// <summary>
    /// R-rc5-15a. §9A.1's rule decides the direction: <b>including is irreversible, leaving out is
    /// not.</b> What must not happen is the silent version of either.
    /// </summary>
    [GitFact]
    public void AnUnattendedBoundaryLeavesALargeFileOutAndTheEntrySaysSo()
    {
        using var ws = Armed();
        var git = ws.Git();

        long previous = LargeFileGuard.ThresholdBytes;
        LargeFileGuard.ThresholdBytes = 4096;
        try
        {
            ws.Write("cells/a/thing.csch", "a");
            ws.WriteBytes("imports/board.gds", 32 * 1024);

            var taken = Take(ws, CheckpointOrigin.WorkspaceClosed, null, attended: false);
            Assert.True(taken.Recorded);

            // Recorded, and the big file is not in it.
            string names = ws.Raw("ls-tree", "-r", "--name-only", taken.Point!.TreeId).Out;
            Assert.Contains("cells/a/thing.csch", names, StringComparison.Ordinal);
            Assert.DoesNotContain("imports/board.gds", names, StringComparison.Ordinal);

            // The trailer names it, so the list can show the entry as incomplete.
            Assert.Contains(CheckpointMessage.LeftOutKey + ": imports/board.gds",
                            ws.Raw("cat-file", "-p", taken.Point.CommitId).Out, StringComparison.Ordinal);

            var point = RestorePoints.List(git).First();
            Assert.True(point.IsIncomplete);
            Assert.Contains("imports/board.gds", point.LeftOut);

            // One entry said so.
            Assert.Contains(taken.Diagnostics, d => d.Id == "revision.large-file.left-out");

            // And the next INTERACTIVE moment still has something to ask about — the file is still
            // outside the history, so the question has not been silently answered.
            Assert.Contains(LargeFileGuard.Find(git, point.TreeId), f => f.RelativePath == "imports/board.gds");
        }
        finally { LargeFileGuard.ThresholdBytes = previous; }
    }

    /// <summary>
    /// A tool that splits its output defeats a per-file threshold — a Palace solve's 150 field pieces
    /// are each 10.7 MB. An unattended boundary leaves out the deepest folder whose new small files add
    /// up past <see cref="LargeFileGuard.FolderThresholdBytes"/>, and never a folder holding a kept
    /// file, because leaving the folder out would take that file out of the entry.
    /// </summary>
    [GitFact]
    public void AnUnattendedBoundaryLeavesOutAFolderOfManySmallFilesButNeverOneHoldingAKeptFile()
    {
        using var ws = Armed();
        (long file, long folder) previous = (LargeFileGuard.ThresholdBytes, LargeFileGuard.FolderThresholdBytes);
        (LargeFileGuard.ThresholdBytes, LargeFileGuard.FolderThresholdBytes) = (4096, 16 * 1024);
        try
        {
            ws.Write("mixed/notes.txt", "kept");
            Assert.True(Take(ws, CheckpointOrigin.WorkspaceClosed, null, attended: false).Recorded);

            for (int i = 0; i < 8; i++) ws.WriteBytes($"solver/run/post/piece{i}.dat", 3000);
            for (int i = 0; i < 8; i++) ws.WriteBytes($"mixed/piece{i}.dat", 3000);
            ws.Write("solver/run/log.txt", "small");

            var taken = Take(ws, CheckpointOrigin.WorkspaceClosed, null, attended: false);
            Assert.True(taken.Recorded);
            Assert.Equal(["solver/run/post/"], taken.Point!.LeftOut);

            string names = ws.Raw("ls-tree", "-r", "--name-only", taken.Point.TreeId).Out;
            Assert.DoesNotContain("solver/run/post/", names, StringComparison.Ordinal);
            Assert.Contains("solver/run/log.txt", names, StringComparison.Ordinal);
            Assert.Contains("mixed/notes.txt", names, StringComparison.Ordinal);
            Assert.Contains("mixed/piece0.dat", names, StringComparison.Ordinal);
        }
        finally { (LargeFileGuard.ThresholdBytes, LargeFileGuard.FolderThresholdBytes) = previous; }
    }

    // ── Gate 10: the guard's three choices, and there is no fourth ───────────────────────────────

    /// <summary>
    /// R-rc5-15, R-rc5-17. <b>There is no path that adds a <c>.gitignore</c> pattern for a file that
    /// is already kept</b> — that is the shape the rejected fourth choice would have had, and it
    /// resolves to either telling the user they turned something off when they did not, or leaving
    /// the history holding one stale version with the current one absent from every copy.
    /// </summary>
    [GitFact]
    public void TheThreeChoicesDoWhatTheySayAndThereIsNoFourth()
    {
        using var ws = Armed();

        long previous = LargeFileGuard.ThresholdBytes;
        LargeFileGuard.ThresholdBytes = 4096;
        try
        {
            ws.WriteBytes("imports/board.gds", 32 * 1024);
            var file = new LargeFile("imports/board.gds", 32 * 1024, "*.gds");

            // "Leave it out this time" writes nothing at all.
            Assert.False(LargeFileGuard.Apply(ws.Root, file, LargeFileChoice.LeaveOutThisTime, false));
            Assert.DoesNotContain("*.gds", File.ReadAllText(Path.Combine(ws.Root, ".gitignore")));

            // "Include it" writes nothing either — it is the absence of an exclusion.
            Assert.False(LargeFileGuard.Apply(ws.Root, file, LargeFileChoice.Include, false));
            Assert.DoesNotContain("*.gds", File.ReadAllText(Path.Combine(ws.Root, ".gitignore")));

            // "Never include files like this" adds the pattern.
            Assert.True(LargeFileGuard.Apply(ws.Root, file, LargeFileChoice.NeverIncludeFilesLikeThis, false));
            Assert.Contains("*.gds", File.ReadAllText(Path.Combine(ws.Root, ".gitignore")));

            // And it REFUSES for a file that is already kept, which is the fourth path's shape.
            var kept = new LargeFile("imports/other.gds", 32 * 1024, "*.other");
            Assert.False(LargeFileGuard.Apply(ws.Root, kept, LargeFileChoice.NeverIncludeFilesLikeThis, true));
            Assert.DoesNotContain("*.other", File.ReadAllText(Path.Combine(ws.Root, ".gitignore")));
        }
        finally { LargeFileGuard.ThresholdBytes = previous; }

        // No code path untracks a file, which is the other half of the rejected option.
        string source = StripComments(
            ReadSource("src/Design/Revision/LargeFileGuard.cs")
          + ReadSource("src/Ui/Views/Dialogs/KeepThisStateDialog.axaml.cs"));

        Assert.DoesNotContain("rm --cached", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--cached\"", source, StringComparison.Ordinal);

        // Exactly three choices, counted rather than assumed.
        Assert.Equal(3, Enum.GetValues<LargeFileChoice>().Length);
    }

    /// <summary>
    /// R-rc5-16. <b>The consequence appears where the choice is made</b>, not only in a manual: a file
    /// matching that pattern is not in the history, so it is not in a restore and not in a copy.
    /// </summary>
    [Fact]
    public void TheThirdChoiceCarriesItsConsequenceAtThePointOfChoosing()
    {
        string text = RestorePointMessages.NeverIncludeConsequence;
        Assert.Contains("not in a restore", text, StringComparison.Ordinal);
        Assert.Contains("not in a copy", text, StringComparison.Ordinal);

        // And the dialog renders it beside the choice.
        string view = ReadSource("src/Ui/Views/Dialogs/KeepThisStateDialog.axaml");
        Assert.Contains("Consequence", view, StringComparison.Ordinal);
    }

    // ── Gate 21: the first recording summarises rather than enumerating ──────────────────────────

    /// <summary>
    /// R-rc5-17a. <b>A dialog naming hundreds of files is not a dialog.</b> The first recording into a
    /// workspace with four years of accumulated output summarises by category and by size, and offers
    /// the same three choices per PATTERN.
    /// </summary>
    [GitFact]
    public void TheFirstRecordingIntoAnExistingWorkspaceGroupsRatherThanEnumerating()
    {
        using var ws = Armed();

        long previous = LargeFileGuard.ThresholdBytes;
        LargeFileGuard.ThresholdBytes = 1024;
        try
        {
            for (int i = 0; i < 100; i++) ws.WriteBytes($"old/run{i}.dat", 4096);
            for (int i = 0; i < 4;  i++)  ws.WriteBytes($"imports/part{i}.gds", 8192);

            var found = LargeFileGuard.Find(ws.Git(), null);
            Assert.Equal(104, found.Count);

            var grouped = LargeFileGuard.SummariseByPattern(found);
            Assert.Equal(2, grouped.Count);

            var dat = grouped.Single(g => g.Pattern == "*.dat");
            Assert.Equal(100, dat.Count);
            Assert.Equal(100 * 4096, dat.TotalBytes);

            var gds = grouped.Single(g => g.Pattern == "*.gds");
            Assert.Equal(4, gds.Count);
        }
        finally { LargeFileGuard.ThresholdBytes = previous; }
    }

    // ── Gate 24: nothing checks out, nothing branches ────────────────────────────────────────────

    /// <summary>
    /// R-rc5-12c, R-rc5-13. <b>rev 4 said git requires a second line of work after going back and
    /// specified one; it does not.</b> A second line is required only if the restore is a checkout,
    /// and it never is — the state is written into the working tree through a private index.
    ///
    /// <para><c>checkout-index</c> is the plumbing that turns an index into files and is the one
    /// permitted spelling; the scan is for the porcelain that moves <c>HEAD</c> or the working tree
    /// out from under an open document.</para>
    /// </summary>
    [Fact]
    public void NoPathChecksOutSwitchesBranchesStashesOrResets()
    {
        string[] paths =
        [
            "src/Design/Revision/WorkspaceRestore.cs",
            "src/Design/Revision/WorkspaceCheckpoints.cs",
            "src/Design/Revision/WorkspaceArming.cs",
            "src/Design/Revision/RestorePoints.cs",
            "src/Design/Revision/CheckpointReferences.cs",
            "src/Design/Revision/BatchSession.cs",
            "src/Ui/Revision/WorkspaceHistoryService.cs",
            "src/Cli/History.cs",
        ];

        foreach (string path in paths)
        {
            string source = StripComments(ReadSource(path));

            foreach (string forbidden in new[]
                     { "\"checkout\"", "\"switch\"", "\"branch\"", "\"stash\"", "\"reset\"", "\"merge\"" })
                Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                             $"{path} invokes git {forbidden}");

            // refs/heads/ is the designer's own history and nothing here writes into it.
            Assert.DoesNotContain("refs/heads", source, StringComparison.Ordinal);
        }

        // The one permitted spelling is present, so the scan above is not passing by absence.
        Assert.Contains("checkout-index", ReadSource("src/Design/Revision/WorkspaceRestore.cs"),
                        StringComparison.Ordinal);
    }

    // ── Gate 19c: no filesystem watcher was added ────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-7c. <c>workspace-and-project-tree.md</c> §9 defers a watcher deliberately and this brief
    /// does not un-defer it — the channel the application already has is what carries the two
    /// messages.
    /// </summary>
    [Fact]
    public void NoFilesystemWatcherWasAdded()
    {
        string[] paths =
        [
            "src/Design/Revision/WindowChannel.cs",
            "src/Design/Revision/BatchSession.cs",
            "src/Ui/Revision/WorkspaceHistoryService.cs",
            "src/Ui/ViewModels/WorkspaceViewModel.Revision.cs",
            "src/Cli/Serve/HistoryBatch.cs",
        ];

        foreach (string path in paths)
            Assert.DoesNotContain("FileSystemWatcher", StripComments(ReadSource(path)),
                                  StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A workspace with an identity, a repository and circuitRF's policy files.</summary>
    private static GitWorkspace Armed()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.True(armed.Armed);
        return ws;
    }

    private static IDisposable Identity(GitWorkspace ws)
    {
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");
        return new AppDataRootScope();
    }

    private static CheckpointOutcome Take(
        GitWorkspace ws, CheckpointOrigin origin, string? label, bool attended = true)
        => WorkspaceCheckpoints.Take(ws.Git(), origin, label, attended);

    private static (int Code, string Out, string Err) RawIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, o, e);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    /// <summary>A repository file, read from the source tree by walking up to the repo root.</summary>
    internal static string ReadSource(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(relative);
    }

    /// <summary>
    /// Comments off before any source scan — the rule this repository's other source-scan gates
    /// already follow, because a scan that read comments would fail on a note explaining why the
    /// thing it forbids is forbidden.
    /// </summary>
    internal static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*//.*$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        return source;
    }
}
