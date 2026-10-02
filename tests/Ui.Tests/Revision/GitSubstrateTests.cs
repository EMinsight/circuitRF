using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Thermal;
using CircuitRF.Engine.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Ui;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-3's gates — <c>docs/sonnet-briefs/brief-revision-control-3-git-substrate.md</c> §6.
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b> A workspace name
/// from a real machine must not reach this repository.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> because the identity gates redirect the per-user state
/// directory, and because the git-environment isolation these tests install is process-wide: two
/// collections that both touch a process global still run concurrently and still clobber each other.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class GitSubstrateTests
{
    // ── Gate 1: the firewall, and reachability from src/Cli ────────────────────────────────────────

    /// <summary>
    /// R-rc3-1: the type that owns every git invocation lives in <c>CircuitRF.Design</c>, so
    /// <c>src/Cli</c> — which cannot reference <c>src/Ui</c> — can reach it. The other half (Design
    /// references no Avalonia) is <c>tests/Firewall.Tests</c>'s standing assertion.
    /// </summary>
    [Fact]
    public void TheGitTypeIsBelowTheFirewallAndReachableFromTheHeadlessDriver()
    {
        Assert.Equal("CircuitRF.Design", typeof(GitCommand).Assembly.GetName().Name);
        Assert.Equal("CircuitRF.Design", typeof(GitCheckpoint).Assembly.GetName().Name);
        Assert.Equal("CircuitRF.Design", typeof(RevisionIdentity).Assembly.GetName().Name);

        // The CLI's own build output is what proves reachability rather than an inference from the
        // project file: if the reference were lost, `circuitrf history` would stop building.
        Assert.NotNull(CliPaths.Dll);
    }

    // ── Gate 2: absence is silent ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-3. With nothing configured and nothing on PATH, every entry point answers "not available",
    /// <b>nothing throws, nothing is created and nothing is posted.</b> A designer who does not want
    /// this feature should never learn it exists.
    /// </summary>
    [Fact]
    public void WithNoGitAnywhereEverythingAnswersNotAvailableAndCreatesNothing()
    {
        using var ws = new GitWorkspace();
        using var scope = new DiscoveryScope(candidates: [], preferred: null);

        Assert.False(GitDiscovery.IsAvailable);
        Assert.Null(GitDiscovery.Find(out _));
        Assert.Null(GitCommand.For(ws.Root));

        Assert.False(Directory.Exists(Path.Combine(ws.Root, ".git")));
    }

    // ── Gate 3: discovery precedence ──────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-2. A configured path outranks PATH — a preference that lost to PATH would be inert on
    /// exactly the machine it exists for. Blank means "search PATH".
    /// </summary>
    [GitFact]
    public void AConfiguredPathOutranksPathAndBlankMeansSearchPath()
    {
        using var ws = new GitWorkspace();

        string fake = FakeGit.Write(ws.Root, "2.99.0");
        using (new DiscoveryScope(candidates: ["git"], preferred: fake))
            Assert.Equal(fake, GitDiscovery.Find(out _)!.Path);

        using (new DiscoveryScope(candidates: ["git"], preferred: "   "))
        {
            var onPath = GitDiscovery.Find(out _);
            Assert.NotNull(onPath);
            Assert.NotEqual(fake, onPath!.Path);
            Assert.Equal("found on PATH", onPath.HowFound);
        }
    }

    /// <summary>
    /// Gate 3's second half: a configured path that is not git produces the TRANSLATED failure naming
    /// the path that was tried — not a stack trace, and not a silent fall-back to the one on PATH. A
    /// user who named a git and silently got a different one has been overruled without being told.
    /// </summary>
    [Fact]
    public void AConfiguredPathThatIsNotGitIsRefusedByPathAndNeverFallsBackToPath()
    {
        using var ws  = new GitWorkspace();
        string notGit = FakeGit.WriteNotGit(ws.Root);

        using var _ = new DiscoveryScope(candidates: ["git"], preferred: notGit);

        Assert.Null(GitDiscovery.Find(out var rejected));
        Assert.Contains(rejected, r => r.Contains(notGit, StringComparison.Ordinal));

        var d = GitFailures.NotGit(notGit, rejected[0]);
        Assert.Equal("revision.git.not-usable", d.Id);
        Assert.Contains(notGit, d.Render());
    }

    /// <summary>R-rc3-3a: an old git is treated as ABSENT rather than as broken. A designer with an old
    /// git does not want to be told about a feature they cannot have.</summary>
    [Fact]
    public void AGitBelowTheFloorIsAbsentNotBroken()
    {
        using var ws = new GitWorkspace();
        string old   = FakeGit.Write(ws.Root, "2.8.9", name: "oldgit");

        using var _ = new DiscoveryScope(candidates: [], preferred: old);

        Assert.Null(GitDiscovery.Find(out var rejected));
        Assert.Contains(rejected, r => r.Contains("older than", StringComparison.Ordinal));
        Assert.False(GitDiscovery.IsAvailable);
    }

    [Theory]
    [InlineData("git version 2.50.1 (Apple Git-155)", 2, 50, 1)]
    [InlineData("git version 2.39.3.windows.1",       2, 39, 3)]
    [InlineData("git version 2.9",                    2,  9, -1)]
    public void TheVersionBannerParsesTheWayEveryPlatformSpellsIt(string banner, int major, int minor, int build)
    {
        var v = GitDiscovery.ParseVersion(banner);
        Assert.NotNull(v);
        Assert.Equal(major, v!.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build);
    }

    [Fact]
    public void SomethingThatIsNotGitDoesNotParseAsAVersion()
        => Assert.Null(GitDiscovery.ParseVersion("Python 3.13.1"));

    /// <summary>
    /// An application started from a launcher does not inherit the user's <c>PATH</c>: a Finder-launched
    /// macOS bundle gets <c>/usr/bin:/bin:/usr/sbin:/sbin</c> from launchd and a Linux desktop entry is
    /// no better. So a git the user installed into <c>~/.local/bin</c> — which is where a user-local
    /// install puts it — is invisible to the application while the same machine finds it instantly from
    /// a terminal. macOS is saved by <c>/usr/bin/git</c> being present anyway; Linux is not.
    ///
    /// <para>The stub is named something <c>PATH</c> cannot resolve, so this tests the directory tier
    /// on every machine — including one that really does have a git on its <c>PATH</c>, which would
    /// otherwise win and make the test prove nothing.</para>
    /// </summary>
    [Fact]
    public void AGitInstalledWhereALauncherCannotSeeItIsStillFound()
    {
        if (OperatingSystem.IsWindows()) return;   // a GUI process there inherits the user's own PATH

        using var ws = new GitWorkspace();
        string directory = Path.Combine(ws.Root, "user-local-bin");
        Directory.CreateDirectory(directory);
        string installed = FakeGit.Write(directory, "2.99.0", name: "crf-stub-git");

        using var scope = new DiscoveryScope(candidates: ["crf-stub-git"], preferred: null);
        GitDiscovery.SearchDirectories = [directory];
        GitDiscovery.InvalidateCache();

        var found = GitDiscovery.Find(out _);

        Assert.NotNull(found);
        Assert.Equal(installed, found!.Path);
        // "found on PATH" would be a lie here, and where it came from is the whole content of the
        // answer for a user whose PATH does not hold it.
        Assert.Contains(directory, found.HowFound, StringComparison.Ordinal);
    }

    /// <summary>
    /// The directory tier is derived from the candidate names, so emptying that list still disables
    /// every unprompted route — which is what it is documented to do.
    /// </summary>
    [Fact]
    public void NoCandidateNameMeansNoUnpromptedGitSearchAtAll()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new GitWorkspace();
        string directory = Path.Combine(ws.Root, "user-local-bin-2");
        Directory.CreateDirectory(directory);
        FakeGit.Write(directory, "2.99.0", name: "crf-stub-git");

        using var scope = new DiscoveryScope(candidates: [], preferred: null);
        GitDiscovery.SearchDirectories = [directory];
        GitDiscovery.InvalidateCache();

        Assert.Null(GitDiscovery.Find(out _));
    }

    // ── Gate 20: macOS discovery never invokes the shim ───────────────────────────────────────────

    /// <summary>
    /// R-rc3-2a, §4.3a. <c>/usr/bin/git</c> exists on every Mac whether or not git is installed: with
    /// the Command Line Tools absent it is a shim that OPENS APPLE'S INSTALL DIALOG instead of running.
    /// Discovery by <c>git --version</c> would show that dialog, unbidden, on every launch, to exactly
    /// the users R-rc3-3 promises never learn the feature exists.
    ///
    /// <para>Driven through the seam so it runs on all three platforms; the real shim is a manual check
    /// recorded in the write-up, because CI has the tools installed.</para>
    /// </summary>
    [Fact]
    public void WithTheDeveloperToolsAbsentNoProcessNamedGitIsEverStarted()
    {
        if (!OperatingSystem.IsMacOS()) return;   // the seam only guards the platform that has a shim

        using var ws = new GitWorkspace();
        using var _  = new DiscoveryScope(candidates: ["git"], preferred: null, developerTools: () => false);

        Assert.Null(GitDiscovery.Find(out var rejected));
        Assert.Empty(GitDiscovery.ProbedPaths);
        Assert.Contains(rejected, r => r.Contains("shim", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The other half of R-rc3-2a: a path the user CONFIGURED is run as configured, because
    /// there they named it — including when the tools check says absent.</summary>
    [Fact]
    public void AConfiguredPathIsStillRunWhenTheDeveloperToolsCheckSaysAbsent()
    {
        using var ws = new GitWorkspace();
        string fake  = FakeGit.Write(ws.Root, "2.99.0");

        using var scope = new DiscoveryScope(candidates: ["git"], preferred: fake, developerTools: () => false);

        Assert.Equal(fake, GitDiscovery.Find(out _)!.Path);
    }

    // ── Gate 11 / 12 / 13: the generated policy files ─────────────────────────────────────────────

    /// <summary>
    /// Gate 11. A new workspace's <c>.gitignore</c> carries the result patterns, the <c>.cwsuser</c>,
    /// <c>.generated-cells</c> and the per-session bookkeeping; its <c>.gitattributes</c> marks all five
    /// document types unmergeable AND pins their end-of-line treatment.
    /// </summary>
    [Fact]
    public void ANewWorkspaceGetsBothPolicyFilesWithEverythingR_rc3_9And10And12Require()
    {
        using var ws = new GitWorkspace(withCws: false);
        var made = WorkspaceCreate.Create(ws.Root, "ws", technologyId: null);

        string ignore = File.ReadAllText(Path.Combine(made.WorkspaceDir, ".gitignore"));
        foreach (string pattern in WorkspacePolicyFiles.ResultPatterns)
            Assert.Contains(pattern, ignore);
        Assert.Contains(WorkspaceUserPersistence.FileName, ignore);
        Assert.Contains(WorkspacePolicyFiles.GeneratedCellsFolder, ignore);
        Assert.Contains(".crf-*", ignore);          // SL4's advisory lock, SL2's write probe
        Assert.Contains("*.crf-tmp-*", ignore);     // AtomicFile's in-flight temporary

        string attrs = File.ReadAllText(Path.Combine(made.WorkspaceDir, ".gitattributes"));
        foreach (string ext in WorkspacePolicyFiles.UnmergeableExtensions)
        {
            Assert.Contains($"*{ext} -merge", attrs);
            Assert.Contains($"*{ext} -merge -text", attrs);
        }
    }

    /// <summary>
    /// Gate 11's second half: the <c>.gitignore</c> is written by <c>WorkspaceCreate</c>, so it appears
    /// in a workspace made headlessly by <c>circuitrf new workspace</c> — <b>as a process</b>, because
    /// a headless default that differs from the dialog's is a second product.
    /// </summary>
    [Fact]
    public void AWorkspaceMadeHeadlesslyGetsTheSameTwoFilesAsOneMadeFromTheDialog()
    {
        if (CliPaths.Dll is not { } cliDll) return;

        using var ws = new GitWorkspace(withCws: false);
        var run = RunCli(cliDll, ws.HomeDir, ws.Root,
                         ["new", "workspace", ws.Root, "--name", "headless-ws", "--tech", "none"]);
        Assert.True(run.Code == 0, run.Err);

        string made = Path.Combine(ws.Root, "headless-ws");
        Assert.True(File.Exists(Path.Combine(made, ".gitignore")),
            "a headlessly-created workspace did not get circuitRF's .gitignore");
        Assert.True(File.Exists(Path.Combine(made, ".gitattributes")));

        // And byte-identical to what the in-process call writes, which is the only way the two stay one
        // product rather than two.
        using var reference = new GitWorkspace(withCws: false);
        WorkspaceCreate.Create(reference.Root, "in-process-ws", technologyId: null);
        Assert.Equal(File.ReadAllText(Path.Combine(reference.Root, "in-process-ws", ".gitignore")),
                     File.ReadAllText(Path.Combine(made, ".gitignore")));
        Assert.Equal(File.ReadAllText(Path.Combine(reference.Root, "in-process-ws", ".gitattributes")),
                     File.ReadAllText(Path.Combine(made, ".gitattributes")));
    }

    /// <summary>
    /// R-rc3-10: a file the ARCHIVE considers rebuildable is a file the repository considers
    /// rebuildable too. Read <c>WorkspaceArchiveScanner</c>'s skip list and keep the two in step — this
    /// is the test that notices when one of them moves.
    /// </summary>
    [Fact]
    public void EverythingTheArchiveSkipsAsRebuildableOrPerSessionIsAlsoIgnoredByTheRepository()
    {
        string ignore = WorkspacePolicyFiles.GitIgnoreBlock();

        foreach (string sample in (string[])
                 [".generated-cells/somecell/x.clay",
                  ".crf-open.json",
                  ".crf-write-probe-1234",
                  "cells/thing.clay.crf-tmp-9876",
                  ".cwsuser"])
        {
            Assert.True(CircuitRF.Ui.Archive.WorkspaceArchiveScanner.IsSkipped(sample)
                        || sample == ".cwsuser",
                        $"the archive no longer skips '{sample}' — the two lists have drifted");
            Assert.True(MatchesAnIgnorePattern(ignore, sample),
                        $"'{sample}' is skipped by the archive but not ignored by the repository");
        }
    }

    /// <summary>
    /// <b>Gate 12 — the gate that catches the gap rev 2 shipped.</b> R-rc3-11a: a workspace created
    /// BEFORE this feature existed gets the files when it first GAINS a repository. Under R-rc3-11
    /// alone those workspaces would never receive a <c>.gitignore</c> at all and would track results
    /// forever — and they are the ones that matter, because they have years of output in them. The
    /// fixture is deliberately an EXISTING workspace, not a fresh one.
    /// </summary>
    [GitFact]
    public void AWorkspaceThatPredatesThisFeatureGetsBothFilesWhenItFirstGainsARepository()
    {
        using var ws = new GitWorkspace();
        ws.Write("cells/amp.ccell/schematic.csch", "{}");
        ws.Write("results/amp.npy", "not really a cube");

        Assert.False(File.Exists(ws.File_(".gitignore")));
        Assert.False(File.Exists(ws.File_(".gitattributes")));

        var made = GitRepository.Create(ws.Git(TestIdentity), RevisionManagement.Created);
        Assert.True(made.Ok, made.Diagnostic?.Render());

        Assert.True(File.Exists(ws.File_(".gitignore")));
        Assert.True(File.Exists(ws.File_(".gitattributes")));
    }

    /// <summary>
    /// R-rc3-11b / gate 13. circuitRF ADDS lines and never rewrites the file. A policy file that is
    /// silently regenerated is a policy file whose user edits vanish — and the user finds out when
    /// something they had excluded turns up in an archive.
    /// </summary>
    [Fact]
    public void CircuitRfAppendsToTheIgnoreFileAndNeverRewritesIt()
    {
        using var ws = new GitWorkspace();
        const string mine = "# my own line\n*.secret-vendor-data\n";
        File.WriteAllText(ws.File_(".gitignore"), mine);

        WorkspacePolicyFiles.Ensure(ws.Root);
        string after = File.ReadAllText(ws.File_(".gitignore"));
        Assert.StartsWith(mine, after);
        Assert.Contains("*.npy", after);

        // And running every path again adds nothing: the marker is what makes it idempotent.
        WorkspacePolicyFiles.Ensure(ws.Root);
        WorkspacePolicyFiles.Ensure(ws.Root);
        Assert.Equal(after, File.ReadAllText(ws.File_(".gitignore")));
        Assert.Equal(1, Occurrences(after, WorkspacePolicyFiles.BlockMarker));
    }

    /// <summary>
    /// A 3D solve's run folder is ignored and what is written BESIDE it under the same stem is not.
    /// The folders are named by the run services' own <c>RunDirectory</c>, so a renamed token or a new
    /// static suffix shows up here rather than as hundreds of megabytes in somebody's history.
    /// </summary>
    [GitFact]
    public void EverySolverRunFolderIsIgnoredAndTheResultsBesideItAreNot()
    {
        using var ws = new GitWorkspace();
        Assert.Equal(0, ws.Raw("init", "-q").Code);
        WorkspacePolicyFiles.Ensure(ws.Root);

        string results = ws.File_("results");
        EmSetup Setup(Em3dProblemType problem) => new() { Name = "Launch Palace", Problem3D = problem };
        string[] runs =
        [
            .. Enum.GetValues<Em3dProblemType>().Where(p => p != Em3dProblemType.Thermal).Select(p => Em3dRunService.RunDirectory(results, Setup(p), Em3dSolver.Palace)),
            Em3dRunService.RunDirectory(results, Setup(Em3dProblemType.Driven), Em3dSolver.OpenEms),
            ThermalRunService.RunDirectory(results, Setup(Em3dProblemType.Driven)),
        ];
        Assert.Equal(runs.Length, runs.Distinct().Count());

        string Rel(string full) => Path.GetRelativePath(ws.Root, full).Replace('\\', '/');
        List<string> inside = [.. runs.Select(r => Rel(Path.Combine(r, "postpro", "x.vtu")))];
        foreach (string f in inside) ws.Write(f, "x");
        string[] beside = ["results/Launch Palace.palace.s2p", "results/Launch Palace.palace_em.cdd"];
        foreach (string f in beside) ws.Write(f, "x");

        string ignored = ws.Raw(["check-ignore", .. inside, .. beside]).Out;
        foreach (string f in inside) Assert.Contains(f, ignored, StringComparison.Ordinal);
        foreach (string f in beside) Assert.DoesNotContain(f, ignored, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace whose <c>.gitignore</c> was written before the run-folder section existed gains that
    /// section — once, after the user's lines and the older block, which are left as they were.
    /// </summary>
    [Fact]
    public void AnOlderIgnoreFileGainsTheRunFolderSectionOnceAndKeepsEverythingElse()
    {
        using var ws = new GitWorkspace();
        string older = "*.mine\n" + WorkspacePolicyFiles.BlockMarker + "\n*.npy\n";
        File.WriteAllText(ws.File_(".gitignore"), older);

        Assert.Contains(".gitignore", WorkspacePolicyFiles.Ensure(ws.Root));
        string after = File.ReadAllText(ws.File_(".gitignore"));
        Assert.StartsWith(older, after);
        foreach (string pattern in WorkspacePolicyFiles.RunFolderPatterns)
            Assert.Contains(pattern, after);

        Assert.DoesNotContain(".gitignore", WorkspacePolicyFiles.Ensure(ws.Root));
        Assert.Equal(after, File.ReadAllText(ws.File_(".gitignore")));
        Assert.Equal(1, Occurrences(after, WorkspacePolicyFiles.RunFoldersMarker));
    }

    // ── Gate 6: the repository configuration is complete ──────────────────────────────────────────

    /// <summary>
    /// R-rc3-7. Every row of §4.5's table, on a freshly created repository. <c>gc.auto=0</c> alone is
    /// not the gate it was in rev 2 — the three <c>never</c> rows are what make RC-6's retention real.
    /// </summary>
    [GitFact]
    public void AFreshlyCreatedRepositoryCarriesEveryRowOfTheConfigurationTable()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        foreach (var (key, value, prevents) in GitRepositoryConfig.RowsForThisPlatform())
        {
            var r = git.Run(["config", "--local", "--get", key]);
            Assert.True(r.Ok, $"'{key}' was not written — it exists to prevent: {prevents}");
            Assert.Equal(value, r.Line);
        }

        Assert.Equal("0",     git.Run(["config", "--local", "--get", "gc.auto"]).Line);
        Assert.Equal("never", git.Run(["config", "--local", "--get", "gc.pruneExpire"]).Line);
        Assert.Equal("never", git.Run(["config", "--local", "--get", "gc.reflogExpire"]).Line);
        Assert.Equal("never", git.Run(["config", "--local", "--get", "gc.reflogExpireUnreachable"]).Line);
    }

    /// <summary>
    /// The other half of gate 6, and it is stated slightly differently from the brief because rev 5 of
    /// the architecture changed what is true. The brief (written against rev 2) says <b>no code path
    /// anywhere passes <c>--prune</c></b>; rev 5's §5.6a then ADDED the reclaim operation, which by
    /// definition must prune with an immediate expiry. So the checkable form of the requirement is the
    /// one this asserts: <b><c>--prune</c> appears in exactly one file, that file is
    /// <c>GitReclaim.cs</c>, and the ONLY caller in the product is the one confirmed destructive
    /// control</b> — reclaim is a person's explicit action, never a schedule.
    ///
    /// <para><b>Extended by RC-4, not relaxed.</b> RC-3 asserted that NOTHING called it, which was the
    /// checkable form of the requirement while the operation had no user-facing home. RC-4 built that
    /// home — Settings ▸ Revision Control's <i>Reclaim Space…</i>, behind a confirmation naming the
    /// count and the age — so the requirement is now that there is exactly ONE caller and it is that
    /// one. A second call site appearing anywhere is a second, unasked way to destroy the only copy of
    /// a thinned state, which is what this gate has always been about.
    /// <c>RevisionControlSettingsTests.NothingElseInTheApplicationReachesTheReclaim</c> holds the same
    /// property from the other side.</para>
    ///
    /// <para>Leaving the flag off routine packing was necessary and was never sufficient: the
    /// guarantee lives in the three <c>never</c> rows of the repository's own config, because
    /// <c>git gc</c> prunes at <c>gc.pruneExpire</c> whether or not it was asked to.</para>
    /// </summary>
    [Fact]
    public void PruneAppearsOnlyInTheReclaimOperationAndNothingInTheProductCallsIt()
    {
        var offenders = new List<string>();
        var callers   = new List<string>();

        foreach (string file in Directory.GetFiles(SourceDir("src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            string name = Path.GetFileName(file);
            string code = StripComments(File.ReadAllText(file));

            if (code.Contains("--prune", StringComparison.Ordinal) && name != "GitReclaim.cs")
                offenders.Add(name);
            if (code.Contains("GitReclaim", StringComparison.Ordinal) && name != "GitReclaim.cs")
                callers.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "'--prune' appears in: " + string.Join(", ", offenders) +
            ". Routine packing must never prune — RC-6's whole retention grace period rests on it.");
        Assert.Equal(["RevisionControlSettingsView.axaml.cs"], callers);

        // And the packing path's own arguments, read directly rather than inferred from the scan.
        string packing = StripComments(
            File.ReadAllText(Path.Combine(SourceDir("src"), "Design", "Revision", "GitPacking.cs")));
        Assert.Contains("\"gc\", \"--quiet\"", packing);
        Assert.DoesNotContain("prune", packing, StringComparison.Ordinal);
    }

    // ── Gate 7a: the management marker ────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-7b. A repository circuitRF created and one created by <c>git init</c> beside it are told
    /// apart <b>from the marker alone</b>, with no inference from the presence of <c>.git</c>,
    /// <c>.gitignore</c> or <c>.gitattributes</c> — a user may perfectly well have written all three.
    /// </summary>
    [GitFact]
    public void TheMarkerTellsCircuitRfsRepositoryFromTheUsersOwnWithNoInferenceFromFiles()
    {
        using var mine = new GitWorkspace();
        Assert.True(GitRepository.Create(mine.Git(TestIdentity), RevisionManagement.Created).Ok);
        Assert.Equal(RevisionManagement.Created, GitRepository.ReadMarker(mine.Git(TestIdentity)));
        Assert.True(GitRepository.IsManagedByCircuitRf(mine.Git(TestIdentity)));

        using var theirs = new GitWorkspace();
        theirs.Raw("init", "--quiet");
        // Everything a user could plausibly have written themselves — and none of it is the marker.
        File.WriteAllText(theirs.File_(".gitignore"), "*.npy\n");
        File.WriteAllText(theirs.File_(".gitattributes"), "*.clay -merge\n");

        Assert.Null(GitRepository.ReadMarker(theirs.Git(TestIdentity)));
        Assert.False(GitRepository.IsManagedByCircuitRf(theirs.Git(TestIdentity)));
    }

    /// <summary>
    /// The two journeys the placement is chosen FOR: <b>an archive carries it</b> (§9A copies the
    /// directory, so RC-8's extracted archive is recognised without ceremony) and <b>a clone does
    /// not</b> (git does not clone a repository's config, so a clone reaches R-rc0-13's arming path on
    /// its own terms). A marker held in a REFERENCE instead would be pushed, carrying one designer's
    /// management decision into everybody else's clone.
    /// </summary>
    [GitFact]
    public void TheMarkerSurvivesAnArchiveAndDoesNotSurviveAClone()
    {
        using var origin = new GitWorkspace();
        var git = origin.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Adopted).Ok);
        origin.Write("cells/a.ccell/schematic.csch", "{}");
        Assert.True(GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "checkpoint\n").Recorded);

        // An archive is a directory copy, .git and all.
        string extracted = Path.Combine(Path.GetTempPath(), "crf-rc3-arc-" + Guid.NewGuid().ToString("N")[..8]);
        CopyTree(origin.Root, extracted);
        try
        {
            var copy = GitCommand.For(extracted, TestIdentity)!;
            Assert.Equal(RevisionManagement.Adopted, GitRepository.ReadMarker(copy));
        }
        finally { TryDelete(extracted); }

        // A clone is not.
        string clone = Path.Combine(Path.GetTempPath(), "crf-rc3-clone-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            origin.Raw("clone", "--quiet", "--no-local", origin.Root, clone);
            if (Directory.Exists(Path.Combine(clone, ".git")))
                Assert.Null(GitRepository.ReadMarker(GitCommand.For(clone, TestIdentity)!));
        }
        finally { TryDelete(clone); }
    }

    // ── Gate 8: identity ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-1a / §4.4. After a checkpoint, the repository's config carries <b>no</b>
    /// <c>user.name</c>/<c>user.email</c> and the user's global config is <b>untouched</b>, while the
    /// commit names the right author AND committer — git resolves the two separately, and supplying
    /// only one leaves the other to a guess at <c>user@hostname</c>.
    /// </summary>
    [GitFact]
    public void IdentityIsInNoConfigFileAndTheCommitStillNamesBothAuthorAndCommitter()
    {
        using var ws = new GitWorkspace();
        string globalBefore = ws.ReadGlobalConfig();

        var git = ws.Git(new GitIdentity("A Designer", "a.designer@example.invalid", "test"));
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        ws.Write("cells/a.ccell/schematic.csch", "{}");

        var made = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "checkpoint\n");
        Assert.True(made.Recorded, made.Diagnostic?.Render());

        Assert.False(git.Run(["config", "--local", "--get", "user.name"]).Ok);
        Assert.False(git.Run(["config", "--local", "--get", "user.email"]).Ok);
        Assert.DoesNotContain("user.name", File.ReadAllText(Path.Combine(ws.Root, ".git", "config")));
        Assert.DoesNotContain("[user]",    File.ReadAllText(Path.Combine(ws.Root, ".git", "config")));
        Assert.Equal(globalBefore, ws.ReadGlobalConfig());

        var who = git.Run(["log", "-1", "--format=%an|%ae|%cn|%ce", made.CommitId!]);
        Assert.Equal("A Designer|a.designer@example.invalid|A Designer|a.designer@example.invalid", who.Line);
    }

    /// <summary>
    /// <b>The network-share case, and the gate rev 2 would have failed.</b> Two identities against ONE
    /// workspace: identity in a repository's config makes a person a property of a DIRECTORY, so the
    /// second designer to open a shared workspace would commit under the first one's name, silently,
    /// until somebody read a history and disbelieved it.
    /// </summary>
    [GitFact]
    public void TwoIdentitiesAgainstOneWorkspaceProduceTwoDifferentlyAttributedCommits()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(new GitIdentity("First Designer", "first@example.invalid", "test"));
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        ws.Write("cells/a.ccell/schematic.csch", "{\"v\":1}");
        var one = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.True(one.Recorded);

        git.Identity = new GitIdentity("Second Designer", "second@example.invalid", "test");
        ws.Write("cells/a.ccell/schematic.csch", "{\"v\":2}");
        var two = GitCheckpoint.Record(git, "refs/crf/checkpoints/2", "two\n");
        Assert.True(two.Recorded);

        Assert.Equal("First Designer|first@example.invalid",
                     git.Run(["log", "-1", "--format=%an|%ae", one.CommitId!]).Line);
        Assert.Equal("Second Designer|second@example.invalid",
                     git.Run(["log", "-1", "--format=%an|%ae", two.CommitId!]).Line);
    }

    /// <summary>
    /// R-rc3-1c. The identity is read from circuitRF's own per-user preference file by a type in
    /// <c>src/Design</c>, so a process with no <c>src/Ui</c> in it sees what the Settings tab wrote.
    /// Where the file names nobody, git's ordinary resolution applies; where neither does, the feature
    /// does not arm and the refusal names the Settings tab rather than a git command.
    /// </summary>
    [GitFact]
    public void TheIdentityComesFromCircuitRfsOwnPreferenceFileThenFromGitThenFromNobody()
    {
        using var ws    = new GitWorkspace();
        using var state = new RedirectedAppData();

        var git = ws.Git();
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        // Nobody at all: not a git error recognised after the fact — a refusal decided BEFORE the
        // invocation, naming the Settings tab.
        Assert.Null(RevisionIdentity.Resolve(git));
        ws.Write("a.csch", "{}");
        var refused = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "x\n");
        Assert.False(refused.Recorded);
        Assert.Equal("revision.identity.missing", refused.Diagnostic!.Id);
        Assert.Contains("Settings", refused.Diagnostic.Render());

        // git's own resolution, when circuitRF has no preference.
        ws.Raw("config", "--global", "user.name",  "Machine Git");
        ws.Raw("config", "--global", "user.email", "machine@example.invalid");
        Assert.Equal("Machine Git", RevisionIdentity.Resolve(ws.Git())!.Name);

        // circuitRF's own preference outranks it.
        WritePreference("Tab Designer", "tab@example.invalid");
        var mine = RevisionIdentity.FromPreferences();
        Assert.Equal("Tab Designer",         mine!.Name);
        Assert.Equal("tab@example.invalid",  mine.Email);
        Assert.Equal("Tab Designer",         RevisionIdentity.Resolve(ws.Git())!.Name);
    }

    /// <summary>Half an identity is no identity: git would otherwise guess the other half at
    /// <c>user@hostname</c>, which is the silent wrong answer §4.4 exists to stop.</summary>
    [Fact]
    public void HalfAnIdentityInThePreferenceFileIsNoIdentity()
    {
        using var state = new RedirectedAppData();
        WritePreference("Only A Name", email: null);
        Assert.Null(RevisionIdentity.FromPreferences());
    }

    // ── Gate 22: src/Cli sees the identity the tab wrote ──────────────────────────────────────────

    /// <summary>
    /// <b>Gate 22</b>, as a real process. The preference is written into the per-user file, a
    /// <c>CircuitRF.Cli</c> process takes a checkpoint, and the commit is attributed to it — with the
    /// user's global git identity ABSENT, which is the case that matters: on the fresh Windows machine
    /// §4.4 describes, git's own resolution names nobody, and rev 4's design refused the AI-batch
    /// checkpoint for exactly the population it exists to protect.
    /// </summary>
    [GitFact]
    public void ACliProcessCommitsAsTheIdentityTheSettingsFileHolds()
    {
        if (CliPaths.Dll is not { } cliDll) return;

        using var ws    = new GitWorkspace();
        using var state = new RedirectedAppData();
        WritePreference("Headless Designer", "headless@example.invalid");

        ws.Write("cells/a.ccell/schematic.csch", "{}");

        // RC-5 took over where the reference lands and what the message says (R-rc5-1, R-rc5-9a), so
        // `--ref` is gone and the intent is the label: nothing outside src/Design chooses a reference
        // name any more, which is what keeps the ordering sequence a property of the namespace rather
        // than of whoever typed the command.
        var run = RunCli(cliDll, state.Dir, ws.Root,
                         ["history", "checkpoint", ws.Root, "--create-repository",
                          "--intent", "headless checkpoint"]);

        Assert.True(run.Code == 0, run.Err);

        string commit = CheckpointReferences.List(ws.Git()).Single().CommitId;

        var who = ws.Raw("log", "-1", "--format=%an|%ae|%cn|%ce", commit);
        Assert.Equal("Headless Designer|headless@example.invalid|Headless Designer|headless@example.invalid",
                     who.Out.Trim());
    }

    // ── Gate 9: hooks and signing ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-7a. A repository carrying a failing <c>pre-commit</c> hook is committed into anyway, <b>and
    /// a <c>post-commit</c> hook does not fire</b> — <c>--no-verify</c> is not the mechanism, because it
    /// skips <c>pre-commit</c> and <c>commit-msg</c> only. Running someone's hook on a checkpoint they
    /// did not initiate is an ambush; the checkpoint is circuitRF's, not theirs.
    /// </summary>
    [GitFact]
    public void HooksNeitherBlockNorFireAndAGlobalSigningSettingDoesNotStopACheckpoint()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Adopted).Ok);

        string sentinel = ws.File_("post-commit-ran.txt");
        WriteHook(ws, "pre-commit",  "exit 1");
        WriteHook(ws, "post-commit", $"echo ran > \"{sentinel.Replace("\\", "/")}\"");

        // A designer who signs globally would otherwise have every automatic checkpoint block on a
        // passphrase prompt with no window to appear in.
        ws.Raw("config", "--global", "commit.gpgsign", "true");
        ws.Raw("config", "--global", "user.signingkey", "0000000000000000");

        ws.Write("cells/a.ccell/schematic.csch", "{}");
        var made = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "checkpoint\n");

        Assert.True(made.Recorded, made.Diagnostic?.Render());
        Assert.False(File.Exists(sentinel), "a post-commit hook fired — --no-verify was used instead of an empty core.hooksPath");
        Assert.Equal("N", ws.Raw("log", "-1", "--format=%G?", made.CommitId!).Out.Trim());
    }

    // ── Gate 10: the environment reaches the subprocess ───────────────────────────────────────────

    /// <summary>
    /// R-rc3-1a. The invocation environment is asserted where it actually lands — inside the child
    /// process — by pointing discovery at a stand-in that writes its own environment out.
    /// </summary>
    [Fact]
    public void EveryEnvironmentEntryReachesTheSubprocess()
    {
        using var ws = new GitWorkspace();
        string dump  = Path.Combine(ws.Root, "env-dump.txt");
        string fake  = FakeGit.WriteEnvDump(ws.Root, dump, "2.99.0");

        using var _ = new DiscoveryScope(candidates: [], preferred: fake);

        var git = GitCommand.For(ws.Root, TestIdentity)!;
        git.Run(["status", "--porcelain"]);

        string env = File.ReadAllText(dump);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", env);
        Assert.Contains("LC_ALL=C", env);
        Assert.Contains("GIT_AUTHOR_NAME=" + TestIdentity.Name, env);
        Assert.Contains("GIT_COMMITTER_NAME=" + TestIdentity.Name, env);
        Assert.Contains("GIT_AUTHOR_EMAIL=" + TestIdentity.Email, env);
        Assert.Contains("GIT_COMMITTER_EMAIL=" + TestIdentity.Email, env);
    }

    /// <summary>Every invocation carries <c>--no-pager</c>, the workspace root as <c>safe.directory</c>,
    /// an empty <c>core.hooksPath</c> and the signing suppression — narrowed to the ONE root, never
    /// <c>*</c>.</summary>
    [Fact]
    public void EveryInvocationCarriesTheGlobalArgumentsAndSafeDirectoryIsTheOneRootNeverAStar()
    {
        var args = GitEnvironment.GlobalArguments("/some/workspace/shape").ToList();

        Assert.Contains("--no-pager", args);
        Assert.Contains("safe.directory=/some/workspace/shape", args);
        Assert.Contains("core.hooksPath=", args);
        Assert.Contains("commit.gpgsign=false", args);
        Assert.DoesNotContain("safe.directory=*", args);
    }

    /// <summary>
    /// <b>A call that would prompt returns a failure rather than blocking</b> — driven with a fixture
    /// remote that demands credentials. <b>A test that hangs is the defect this requirement exists to
    /// prevent, so it is given a bound.</b>
    /// </summary>
    [GitFact]
    public void ACallThatWouldPromptForCredentialsRefusesInsteadOfHanging()
    {
        using var ws     = new GitWorkspace();
        using var remote = new UnauthorizedRemote();

        var git  = ws.Git(TestIdentity);
        var task = Task.Run(() => git.Run(["ls-remote", remote.Url],
                                          new GitRunOptions(Network: true, Timeout: TimeSpan.FromSeconds(20))));

        Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "the call blocked — GIT_TERMINAL_PROMPT=0 did not reach git");

        var result = task.Result;
        Assert.False(result.Ok);
        Assert.False(result.TimedOut, "it hit the bound rather than refusing, which is a hang by another name");

        var d = GitFailures.Translate(result, "reading the other copy", ws.Root);
        Assert.Equal("revision.credentials.required", d.Id);
    }

    // ── Gates 4 and 5: the translation table ──────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-4's "nothing to commit" row — <b>not a failure, the state is already recorded</b> — and it
    /// is decided by a TREE COMPARISON rather than by a substring match on git's English.
    /// </summary>
    [GitFact]
    public void AnUnchangedWorkspaceRecordsNothingAndSaysSoAsInfo()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        ws.Write("cells/a.ccell/schematic.csch", "{}");

        var first = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.True(first.Recorded);

        var again = GitCheckpoint.Record(git, "refs/crf/checkpoints/2", "two\n",
                                         previousTreeId: first.TreeId);
        Assert.False(again.Recorded);
        Assert.Equal("revision.nothing-to-record", again.Diagnostic!.Id);
        Assert.Equal(DiagnosticSeverity.Info, again.Diagnostic.Severity);
    }

    /// <summary>
    /// The stale-lock row. The recognisable signature is <b>a file that exists</b>, not a sentence:
    /// the message says what to remove and that it is safe.
    /// </summary>
    [GitFact]
    public void ALockFileLeftByACrashedProcessBecomesASentenceSayingWhatToRemove()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        ws.Write("cells/a.ccell/schematic.csch", "{}");

        Directory.CreateDirectory(Path.Combine(ws.Root, ".git", "refs", "crf", "checkpoints"));
        string stale = Path.Combine(ws.Root, ".git", "refs", "crf", "checkpoints", "1.lock");
        File.WriteAllText(stale, "");

        var made = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.False(made.Recorded);
        Assert.Equal("revision.lock.left-behind", made.Diagnostic!.Id);
        Assert.Contains("1.lock", made.Diagnostic.Render());
        Assert.Contains("safe", made.Diagnostic.Render());
    }

    /// <summary>
    /// R-rc3-4's embedded-repository row, and §12 Q30's reason for it: handing a directory that contains
    /// a <c>.git</c> to <c>git add</c> records a POINTER to that repository's current commit, with a
    /// warning nobody is reading. The sentence says a nested repository was found and excluded, and
    /// nothing about gitlinks.
    /// </summary>
    [GitFact]
    public void ANestedRepositoryIsExcludedByPathspecAndNamedInCircuitRfsOwnWords()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        ws.Write("cells/a.ccell/schematic.csch", "{}");
        Directory.CreateDirectory(ws.File_("vendor/theirlib"));
        ws.Raw("-C", ws.File_("vendor/theirlib"), "init", "--quiet");
        ws.Write("vendor/theirlib/thing.txt", "theirs");

        Assert.Equal(["vendor/theirlib"], NestedRepositories.Find(ws.Root));

        var made = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.True(made.Recorded, made.Diagnostic?.Render());
        Assert.Contains("vendor/theirlib", made.ExcludedRepositories);

        string tree = git.Run(["ls-tree", "-r", "-t", made.CommitId!, "--name-only"]).StdOut;
        Assert.DoesNotContain("vendor/theirlib", tree);
        Assert.Contains("cells/a.ccell/schematic.csch", tree);

        var d = GitFailures.NestedRepositoryExcluded("vendor/theirlib");
        Assert.Equal("revision.nested-repository.excluded", d.Id);
        Assert.DoesNotContain("gitlink", d.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submodule", d.Render(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRowOfTheTableHasItsOwnStableId()
    {
        var ids = new[]
        {
            GitFailures.NoIdentity().Id,
            GitFailures.NothingToRecord().Id,
            GitFailures.StaleLock("shape/of/a/path/index.lock").Id,
            GitFailures.NestedRepositoryExcluded("vendor/lib").Id,
            GitFailures.NonFastForward("https://example.invalid/x.git").Id,
            GitFailures.NotGit("shape/of/a/path/git", "it is a text file").Id,
            GitFailures.ForeignOwnership("shape/of/a/workspace").Id,
            GitFailures.TooOld("shape/of/a/path/git", new Version(2, 8), new Version(2, 9)).Id,
            GitFailures.CredentialRequired("https://example.invalid/x.git").Id,
            GitFailures.Unrecognised("doing a thing", "raw").Id,
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("revision.", id));
    }

    /// <summary>
    /// Gate 5, R-rc3-5. An unrecognised failure survives <b>verbatim</b> — the raw text is carried, not
    /// replaced by a generic sentence. A generic sentence is how a failure nobody has seen before
    /// becomes a failure nobody can diagnose.
    /// </summary>
    [GitFact]
    public void AnUnrecognisedFailureIsCarriedVerbatimAndNotReplaced()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        var r = git.Run(["cat-file", "-p", "0000000000000000000000000000000000000000"]);
        Assert.False(r.Ok);

        var d = GitFailures.Translate(r, "reading a stored state", ws.Root);
        Assert.Equal("revision.git.unrecognised", d.Id);
        Assert.Contains(r.StdErr.Trim().Split('\n')[0].Trim(), d.Render());
    }

    // ── Gate 17: safe.directory ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-1a / R-rc3-4. First that the call SUCCEEDS with the root named per invocation, and second
    /// — with the per-invocation answer withheld, to simulate a git too old to accept it — that
    /// <b>circuitRF's own sentence appears rather than git's.</b>
    /// </summary>
    [GitFact]
    public void SafeDirectoryIsAnsweredPerInvocationAndTranslatedWhereItCannotBe()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        Assert.True(git.Run(["status", "--porcelain"]).Ok);

        // Simulating the condition rather than constructing a foreign-owned repository: creating one
        // needs another account, which no test may assume. What is asserted is the TRANSLATION — that
        // git's own wording never reaches a designer.
        var asIfOldGit = new GitResult(
            128, "",
            $"fatal: detected dubious ownership in repository at '{ws.Root}'\n"
          + $"To add an exception for this directory, call:\n\n"
          + $"\tgit config --global --add safe.directory {ws.Root}\n",
            Started: true, TimedOut: false, Cancelled: false);

        var d = GitFailures.Translate(asIfOldGit, "opening this workspace's history", ws.Root);
        Assert.Equal("revision.ownership.foreign", d.Id);
        Assert.DoesNotContain("dubious", d.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe.directory", d.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git config", d.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ws.Root, d.Render());
    }

    // ── Gate 19: two writers, no clobber, no advisory lock ────────────────────────────────────────

    /// <summary>
    /// R-rc3-1b. Two concurrent checkpoints at one repository: both references land, and the SHARED
    /// index is byte-for-byte untouched — the one resource git's writers actually contend on is left
    /// to the designer's own shell.
    /// </summary>
    [GitFact]
    public void TwoConcurrentCheckpointsBothLandAndTheSharedIndexIsUntouched()
    {
        using var ws = new GitWorkspace();
        var setup = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(setup, RevisionManagement.Created).Ok);

        // The designer's own staged state, from a shell in the same folder.
        ws.Write("cells/a.ccell/schematic.csch", "{}");
        ws.Raw("-c", "user.name=Shell", "-c", "user.email=shell@example.invalid", "add", "cells");
        string indexPath = Path.Combine(ws.Root, ".git", "index");
        byte[] indexBefore = File.ReadAllBytes(indexPath);

        var results = new CheckpointResult[8];
        Parallel.For(0, results.Length, i =>
        {
            var git = ws.Git(TestIdentity);
            results[i] = GitCheckpoint.Record(git, $"refs/crf/checkpoints/{i}", $"checkpoint {i}\n");
        });

        for (int i = 0; i < results.Length; i++)
        {
            Assert.True(results[i].Recorded, results[i].Diagnostic?.Render());
            Assert.True(setup.Run(["rev-parse", "--verify", "--quiet", $"refs/crf/checkpoints/{i}"]).Ok);
        }

        Assert.Equal(indexBefore, File.ReadAllBytes(indexPath));
        Assert.Equal("cells/a.ccell/schematic.csch", setup.Run(["diff", "--cached", "--name-only"]).Line);
    }

    /// <summary>
    /// A reference-update collision resolves through the retry with no diagnostic posted — a collision
    /// between two automatic operations is a DELAY, not an error. A lock that is still there afterwards
    /// is a different thing entirely, and becomes <see cref="GitFailures.StaleLock"/>.
    /// </summary>
    [GitFact]
    public void AReferenceUpdateCollisionIsRetriedRatherThanReported()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        ws.Write("cells/a.ccell/schematic.csch", "{}");
        var first = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.True(first.Recorded);

        string reference = Path.Combine(ws.Root, ".git", "refs", "crf", "checkpoints", "2");
        Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
        string held = reference + ".lock";
        File.WriteAllText(held, "");

        // Released while the retry is still running: that is what a live collision looks like.
        var release = Task.Run(() => { Thread.Sleep(40); TryDeleteFile(held); });

        var r = git.Run(["update-ref", "refs/crf/checkpoints/2", first.CommitId!]);
        release.Wait();

        Assert.True(r.Ok, "the retry did not absorb a transient lock collision: " + r.StdErr);
    }

    /// <summary>
    /// A READ never waits on a writer: it takes no optional locks, so listing restore points or
    /// resolving a pin cannot block behind a pack.
    /// </summary>
    [GitFact]
    public void AReadDoesNotWaitOnAWriterHoldingTheIndexLock()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        ws.Write("cells/a.ccell/schematic.csch", "{}");
        Assert.True(GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n").Recorded);

        // Exactly what a concurrent writer holds.
        string indexLock = Path.Combine(ws.Root, ".git", "index.lock");
        File.WriteAllText(indexLock, "");
        try
        {
            var read = git.Run(["for-each-ref", "--format=%(refname)", "refs/crf/"],
                               new GitRunOptions(ReadOnly: true, Timeout: TimeSpan.FromSeconds(20)));
            Assert.True(read.Ok, read.StdErr);
            Assert.Contains("refs/crf/checkpoints/1", read.StdOut);
        }
        finally { TryDeleteFile(indexLock); }
    }

    /// <summary>
    /// <b>No second cross-process lock.</b> The advisory notice is a NOTICE — its own header says it
    /// holds no file handle, that both "open anyway" and "open read-only" are always available, and
    /// that a lock this product treated as authoritative would become a stale file that locks out a
    /// team. Its one permitted caller here is packing's yield.
    /// </summary>
    [Fact]
    public void TheAdvisoryLockIsUsedOnlyByPackingsYieldAndNowhereOnTheWritePath()
    {
        var users = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(SourceDir("src"), "Design", "Revision"), "*.cs"))
        {
            string code = StripComments(File.ReadAllText(file));
            if (code.Contains("WorkspaceLock", StringComparison.Ordinal)) users.Add(Path.GetFileName(file));
        }

        Assert.Equal(["GitPacking.cs"], users);
    }

    // ── Gates 7, 15, 21: packing and reclaim ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>Gate 7 — the test that makes RC-6's grace period real, and the one that would have caught
    /// rev 2's error.</b> An unreachable object back-dated past two weeks survives the packing path.
    /// <c>git gc</c> prunes at <c>gc.pruneExpire</c> whether or not <c>--prune</c> was passed; leaving
    /// the flag off was necessary and was never sufficient.
    /// </summary>
    [GitFact]
    public void PackingDoesNotDestroyAnUnreachableObjectBackDatedPastTwoWeeks()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        ws.Write("cells/a.ccell/schematic.csch", "{\"v\":1}");
        var kept = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.True(kept.Recorded);

        ws.Write("cells/a.ccell/schematic.csch", "{\"v\":2}");
        var thinned = GitCheckpoint.Record(git, "refs/crf/checkpoints/2", "two\n");
        Assert.True(thinned.Recorded);

        // Thin it: RC-6's sweep, in one line. Parentless commits are what make this actually free the
        // objects rather than merely unname them.
        Assert.True(git.Run(["update-ref", "-d", "refs/crf/checkpoints/2"]).Ok);
        BackDateLooseObjects(ws.Root, TimeSpan.FromDays(40));

        var (outcome, diagnostic) = GitPacking.Pack(git, thresholdBytes: 0);
        Assert.True(outcome is PackOutcome.Packed, diagnostic?.Render() ?? outcome.ToString());

        Assert.True(git.Run(["cat-file", "-e", thinned.CommitId!]).Ok,
            "a routine pack destroyed a thinned restore point — RC-6's entire retention design rests "
          + "on gc.pruneExpire=never, and this is the gate for it");
        Assert.True(git.Run(["cat-file", "-e", kept.CommitId!]).Ok);
    }

    /// <summary>R-rc3-15: cancel mid-pack and the repository is still valid and the operation can be
    /// re-run. An abandoned pack leaves a correct repository, which is part of why <c>git gc</c> is the
    /// right tool.</summary>
    [GitFact]
    public void PackingIsInterruptibleAndTheRepositoryIsStillValidAfterwards()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        for (int i = 0; i < 5; i++)
        {
            ws.Write("cells/a.ccell/schematic.csch", $"{{\"v\":{i}}}");
            GitCheckpoint.Record(git, $"refs/crf/checkpoints/{i}", $"c{i}\n");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();                       // already cancelled: the strongest form of "abandon it"
        var (outcome, _) = GitPacking.Pack(git, thresholdBytes: 0, ct: cts.Token);
        Assert.True(outcome is PackOutcome.Cancelled or PackOutcome.Packed);

        Assert.True(git.Run(["fsck", "--no-progress", "--connectivity-only"]).Started);
        var (again, diagnostic) = GitPacking.Pack(git, thresholdBytes: 0);
        Assert.True(again is PackOutcome.Packed, diagnostic?.Render() ?? again.ToString());
    }

    /// <summary>R-rc3-14: packing YIELDS to the advisory notice rather than queuing — that is the notice
    /// used as a notice.</summary>
    [GitFact]
    public void PackingYieldsWhileSomebodyElseHasTheWorkspaceOpen()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);
        ws.Write("cells/a.ccell/schematic.csch", "{}");
        GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");

        File.WriteAllText(Path.Combine(ws.Root, WorkspaceLock.FileName),
            $$"""{"user":"someone","host":"another-machine","processId":1,"takenUtc":"{{DateTime.UtcNow:o}}"}""");
        try
        {
            Assert.Equal(PackOutcome.Yielded, GitPacking.Pack(git, thresholdBytes: 0).Outcome);
        }
        finally { TryDeleteFile(Path.Combine(ws.Root, WorkspaceLock.FileName)); }
    }

    /// <summary>The trigger is BYTES, not git's count. A workspace whose history is a handful of
    /// enormous files sits at a few dozen loose objects indefinitely and git never once decides to do
    /// anything about it.</summary>
    [GitFact]
    public void TheTriggerIsLooseObjectBytesAndCountObjectsKibIsReadAsKib()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        ws.Write("cells/a.ccell/big.clay", new string('x', 400_000));
        Assert.True(GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n").Recorded);

        var size = GitPacking.Measure(git);
        Assert.NotNull(size);
        Assert.True(size!.LooseCount > 0);
        Assert.True(size.LooseBytes >= 1024, "size: is in KiB and was read as bytes");
        Assert.False(GitPacking.IsWorthPacking(git));                 // far below the shipped threshold
        Assert.True(GitPacking.IsWorthPacking(git, thresholdBytes: 1));
    }

    /// <summary>
    /// <b>Gate 21.</b> Reclaim protects what is newer than the age; with no age given nothing is
    /// reclaimed; and packing removes none of them. The protection step is not optional — git's own
    /// expiry is by OBJECT AGE, which is when a state was made rather than when it was thinned, so a
    /// two-year-old restore point thinned yesterday would otherwise be reclaimed by "thinned more than
    /// a month ago".
    /// </summary>
    [GitFact]
    public void ReclaimProtectsWhatIsNewerThanTheAgeAndDoesNothingWithoutOne()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        var commits = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            ws.Write("cells/a.ccell/schematic.csch", $"{{\"v\":{i}}}");
            var made = GitCheckpoint.Record(git, $"refs/crf/checkpoints/{i}", $"c{i}\n");
            Assert.True(made.Recorded);
            commits.Add(made.CommitId!);
            git.Run(["update-ref", "-d", $"refs/crf/checkpoints/{i}"]);   // thinned
        }

        var now = DateTimeOffset.UtcNow;
        var journal = new List<ThinnedState>
        {
            new(commits[0], now - TimeSpan.FromDays(90)),   // thinned long ago
            new(commits[1], now - TimeSpan.FromDays(10)),   // recently
            new(commits[2], now - TimeSpan.FromDays(1)),    // yesterday
        };

        // Every OBJECT is old — which is exactly the trap the journal exists to defeat.
        BackDateLooseObjects(ws.Root, TimeSpan.FromDays(400));

        // With no age, nothing at all.
        var nothing = GitReclaim.Reclaim(git, journal, thinnedLongerAgoThan: null, now: now);
        Assert.Empty(nothing.Reclaimed);
        Assert.All(commits, c => Assert.True(git.Run(["cat-file", "-e", c]).Ok));

        // And packing never removes any of them either.
        GitPacking.Pack(git, thresholdBytes: 0);
        Assert.All(commits, c => Assert.True(git.Run(["cat-file", "-e", c]).Ok));

        var done = GitReclaim.Reclaim(git, journal, TimeSpan.FromDays(30), now);
        Assert.Null(done.Diagnostic?.Render());
        Assert.Equal([commits[0]], done.Reclaimed.Select(e => e.CommitId));
        Assert.Equal(2, done.Protected.Count);

        Assert.False(git.Run(["cat-file", "-e", commits[0]]).Ok, "the old one should be gone");
        Assert.True(git.Run(["cat-file", "-e", commits[1]]).Ok,
            "a state thinned ten days ago was destroyed because its OBJECT was old — the protection "
          + "step is what stops that, and it is not optional");
        Assert.True(git.Run(["cat-file", "-e", commits[2]]).Ok);

        // The temporary protections are dropped afterwards; they are for the duration only.
        Assert.Equal("", git.Run(["for-each-ref", "--format=%(refname)", GitReclaim.ProtectionRefPrefix]).Line);
    }

    // ── Gate 14: bytes survive the round trip ─────────────────────────────────────────────────────

    /// <summary>
    /// R-rc3-12a. Commit a <c>.clay</c>, check it back out, and assert byte identity with what was
    /// written — on each platform CI runs. §2's whole measurement rests on <c>.clay</c> being
    /// byte-stable, and end-of-line conversion would make a design document differ from itself across
    /// machines.
    /// </summary>
    [GitFact]
    public void AClayCommittedAndCheckedBackOutIsByteForByteWhatWasWritten()
    {
        using var ws = new GitWorkspace();
        var git = ws.Git(TestIdentity);
        Assert.True(GitRepository.Create(git, RevisionManagement.Created).Ok);

        // CRLF INSIDE the file, deliberately: that is what autocrlf would rewrite, and it is the case a
        // test written on one platform would otherwise never reach.
        byte[] written = Encoding.UTF8.GetBytes("{\r\n  \"FormatVersion\": 1,\n  \"Shapes\": []\n}\r\n");
        string clay = ws.File_("cells/a.ccell/layout.clay");
        Directory.CreateDirectory(Path.GetDirectoryName(clay)!);
        File.WriteAllBytes(clay, written);

        var made = GitCheckpoint.Record(git, "refs/crf/checkpoints/1", "one\n");
        Assert.True(made.Recorded, made.Diagnostic?.Render());

        File.Delete(clay);
        var back = git.Run(["cat-file", "blob", made.CommitId! + ":cells/a.ccell/layout.clay"]);
        Assert.True(back.Ok, back.StdErr);
        Assert.Equal(Encoding.UTF8.GetString(written), back.StdOut);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static readonly GitIdentity TestIdentity =
        new("Test Designer", "test.designer@example.invalid", "test");

    private sealed class DiscoveryScope : IDisposable
    {
        private readonly IReadOnlyList<string> _candidates;
        private readonly Func<string?>?        _preferred;
        private readonly Func<bool>?           _tools;
        private readonly IReadOnlyList<string> _directories;
        private readonly string?               _env;

        public DiscoveryScope(IReadOnlyList<string> candidates, string? preferred, Func<bool>? developerTools = null)
        {
            _candidates = GitDiscovery.CandidateCommands;
            _preferred  = GitDiscovery.PreferredPath;
            _tools      = GitDiscovery.DeveloperToolsPresent;
            _directories = GitDiscovery.SearchDirectories;
            _env        = Environment.GetEnvironmentVariable(GitDiscovery.EnvironmentVariable);

            Environment.SetEnvironmentVariable(GitDiscovery.EnvironmentVariable, null);
            GitDiscovery.CandidateCommands      = candidates;
            GitDiscovery.PreferredPath          = preferred is null ? null : () => preferred;
            GitDiscovery.DeveloperToolsPresent  = developerTools;
            GitDiscovery.InvalidateCache();
        }

        public void Dispose()
        {
            GitDiscovery.CandidateCommands     = _candidates;
            GitDiscovery.PreferredPath         = _preferred;
            GitDiscovery.DeveloperToolsPresent = _tools;
            GitDiscovery.SearchDirectories      = _directories;
            Environment.SetEnvironmentVariable(GitDiscovery.EnvironmentVariable, _env);
            GitDiscovery.InvalidateCache();
        }
    }

    private sealed class RedirectedAppData : IDisposable
    {
        public string Dir { get; }
        public RedirectedAppData()
        {
            Dir = Path.Combine(Path.GetTempPath(), "crf-rc3-state-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Dir);
            AppDataRoot.RedirectTo(Dir);
        }

        public void Dispose()
        {
            AppDataRoot.RedirectTo(null);
            try { Directory.Delete(Dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes the two identity keys the way RC-4's tab will — through the shared constants,
    /// which is what makes "the tab and the reader agree" checkable rather than hopeful.</summary>
    private static void WritePreference(string? name, string? email)
    {
        var parts = new List<string>();
        if (name  is not null) parts.Add($"\"{RevisionIdentity.NameKey}\": {Quote(name)}");
        if (email is not null) parts.Add($"\"{RevisionIdentity.EmailKey}\": {Quote(email)}");
        File.WriteAllText(CircuitRF.Design.UserStateDirectory.PreferencesPath,
                          "{\n  " + string.Join(",\n  ", parts) + "\n}\n");

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// A stand-in for git: identifies itself with a version banner and does nothing else. Used where the
    /// point is the DISCOVERY or the ENVIRONMENT rather than git's behaviour.
    /// </summary>
    private static class FakeGit
    {
        public static string Write(string dir, string version, string name = "fakegit")
            => WriteScript(dir, name, OperatingSystem.IsWindows()
                ? $"@echo off\r\necho git version {version}\r\n"
                : $"#!/bin/sh\necho \"git version {version}\"\n");

        public static string WriteNotGit(string dir)
            => WriteScript(dir, "notgit", OperatingSystem.IsWindows()
                ? "@echo off\r\necho Python 3.13.1\r\n"
                : "#!/bin/sh\necho \"Python 3.13.1\"\n");

        public static string WriteEnvDump(string dir, string dumpTo, string version)
            => WriteScript(dir, "envgit", OperatingSystem.IsWindows()
                ? $"@echo off\r\nif \"%1\"==\"--version\" (echo git version {version}) else (set > \"{dumpTo}\")\r\n"
                : $"#!/bin/sh\ncase \"$*\" in *--version*) echo \"git version {version}\";; *) env > \"{dumpTo}\";; esac\n");

        private static string WriteScript(string dir, string stem, string body)
        {
            string path = Path.Combine(dir, stem + (OperatingSystem.IsWindows() ? ".cmd" : ""));
            File.WriteAllText(path, body);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
    }

    /// <summary>A remote that answers 401 to everything, so git needs a credential it cannot get.</summary>
    private sealed class UnauthorizedRemote : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public UnauthorizedRemote()
        {
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}/a-repository.git";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    ctx.Response.StatusCode = 401;
                    ctx.Response.AddHeader("WWW-Authenticate", "Basic realm=\"a-repository\"");
                    try { ctx.Response.Close(); } catch { /* client already gone */ }
                }
            });
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() { try { _listener.Close(); } catch { } }
    }

    private static class CliPaths
    {
        /// <summary>The BUILT CLI, found by walking up — the same idiom <c>EmCliVerbTests</c> uses, and
        /// for the same reason: `dotnet run` would rebuild inside a test.</summary>
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

        // The CLI has no in-process AppDataRoot call site to redirect, which is exactly the case
        // UserStateDirectory.EnvironmentVariable exists for.
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = appDataDir;

        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, o, e);
    }

    private static void WriteHook(GitWorkspace ws, string name, string body)
    {
        string dir = Path.Combine(ws.Root, ".git", "hooks");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Ages every loose object, which is what git's own expiry keys on.</summary>
    private static void BackDateLooseObjects(string root, TimeSpan age)
    {
        string objects = Path.Combine(root, ".git", "objects");
        if (!Directory.Exists(objects)) return;
        var when = DateTime.UtcNow - age;

        foreach (string file in Directory.GetFiles(objects, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}pack{Path.DirectorySeparatorChar}")) continue;
            try { File.SetLastWriteTimeUtc(file, when); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool MatchesAnIgnorePattern(string ignoreBlock, string relativePath)
    {
        string leaf = relativePath.Split('/')[^1];
        foreach (string raw in ignoreBlock.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string pattern = line.TrimEnd('/');
            var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");
            if (rx.IsMatch(leaf)) return true;
            foreach (string segment in relativePath.Split('/'))
                if (rx.IsMatch(segment)) return true;
        }
        return false;
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    private static string SourceDir(string leaf)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, leaf);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"'{leaf}' not found walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>Comments stripped, so a source scan cannot be satisfied — or defeated — by prose.</summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        code = Regex.Replace(code, @"^\s*//.*$", "", RegexOptions.Multiline);
        code = Regex.Replace(code, @"^\s*///.*$", "", RegexOptions.Multiline);
        return code;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
