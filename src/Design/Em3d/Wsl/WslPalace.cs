using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 — what a run needs to know about a Palace found in the Linux subsystem before it starts:
/// the distribution's home, the MPI launcher beside that Palace, and the memory the subsystem's VM has.
/// </summary>
public static class WslPalace
{
    /// <summary>
    /// The runner for <paramref name="palace"/>, which discovery found inside a distribution, or null with
    /// <paramref name="refusal"/> saying why the distribution cannot take the run.
    /// </summary>
    /// <param name="fieldDirectories">The field directories the run's configuration asks Palace to write
    /// (<see cref="PalaceConfigWriter.FieldDirectories"/>), copied back with the CSVs.</param>
    internal static WslPalaceRunner? Runner(IWsl wsl, SolverInstallation palace, IReadOnlyList<string> fieldDirectories,
                                            out string? refusal)
    {
        refusal = null;
        var session = new WslSession(wsl, palace.Distribution!);
        if (session.Home(out string? why) is not { } home)
        {
            refusal = $"Palace is in the Linux subsystem distribution '{palace.Distribution}', and {why}.";
            return null;
        }
        var (mpirun, how) = FindMpiLauncher(session, home, palace.Path);
        return new WslPalaceRunner(session, home, mpirun, how) { FieldDirectories = fieldDirectories };
    }

    /// <summary>
    /// R-em3d26-2b — the MPI Palace runs under inside the distribution, found the way series 1's Spack route
    /// finds it natively: an <c>mpirun</c> beside Palace; then the MPI Palace was LINKED against, from the
    /// Spack tree circuitRF installed it in (its record names the tree), then from the user's own Spack
    /// trees; then the distribution's login-shell <c>PATH</c>. A named launcher in Settings is a WINDOWS
    /// path and cannot run inside, so it is not consulted here.
    /// </summary>
    public static (string? Path, string How) FindMpiLauncher(WslSession session, string home, string palace)
    {
        string beside = WslPaths.Combine(WslPaths.Parent(palace), "mpirun");
        if (session.Exists(beside)) return (beside, "found beside Palace");

        var trees = SolverDiscovery.SubsystemRecords(session, home, SolverTool.Palace)
            .Where(r => r.SpackInstallTree is not null && palace.StartsWith(r.Home.TrimEnd('/') + "/", StringComparison.Ordinal))
            .Select(r => r.SpackInstallTree!).ToList();
        if (trees.Count > 0 && SpackInstalls.MpiLauncherFor(palace, trees, session.ToWindows) is { } own)
            return (own, "the MPI Palace was built with, from the Spack tree circuitRF installed it in");

        string[] roots = [WslPaths.Combine(home, "spack", "opt", "spack"), WslPaths.Combine(home, "opt", "spack"), "/opt/spack"];
        if (SpackInstalls.MpiLauncherFor(palace, roots, session.ToWindows) is { } linked)
            return (linked, "the MPI Palace was built with, from its Spack installation");

        var run = session.Exec(["bash", "-lc", WslSession.LoginShellCommandV, "bash", "mpirun"]);
        string path = run.Text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (run.Ok && path.StartsWith('/')) return (path, "found on the distribution's PATH");
        return (null, $"no MPI launcher (mpirun) was found beside Palace, in its Spack installation or on the PATH of " +
                      $"the Linux subsystem distribution '{session.Distribution}'");
    }

    /// <summary>
    /// R-em3d26-2c — the subsystem's memory as the run's memory check sees it: <c>free -b</c> inside the
    /// distribution, and the remedy naming <c>.wslconfig</c>'s <c>memory=</c>, with the file's path under
    /// the user's profile. Null when the figure cannot be read (the check then has nothing to compare with).
    /// </summary>
    /// <param name="hostBytes">This computer's memory — the most the subsystem could be given. Defaults to
    /// <see cref="MachineMemory.PhysicalBytes"/>, which on Windows is the host's figure.</param>
    public static Em3dMemoryScope? MemoryScope(WslSession session, string? userProfile = null, long? hostBytes = null)
    {
        if (session.MemoryBytes() is not { } bytes || bytes <= 0) return null;
        long host = hostBytes ?? MachineMemory.PhysicalBytes;
        return new Em3dMemoryScope(bytes, "the Linux subsystem's", estimate => WslConfigRemedy(estimate, bytes, host, userProfile));
    }

    /// <summary>The <c>.wslconfig</c> under <paramref name="userProfile"/> (default: this user's).</summary>
    public static string WslConfigPath(string? userProfile = null)
    {
        string profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length == 0 ? @"%UserProfile%\.wslconfig" : profile.TrimEnd('\\', '/') + @"\.wslconfig";
    }

    private const long GiB = 1L << 30;

    /// <summary>
    /// Designer feedback round 11 — the <c>memory=</c> to suggest on a computer with <paramref name="hostBytes"/>,
    /// in whole gigabytes: the host's memory less what Windows keeps for itself, the larger of 2 GB and a
    /// quarter of it, rounded down. 5 GB on an 8 GB laptop, 24 GB on 32 GB. It used to be a fixed
    /// "memory=24GB", which on the reporter's 8 GB laptop was three times the memory there was.
    /// WSL reads <c>GB</c> as 2^30 bytes, so the figure is in those units. Null when the host is unknown.
    /// </summary>
    public static int? SuggestedMemoryGb(long hostBytes)
        => hostBytes <= 0 ? null : (int)Math.Max(0, (hostBytes - Math.Max(2 * GiB, hostBytes / 4)) / GiB);

    /// <summary>
    /// What a memory warning says about the subsystem's size for a run needing <paramref name="estimate"/>
    /// bytes, when the subsystem has <paramref name="subsystemBytes"/> of <paramref name="hostBytes"/>.
    /// The setting is offered only where it can make the run FIT — a sentence pointing at a file whose
    /// largest sensible value still leaves the run short sends the reader to the one remedy that cannot
    /// work; then the sentence says so instead, and names no file.
    /// </summary>
    public static Em3dScopeRemedy WslConfigRemedy(long estimate, long subsystemBytes, long hostBytes, string? userProfile = null)
    {
        string file = WslConfigPath(userProfile);
        if (SuggestedMemoryGb(hostBytes) is not { } gb)
            return new($"To give the Linux subsystem more memory: set 'memory=' under '[wsl2]' in .wslconfig, then run " +
                       "'wsl --shutdown'.", file);
        long suggested = gb * GiB;
        if (suggested <= subsystemBytes)
            return new($"The Linux subsystem already has most of this computer's {MachineMemory.Format(hostBytes)}.", null);
        if (estimate <= suggested)
            return new($"The Linux subsystem gets only part of this computer's {MachineMemory.Format(hostBytes)}; to give " +
                       $"it more, set 'memory={gb}GB' under '[wsl2]' in .wslconfig, then run 'wsl --shutdown'.", file);
        return new($"Giving the Linux subsystem more memory would not be enough: this computer has " +
                   $"{MachineMemory.Format(hostBytes)} in all.", null);
    }
}
