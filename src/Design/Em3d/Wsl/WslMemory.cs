using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-97 — giving the Linux subsystem more memory: <c>memory=</c> in the user's <c>.wslconfig</c>, then,
/// when asked, <c>wsl --shutdown</c> so the next start reads it. The dialog, the memory warning's row and
/// Settings ▸ 3D EM all go through here, and every step goes through <see cref="IWsl"/>, so the whole operation
/// runs against the test fake.
///
/// <para><b>Two facts shape it.</b> <c>.wslconfig</c> is machine-wide for this user — every distribution and Docker
/// Desktop's WSL backend read it. And a change takes effect only after <c>wsl --shutdown</c>, which stops
/// EVERYTHING running in the subsystem; so the restart is refused while circuitRF itself has work there
/// (<see cref="RestartBlockers"/>), and the dialog names what else it stops.</para>
/// </summary>
public static class WslMemory
{
    /// <summary>The smallest <c>memory=</c> offered: below it a distribution barely boots.</summary>
    public const int MinimumGb = 2;

    private const long GiB = 1L << 30;

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether <paramref name="path"/> is a <c>.wslconfig</c> — the file a memory warning names when
    /// raising the subsystem's memory can make the run fit.</summary>
    public static bool IsWslConfig(string? path)
        => path is not null && string.Equals(Path.GetFileName(path.Replace('\\', '/')), ".wslconfig", StringComparison.OrdinalIgnoreCase);

    /// <summary>The largest <c>memory=</c> offered on a computer with <paramref name="hostBytes"/>: all of it.</summary>
    public static int MaximumGb(long hostBytes) => (int)Math.Max(MinimumGb, hostBytes / GiB);

    /// <summary>Null when <paramref name="gb"/> may be written on this computer; otherwise why not.</summary>
    public static string? ValueRefusal(int gb, long hostBytes)
    {
        if (gb < MinimumGb) return $"At least {MinimumGb} GB: below that a Linux distribution barely starts.";
        if (hostBytes > 0 && gb > MaximumGb(hostBytes))
            return $"At most {MaximumGb(hostBytes)} GB: this computer has {MachineMemory.Format(hostBytes)}.";
        return null;
    }

    /// <summary>
    /// What the dialog shows, read live: the VM's memory (<c>free -b</c> in <paramref name="distribution"/>, which
    /// starts it), the file, this computer's memory, the suggestion, and the distributions running now.
    /// </summary>
    public static WslMemoryState Read(IWsl wsl, string distribution, string configPath, long hostBytes)
    {
        var running = WslDistributions.Read(wsl) is { Ready: true } s
            ? s.Distributions.Where(d => d.Running).Select(d => d.Name).ToList()
            : [];
        long? vm = new WslSession(wsl, distribution).MemoryBytes();
        return new(distribution, vm, hostBytes, WslPalace.SuggestedMemoryGb(hostBytes), WslConfigFile.Read(configPath), running);
    }

    /// <summary>
    /// Everything a restart would stop that circuitRF started: this process's runs and installs in the subsystem
    /// (<see cref="WslInUse"/>), then any other circuitRF process's hold on a circuitRF-installed subsystem home
    /// (<see cref="SolverInUse"/>'s lock in its mirror). Empty when a restart stops nothing of circuitRF's.
    /// </summary>
    public static IReadOnlyList<string> RestartBlockers(IEnumerable<string>? localRoots = null)
    {
        var found = new List<string>(WslInUse.Current());
        var roots = (localRoots ?? SolverHomes.DefaultRoots).ToList();
        foreach (var tool in Enum.GetValues<SolverTool>())
            foreach (var record in WslSolverHomes.Mirrors(roots, tool))
                foreach (string root in roots)
                {
                    string mirror = WslSolverHomes.MirrorDirectory(root, record);
                    // This process's own holders there are already in the list, by WslInUse's fuller sentence.
                    if (Directory.Exists(mirror))
                        found.AddRange(SolverInUse.HoldersOf(mirror).Where(h => !found.Any(f => f == h || f.StartsWith(h + " is running in", StringComparison.Ordinal))));
                }
        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The sentence a disabled restart says.</summary>
    public static string RestartRefusal(IReadOnlyList<string> blockers)
        => $"Restarting the subsystem would stop circuitRF's own work: {string.Join("; ", blockers)}. Save only " +
           "applies the change the next time the subsystem starts.";

    /// <summary>
    /// Writes <c>memory=<paramref name="gb"/>GB</c> into the file <paramref name="state"/> read and, when
    /// <paramref name="restart"/>, shuts the subsystem down and reads the VM's memory again (which starts it).
    /// Refused — nothing written, nothing stopped — for a value outside <see cref="ValueRefusal"/>, and for a
    /// restart while <see cref="RestartBlockers"/> names something.
    /// </summary>
    public static WslMemoryOutcome Apply(IWsl wsl, WslMemoryState state, int gb, bool restart,
                                         IEnumerable<string>? localRoots = null)
    {
        var config = state.Config;
        if (ValueRefusal(gb, state.HostBytes) is { } bad) return new(WslMemoryStatus.Refused, bad, null, null);
        if (config.ReadError is { } unreadable)
            return new(WslMemoryStatus.WriteFailed, $"{config.Path} could not be read, so it was not changed: {unreadable}", null, null);
        if (restart && RestartBlockers(localRoots) is { Count: > 0 } blockers)
            return new(WslMemoryStatus.Refused, RestartRefusal(blockers), null, null);

        var edit = WslConfigFile.WithMemory(config.Exists ? config.Text : null, gb);
        string? backup;
        try { backup = config.Write(edit.Text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(WslMemoryStatus.WriteFailed, $"{config.Path} could not be written, so nothing changed: {e.Message}", null, null);
        }

        string saved = $"Saved '{edit.After}' under '[wsl2]' in .wslconfig" +
                       (backup is null ? "." : $"; the previous file is kept as {Path.GetFileName(backup)}.");
        if (!restart)
            return new(WslMemoryStatus.Saved, saved + " It applies the next time the Linux subsystem starts: after " +
                                              "'wsl --shutdown', or a restart of Windows.", backup, null);

        var shutdown = wsl.Run(["--shutdown"], ShutdownTimeout);
        if (!shutdown.Ok)
        {
            string said = shutdown.Failure ?? (shutdown.Message.Length > 0 ? shutdown.Message : $"exit {shutdown.ExitCode}");
            return new(WslMemoryStatus.RestartFailed, saved + $" Restarting the subsystem failed ('wsl --shutdown': {said}); " +
                                                      "the change applies the next time it starts.", backup, null);
        }

        // A fresh session: the old one kept the figure it read before the restart.
        long? after = new WslSession(wsl, state.Distribution).MemoryBytes();
        if (after is not { } now)
            return new(WslMemoryStatus.Restarted, saved + $" The subsystem was restarted, and '{state.Distribution}' did " +
                                                  "not report its memory afterwards.", backup, null);
        string had = state.SubsystemBytes is { } b ? $" (it had {MachineMemory.Format(b)})" : "";
        long asked = gb * GiB;
        if (state.SubsystemBytes is { } before && asked > before && now - before < (asked - before) / 2)
            return new(WslMemoryStatus.NotRaised,
                       saved + $" After restarting, '{state.Distribution}' still has {MachineMemory.Format(now)}{had}, so " +
                       "WSL did not apply it. The likely cause is a WSL that does not read .wslconfig: WSL 1 never does, and " +
                       "a WSL from before the Microsoft Store version can miss it. 'wsl --update' installs the current one; " +
                       "check the file's '[wsl2]' section too.", backup, now);
        return new(WslMemoryStatus.Restarted, saved + $" The Linux subsystem was restarted: '{state.Distribution}' now has " +
                                              $"{MachineMemory.Format(now)}{had}.", backup, now);
    }
}

/// <summary>What <see cref="WslMemory.Read"/> found.</summary>
/// <param name="Distribution">The distribution whose VM memory is read (all share one VM).</param>
/// <param name="SubsystemBytes">The VM's memory as <c>free -b</c> reads it; null when unread.</param>
/// <param name="SuggestedGb">The starting value (<see cref="WslPalace.SuggestedMemoryGb"/>).</param>
/// <param name="Running">The distributions running now, which a restart stops.</param>
public sealed record WslMemoryState(string Distribution, long? SubsystemBytes, long HostBytes, int? SuggestedGb,
                                    WslConfigFile Config, IReadOnlyList<string> Running);

public enum WslMemoryStatus
{
    /// <summary>Written; applies at the next start.</summary>
    Saved,
    /// <summary>Written, restarted, and the new figure reported.</summary>
    Restarted,
    /// <summary>Written and restarted, and the VM's memory did not move by half the change.</summary>
    NotRaised,
    /// <summary>Written; <c>wsl --shutdown</c> failed, with its own output.</summary>
    RestartFailed,
    /// <summary>The file could not be read or written; nothing changed.</summary>
    WriteFailed,
    /// <summary>Refused before anything was written (a value out of range, or circuitRF's own work in the subsystem).</summary>
    Refused,
}

/// <param name="Report">The sentence Messages shows.</param>
/// <param name="Backup">The backup kept beside the file, when there was a file.</param>
/// <param name="SubsystemBytesAfter">The VM's memory read after a restart.</param>
public sealed record WslMemoryOutcome(WslMemoryStatus Status, string Report, string? Backup, long? SubsystemBytesAfter)
{
    public bool Written => Status is WslMemoryStatus.Saved or WslMemoryStatus.Restarted or WslMemoryStatus.NotRaised
                                  or WslMemoryStatus.RestartFailed;
}
