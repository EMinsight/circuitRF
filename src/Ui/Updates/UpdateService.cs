using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Updates;

/// <summary>Why a check ended. Only two of these are ever visible to the user.</summary>
public enum CheckOutcome
{
    /// <summary>Automatic updates are off, or an override forbids them. No socket was opened.</summary>
    Disabled,

    /// <summary>This install cannot write itself — the .msi and the .deb. Notify-only.</summary>
    NotifyOnly,

    /// <summary>Nothing newer on this channel. The overwhelmingly common outcome.</summary>
    UpToDate,

    /// <summary>A new version is staged and inert. One Message Panel line was posted.</summary>
    Staged,

    /// <summary>Not enough disk. One of only two conditions allowed to be visible (design §13.5).</summary>
    InsufficientSpace,

    /// <summary>Network, DNS, timeout, rate limit, verification failure. Silent.</summary>
    Failed,

    /// <summary>Skipped by the 24-hour throttle.</summary>
    Throttled,

    /// <summary>
    /// The user stopped the download from its progress bar. <b>Not a failure</b>, and deliberately
    /// distinct from <see cref="Failed"/>: the partial transfer stays on disk and the next check
    /// resumes from it, so nothing was lost and there is nothing to report as having gone wrong.
    /// </summary>
    Cancelled,
}

/// <summary>What a check concluded, with the figures the two visible cases need.</summary>
public sealed record CheckResult(
    CheckOutcome Outcome,
    string? Version = null,
    long RequiredBytes = 0,
    long AvailableBytes = 0,
    string Detail = "");

/// <summary>
/// The whole update lifecycle, in one place: check, space, download, verify, stage, notify.
///
/// <para><b>The order is the design</b> (R-AU-23): reclaim, check space, download, verify, unpack,
/// rename into place. Everything expensive happens where abandoning costs nothing, and by the time
/// anything the running install depends on is touched, only renames remain. A disk that fills
/// mid-update therefore loses a download, never an installation.</para>
///
/// <para><b>Failure is silent.</b> No Message Panel entry, no dialog, no toast, for an unreachable
/// network, a timeout, a rate limit or a verification failure. An unreachable network is the NORMAL
/// state for a large fraction of this application's users — lab machines, air-gapped networks, hotel
/// wifi — and a recurring "couldn't check for updates" line would be a defect, not a feature.</para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>At most one line per 30 days when a full disk is the sole reason updates are not happening.</summary>
    public static readonly TimeSpan SpaceNoticeInterval = TimeSpan.FromDays(30);

    /// <summary>One check per day, with jitter applied by the scheduler.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Where a notify-only install is pointed. The one URL this subsystem shows a user.</summary>
    public const string ReleasesPageUrl = "https://github.com/potatobeanradio/circuitRF/releases";

    /// <summary>
    /// Why a WRITABLE install is still notify-only: the running binary carries no publisher identity,
    /// so R-AU-25's third step has nothing to compare a payload against.
    ///
    /// <para>It is a distinct sentence from the read-only one because the cause is different and the
    /// remedy belongs to a different person — an unsigned build is fixed by whoever built it, and
    /// telling the user their installation is in the wrong place would send them to re-install
    /// something that is exactly where it should be.</para>
    /// </summary>
    public const string UnsignedBuildReason =
        "This build is not signed, so it cannot verify that an update came from us and will not "
        + "install one.";

    /// <summary>Marks a check that found the newest version already downloaded. Not an error.</summary>
    public const string AlreadyStagedDetail = "already staged";

    /// <summary>Why an AppImage is notify-only, as the manual check's dialog says it.</summary>
    public const string AppImageReason =
        "An AppImage does not update itself; Download on its line in the Messages panel saves the new "
        + "one beside it.";

    /// <summary>The label on an AppImage's update line.</summary>
    public const string DownloadLabel = "Download";

    private readonly Func<IUpdateFeed> _feedFactory;
    private readonly IFreeSpaceProbe _space;
    private readonly IMessageSink? _messages;
    private readonly Func<InstallSite> _site;
    private readonly ReleaseTrust _trust;

    /// <param name="trust">
    /// Which release key this service checks against. Defaults to the key compiled into the build,
    /// which is what the application always uses — <b>a value passed in, never a global that can be
    /// set</b>, so a test can exercise the unkeyed path without a mutable trust anchor existing.
    /// </param>
    public UpdateService(
        Func<IUpdateFeed> feedFactory,
        IFreeSpaceProbe space,
        IMessageSink? messages,
        Func<InstallSite>? site = null,
        ReleaseTrust? trust = null)
    {
        _feedFactory = feedFactory;
        _space       = space;
        _messages    = messages;
        _site        = site ?? UpdateInstallSite.Detect;
        _trust       = trust ?? ReleaseTrust.Compiled;
    }

    /// <summary>How many times the feed was constructed. The counter R-AU-44's gate reads.</summary>
    public int FeedsCreated { get; private set; }

    /// <summary>
    /// The client an AppImage Download fetches with. The shipping value is
    /// <see cref="UpdateDownloader.CreateDownloadHttpClient"/>; settable only so a test can hand in a
    /// client whose handler serves canned bytes, since no test may make a network call.
    /// </summary>
    internal Func<HttpClient> DownloadClient { get; init; } = UpdateDownloader.CreateDownloadHttpClient;

    /// <summary>Opens a release page in the browser. Settable only so a test can observe it.</summary>
    internal Action<string> OpenLink { get; init; } = DocLauncher.OpenLink;

    /// <summary>Shows a downloaded file in its folder. Settable only so a test can observe it.</summary>
    internal Action<string> RevealFile { get; init; } = path => FileReveal.Reveal(path);

    /// <summary>1 while an AppImage Download is running, so a second click does not start a second one.</summary>
    private int _appImageBusy;

    /// <summary>
    /// One complete check.
    ///
    /// <para><paramref name="manual"/> is Help ▸ Check for Updates…: it ignores the 24-hour throttle
    /// and is the one place a network failure may be reported, because the user explicitly asked.</para>
    /// </summary>
    public async Task<CheckResult> CheckAsync(
        bool manual, CancellationToken ct, RunCancellation? cancellation = null)
    {
        UpdatePolicyState policy = UpdatePolicy.Current;

        // R-AU-44: "never checks" is LITERAL. The preference is read BEFORE an HttpClient is
        // constructed, not consulted afterwards to decide whether to act on the result. With
        // automatic updates off, circuitRF opens no socket for any reason.
        if (!policy.AutomaticUpdates)
            return new CheckResult(CheckOutcome.Disabled, Detail: policy.Reason);

        InstallSite site = _site();

        // WHY THIS IS NOT A RETURN. R-AU-1 and design §1.1 both say a read-only install is
        // NOTIFY-ONLY — "they check, and post a Message Panel line with a link, but never write".
        // Returning here skipped the CHECK as well as the write, so every .msi, .deb and
        // standard-user macOS install was silently never told a new version existed (found in a
        // second review, 2026-08-25). The half that must not happen is the WRITING; the check is
        // the point of the mode.
        string? notifyOnlyReason = null;
        if (site.Shape == InstallShape.AppImage)
        {
            // Never updated silently (it runs from a read-only mount), and NOT the read-only sentence
            // below: an AppImage in ~/Applications is exactly where it should be. Its line offers a
            // Download instead; see AnnounceAppImage.
            notifyOnlyReason = AppImageReason;
        }
        else if (!site.CanSelfUpdate)
        {
            notifyOnlyReason = $"This {UpdateApp.Name} installation is in a location this account "
                             + "cannot write, so it does not update itself.";
        }
        else if (!await PayloadVerifier.RunningBuildCanAcceptUpdatesAsync(site, ct, _trust).ConfigureAwait(false))
        {
            // R-AU-25's publisher check compares the staged payload against the RUNNING application,
            // so a running build with no publisher identity of its own can never accept anything — an
            // ad-hoc macOS bundle or an unsigned Windows publish. Asking that BEFORE the feed rather
            // than after the unpack stops a developer build fetching and discarding the full ~160 MB
            // payload on a timer, and stops the refusal being recorded in the blacklist — which is
            // shared with the real installation on the same machine, since AppDataRoot is one
            // directory, and would have permanently withheld a perfectly good release from it.
            //
            // It is a NOTIFY-ONLY reason and not a silent stop, because the cause is fixable by
            // whoever built the binary and invisible to whoever is running it.
            notifyOnlyReason = UnsignedBuildReason;
        }

        UpdateState state = UpdateStateIo.Load();

        if (!manual && state.LastCheckUtc is DateTime last &&
            DateTime.UtcNow - last < CheckInterval)
            return new CheckResult(CheckOutcome.Throttled);

        if (!SemanticVersion.TryParse(AppVersion.Display, out SemanticVersion? running) || running is null)
            return new CheckResult(CheckOutcome.Failed, Detail: "the running version is not a version");

        UpdatePlatform? platform = UpdateAssetNames.CurrentPlatform();
        if (platform is null) return new CheckResult(CheckOutcome.Failed, Detail: "unsupported platform");

        // ── 1. reclaim our own debris, BEFORE measuring space ────────────────────────────────
        var reclaimer = new UpdateReclaimer(
            UpdatePaths.Root,
            site.Shape == InstallShape.VersionedPointer ? site.Root : null,
            previousVersionDirectoryName: state.PreviousPath);

        // Not on the notify-only path: that mode writes nothing, and it has no debris of its own
        // because it never staged anything.
        if (notifyOnlyReason is null)
            try { reclaimer.ReclaimDebris(); } catch { /* the next launch tries again */ }

        // ── 2. the feed ──────────────────────────────────────────────────────────────────────
        UpdateCandidate? candidate;
        try
        {
            FeedsCreated++;
            IUpdateFeed feed = _feedFactory();

            candidate = await UpdateSelector.SelectAsync(
                feed, await feed.ListReleasesAsync(ct).ConfigureAwait(false),
                running, policy.IncludeBetas, UpdateApp.Name, platform.Value,
                RuntimeInformation.ProcessArchitecture, ct, _trust, site.Shape).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new CheckResult(CheckOutcome.Failed, Detail: "cancelled"); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return new CheckResult(CheckOutcome.Failed, Detail: e.Message);
        }

        // A successful CHECK updates the timestamp even when nothing came of it — otherwise a
        // machine with no updates available would re-ask every time the app started.
        UpdateStateIo.Update(s =>
        {
            s.LastCheckUtc = DateTime.UtcNow;
            if (candidate?.FeedUrl is not null) s.FeedUrl = candidate.FeedUrl;
        });

        if (candidate is null)
            return new CheckResult(CheckOutcome.UpToDate, Detail: notifyOnlyReason ?? "");

        string version = candidate.Release.VersionText;

        // ── 2b. notify-only: one line with a link, and not one byte written ─────────────────
        if (site.Shape == InstallShape.AppImage)
        {
            // A version whose AppImage already failed its hash is not offered again: the button would
            // fetch the same bytes and refuse them the same way.
            if (state.IsBlacklisted(version))
                return new CheckResult(CheckOutcome.UpToDate, Detail: "blacklisted");

            AnnounceAppImage(candidate, site, manual);
            return new CheckResult(CheckOutcome.NotifyOnly, version, Detail: notifyOnlyReason!);
        }

        if (notifyOnlyReason is not null)
        {
            AnnounceNotifyOnly(version, notifyOnlyReason, manual);
            return new CheckResult(CheckOutcome.NotifyOnly, version, Detail: notifyOnlyReason);
        }

        if (state.IsBlacklisted(version))
            return new CheckResult(CheckOutcome.UpToDate, Detail: "blacklisted");

        if (string.Equals(state.StagedVersion, version, StringComparison.Ordinal))
            return new CheckResult(CheckOutcome.Staged, version, Detail: AlreadyStagedDetail);

        // A version that has ALREADY been swapped in and is waiting to prove it starts is not a
        // version to fetch again — and re-fetching it was destructive, not merely wasteful (found in
        // a second review, 2026-08-25). `RecordSwap` clears StagedVersion when it flips the pointer,
        // so only PendingVersion records it; without this guard Help ▸ Check for Updates… (which
        // ignores the throttle) re-ran the whole fetch with destinationDir = <root>/app-<version>,
        // and UpdateStager.Promote deletes an existing destination before renaming into it — i.e. it
        // deleted the directory `current` names. A failure or a crash between those two steps leaves
        // the stub with a pointer to a directory that is not there and an application that will not
        // start. UpdateStager.Promote now refuses that delete as well; this is the cheap half.
        if (string.Equals(state.PendingVersion, version, StringComparison.Ordinal))
            return new CheckResult(CheckOutcome.Staged, version, Detail: AlreadyStagedDetail);

        // ── 3. space, against PEAK, before a byte is transferred ─────────────────────────────
        long download = candidate.Asset.Size > 0 ? candidate.Asset.Size : 0;
        long expanded = UpdateSpace.EstimateExpandedBytes(download);
        long required = UpdateSpace.RequiredFreeSpace(download, expanded);

        Directory.CreateDirectory(UpdatePaths.Staging);

        bool Enough() => _space.AvailableFreeSpace(UpdatePaths.Staging) >= required;

        if (!Enough())
        {
            reclaimer.ReclaimUntil(Enough, previousVersionReleasable: (state.LaunchAttempts ?? 0) == 0);

            if (!Enough())
            {
                long available = _space.AvailableFreeSpace(UpdatePaths.Staging);
                ReportSpace(manual, required, available);
                return new CheckResult(CheckOutcome.InsufficientSpace, version, required, available);
            }
        }

        // ── 4-6. download, verify, stage ────────────────────────────────────────────────────
        return await FetchVerifyStageAsync(
                         candidate, site, required, running.ToString(), manual, ct, cancellation)
                     .ConfigureAwait(false);
    }

    /// <summary>
    /// Owns the live Messages row for the whole of download, verify and stage, and settles it on
    /// every exit path.
    ///
    /// <para><b>One row, not one per phase.</b> The download is the only phase with an honest
    /// denominator, but it is not the only slow one — a macOS <c>codesign --verify --deep</c> over a
    /// 335 MB unpacked bundle is minutes of its own. A bar that reaches 100% and is then replaced by
    /// silence has moved the stall rather than removed it, so the same row carries on
    /// indeterminate through the phases that cannot be counted.</para>
    ///
    /// <para><b>The row is settled in a <c>finally</c>.</b> There are a dozen ways out of the core
    /// below and most of them are silent by design (§9's "failure is silent"); a row left live is a
    /// bar that animates for the rest of the session. Silence about the CAUSE is the policy — a
    /// permanently spinning bar is not what it asks for.</para>
    /// </summary>
    private async Task<CheckResult> FetchVerifyStageAsync(
        UpdateCandidate candidate, InstallSite site, long required, string runningVersion,
        bool manual, CancellationToken ct, RunCancellation? cancellation)
    {
        string version = candidate.Release.VersionText;
        string title   = $"Downloading {UpdateApp.Name} {version}";

        IProgressMessage? live = _messages?.BeginProgress(title);

        // Before the first byte moves, so a Cancel is available for the whole of the transfer rather
        // than from whenever the first progress report happens to arrive.
        live?.BindCancellation(cancellation);

        CheckResult result;
        try
        {
            result = await FetchVerifyStageCoreAsync(
                candidate, site, required, runningVersion, manual, ct, live, title)
                .ConfigureAwait(false);
        }
        catch
        {
            live?.Complete(MessageLevel.Info, $"{title} - stopped.");
            throw;
        }

        SettleDownloadRow(live, title, runningVersion, result);
        return result;
    }

    /// <summary>
    /// How a finished row reads. <b>Only two outcomes are allowed to be visible as such</b> (design
    /// §13.5): a staged update and a full disk. Everything else settles quietly at
    /// <see cref="MessageLevel.Info"/> — the row is removed as a live bar without being promoted into
    /// a warning the policy says not to post.
    /// </summary>
    private void SettleDownloadRow(IProgressMessage? live, string title, string runningVersion,
                                   CheckResult result)
    {
        // The staged case settles the row INTO the announcement, so the bar the user has been
        // watching turns into the Relaunch button on that same line (owner request, 2026-09-09).
        // The download and the offer it was leading up to are one event, and were previously drawn
        // as two rows — a "- done." line, and the thing the user actually wanted underneath it.
        //
        // Announce still owns the once-per-version gate, so a version already announced settles the
        // row the way it always did: there is no second offer to make, and a row left saying
        // "installing" would be worse than a plain "- done."
        if (result.Outcome == CheckOutcome.Staged)
        {
            if (Announce(runningVersion, result.Version ?? "", live)) return;
            live?.Complete(MessageLevel.Info, $"{title} - done.");
            return;
        }

        if (live is null) return;

        switch (result.Outcome)
        {
            case CheckOutcome.Cancelled:
                // Deliberately does NOT promise a resume. UpdateDownloader keeps the .partial and can
                // resume from it, but ReclaimDebris deletes the whole of staging/ at the start of
                // every check (UpdateService step 1), so in the shipping flow the next check starts
                // the transfer over. Saying "we kept what arrived" would be a comfortable sentence
                // that is not true of the system, only of one component in it.
                live.Complete(MessageLevel.Info, $"{title} - stopped.");
                break;

            case CheckOutcome.InsufficientSpace:
                // ReportSpace has already posted the figures on its own row.
                live.Complete(MessageLevel.Info, $"{title} - not enough disk space.");
                break;

            default:
                live.Complete(MessageLevel.Info, $"{title} - did not complete.");
                break;
        }
    }

    private async Task<CheckResult> FetchVerifyStageCoreAsync(
        UpdateCandidate candidate, InstallSite site, long required, string runningVersion,
        bool manual, CancellationToken ct, IProgressMessage? live, string title)
    {
        string version = candidate.Release.VersionText;

        // A SEPARATE client from the feed's: HttpClient.Timeout bounds the whole operation including
        // the response body, so the feed's 30 s would be 30 s to move 160 MB. Progress is policed by
        // an idle timeout inside the read loop instead.
        using HttpClient http = UpdateDownloader.CreateDownloadHttpClient();
        var downloader = new UpdateDownloader(http, _space);

        // The row starts indeterminate rather than at zero: between here and the first buffer sits a
        // DNS lookup, a TLS handshake and a redirect to the asset host, which on a slow link is the
        // several seconds that prompted this feature. A bar pinned at 0% reads as stuck.
        live?.Update(title, indeterminate: true);

        IProgress<long>? progress = live is null
            ? null
            : new DownloadProgressReporter(live, title, candidate.Asset.Size);

        DownloadResult dl = await downloader
            .DownloadAsync(candidate.Asset, UpdatePaths.Staging, required, progress, ct).ConfigureAwait(false);

        if (dl.Outcome == DownloadOutcome.OutOfSpace)
        {
            long available = _space.AvailableFreeSpace(UpdatePaths.Staging);
            ReportSpace(manual, required, available);
            return new CheckResult(CheckOutcome.InsufficientSpace, version, required, available);
        }

        // The user's own Cancel, which is not a failure and must not be reported as one: the
        // .partial stays on disk and the next check resumes from it (UpdateDownloader's read loop),
        // so nothing was lost and nothing is owed an explanation.
        if (dl.Outcome == DownloadOutcome.Cancelled)
            return new CheckResult(CheckOutcome.Cancelled, version);

        if (dl.Outcome != DownloadOutcome.Completed || dl.Path is null)
            return new CheckResult(CheckOutcome.Failed, version, Detail: dl.Outcome.ToString());

        // A SIGNED manifest's SHA-256 is not best-effort — it is the whole of the integrity guarantee
        // on Linux and for the Windows Python payload, because it is the only thing carrying the
        // signature's proof through to the bytes (design §15.5). UpdateSelector already refuses a
        // signed manifest that names no hash; this is the assertion at the point of use, so that no
        // future path can arrive here having skipped it.
        if (candidate.ManifestSigned && !UpdateManifest.IsSha256Hex(candidate.Asset.Sha256))
            return Reject(version, dl.Path, null, "a signed manifest named no digest for this asset",
                          verificationFailure: true);

        // Hash first — cheap, and it catches a truncated or substituted transfer before an unpack.
        live?.Update(title, "verifying", indeterminate: true);

        VerifyResult hash = await PayloadVerifier
            .VerifyHashAsync(dl.Path, candidate.Asset.Sha256, ct).ConfigureAwait(false);

        if (!hash.Ok) return Reject(version, dl.Path, null, hash.Detail, verificationFailure: true);

        live?.Update(title, "installing", indeterminate: true);

        var stager = new UpdateStager(_space);
        StageResult staged;

        // The signature and the identity run against the .partial tree, BEFORE the rename that gives
        // it a real name (R-AU-27). Verifying afterwards left an unverified app-<ver> holding a real
        // name in the live install root whenever the process died in between.
        Task<VerifyResult> Verify(string partialPath) => VerifyStagedAsync(site, partialPath, ct, _trust);

        if (site.Shape == InstallShape.MacOsBundle)
        {
            // The CONTAINER, before hdiutil is handed it. Mounting is the one step that gives
            // attacker-supplied bytes to a kernel filesystem parser, so it does not happen until the
            // image itself carries our Developer ID (security review, 2026-08-25).
            VerifyResult image = await PayloadVerifier
                .VerifyMacImageAsync(dl.Path, site.Root, ct).ConfigureAwait(false);

            if (!image.Ok) return Reject(version, dl.Path, null, image.Detail, verificationFailure: true);

            string bundleName = UpdateApp.Name + ".app";
            Directory.CreateDirectory(UpdatePaths.Staged);

            staged = await stager.StageMacBundleAsync(
                dl.Path, bundleName,
                Path.Combine(UpdatePaths.Staged, version, bundleName), ct, Verify).ConfigureAwait(false);
        }
        else
        {
            string exe = OperatingSystem.IsWindows() ? UpdateApp.Name + ".exe" : UpdateApp.Name;
            staged = await stager.StageArchiveAsync(
                dl.Path, Path.Combine(site.Root, UpdateInstallSite.VersionDirPrefix + version),
                exe, ct, Verify).ConfigureAwait(false);
        }

        if (!staged.Ok || staged.StagedPath is null)
            return Reject(version, dl.Path, staged.StagedPath, staged.Detail,
                          verificationFailure: staged.Outcome == StageOutcome.VerificationFailed,
                          installRoot: site.Root);

        // The download can go the moment unpacking succeeded — which drops the requirement from peak
        // to transient partway through, even though the CHECK was made against peak.
        try { File.Delete(dl.Path); } catch { /* the reclaim takes it */ }

        UpdateStateIo.Update(s =>
        {
            s.StagedVersion      = version;
            s.StagedIsPreRelease = candidate.Release.IsPreRelease;
            s.StagedPath = site.Shape == InstallShape.MacOsBundle
                ? staged.StagedPath
                : UpdateInstallSite.VersionDirPrefix + version;
        });

        // The announcement is made by SettleDownloadRow, on the row this run has been drawing —
        // see there for why it is not posted here.
        return new CheckResult(CheckOutcome.Staged, version);
    }

    private static async Task<VerifyResult> VerifyStagedAsync(
        InstallSite site, string stagedPath, CancellationToken ct, ReleaseTrust trust)
    {
        if (site.Shape == InstallShape.MacOsBundle)
            return await PayloadVerifier.VerifyMacBundleAsync(stagedPath, site.Root, ct).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            // Design §15.5: an unsigned Windows build carrying a release key is anchored by the
            // signed manifest's SHA-256 instead, which is verified before this runs and covers every
            // byte rather than only the PEs. A SIGNED build is still checked here, so a certificate
            // adds a second anchor rather than replacing the key.
            if (!PayloadVerifier.WindowsPlatformCheckApplies(
                    PayloadVerifier.RunningWindowsPublisher(),
                    trust.RequireSignedManifest))
                return new VerifyResult(VerifyOutcome.NotApplicable,
                                        "unsigned build; the signed manifest's digest is the anchor");

            string exe = UpdateApp.Name + ".exe";
            return PayloadVerifier.VerifyWindowsTree(
                stagedPath, Path.Combine(AppContext.BaseDirectory, exe), exe);
        }

        // Linux has no signing infrastructure to check against; TLS and the hash are what there is.
        return new VerifyResult(VerifyOutcome.NotApplicable, "no platform signature to check");
    }

    /// <summary>
    /// Discard and say nothing — and <b>blacklist only when verification is what failed</b>.
    ///
    /// <para>The blacklist is permanent, and it is shared: <c>AppDataRoot</c> is one directory for
    /// all three applications and every build of them, so an entry withholds that release from the
    /// real installation on the machine too. That is the right price for a payload that was not
    /// signed by us — it is not a thing to retry — and the wrong price for a <c>tar</c> that was not
    /// on the box, a file another process had open, or any of the other transient reasons an unpack
    /// fails. Those used to blacklist as well, which stranded a user on their current version
    /// permanently and silently, with the next check's log line the only evidence anywhere (security
    /// review, 2026-08-25). A transient failure now simply retries at the next check.</para>
    /// </summary>
    private static CheckResult Reject(
        string version, string? download, string? staged, string detail, bool verificationFailure,
        string? installRoot = null)
    {
        UpdateStager.Discard(staged, installRoot);
        try { if (download is not null) File.Delete(download); } catch { /* the reclaim takes it */ }

        if (verificationFailure) UpdateStateIo.Update(s => s.Blacklist_Add(version));
        return new CheckResult(CheckOutcome.Failed, version, Detail: detail);
    }

    // ── the only two things the user ever sees ───────────────────────────────────────────────

    /// <summary>
    /// R-AU-47. One Info line per staged version, named after the RUNNING application and worded
    /// from the installed version to the newly staged one — so if several versions stage before the
    /// user relaunches, the last line on screen is always the true end state.
    ///
    /// <para><b>The line carries a Relaunch button, and the original refusal to have one is answered
    /// rather than reversed</b> (owner request, 2026-09-06). The refusal was that the app can be
    /// holding unsaved workspaces, so a one-click relaunch invites data loss to save a keystroke.
    /// That is still true, and it is the reason the button does not restart anything itself: it runs
    /// the SAME shutdown the File menu's Quit runs, so every window is asked about its unsaved work
    /// first and any prompt the user cancels calls the whole relaunch off. What the button removes is
    /// the keystroke, not the question. The workspaces open at that moment are reopened by the new
    /// version (<see cref="RelaunchSession"/>), which is the other half of why it is now worth
    /// offering: the disruption an update costs the user is meant to be nothing at all.</para>
    ///
    /// <para><b>The sentence still stands on its own with no button.</b> A build with no handler
    /// installed — harmonicaRF, wBond, a headless sink — posts exactly the line it always did.</para>
    /// </summary>
    /// <param name="row">The live download row, when there is one — settled INTO the announcement
    /// rather than followed by it.</param>
    /// <returns>Whether the announcement was made; false when this version has already been
    /// announced, which leaves the caller owing the row an ordinary outcome.</returns>
    private bool Announce(string from, string to, IProgressMessage? row = null)
    {
        UpdateState s = UpdateStateIo.Load();
        if (s.Announced is not null && s.Announced.Contains(to)) return false;

        PostAnnouncement(_messages, from, to, row);
        UpdateStateIo.Update(st => st.Announced_Add(to));
        return true;
    }

    /// <summary>
    /// The announcement itself, separated from the once-per-version bookkeeping around it so the
    /// wording and the link can be driven directly. Reaching it through <see cref="CheckAsync"/>
    /// means faking a signed release, a download and an unpack, none of which this decision depends
    /// on — and a decision only a test can reach is a decision the application does not make.
    /// </summary>
    internal static void PostAnnouncement(
        Messages.IMessageSink? sink, string from, string to, Messages.IProgressMessage? row = null)
    {
        // TWO WORDINGS, AND THE BUTTON IS WHAT DECIDES WHICH (owner request, 2026-09-09: the line
        // and the button have to fit on ONE line). Next to a button, three clauses wrap onto a
        // second and third row and push the button down with them — the button lands wherever the
        // sentence happens to end, which is the layout the shortening exists to remove.
        //
        // SHORTENED AGAIN, to the outcome and nothing else (owner request, 2026-09-10): once the
        // install has finished there is nothing left to explain, and the row is the download's own
        // row — the same line that has been reading "installing" while it worked, now reading
        // "installed" with the button where its bar was. The instruction is the button.
        //
        // The one thing this gives up is the four-word instruction the line used to carry for a sink
        // that cannot render an action (IMessageSink.PostAction's default drops the button and posts
        // the text). That case cannot arise in circuitRF — the App that installs the relaunch
        // handler is the one that owns MessagesTool, which implements PostAction — and the build
        // with no handler at all, harmonicaRF and wBond and any headless sink, still gets the whole
        // sentence below.
        string shortLine = $"{UpdateApp.Name} {to} installed";

        // With no button, the full sentence, unchanged: it is the only thing telling the user what
        // to do, and a build with no handler (harmonicaRF, wBond, a headless sink) has no other
        // route to the instruction or to the setting.
        string spelledOut =
            $"{UpdateApp.Name} updated from {from} to {to} in the background. "
            + $"Relaunch {UpdateApp.Name} to start using the version. "
            + "Automatic updates can be disabled in Settings, under Security & Permissions.";

        // Just the verb: the line beside it already names the application, and repeating it there
        // is half the width of a row that exists to be short.
        string label = "Relaunch";
        Func<Task>? relaunchHandler = RelaunchRequest.Handler;

        // On the download's own row when there is one: its bar and its counter go, and the button
        // takes the bar's place. No row (a headless check, a sink with no live support) posts the
        // line as its own message exactly as before.
        if (row is not null)
        {
            if (relaunchHandler is { } onRow)
                row.CompleteWithAction(Messages.MessageLevel.Info, shortLine, label, onRow);
            else
                row.Complete(Messages.MessageLevel.Info, spelledOut);
            return;
        }

        if (sink is not null && relaunchHandler is { } relaunch)
            sink.PostAction(Messages.MessageLevel.Info, shortLine, label, relaunch);
        else
            sink?.Info(spelledOut);
    }

    /// <summary>
    /// R-AU-1's other half. A read-only install — the per-machine <c>.msi</c>, the <c>.deb</c>,
    /// <c>/Applications</c> as a standard user — <b>checks, and says so once</b>. It is the only
    /// thing those users ever get from this subsystem, and skipping it made the whole mode
    /// indistinguishable from the feature being off.
    ///
    /// <para>Once per version in the BACKGROUND, through the same <c>Announced</c> list the staging
    /// line uses, so a machine that checks daily for a month does not post the same line thirty
    /// times — and <b>every time on a manual check</b>, because a user who has just picked
    /// Help ▸ Check for Updates… and gets silence has been told nothing. Same asymmetry as
    /// <see cref="ReportSpace"/>, for the same reason.</para>
    ///
    /// <para>And no button: the user goes to the releases page in their own browser, in their own
    /// time. That is this line's own choice, not a rule it inherits: R-AU-48 is about a control that
    /// RELAUNCHES the application, which a link to a web page never was.</para>
    /// </summary>
    private void AnnounceNotifyOnly(string version, string reason, bool manual)
    {
        UpdateState s = UpdateStateIo.Load();
        if (!manual && s.Announced is not null && s.Announced.Contains(version)) return;

        _messages?.Info(
            $"{UpdateApp.Name} {version} is available. {reason} "
            + $"You can download it from {ReleasesPageUrl}");

        UpdateStateIo.Update(st => st.Announced_Add(version));
    }

    /// <summary>
    /// An AppImage install's line: <c>circuitRF &lt;ver&gt; is available</c>, with a Download button.
    ///
    /// <para><b>R-AU-48 does not apply to this button.</b> R-AU-48 forbids a control that relaunches
    /// the application, because the application may hold unsaved work. Download closes nothing and
    /// starts nothing: it saves a new file beside the running one, and the user starts it when they
    /// choose.</para>
    ///
    /// <para>Once per version in the background, through <c>Announced</c>, and every time on a manual
    /// check — the same asymmetry as <see cref="AnnounceNotifyOnly"/>.</para>
    ///
    /// <para><b>A folder this account cannot write</b> (an AppImage run from a read-only share) gets
    /// the same button, which opens the release page instead; nothing is written, and the line says
    /// what the browser's download will need before it runs.</para>
    /// </summary>
    private void AnnounceAppImage(UpdateCandidate candidate, InstallSite site, bool manual)
    {
        string version = candidate.Release.VersionText;
        UpdateState s = UpdateStateIo.Load();
        if (!manual && s.Announced is not null && s.Announced.Contains(version)) return;

        if (site.IsWritable)
        {
            _messages?.PostAction(MessageLevel.Info, $"{UpdateApp.Name} {version} is available",
                                  DownloadLabel, () => DownloadAppImageAsync(candidate, site));
        }
        else
        {
            string page = $"{ReleasesPageUrl}/tag/{Uri.EscapeDataString(candidate.Release.TagName)}";
            _messages?.PostAction(MessageLevel.Info,
                $"{UpdateApp.Name} {version} is available. This folder cannot be written, so Download "
                + "opens its release page; make the downloaded file executable (chmod +x) before running it.",
                DownloadLabel, () => { OpenLink(page); return Task.CompletedTask; });
        }

        UpdateStateIo.Update(st => st.Announced_Add(version));
    }

    /// <summary>
    /// The Download click: fetch the new AppImage beside the running one, verify it, make it
    /// executable, and say where it is. Nothing is replaced, deleted or relaunched.
    ///
    /// <para><b>Failure is reported, on the row.</b> R-AU-50's background silence is about checks the
    /// user never asked for; this is a click, and silence after a click is no answer at all — the
    /// asymmetry R-AU-49 already makes for a manual check.</para>
    /// </summary>
    internal async Task DownloadAppImageAsync(UpdateCandidate candidate, InstallSite site)
    {
        if (Interlocked.Exchange(ref _appImageBusy, 1) == 1) return;
        try { await DownloadAppImageCoreAsync(candidate, site).ConfigureAwait(false); }
        finally { Volatile.Write(ref _appImageBusy, 0); }
    }

    private async Task DownloadAppImageCoreAsync(UpdateCandidate candidate, InstallSite site)
    {
        string version = candidate.Release.VersionText;
        ReleaseAsset asset = candidate.Asset;
        string title = $"Downloading {UpdateApp.Name} {version}";

        using var cts = new CancellationTokenSource();
        IProgressMessage? row = _messages?.BeginProgress(title);
        row?.BindCancellation(new RunCancellation("the download", cts.Cancel));

        void Fail(string why) => row?.Complete(MessageLevel.Warning, $"{title} - {why}");

        // A keyed build takes its digest from the SIGNED manifest and from nowhere else; without one
        // the bytes are anchored by nothing but the host that served them.
        if (_trust.RequireSignedManifest &&
            (!candidate.ManifestSigned || !UpdateManifest.IsSha256Hex(asset.Sha256)))
        {
            Fail("the release's signed manifest names no digest for it, so it was not downloaded.");
            return;
        }

        if (!UpdateAssetNames.IsSafeAssetFileName(asset.Name))
        {
            Fail("the release names it in a way that cannot be a file name, so it was not downloaded.");
            return;
        }

        string final   = Path.Combine(site.Root, asset.Name);
        string partial = final + UpdatePaths.PartialSuffix;

        void Done() => row?.CompleteWithAction(MessageLevel.Info,
            $"{UpdateApp.Name} {version} downloaded to {final}", "Open Folder",
            () => { RevealFile(final); return Task.CompletedTask; });

        // Already here, and the right bytes: nothing to fetch.
        if (File.Exists(final) && UpdateManifest.IsSha256Hex(asset.Sha256) &&
            (await PayloadVerifier.VerifyHashAsync(final, asset.Sha256, cts.Token).ConfigureAwait(false)).Ok)
        {
            Done();
            return;
        }

        long required = UpdateSpace.RequiredFreeSpace(asset.Size > 0 ? asset.Size : 0, 0);
        long available = _space.AvailableFreeSpace(site.Root);
        if (available < required)
        {
            Fail($"it needs about {UpdateSpace.FormatBytes(required)} of free disk space and there is "
                 + $"{UpdateSpace.FormatBytes(available)} available.");
            return;
        }

        void Discard()
        {
            try { File.Delete(partial); } catch { /* best effort */ }
            try { File.Delete(partial + UpdatePaths.PartialSuffix); } catch { /* best effort */ }
        }

        try
        {
            // The downloader lands a transfer in <name>.partial and gives it <name> once the byte count
            // is right. Handing it "<asset>.partial" as the name makes ITS final name our .partial, so
            // the real name is given only after the hash and the mode below — nothing unverified ever
            // sits beside the running AppImage under a name someone would start.
            using HttpClient http = DownloadClient();
            var downloader = new UpdateDownloader(http, _space);
            IProgress<long>? progress = row is null ? null : new DownloadProgressReporter(row, title, asset.Size);

            DownloadResult dl = await downloader
                .DownloadAsync(asset with { Name = asset.Name + UpdatePaths.PartialSuffix },
                               site.Root, required, progress, cts.Token)
                .ConfigureAwait(false);

            switch (dl.Outcome)
            {
                case DownloadOutcome.Completed when dl.Path is not null: break;
                case DownloadOutcome.Cancelled:  Discard(); row?.Complete(MessageLevel.Info, $"{title} - stopped."); return;
                case DownloadOutcome.OutOfSpace: Discard(); Fail("the disk filled during the download."); return;
                default:                         Discard(); Fail("the download did not complete."); return;
            }

            row?.Update(title, "verifying", indeterminate: true);
            VerifyResult hash = await PayloadVerifier.VerifyHashAsync(partial, asset.Sha256, cts.Token).ConfigureAwait(false);
            if (!hash.Ok)
            {
                Discard();
                UpdateStateIo.Update(st => st.Blacklist_Add(version));
                Fail("it does not match the digest the release published, so it was deleted.");
                return;
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(partial,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            AtomicFile.ReplaceOrMove(partial, final);
            Done();
        }
        catch (OperationCanceledException)
        {
            Discard();
            row?.Complete(MessageLevel.Info, $"{title} - stopped.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Discard();
            Fail(e.Message);
        }
    }

    /// <summary>
    /// The one exception to silence, and it is narrow. Being offline is often permanent and often
    /// intentional; a full disk is an accident the user wants to know about and can act on. So: the
    /// manual check reports it with figures, and the background check posts at most ONE line per
    /// 30 days — information, not nagging.
    /// </summary>
    private void ReportSpace(bool manual, long required, long available)
    {
        string line = $"{UpdateApp.Name} needs about {UpdateSpace.FormatBytes(required)} of free disk "
                    + $"space to install the update and there is {UpdateSpace.FormatBytes(available)} "
                    + "available. The update was not downloaded.";

        if (manual) { _messages?.Warning(line); return; }

        UpdateState s = UpdateStateIo.Load();
        if (s.LastSpaceNoticeUtc is DateTime when && DateTime.UtcNow - when < SpaceNoticeInterval) return;

        _messages?.Warning(line);
        UpdateStateIo.Update(st => st.LastSpaceNoticeUtc = DateTime.UtcNow);
    }
}
