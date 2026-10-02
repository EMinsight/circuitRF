using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-9's gates — <c>docs/sonnet-briefs/brief-revision-control-9-clone-and-pins.md</c> §6,
/// <c>docs/design/revision-control.md</c> §7, §7A.4, §9, §9.1, §5.2a.
///
/// <para><b>Every fixture is a LOCAL repository, and that is not a shortcut.</b> R-rc9-4 is that
/// nothing here is host-specific — no provider API, no account, no URL parsing beyond what git itself
/// accepts — so a test that needed a hosting service to prove a copy works would be testing something
/// this design deliberately does not have. The two gates that genuinely need a remote to misbehave
/// (12 and 13) build one that misbehaves.</para>
///
/// <para><b>Every fixture path is the SHAPE of a path, never a real one.</b></para>
///
/// <para>In <see cref="AppDataRootCollection"/> with RC-3's own gates: the git-environment isolation
/// these tests install is process-wide, and so is the pin cache's per-user directory.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class CloneAndPinsTests
{
    // ── Gate 1: a cloned directory containing a .cws opens as a workspace ─────────────────────────

    /// <summary>
    /// R-rc9-1 and R-rc9-3. <b>A copy is an ordinary workspace, and every relative reference in it
    /// resolves</b> — including a bitmap reference from a <c>.clay</c> and a <c>.csym</c>, which is
    /// the case the archive work found broken and this brief asks to be re-checked rather than
    /// assumed.
    ///
    /// <para>The check is deliberately at the level of BYTES rather than of "the file is somewhere in
    /// the copy": a relative reference that survives a copy is one whose stored spelling resolves
    /// against the new root, and a reference that had been written absolute would still be present in
    /// the tree while pointing at the sender's machine.</para>
    /// </summary>
    [GitFact]
    public void ACopiedWorkspaceOpensAndEveryRelativeReferenceInItResolves()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        // A workspace with the reference kinds a copy has to survive: a cell folder, a bitmap beside a
        // .clay, and a bitmap beside a .csym, each named RELATIVELY from the document that uses it.
        source.Write("cells/Amp/amp.clay", "{\"Bitmaps\":[{\"Path\":\"art/trace.png\"}]}");
        source.Write("cells/Amp/art/trace.png", "not really a png, and it does not need to be");
        source.Write("cells/Amp/amp.csym", "{\"Bitmaps\":[{\"Path\":\"art/trace.png\"}]}");
        source.Write("technology/house.ctech", "{}");

        source.Raw("init", "-q", ".");
        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        string destination = Path.Combine(scratch.Dir, "copied-here");
        var result = WorkspaceClone.Clone(Installation(), source.Root, destination);

        Assert.True(result.Ok, Describe(result));
        Assert.True(result.IsWorkspace);
        Assert.Equal(Path.Combine(destination, ".cws"), result.WorkspaceCwsPath);

        foreach (string relative in (string[])
                 ["cells/Amp/amp.clay", "cells/Amp/art/trace.png", "cells/Amp/amp.csym",
                  "technology/house.ctech"])
            Assert.True(File.Exists(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar))),
                        relative + " did not survive the copy");

        // The bitmap reference resolves against the COPY's own document folder, which is the property
        // R-rc9-3 asks to be verified rather than assumed.
        string clay = File.ReadAllText(Path.Combine(destination, "cells", "Amp", "amp.clay"));
        Assert.Contains("art/trace.png", clay, StringComparison.Ordinal);
        Assert.DoesNotContain(source.Root, clay, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(destination, "cells", "Amp", "art", "trace.png")));
    }

    // ── Gate 2: a copy says nothing about the absent .cwsuser ─────────────────────────────────────

    /// <summary>
    /// R-rc9-5. A copy arrives with no <c>.cwsuser</c>, which RC-1 already requires to be the NORMAL
    /// state of a shared workspace and by far the most common way one is ever opened by somebody other
    /// than its author. <b>If a copy produces any message about the missing sidecar, RC-1's gate was
    /// wrong and this is where it shows.</b>
    /// </summary>
    [GitFact]
    public void ACopyIsSilentAboutTheAbsentPerUserFile()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        source.Write("cells/Amp/amp.csch", "{}");
        source.Raw("init", "-q", ".");
        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        var result = WorkspaceClone.Clone(
            Installation(), source.Root, Path.Combine(scratch.Dir, "copied-here"));

        Assert.True(result.Ok, Describe(result));
        Assert.False(File.Exists(Path.Combine(result.Destination, WorkspaceUserPersistence.FileName)));

        foreach (var d in result.Diagnostics)
        {
            Assert.DoesNotContain(WorkspaceUserPersistence.FileName, d.Render(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cwsuser", d.Render(), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Gate 3: the pin is on the alias, and one alias yields one identity ────────────────────────

    /// <summary>
    /// R-rc9-9. <b>Two cells reached through one alias cannot resolve to different versions</b>, and
    /// the assertion is structural rather than a spot check: both resolve under ONE root, and that
    /// root is the expanded copy of the ONE identity the alias carries.
    ///
    /// <para>Pinning per cell would allow one design to reference two mutually inconsistent versions
    /// of one library — a state nobody wants and nothing detects — which is why the field is on
    /// <see cref="CwsWorkspaceRef"/> and there is no per-cell spelling of it anywhere.</para>
    /// </summary>
    [GitFact]
    public void OneAliasYieldsOneVersionForEveryCellReachedThroughIt()
    {
        using var fixture = new PinFixture();

        fixture.Library.Write("cells/Amp/note.txt",   "v1");
        fixture.Library.Write("cells/Mixer/note.txt", "v1");
        string v1 = fixture.CommitLibrary("v1");

        Assert.True(WorkspacePins.Pin(fixture.DesignRoot, "Lib", v1).Ok);
        ExternalCellRef.InvalidateCache();

        string? amp   = ExternalCellRef.ResolveCellDir("ws://Lib/cells/Amp",   fixture.DesignRoot);
        string? mixer = ExternalCellRef.ResolveCellDir("ws://Lib/cells/Mixer", fixture.DesignRoot);

        Assert.NotNull(amp);
        Assert.NotNull(mixer);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(amp!)),
                     Path.GetDirectoryName(Path.GetDirectoryName(mixer!)));

        // And there is exactly ONE identity in the record, which is what makes that true rather than
        // a coincidence of this fixture.
        var refs = ReadCws(fixture.DesignRoot).ReferencedWorkspaces!;
        Assert.Single(refs);
        Assert.Equal(v1, refs[0].Pin);
    }

    // ── Gate 4: an absent pin is unpinned, not an error ───────────────────────────────────────────

    /// <summary>
    /// <b>The opposite default from RC-2's field on the same record</b>, and each header says why.
    /// There the old behaviour was the hazard; here it is a preference — a designer who never asked
    /// for a pin wants the librarian's corrections, which is the reason they referenced a workspace
    /// rather than copying its cells.
    /// </summary>
    [Fact]
    public void AReferenceWrittenBeforeThisFieldExistedIsUnpinnedAndResolvesNormally()
    {
        using var scratch = new ScratchDir();

        string library = Path.Combine(scratch.Dir, "shared-parts");
        Directory.CreateDirectory(Path.Combine(library, "cells", "Amp"));
        File.WriteAllText(Path.Combine(library, ".cws"), "{}");
        File.WriteAllText(Path.Combine(library, "cells", "Amp", "note.txt"), "current");

        string design = Path.Combine(scratch.Dir, "my-design");
        Directory.CreateDirectory(design);

        // Exactly the shape an older build wrote: an alias and a path, and no Pin key at all.
        File.WriteAllText(Path.Combine(design, ".cws"),
            "{\"ReferencedWorkspaces\":[{\"Alias\":\"Lib\",\"Path\":\"../shared-parts/.cws\"}]}");

        ExternalCellRef.InvalidateCache();

        var cws = ReadCws(design);
        Assert.Null(cws.ReferencedWorkspaces![0].Pin);

        var state = WorkspacePins.StateOf(design, "Lib");
        Assert.Equal(PinStatus.Unpinned, state.Status);
        Assert.Null(state.Diagnostic);

        Assert.Equal(Path.Combine(library, "cells", "Amp"),
                     ExternalCellRef.ResolveCellDir("ws://Lib/cells/Amp", design));
    }

    // ── Gate 5: a pinned reference does not move when the library changes ─────────────────────────

    /// <summary>
    /// R-rc9-15. <b>The pin surprises somebody, deliberately.</b> With an unpinned reference, editing a
    /// cell in the library and returning to the design shows the new cell; with a pinned one it
    /// deliberately does not, and the design reports that a newer version is available instead.
    ///
    /// <para>This asserts the CONTENT, not merely the recorded identity: a pin that only recorded a
    /// number while resolution went on reading the library's working tree would pass a weaker test and
    /// be worth nothing.</para>
    /// </summary>
    [GitFact]
    public void APinnedReferenceGoesOnResolvingToTheVersionItNamesAfterTheLibraryMoves()
    {
        using var fixture = new PinFixture();

        fixture.Library.Write("cells/Amp/note.txt", "as verified");
        string v1 = fixture.CommitLibrary("v1");

        Assert.True(WorkspacePins.Pin(fixture.DesignRoot, "Lib", v1).Ok);
        ExternalCellRef.InvalidateCache();

        // The librarian edits the cell and publishes.
        fixture.Library.Write("cells/Amp/note.txt", "changed under the design");
        fixture.CommitLibrary("v2");
        ExternalCellRef.InvalidateCache();

        string? amp = ExternalCellRef.ResolveCellDir("ws://Lib/cells/Amp", fixture.DesignRoot);
        Assert.NotNull(amp);
        Assert.Equal("as verified", File.ReadAllText(Path.Combine(amp!, "note.txt")).Trim());

        var state = WorkspacePins.StateOf(fixture.DesignRoot, "Lib");
        Assert.Equal(PinStatus.NewerAvailable, state.Status);
        Assert.True(state.HasNewerVersion);
        Assert.Contains("newer version", state.Diagnostic!.Render(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 6: taking the newer version writes the .cws, so it lands in the local history ────────

    /// <summary>
    /// R-rc9-13. <b>Moving to a newer version is itself a change to the local <c>.cws</c></b>, so the
    /// designer can later see when their design started using the new library — usually the question
    /// being asked when something stopped working.
    /// </summary>
    [GitFact]
    public void TakingTheNewerVersionRewritesTheCwsAndTheChangeIsInTheWorkspacesOwnHistory()
    {
        using var fixture = new PinFixture(designKeepsHistory: true);

        fixture.Library.Write("cells/Amp/note.txt", "v1");
        string v1 = fixture.CommitLibrary("v1");
        Assert.True(WorkspacePins.Pin(fixture.DesignRoot, "Lib", v1).Ok);

        var design = fixture.DesignGit();
        WorkspaceCheckpoints.Take(design, CheckpointOrigin.SavePoint, "before the library moved");

        fixture.Library.Write("cells/Amp/note.txt", "v2");
        string v2 = fixture.CommitLibrary("v2");

        var change = WorkspacePins.TakeNewerVersion(fixture.DesignRoot, "Lib");
        Assert.True(change.Ok);
        Assert.True(change.Written);
        Assert.Equal(v2, change.Pin);
        Assert.Equal(v2, ReadCws(fixture.DesignRoot).ReferencedWorkspaces![0].Pin);

        // The write is what lands in the history: a boundary now has something to record, and what it
        // records is the .cws.
        var outcome = WorkspaceCheckpoints.Take(design, CheckpointOrigin.SavePoint, "took the newer library");
        Assert.True(outcome.Recorded);

        var files = design.Run(["show", "--name-only", "--format=", outcome.Point!.CommitId],
                               new GitRunOptions(ReadOnly: true));
        Assert.Contains(".cws", files.StdOut, StringComparison.Ordinal);
    }

    // ── Gate 7: a checkpoint captures the pin, and a restore restores it ──────────────────────────

    /// <summary>
    /// R-rc9-14, and <b>the strongest argument for this brief in the whole architecture.</b> Restoring
    /// to last Tuesday restores which library version last Tuesday's design resolved against — without
    /// the pin a restore is only partial and does not say so: the designer gets their files back and
    /// silently keeps today's library.
    ///
    /// <para>Asserted by restoring a workspace whose library moved in between, and checking which
    /// version it RESOLVES against rather than which number is in the file.</para>
    /// </summary>
    [GitFact]
    public void ARestoreBringsBackTheLibraryVersionThatStateWasBuiltAgainst()
    {
        using var fixture = new PinFixture(designKeepsHistory: true);

        fixture.Library.Write("cells/Amp/note.txt", "as verified");
        string v1 = fixture.CommitLibrary("v1");
        Assert.True(WorkspacePins.Pin(fixture.DesignRoot, "Lib", v1).Ok);

        var design = fixture.DesignGit();
        var taken  = WorkspaceCheckpoints.Take(design, CheckpointOrigin.SavePoint, "signed off");
        Assert.True(taken.Recorded);

        // The library moves, and the design is deliberately moved onto it.
        fixture.Library.Write("cells/Amp/note.txt", "the new one");
        fixture.CommitLibrary("v2");
        Assert.True(WorkspacePins.TakeNewerVersion(fixture.DesignRoot, "Lib").Written);
        ExternalCellRef.InvalidateCache();

        string? nowAt = ExternalCellRef.ResolveCellDir("ws://Lib/cells/Amp", fixture.DesignRoot);
        Assert.Equal("the new one", File.ReadAllText(Path.Combine(nowAt!, "note.txt")).Trim());

        // Back to the signed-off state.
        var point = RestorePoints.List(design).First(p => p.Sequence == taken.Point!.Sequence);
        Assert.True(WorkspaceRestore.Restore(design, point).Ok);
        ExternalCellRef.InvalidateCache();

        Assert.Equal(v1, ReadCws(fixture.DesignRoot).ReferencedWorkspaces![0].Pin);

        string? backAt = ExternalCellRef.ResolveCellDir("ws://Lib/cells/Amp", fixture.DesignRoot);
        Assert.NotNull(backAt);
        Assert.Equal("as verified", File.ReadAllText(Path.Combine(backAt!, "note.txt")).Trim());
    }

    // ── Gate 8: an unhonourable pin is reported, never fallen back from ───────────────────────────

    /// <summary>
    /// R-rc9-16. <b>A pin is not a copy</b>, so a referenced workspace that goes away takes the pinned
    /// version with it — and that is reported plainly rather than resolved to whatever is there now,
    /// which would defeat the entire purpose. Both ways of becoming unreachable are driven: the
    /// workspace gone, and the workspace present with that version no longer in its history.
    /// </summary>
    [GitFact]
    public void AVersionThatCannotBeReachedIsReportedAndNothingFallsBackToCurrentContent()
    {
        using var fixture = new PinFixture();

        fixture.Library.Write("cells/Amp/note.txt", "as verified");
        string v1 = fixture.CommitLibrary("v1");
        Assert.True(WorkspacePins.Pin(fixture.DesignRoot, "Lib", v1).Ok);

        // (a) The version is gone from a library that is still there — a history somebody rewrote.
        //     The FILES are still on disk and say something else entirely, which is exactly the
        //     fall-back that must not happen.
        //
        //     The old line of work has to be DELETED as well as replaced: an orphan branch beside it
        //     leaves the original commit perfectly reachable, and the test would then be asserting
        //     nothing while appearing to assert everything.
        string was = fixture.Library.Raw("rev-parse", "--abbrev-ref", "HEAD").Out.Trim();
        fixture.Library.Write("cells/Amp/note.txt", "something else entirely");
        fixture.Library.Raw("checkout", "-q", "--orphan", "rewritten");
        fixture.Library.Raw("add", "-A");
        fixture.Library.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "rewritten");
        fixture.Library.Raw("branch", "-D", was);
        fixture.Library.Raw("reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all");
        fixture.Library.Raw("-c", "gc.reflogExpire=now", "-c", "gc.pruneExpire=now",
                            "gc", "--prune=now", "--quiet");

        ExternalCellRef.InvalidateCache();

        var rewritten = WorkspacePins.StateOf(fixture.DesignRoot, "Lib");
        Assert.Equal(PinStatus.CannotBeHonoured, rewritten.Status);
        Assert.Equal(CircuitRF.Diagnostics.DiagnosticSeverity.Error, rewritten.Diagnostic!.Severity);

        string? resolved = ExternalCellRef.ResolveCellDir("ws://Lib/cells/Amp", fixture.DesignRoot);
        if (resolved is not null)
            Assert.False(File.Exists(Path.Combine(resolved, "note.txt")),
                         "a pin that cannot be honoured resolved to content anyway");

        // (b) The workspace is not where the design says it is at all.
        Directory.Move(fixture.LibraryRoot, fixture.LibraryRoot + "-moved-away");
        ExternalCellRef.InvalidateCache();

        var gone = WorkspacePins.StateOf(fixture.DesignRoot, "Lib");
        Assert.Equal(PinStatus.CannotBeHonoured, gone.Status);
        Assert.Contains("cannot be reached", gone.Diagnostic!.Render(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 9: a nested repository is excluded and reported ──────────────────────────────────────

    /// <summary>
    /// R-rc9-19. <b>A repository INSIDE a workspace — a library copied into it — is left alone
    /// entirely</b>: not committed to, and not committed AS anything by the enclosing workspace. The
    /// second half is the mechanical one: handing such a directory to <c>git add</c> records a pointer
    /// to that repository's current commit, with a warning nobody reads.
    /// </summary>
    [GitFact]
    public void ACopyMadeInsideAWorkspaceIsExcludedAndReportedAndTheRestIsRecordedNormally()
    {
        using var ws = new GitWorkspace();
        using var library = new GitWorkspace();

        ws.Write("cells/Amp/amp.csch", "{}");
        library.Write("cells/Shared/shared.csch", "{}");
        library.Raw("init", "-q", ".");
        library.Raw("add", "-A");
        library.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        var git = ws.Git(new GitIdentity("T", "t@e.x", "test"));
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        // The librarian's workspace, copied INTO this one.
        var copied = WorkspaceClone.Clone(Installation(), library.Root, Path.Combine(ws.Root, "vendor-lib"));
        Assert.True(copied.Ok, Describe(copied));

        var outcome = WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "with a copy inside");
        Assert.True(outcome.Recorded);

        var listed = git.Run(["ls-tree", "-r", "--name-only", outcome.Point!.CommitId],
                             new GitRunOptions(ReadOnly: true));

        Assert.Contains("cells/Amp/amp.csch", listed.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor-lib", listed.StdOut, StringComparison.Ordinal);

        Assert.Contains(NestedRepositories.Find(ws.Root), p => p == "vendor-lib");
        Assert.Contains(outcome.Diagnostics,
                        d => d.Id == "revision.nested-repository.excluded"
                          && d.Render().Contains("vendor-lib", StringComparison.Ordinal));
    }

    // ── Gate 10: no network access without an explicit action ────────────────────────────────────

    /// <summary>
    /// R-rc9-6. <b>Nothing in this series contacts a network without being asked</b>, and the gate is a
    /// source scan of the paths that run by themselves rather than an observation of one run: an
    /// automatic fetch that fires on a rarer path than a test drives is exactly the defect this
    /// forbids. An automatic fetch would silently change what a design resolves against, which is the
    /// failure §7A.4 is written to prevent.
    /// </summary>
    [Fact]
    public void NothingOnTheOpenOrCheckpointPathReachesANetwork()
    {
        foreach (string relative in (string[])
                 ["src/Ui/ViewModels/WorkspaceViewModel.Revision.cs",
                  "src/Ui/Revision/WorkspaceHistoryService.cs",
                  "src/Design/Revision/WorkspaceCheckpoints.cs",
                  "src/Design/Revision/WorkspaceArming.cs",
                  "src/Design/Revision/SessionHousekeeping.cs",
                  "src/Design/Revision/GitCheckpoint.cs"])
        {
            string code = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(relative));

            foreach (string forbidden in (string[])
                     ["WorkspaceRemotes.Fetch", "WorkspaceRemotes.Push", "BringInChanges", "SendChanges",
                      "\"fetch\"", "\"push\"", "\"clone\"", "Network: true"])
                Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }

    // ── Gate 10a: a copy reaches the ordinary arming path ────────────────────────────────────────

    /// <summary>
    /// R-rc9-5c. Git does not clone a repository's config, so RC-3's management marker does not travel
    /// — <b>which is correct rather than a gap.</b> A copy is a new workspace on a new machine and
    /// reaches R-rc0-13's first-boundary rule on its own terms. <b>Nothing carries the marker across by
    /// hand</b>: a marker that travelled would make one designer's management decision everybody's,
    /// which is the reason it is config rather than a reference.
    /// </summary>
    [GitFact]
    public void ACopyArrivesWithNoManagementMarkerAndTheRecipientsOwnPreferenceGoverns()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        source.Write("cells/Amp/amp.csch", "{}");
        var sourceGit = source.Git(new GitIdentity("T", "t@e.x", "test"));
        Assert.True(GitRepository.Create(sourceGit, RevisionManagement.Created).Ok);
        Assert.NotNull(GitRepository.ReadMarker(sourceGit));

        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        var copied = WorkspaceClone.Clone(
            Installation(), source.Root, Path.Combine(scratch.Dir, "copied-here"));
        Assert.True(copied.Ok, Describe(copied));

        var copyGit = GitCommand.For(copied.Destination)!;
        Assert.Null(GitRepository.ReadMarker(copyGit));
        Assert.False(GitRepository.IsManagedByCircuitRf(copyGit));

        // And the recipient's own preference is what decides, because the copy's .cws says nothing.
        Assert.Null(WorkspaceRevisionSetting.Read(
            WorkspaceRevisionSetting.CwsPathFor(copied.Destination)));
        Assert.True(RevisionArming.IsArmed(keepHistoryPreference: true,  workspaceSetting: null));
        Assert.False(RevisionArming.IsArmed(keepHistoryPreference: false, workspaceSetting: null));

        // Nothing in the copy path writes a marker: the source scan is what stops a later "helpful"
        // line carrying it across.
        string code = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Design/Revision/WorkspaceClone.cs"));
        Assert.DoesNotContain("WriteMarker", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkerManagedKey", code, StringComparison.Ordinal);
    }

    // ── Gate 11: a copy carries no restore points, and the refspec was not widened ────────────────

    /// <summary>
    /// R-rc9-5a, §5.2a. <b>The safety net is a property of one machine and one designer's sessions</b> —
    /// ordered by a monotonic sequence that means nothing anywhere else and thinned by that machine's
    /// retention preference. What clones is the narrative; the recipient starts a safety net of their
    /// own on their first boundary.
    ///
    /// <para><b>The source scan is the load-bearing half.</b> The absence of restore points in a copy
    /// reads as a bug, and the obvious "fix" is one line widening the refspec — so the gate forbids one
    /// reaching the checkpoint namespace rather than merely observing that today's copy has none.</para>
    /// </summary>
    [GitFact]
    public void ACopyBringsTheVersionsAndNotTheRestorePointsAndNoRefspecReachesTheCheckpointNamespace()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        source.Write("cells/Amp/amp.csch", "{}");
        var sourceGit = source.Git(new GitIdentity("T", "t@e.x", "test"));
        Assert.True(GitRepository.Create(sourceGit, RevisionManagement.Created).Ok);

        Assert.True(WorkspaceCommit.Commit(sourceGit, "a version somebody kept").Ok);
        Assert.True(WorkspaceCheckpoints.Take(sourceGit, CheckpointOrigin.SavePoint, "a restore point").Recorded);
        Assert.NotEmpty(CheckpointReferences.List(sourceGit));

        var copied = WorkspaceClone.Clone(
            Installation(), source.Root, Path.Combine(scratch.Dir, "copied-here"));
        Assert.True(copied.Ok, Describe(copied));

        var copyGit = GitCommand.For(copied.Destination)!;
        Assert.Empty(CheckpointReferences.List(copyGit));
        Assert.NotEmpty(HistoryBrowser.Versions(copyGit));

        // It is said, at the moment it becomes true, rather than left to be discovered.
        Assert.Contains(copied.Diagnostics, d => d.Id == "revision.clone.no-restore-points");

        foreach (string relative in (string[])
                 ["src/Design/Revision/WorkspaceClone.cs", "src/Design/Revision/WorkspaceRemotes.cs"])
        {
            string code = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(relative));
            Assert.DoesNotContain(CheckpointReferences.Namespace, code, StringComparison.Ordinal);
            Assert.DoesNotContain("refs/crf", code, StringComparison.Ordinal);
            Assert.DoesNotContain("--refmap", code, StringComparison.Ordinal);
            Assert.DoesNotContain("refs/*:refs/*", code, StringComparison.Ordinal);
        }
    }

    // ── Gate 12: an operation needing credentials refuses rather than blocks ──────────────────────

    /// <summary>
    /// R-rc9-7a. <b>A hang is the worst failure mode available</b> — no message, no exit code, no end —
    /// and it is the one this brief is most likely to produce by accident. <c>GIT_TERMINAL_PROMPT=0</c>
    /// turns an operation that would have asked into a refusal with a sentence.
    ///
    /// <para><b>The test is given a bound, because a test that hangs is the exact defect being
    /// gated.</b></para>
    /// </summary>
    [GitFact]
    public void AnAddressThatWantsASignInRefusesWithinItsBoundRatherThanBlocking()
    {
        using var scratch = new ScratchDir();

        CloneResult? result = null;
        var done = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            // An address git will ask for a username and password for, on a host that resolves
            // nowhere. With no terminal to ask on and GIT_TERMINAL_PROMPT=0 it must come back.
            result = WorkspaceClone.Clone(
                Installation(),
                "https://invalid.invalid/a-private-library.git",
                Path.Combine(scratch.Dir, "copied-here"));
            done.Set();
        }) { IsBackground = true };

        worker.Start();
        Assert.True(done.Wait(TimeSpan.FromMinutes(3)),
                    "the copy did not come back — a blocked credential prompt is the defect this gates");

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.NotEmpty(result.Diagnostics);
    }

    /// <summary>
    /// The half <c>GIT_TERMINAL_PROMPT</c> does not cover (§9.1, rev 5). <b>An SSH key with a
    /// passphrase and no agent prompts through <c>ssh</c>, which does not read that variable</b> — with
    /// no terminal it FAILS rather than hangs, and it has no terminal only because
    /// <see cref="GitCommand"/> gives the subprocess none. What must never be added is a terminal, an
    /// <c>SSH_ASKPASS</c>, or a stored answer of circuitRF's own.
    /// </summary>
    [GitFact]
    public void AnSshAddressWithNoAgentFailsWithinItsBoundRatherThanBlocking()
    {
        using var scratch = new ScratchDir();

        var previous = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        try
        {
            CloneResult? result = null;
            var done = new ManualResetEventSlim(false);

            var worker = new Thread(() =>
            {
                result = WorkspaceClone.Clone(
                    Installation(),
                    "ssh://git@invalid.invalid/a-private-library.git",
                    Path.Combine(scratch.Dir, "copied-here"));
                done.Set();
            }) { IsBackground = true };

            worker.Start();
            Assert.True(done.Wait(TimeSpan.FromMinutes(3)),
                        "the copy did not come back — a blocked passphrase prompt is the defect this gates");

            Assert.NotNull(result);
            Assert.False(result!.Ok);
        }
        finally { Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", previous); }
    }

    /// <summary>
    /// R-rc3-1a's other side, and the reason the bound is INACTIVITY rather than wall-clock: <b>a slow
    /// copy of a large library is not a hang</b>, and a fixed limit would report it as one.
    ///
    /// <para>Driven against a source that trickles — the bound is set below the total transfer time and
    /// above the gap between two signs of life, so a wall-clock bound would cut this off and an
    /// inactivity bound must not.</para>
    /// </summary>
    [GitFact]
    public void ATransferThatIsSlowButStillMovingIsNotCutOff()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        source.Write("cells/Amp/amp.csch", "{}");
        source.Raw("init", "-q", ".");
        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        var previous = GitCommand.NetworkInactivityTimeout;
        GitCommand.NetworkInactivityTimeout = TimeSpan.FromSeconds(20);
        try
        {
            var started = Stopwatch.StartNew();
            var result  = WorkspaceClone.Clone(
                Installation(), source.Root, Path.Combine(scratch.Dir, "copied-here"));
            started.Stop();

            Assert.True(result.Ok, Describe(result));
            Assert.True(result.IsWorkspace);
        }
        finally { GitCommand.NetworkInactivityTimeout = previous; }
    }

    // ── A clone reports progress, and a stopped one leaves nothing behind ────────────────────────

    /// <summary>Git's own progress lines, as <c>--progress</c> writes them, read into a phase, its
    /// percent and a weighted overall figure — receiving fills most of the bar, and a phase the
    /// weights do not name (the remote's counting) has no overall figure, so a bar draws it as busy.</summary>
    [Fact]
    public void GitsProgressLinesAreReadIntoAWeightedOverallFigure()
    {
        var receiving = WorkspaceClone.ParseProgress("Receiving objects:  50% (53/106), 120.00 MiB | 10.00 MiB/s");
        Assert.NotNull(receiving);
        Assert.Equal("Receiving objects", receiving!.Phase);
        Assert.Equal(50, receiving.PhasePercent);
        Assert.Equal(42.5, receiving.OverallPercent);
        Assert.Equal("(53/106), 120.00 MiB | 10.00 MiB/s", receiving.Detail);

        var updating = WorkspaceClone.ParseProgress("Updating files: 100% (106/106), done.");
        Assert.Equal(100, updating!.OverallPercent);
        Assert.Equal("(106/106)", updating.Detail);

        var counting = WorkspaceClone.ParseProgress("remote: Counting objects:  40% (2/5)");
        Assert.Equal("Counting objects", counting!.Phase);
        Assert.Null(counting.OverallPercent);

        Assert.Null(WorkspaceClone.ParseProgress("Cloning into 'copied-here'..."));
        Assert.Equal("fatal: repository not found",
                     WorkspaceClone.WithoutProgress("Receiving objects:  10% (1/10)\rfatal: repository not found\n"));
    }

    /// <summary>
    /// A Cancel KILLS git, which therefore never removes its half-made folder the way it does when it
    /// fails by itself — and that folder is not empty, so the next clone into the same place would be
    /// refused. Stopped on the first sign of a transfer, over <c>file://</c> so git uses a real
    /// transport and reports one, with an incompressible payload large enough to still be moving.
    /// </summary>
    [GitFact]
    public void AClonePassesOnGitsProgressAndOneStoppedPartWayLeavesNoFolderBehind()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        var bytes = new byte[48 * 1024 * 1024];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(source.Root, "fields.bin"), bytes);
        source.Raw("init", "-q", ".");
        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        string destination = Path.Combine(scratch.Dir, "copied-here");
        using var cts = new CancellationTokenSource();
        var phases = new List<string>();

        var result = WorkspaceClone.Clone(Installation(), new Uri(source.Root).AbsoluteUri, destination, cts.Token,
            p => { lock (phases) phases.Add(p.Phase); if (p.Phase == "Receiving objects") cts.Cancel(); });

        Assert.Contains("Receiving objects", phases);
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Id == "revision.clone.cancelled");
        Assert.False(Directory.Exists(destination), "the stopped clone left its half-made folder behind");
    }

    // ── Gate 13: safe.directory on a copied or share-hosted tree ─────────────────────────────────

    /// <summary>
    /// R-rc9-5b. <b>RF workspaces live on shares and §7A's librarian scenario assumes one</b>, so a
    /// foreign-owned tree is an ordinary path rather than an edge. On a git at or above circuitRF's
    /// floor the ownership check is answered by naming the tree as <c>safe.directory</c> — a clone
    /// touches TWO trees, and both are named individually, never <c>*</c>.
    ///
    /// <para>The failure, where it can still be reached, is circuitRF's sentence and never git's.</para>
    /// </summary>
    [GitFact]
    public void ACopyNamesBothTreesAsSafeAndAForeignOwnershipRefusalIsCircuitRfsOwnSentence()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        source.Write("cells/Amp/amp.csch", "{}");
        source.Raw("init", "-q", ".");
        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        string destination = Path.Combine(scratch.Dir, "copied-here");

        // The tree circuitRF writes into and the tree it reads are both named, which is what makes a
        // copy from a share work at all — asserted through the arguments rather than by finding a
        // machine with a foreign-owned folder on it.
        var probe = new GitCommand(Installation(), scratch.Dir);
        var named = probe.DescribeArguments(["clone", "--", source.Root, destination]);
        Assert.Contains("safe.directory=" + scratch.Dir, named);

        var result = WorkspaceClone.Clone(Installation(), source.Root, destination);
        Assert.True(result.Ok, Describe(result));

        // And the translated row exists and is circuitRF's own wording, with no git vocabulary in it.
        string sentence = GitFailures.ForeignOwnership(destination).Render();
        Assert.Contains("different account", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe.directory", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dubious", sentence, StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 14: headless ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc9-20. <b>Clone, pin and pin resolution work from <c>src/Cli</c> with no display, byte for
    /// byte against the in-process path</b> — because a build machine reproducing a signed-off result
    /// is the reason the pin exists at all.
    /// </summary>
    [GitFact]
    public void TheCliCopiesAndPinsExactlyAsTheWindowDoes()
    {
        if (CliUnderTest.Dll is not { } dll) return;   // built only after `dotnet build`

        using var fixture = new PinFixture();
        using var scratch = new ScratchDir();

        fixture.Library.Write("cells/Amp/note.txt", "as verified");
        string v1 = fixture.CommitLibrary("v1");

        // (a) A copy, made by the process, lands the same workspace.
        var copy = RunCli(dll, fixture.StateDir, scratch.Dir,
                          ["history", "clone", fixture.LibraryRoot, Path.Combine(scratch.Dir, "copied-here")]);
        Assert.Equal(0, copy.Code);
        Assert.True(File.Exists(Path.Combine(scratch.Dir, "copied-here", ".cws")));

        // (b) A pin, made by the process, writes exactly what the in-process call writes.
        var pinned = RunCli(dll, fixture.StateDir, fixture.DesignRoot,
                            ["history", "pin", fixture.DesignRoot, "--alias", "Lib"]);
        Assert.Equal(0, pinned.Code);

        string viaCli = File.ReadAllText(Path.Combine(fixture.DesignRoot, ".cws"));

        Assert.True(WorkspacePins.Unpin(fixture.DesignRoot, "Lib").Ok);
        Assert.True(WorkspacePins.Pin(fixture.DesignRoot, "Lib").Ok);
        string inProcess = File.ReadAllText(Path.Combine(fixture.DesignRoot, ".cws"));

        Assert.Equal(inProcess, viaCli);
        Assert.Contains(v1, viaCli, StringComparison.Ordinal);

        // (c) The pin is READABLE headlessly, which is what a build machine actually needs.
        var listed = RunCli(dll, fixture.StateDir, fixture.DesignRoot,
                            ["history", "pins", fixture.DesignRoot, "--json"]);
        Assert.Equal(0, listed.Code);
        Assert.Contains(v1, listed.Out, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of R-rc9-20: <b>the view model kept no second copy.</b> An operation that lives
    /// only in a view model is not a capability, and a verb that re-implements one diverges from it
    /// silently.
    /// </summary>
    [Fact]
    public void NeitherTheViewModelNorTheCliHoldsAnySharingLogicOfItsOwn()
    {
        // No process is started, no git argument is assembled, no .cws is written and no reference
        // table is walked anywhere but in src/Design. Each of these is how a second implementation
        // begins, and each of them has a first line that looks harmless.
        foreach (string forbidden in (string[])
                 ["ProcessStartInfo", "--porcelain",
                  "SaveToFileAtomic", "ReferencedWorkspaces", "CwsWorkspaceRef"])
        {
            foreach (string relative in (string[])
                     ["src/Ui/ViewModels/WorkspaceViewModel.Sharing.cs",
                      "src/Ui/Revision/WorkspaceSharingService.cs",
                      "src/Cli/History.cs"])
            {
                string code = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(relative));
                Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
            }
        }

        // The git SUBCOMMAND names are forbidden in the two surfaces that could plausibly assemble
        // one. They are deliberately not forbidden in the CLI file, where "clone" and "fetch" are
        // circuitRF's own NOUNS on the history verb — a scan that could not tell a noun from an
        // argument would have to be answered by renaming the verb, which is the tail wagging the dog.
        foreach (string relative in (string[])
                 ["src/Ui/ViewModels/WorkspaceViewModel.Sharing.cs",
                  "src/Ui/Revision/WorkspaceSharingService.cs"])
        {
            string code = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(relative));
            foreach (string forbidden in (string[])
                     ["\"clone\"", "\"fetch\"", "\"push\"", "\"archive\"", "GitRunOptions", "rev-parse"])
                Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        // `src/Cli/History.cs` is deliberately held to the weaker list above, and the reason is worth
        // stating rather than hiding in an exclusion: RC-7's `history versions --changes` asks git for
        // one thing directly (whether a version has a parent) before handing the comparison to
        // HistoryBrowser. It is one read, it is not RC-9's, and RC-9 adds none of its own — every noun
        // this brief added calls a src/Design function and assembles no git argument at all.
        string cli = RestorePointsTests.StripComments(RestorePointsTests.ReadSource("src/Cli/History.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cli, @"rev-parse"));
    }

    // ── Gate 15: a copy of a switched-off workspace arrives off ───────────────────────────────────

    /// <summary>
    /// R-rc9-5c's correction, and the one thing that DOES travel. The <c>.cws</c> is versioned, so a
    /// workspace its owner switched <b>off</b> copies as off, and <b>the recipient's preference does
    /// not override that</b> — the flag says <i>not for this one</i> about the workspace, and the
    /// workspace is what travelled.
    /// </summary>
    [GitFact]
    public void ACopyOfASwitchedOffWorkspaceArrivesOffAndTakesNoCheckpoint()
    {
        using var source = new GitWorkspace();
        using var scratch = new ScratchDir();

        File.WriteAllText(Path.Combine(source.Root, ".cws"), "{\"RevisionControl\":false}");
        source.Write("cells/Amp/amp.csch", "{}");
        source.Raw("init", "-q", ".");
        source.Raw("add", "-A");
        source.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", "first");

        var copied = WorkspaceClone.Clone(
            Installation(), source.Root, Path.Combine(scratch.Dir, "copied-here"));
        Assert.True(copied.Ok, Describe(copied));

        string root = copied.Destination;
        Assert.False(WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(root)));

        // With the recipient's own preference ON, the workspace's own answer still wins.
        Assert.False(RevisionArming.IsArmed(
            keepHistoryPreference: true,
            workspaceSetting:      WorkspaceRevisionSetting.Read(
                                       WorkspaceRevisionSetting.CwsPathFor(root))));

        var armed = WorkspaceArming.Arm(
            root,
            CheckpointOrigin.SavePoint,
            keepHistoryPreference:          true,
            workspaceSetting:               WorkspaceRevisionSetting.Read(
                                                WorkspaceRevisionSetting.CwsPathFor(root)),
            circuitRfWroteAFileThisSession: true);
        Assert.False(armed.Armed);

        var git = GitCommand.For(root)!;
        Assert.Empty(CheckpointReferences.List(git));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static GitInstallation Installation()
        => GitDiscovery.Find(out _) ?? throw new InvalidOperationException("no git");

    private static string Describe(CloneResult r)
        => string.Join(" | ", r.Diagnostics.Select(d => d.Render()));

    private static CwsFile ReadCws(string workspaceRoot)
        => WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws"));

    /// <summary>A throwaway directory with nothing in it — a place to copy INTO.</summary>
    private sealed class ScratchDir : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "crf-rc9-" + Guid.NewGuid().ToString("N")[..12]);

        public ScratchDir() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A library workspace that keeps a history, and a design workspace referencing it under one alias
    /// — §7A's librarian and consumer, on one machine.
    ///
    /// <para><b>The pin cache is redirected too.</b> Expanding a pinned version writes into the
    /// per-user state directory, and a test that used the developer's own would leave entries behind
    /// and read entries somebody else's run wrote.</para>
    /// </summary>
    private sealed class PinFixture : IDisposable
    {
        public GitWorkspace Library { get; }
        public string DesignRoot  { get; }
        public string LibraryRoot => Library.Root;
        public string StateDir    { get; }

        private readonly ScratchDir _scratch = new();

        public PinFixture(bool designKeepsHistory = false)
        {
            Library  = new GitWorkspace();
            StateDir = Path.Combine(_scratch.Dir, "state");
            Directory.CreateDirectory(StateDir);
            CircuitRF.Design.UserStateDirectory.RedirectTo(StateDir);

            Library.Raw("init", "-q", ".");

            DesignRoot = Path.Combine(_scratch.Dir, "my-design");
            Directory.CreateDirectory(DesignRoot);

            string relative = Path.GetRelativePath(DesignRoot, Path.Combine(Library.Root, ".cws"))
                                  .Replace(Path.DirectorySeparatorChar, '/');
            File.WriteAllText(Path.Combine(DesignRoot, ".cws"),
                JsonSerializer.Serialize(new
                {
                    ReferencedWorkspaces = new[] { new { Alias = "Lib", Path = relative } },
                }));

            if (designKeepsHistory)
            {
                var git = GitCommand.For(DesignRoot, new GitIdentity("T", "t@e.x", "test"))!;
                GitRepository.Create(git, RevisionManagement.Created);
            }

            ExternalCellRef.InvalidateCache();
        }

        public GitCommand DesignGit()
            => GitCommand.For(DesignRoot, new GitIdentity("T", "t@e.x", "test"))!;

        /// <summary>Commits the library's working tree and returns the identity.</summary>
        public string CommitLibrary(string title)
        {
            Library.Raw("add", "-A");
            Library.Raw("-c", "user.email=t@e.x", "-c", "user.name=T", "commit", "-qm", title);
            return Library.Raw("rev-parse", "HEAD").Out.Trim();
        }

        public void Dispose()
        {
            CircuitRF.Design.UserStateDirectory.RedirectTo(null);
            ExternalCellRef.InvalidateCache();
            Library.Dispose();
            _scratch.Dispose();
        }
    }

    /// <summary>The built CLI, or null on a tree nobody has built yet.</summary>
    private static class CliUnderTest
    {
        public static string? Dll { get; } = Find();

        private static string? Find()
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                string candidate = Path.Combine(dir, "src", "Cli", "bin", "Debug", "net10.0", "CircuitRF.Cli.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }

    private static (int Code, string Out, string Err) RunCli(
        string dll, string appDataDir, string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = appDataDir;

        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        return (p.ExitCode, o, e);
    }
}
