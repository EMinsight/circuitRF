using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Wsl;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// brief-em3d-97 R-em3d97-2 — what the <i>Linux subsystem memory</i> dialog shows and does, with no Avalonia in
/// it, so every gate runs against the fake <see cref="IWsl"/>. The dialog (<c>Views.Dialogs.WslMemoryDialog</c>)
/// only lays it out. Everything is read live when it is built: circuitRF keeps no copy of the value.
/// </summary>
public sealed class WslMemoryDialogModel
{
    private readonly IWsl _wsl;
    private readonly IReadOnlyList<string>? _localRoots;

    public WslMemoryDialogModel(IWsl wsl, WslMemoryState state, IReadOnlyList<string>? localRoots = null)
    {
        _wsl = wsl;
        _localRoots = localRoots;
        State = state;
        Blockers = WslMemory.RestartBlockers(localRoots);
        Proposed = Math.Clamp(state.SuggestedGb ?? WslMemory.MinimumGb, WslMemory.MinimumGb, MaximumGb);
    }

    /// <summary>Reads everything the dialog shows: the distribution's VM memory (which starts it), the file and
    /// the running distributions. Null with <paramref name="refusal"/> when the subsystem has no distribution to ask.</summary>
    public static WslMemoryDialogModel? Load(IWsl wsl, string? preferredDistribution, string configPath, long hostBytes,
                                             out string? refusal, IReadOnlyList<string>? localRoots = null)
    {
        refusal = null;
        if (!wsl.Available) { refusal = WslDistributions.FeatureMissingRefusal; return null; }
        var listing = WslDistributions.Read(wsl);
        if (!listing.Ready) { refusal = listing.Refusal; return null; }
        if (PickDistribution(listing, preferredDistribution) is not { } distro)
        {
            refusal = "No WSL 2 distribution is installed, and only WSL 2 reads .wslconfig.";
            return null;
        }
        return new(wsl, WslMemory.Read(wsl, distro, configPath, hostBytes), localRoots);
    }

    /// <summary>
    /// The distribution whose memory is read. Every WSL 2 distribution runs in the ONE virtual machine, so any of
    /// them answers for all; a running one is asked first because asking it starts nothing, then the preferred
    /// (Palace's), then the default.
    /// </summary>
    public static string? PickDistribution(WslSubsystem listing, string? preferred)
    {
        var v2 = listing.Version2;
        return (v2.FirstOrDefault(d => d.Running)
                ?? v2.FirstOrDefault(d => string.Equals(d.Name, preferred, StringComparison.OrdinalIgnoreCase))
                ?? v2.FirstOrDefault(d => d.IsDefault)
                ?? v2.FirstOrDefault())?.Name;
    }

    public WslMemoryState State { get; }

    /// <summary>circuitRF's own work in the subsystem, which a restart would stop.</summary>
    public IReadOnlyList<string> Blockers { get; }

    /// <summary>The value in the box, whole GB.</summary>
    public int Proposed { get; set; }

    public int MaximumGb => WslMemory.MaximumGb(State.HostBytes);

    public string ConfigPath => State.Config.Path;

    // ── Now ──────────────────────────────────────────────────────────────────────────────────

    public string SubsystemLine => State.SubsystemBytes is { } b
        ? $"The Linux subsystem has {MachineMemory.Format(b)} (as 'free' reads it in '{State.Distribution}')."
        : $"The Linux subsystem's memory could not be read from '{State.Distribution}'.";

    public string FileLine => State.Config.ReadError is { } why
        ? $".wslconfig could not be read: {why}"
        : State.Config.Memory is { } m
            ? $".wslconfig sets memory={m}."
            : ".wslconfig does not set memory: WSL's default, half this computer's memory.";

    public string HostLine => $"This computer has {MachineMemory.Format(State.HostBytes)}.";

    // ── Proposed ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Why the box's value cannot be written; null when it can.</summary>
    public string? ValueRefusal => WslMemory.ValueRefusal(Proposed, State.HostBytes);

    // ── The change ───────────────────────────────────────────────────────────────────────────

    public WslConfigEdit Edit => WslConfigFile.WithMemory(State.Config.Exists ? State.Config.Text : null, Proposed);

    public string BeforeLine => Edit.Before ?? (State.Config.Exists ? "(no memory= line)" : "(no .wslconfig file)");

    public string AfterLine => Edit.After;

    /// <summary>Said when <c>[wsl2]</c> repeats the key: WSL reads the last, so only that one changes.</summary>
    public string? RepeatNote => Edit.Repeats > 1
        ? $"[wsl2] sets memory= {Edit.Repeats} times. WSL reads the last one, so only that line changes; the others are left as they are."
        : null;

    // ── What restarting stops ────────────────────────────────────────────────────────────────

    public const string RestartSentence =
        "Applying it restarts the Linux subsystem: every program running in it stops (other terminals, containers, " +
        "Docker Desktop's engine).";

    public string RunningLine => State.Running.Count == 0
        ? "No distribution is running now."
        : $"Running now: {string.Join(", ", State.Running)}.";

    public const string SaveOnlyNote = "Save only keeps everything running; the change applies the next time the subsystem starts.";

    // ── the three buttons ────────────────────────────────────────────────────────────────────

    public bool CanSave => ValueRefusal is null && State.Config.ReadError is null;

    /// <summary>Why Save and restart is disabled, naming circuitRF's own work; null when it is not.</summary>
    public string? RestartBlocked => Blockers.Count > 0 ? WslMemory.RestartRefusal(Blockers) : null;

    public bool CanRestart => CanSave && RestartBlocked is null;

    public WslMemoryOutcome SaveAndRestart() => WslMemory.Apply(_wsl, State, Proposed, restart: true, _localRoots);

    public WslMemoryOutcome SaveOnly() => WslMemory.Apply(_wsl, State, Proposed, restart: false, _localRoots);

    /// <summary>Posts <paramref name="outcome"/>, with the file as the row's link.</summary>
    public static void Report(IMessageSink sink, WslMemoryOutcome outcome, string configPath)
    {
        var level = outcome.Status switch
        {
            WslMemoryStatus.Restarted                                => MessageLevel.Success,
            WslMemoryStatus.Saved                                    => MessageLevel.Info,
            WslMemoryStatus.NotRaised or WslMemoryStatus.RestartFailed
                                      or WslMemoryStatus.Refused     => MessageLevel.Warning,
            _                                                        => MessageLevel.Error,
        };
        sink.Post(level, outcome.Report, configPath);
    }

    /// <summary>The label the memory warning's row and the 150 % confirmation offer.</summary>
    public const string RowAction = "Give the subsystem more memory…";

    /// <summary>The Settings ▸ 3D EM line: "Linux subsystem: 3.8 GB of 7.6 GB".</summary>
    public static string SettingsLine(long? subsystemBytes, long hostBytes)
        => subsystemBytes is { } b
            ? $"Linux subsystem: {MachineMemory.Format(b)} of {MachineMemory.Format(hostBytes)}"
            : "Linux subsystem: memory not read";
}
