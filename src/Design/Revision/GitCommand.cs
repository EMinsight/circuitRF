using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace CircuitRF.Design.Revision;

/// <summary>What one git invocation did.</summary>
/// <param name="ExitCode">git's own exit code; -1 when it could not be started at all.</param>
/// <param name="StdOut">Standard output, whole.</param>
/// <param name="StdErr">Standard error, whole. Where git puts its diagnostics.</param>
/// <param name="Started">False when the process never began — a path that is not executable.</param>
/// <param name="TimedOut">True when the bound was reached and the process was killed.</param>
/// <param name="Cancelled">True when the caller's token was signalled and the process was killed.</param>
public sealed record GitResult(
    int ExitCode, string StdOut, string StdErr, bool Started, bool TimedOut, bool Cancelled)
{
    public bool Ok => Started && !TimedOut && !Cancelled && ExitCode == 0;

    /// <summary>Standard output with the trailing newline off — what a single-value query wants.</summary>
    public string Line => StdOut.Trim('\n', '\r', ' ', '\t');
}

/// <summary>How one invocation differs from the default.</summary>
/// <param name="StandardInput">Text piped to git's stdin, or null. Used for a commit message, which
/// must never be assembled into a command line.</param>
/// <param name="ReadOnly">
/// <b>A read never waits on a writer</b> (R-rc3-1b, §4.6). Listing restore points, resolving a pin or
/// computing an archive's figures must not block behind a pack, so a read sets
/// <c>GIT_OPTIONAL_LOCKS=0</c> and git skips the index refresh that would take the index lock.
/// </param>
/// <param name="Network">
/// The bound is INACTIVITY rather than wall-clock (§4.5, §9.1). A fixed limit turns RC-9's legitimate
/// slow clone of a large library into a reported failure, which is not the defect the bound exists for.
/// </param>
/// <param name="IndexFile">
/// A private <c>GIT_INDEX_FILE</c>. <b>A checkpoint never touches the shared index</b> (R-rc3-1b,
/// §5.2b): the one resource git's writers actually contend on is left to the designer's own shell, and
/// two circuitRF processes contend only on reference updates, which git makes atomic.
/// </param>
/// <param name="Timeout">Overrides the default bound for this call.</param>
/// <param name="SafeDirectories">
/// Extra paths named as <c>safe.directory</c> alongside the workspace root (R-rc9-5b, §4.7). <b>A
/// clone touches two trees, not one</b> — the destination's parent and, when the source is a local
/// path, the source repository — and git's ownership check applies to each of them separately. Named
/// individually, never <c>*</c>, exactly as the root itself is.
/// </param>
/// <param name="Environment">
/// Variables set for this one invocation, <b>applied after <see cref="GitEnvironment.Apply"/> so a
/// caller can override what it sets</b> (RC-11 R-rc11-10).
///
/// <para>Its one use is a correction that must not restamp the version it corrects: the author, the
/// committer and both recorded times are handed back to <c>commit-tree</c> through git's own
/// <c>GIT_AUTHOR_*</c> and <c>GIT_COMMITTER_*</c> variables, which is the only interface git offers
/// for writing an object with a time other than now. <b>Nothing here may carry a credential</b>
/// (§9.1) — circuitRF holds none and supplies none.</para>
/// </param>
/// <param name="StdErrSegment">
/// Called, on a background thread, with each piece of standard error as it arrives, split at every
/// <c>\r</c> and <c>\n</c>. <b>Git redraws a progress line with <c>\r</c></b>, so splitting at the
/// newline alone would hold the whole of a clone's progress back until it finished. Standard error is
/// still collected whole into <see cref="GitResult.StdErr"/> either way.
/// </param>
public sealed record GitRunOptions(
    string?   StandardInput = null,
    bool      ReadOnly      = false,
    bool      Network       = false,
    string?   IndexFile     = null,
    TimeSpan? Timeout       = null,
    IReadOnlyList<string>? SafeDirectories = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    Action<string>? StdErrSegment = null);

/// <summary>
/// <b>The one type that starts a git process.</b> Every git invocation circuitRF makes goes through
/// here, and no git library is linked, ever.
///
/// <para><b>Why a subprocess at all</b> (<c>revision-control.md</c> §4.1):</para>
/// <list type="bullet">
///   <item><description><b>The escape hatch survives.</b> The repository is an ordinary git
///   repository, readable and repairable by every existing tool, including the one the user's IT
///   department already supports. Anything circuitRF's UI cannot fix, the command line can — worth
///   more than any convenience an embedded library would buy, and lost the moment circuitRF writes a
///   format only circuitRF understands.</description></item>
///   <item><description><b>The failure mode is an exit code</b>, not an in-process fault in a native
///   library, which matters given this repo's standing cross-platform native-dependency
///   policy.</description></item>
///   <item><description><b>Absence is detectable and cheap</b> — see <see cref="GitDiscovery"/>, and
///   the silence R-rc3-3 requires of it.</description></item>
/// </list>
///
/// <para><b>Why it is in <c>src/Design</c></b> (R-rc3-1). <c>src/Cli</c> must be able to take a
/// checkpoint headlessly — §1.2's agent is out of process — and <c>src/Cli</c> cannot reference
/// <c>src/Ui</c>. Anything above the firewall would put the safety net out of reach of the very caller
/// it is written for. <c>tests/Firewall.Tests</c> is what holds this.</para>
///
/// <para><b><c>src/Ui/Updates/ProcessRunner.cs</c> is the in-repo precedent, read and not imported</b>
/// — it lives above the firewall, so this project cannot reference it. The trap in its header applies
/// here word for word: <b>a bare program name resolves through <c>PATH</c>, and <c>PATH</c> is
/// attacker-influenced on the platforms this targets.</b> <see cref="GitDiscovery"/> resolves the
/// configured or discovered path to an absolute one ONCE, and this starts that.</para>
///
/// <para><b>Every call is bounded.</b> A subprocess this type cannot bound is a subprocess that can
/// wedge a workspace close.</para>
/// </summary>
public sealed class GitCommand
{
    /// <summary>Wall-clock bound for an ordinary local call.</summary>
    public static TimeSpan LocalTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Bound for a long local call that is expected to work hard — packing (R-rc3-13). Also
    /// cancellable, which is the part that matters: R-rc3-15 requires packing to be abandonable.</summary>
    public static TimeSpan PackTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>INACTIVITY bound for a network call — see <see cref="GitRunOptions.Network"/>.</summary>
    public static TimeSpan NetworkInactivityTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public GitInstallation Installation { get; }

    /// <summary>The open workspace's root. The working directory of every call, and the one path named
    /// as <c>safe.directory</c>. <b>circuitRF writes to exactly one repository</b> (R-rc0-5): not an
    /// ancestor, not a referenced workspace, not a nested one.</summary>
    public string WorkspaceRoot { get; }

    /// <summary>Who commits. Null means git's own resolution applies (§4.4).</summary>
    public GitIdentity? Identity { get; set; }

    public GitCommand(GitInstallation installation, string workspaceRoot, GitIdentity? identity = null)
    {
        Installation  = installation;
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        Identity      = identity;
    }

    /// <summary>
    /// A driver for <paramref name="workspaceRoot"/>, or <b>null when there is no usable git</b> —
    /// which is R-rc3-3's silence expressed as a type: a caller that cannot get one has nothing to
    /// report and nothing to disable.
    /// </summary>
    public static GitCommand? For(string workspaceRoot, GitIdentity? identity = null)
        => GitDiscovery.Find(out _) is { } installation
            ? new GitCommand(installation, workspaceRoot, identity)
            : null;

    public GitResult Run(params string[] arguments) => Run(arguments, null, CancellationToken.None);

    public GitResult Run(IReadOnlyList<string> arguments, GitRunOptions? options,
                         CancellationToken ct = default)
    {
        options ??= new GitRunOptions();

        var psi = new ProcessStartInfo(Installation.Path)
        {
            WorkingDirectory       = WorkspaceRoot,   // explicit, never inherited (§4.5)
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = options.StandardInput is not null,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (string a in GitEnvironment.GlobalArguments(WorkspaceRoot)) psi.ArgumentList.Add(a);
        foreach (string extra in options.SafeDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("safe.directory=" + extra);
        }
        foreach (string a in arguments) psi.ArgumentList.Add(a);

        GitEnvironment.Apply(psi.Environment, Identity);
        if (options.ReadOnly)  psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (options.IndexFile is { Length: > 0 } index) psi.Environment["GIT_INDEX_FILE"] = index;

        // LAST, so a caller that has to write an object with a recorded identity and a recorded time
        // (RC-11 R-rc11-10) overrides what Apply set from `Identity` rather than being overridden by it.
        foreach (var (key, value) in options.Environment ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

        TimeSpan bound = options.Timeout
                      ?? (options.Network ? NetworkInactivityTimeout : LocalTimeout);

        var result = Execute(psi, options.StandardInput, bound, options.Network, options.StdErrSegment, ct);

        // R-rc3-1b. Git's own lock files serialise what a private index does not — they are
        // atomic-create files, which is the correct primitive and the one a second implementation
        // would have had to reinvent. A COLLISION BETWEEN TWO AUTOMATIC OPERATIONS IS A DELAY, NOT AN
        // ERROR, so it is retried for a short bound before GitFailures translates it.
        //
        // The bound is also what separates a collision from a CRASHED process's leftover: a live
        // collision clears within a few tens of milliseconds, a stale lock is still there afterwards
        // and becomes GitFailures.StaleLock — which names the file and says removing it is safe.
        for (int attempt = 1; attempt <= LockRetries && !options.ReadOnly && !ct.IsCancellationRequested; attempt++)
        {
            if (!LooksLikeLockCollision(result)) break;
            Thread.Sleep(LockRetryDelayMs * attempt);
            result = Execute(psi, options.StandardInput, bound, options.Network, options.StdErrSegment, ct);
        }

        return result;
    }

    /// <summary>How many times a lock collision is retried, and the base delay between attempts —
    /// roughly a quarter of a second in total, which is far longer than a reference update takes and
    /// far shorter than anyone notices.</summary>
    private const int LockRetries      = 6;
    private const int LockRetryDelayMs = 12;

    /// <summary>
    /// Whether a failure is a contended lock file rather than a real refusal. Keyed on git's exit
    /// having failed AND the word <c>.lock</c> appearing in its output — not on a sentence, because
    /// git spells this several ways ("Unable to create '…/index.lock': File exists", "cannot lock ref",
    /// "could not lock config file").
    /// </summary>
    private static bool LooksLikeLockCollision(GitResult r)
        => r.Started && !r.TimedOut && !r.Cancelled && r.ExitCode != 0
           && (r.StdErr.Contains(".lock", StringComparison.Ordinal)
            || r.StdErr.Contains("cannot lock", StringComparison.OrdinalIgnoreCase));

    /// <summary>The arguments as they would appear on a command line — for a message, a log, or a test
    /// that asserts what was passed. Never used to BUILD one: every argument goes through
    /// <c>ArgumentList</c>.</summary>
    public IReadOnlyList<string> DescribeArguments(IReadOnlyList<string> arguments)
        => [.. GitEnvironment.GlobalArguments(WorkspaceRoot), .. arguments];

    private static GitResult Execute(
        ProcessStartInfo psi, string? stdin, TimeSpan bound, bool inactivity, Action<string>? onErrSegment,
        CancellationToken ct)
    {
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception e) { return new GitResult(-1, "", e.Message, false, false, false); }
        if (p is null) return new GitResult(-1, "", "could not start", false, false, false);

        using (p)
        {
            var outBuf   = new StringBuilder();
            var errBuf   = new StringBuilder();
            var lastSeen = new StrongBox<long>(Environment.TickCount64);

            Task pumpOut = Pump(p.StandardOutput, outBuf, lastSeen);
            Task pumpErr = Pump(p.StandardError,  errBuf, lastSeen, onErrSegment);

            if (stdin is not null)
            {
                try
                {
                    p.StandardInput.Write(stdin);
                    p.StandardInput.Flush();
                }
                catch (IOException) { /* git closed it early; its exit code is the real answer */ }
                finally { try { p.StandardInput.Close(); } catch { /* already closed */ } }
            }

            long started   = Environment.TickCount64;
            bool timedOut  = false;
            bool cancelled = false;

            while (!p.WaitForExit(25))
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                long since = Environment.TickCount64
                           - (inactivity ? Volatile.Read(ref lastSeen.Value) : started);
                if (since > (long)bound.TotalMilliseconds) { timedOut = true; break; }
            }

            if (timedOut || cancelled)
            {
                // Kill the tree: git delegates to helpers (a credential helper, ssh, gc's own child
                // processes) and killing only the parent leaves them holding the pipes open.
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                try { p.WaitForExit(5_000); } catch { /* nothing more to wait for */ }
            }

            try { Task.WaitAll([pumpOut, pumpErr], 5_000); } catch { /* the streams are closed */ }

            int code;
            try { code = p.HasExited ? p.ExitCode : -1; } catch { code = -1; }

            string so, se;
            lock (outBuf) so = outBuf.ToString();
            lock (errBuf) se = errBuf.ToString();

            return new GitResult(code, so, se, true, timedOut, cancelled);
        }
    }

    private static async Task Pump(StreamReader reader, StringBuilder into, StrongBox<long> lastSeen,
                                   Action<string>? onSegment = null)
    {
        var buffer  = new char[4096];
        var segment = onSegment is null ? null : new StringBuilder();
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                lock (into) into.Append(buffer, 0, n);
                Volatile.Write(ref lastSeen.Value, Environment.TickCount64);

                if (segment is null) continue;
                for (int i = 0; i < n; i++)
                {
                    char c = buffer[i];
                    if (c is not ('\r' or '\n')) { segment.Append(c); continue; }
                    if (segment.Length > 0) Report(onSegment!, segment.ToString());
                    segment.Clear();
                }
            }
            if (segment is { Length: > 0 }) Report(onSegment!, segment.ToString());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The process was killed out from under the read. What was collected before that is still
            // the honest answer, and it is already in `into`.
        }
    }

    /// <summary>An observer that throws must not take the pump down with it: the pump is what keeps
    /// the pipe drained, and a stalled pipe is a git that never exits.</summary>
    private static void Report(Action<string> onSegment, string text)
    {
        try { onSegment(text); }
        catch (Exception) { /* the observer's problem, never the process's */ }
    }

    // ── Small primitives every later brief needs, kept here so nobody writes a second one ──────────

    /// <summary>
    /// The repository root <see cref="WorkspaceRoot"/> falls inside, or null when it is in none.
    /// <b>It walks UP, never down</b> — which is why a repository NESTED inside the workspace needs a
    /// detection of its own (<see cref="NestedRepositories"/>, §12 Q30).
    /// </summary>
    public string? TopLevel()
    {
        var r = Run(["rev-parse", "--show-toplevel"], new GitRunOptions(ReadOnly: true));
        if (!r.Ok || r.Line.Length == 0) return null;
        try { return Path.GetFullPath(r.Line); } catch { return null; }
    }

    /// <summary>
    /// True when this workspace's own root IS the repository root — not an ancestor's, which R-rc0-5
    /// makes an entirely different situation.
    ///
    /// <para><b>Asked as <c>--show-prefix</c> rather than by comparing paths, and that is not a
    /// stylistic choice.</b> Comparing <see cref="TopLevel"/> against <see cref="WorkspaceRoot"/> as
    /// strings gets the answer WRONG wherever a symlink sits above the workspace, because git prints
    /// its own resolved view and .NET's <c>GetFullPath</c> does not resolve links: on macOS
    /// <c>/var/…</c> against git's <c>/private/var/…</c>, and on any machine whose home directory or
    /// network share is reached through one. The symptom is a repository circuitRF just created being
    /// reported as absent. <c>--show-prefix</c> is empty exactly at the top of the work tree, which is
    /// the question, and git answers it in its own coordinates.</para>
    /// </summary>
    public bool IsRepositoryRoot()
    {
        var r = Run(["rev-parse", "--show-prefix"], new GitRunOptions(ReadOnly: true));
        return r.Ok && r.StdOut.Trim().Length == 0;
    }
}
