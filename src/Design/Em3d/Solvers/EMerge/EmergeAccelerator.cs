// brief-emi-3 — EMerge at its fastest without being asked. The recipe's optional tail builds the faster sparse solver
// when the machine has what that needs (SolverInstaller's optional steps); this file is what EMerge makes of it: the one
// sentence a user reads about it (Settings, the install summary, a run's Note), finishing the build later at the points
// circuitRF talks to EMerge anyway, and the thread environment a run sets.

using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d;

public static class EmergeAccelerator
{
    /// <summary>R-emi3-5 — EMerge 3.0's eigenmode solve is single-threaded whatever is installed. Said once in an
    /// Eigenmode run's Notes; nothing works around it.</summary>
    public const string EigenmodeNote =
        "EMerge 3.0 solves an eigenmode problem on one core, whichever sparse solver is installed.";

    /// <summary>
    /// R-emi3-4 — THE sentence about EMerge's speed, and the only function that words it: the Settings row
    /// (<see cref="SolverDiscovery.DescribeForSettings"/>), the install summary and a run's Note all call it. The
    /// tier first, then, when circuitRF's own install has a faster solver it could not build yet, what to install
    /// and that circuitRF does the rest. The prerequisite's name and its command are the recipe's, never spelled here.
    /// </summary>
    public static string Sentence(EmergeProbe probe, InstallRecord? record, SolverRecipe? recipe, SolverInstaller? installer = null)
    {
        string tier = probe.Tier switch
        {
            EmergeSpeedTier.Fast     => "Fast sparse solver",
            EmergeSpeedTier.Baseline => "Baseline sparse solver, one core",
            _                        => "Sparse solver not reported",
        };
        if (Pending(record, recipe) is not ({ } entry, { } when)) return tier + ".";
        string faster = probe.Tier == EmergeSpeedTier.Fast ? "a faster one" : "the faster one";
        if (entry.Status == OptionalStepsStatus.Failed)
            return $"{tier}. circuitRF could not build {faster} ({entry.Detail}); it tries again when {when.Name} change.";
        string remedy = (installer ?? new SolverInstaller()).Remedy(when);
        return $"{tier}. Install {when.Name} ({remedy}) and circuitRF will add {faster} automatically.";
    }

    /// <summary><see cref="Sentence(EmergeProbe, InstallRecord?, SolverRecipe?, SolverInstaller?)"/> for a found
    /// EMerge: its probe, and — when circuitRF installed it — its record and recipe. Null when it was not probed.</summary>
    public static string? Sentence(SolverInstallation? found)
        => found?.Emerge is { } probe ? Sentence(probe, RecordOf(found), RecipeOf(RecordOf(found))) : null;

    /// <summary>The group of optional steps still to do, with its prerequisite: the first one not built. Null when
    /// there is none — no record, a recipe with no optional steps, or everything built. A group the record does not
    /// mention (an install from before EMI-3) is still to do, exactly as finishing treats it.</summary>
    internal static (OptionalStepsRecord Entry, RecipePrerequisite When)? Pending(InstallRecord? record, SolverRecipe? recipe)
    {
        if (record is null || recipe is null) return null;
        foreach (var (when, _) in recipe.OptionalGroups)
        {
            var entry = record.Optional?.FirstOrDefault(e => e.When == when.Name)
                        ?? new OptionalStepsRecord(when.Name, OptionalStepsStatus.Missing, "unmet");
            if (entry.Status != OptionalStepsStatus.Built) return (entry, when);
        }
        return null;
    }

    /// <summary>
    /// R-emi3-4 — the prerequisite's own installer for the Settings button (<i>Install the command line
    /// tools…</i>), or null when nothing is pending, it has none, or it failed rather than went missing (installing
    /// it again would change nothing).
    /// </summary>
    public static RecipePrerequisite? InstallerFor(SolverInstallation? found)
        => Pending(RecordOf(found), RecipeOf(RecordOf(found))) is ({ Status: OptionalStepsStatus.Missing }, { Installer.Count: > 0 } when)
            ? when : null;

    /// <summary>The record of the home <paramref name="found"/> lives in, when circuitRF installed it.</summary>
    public static InstallRecord? RecordOf(SolverInstallation? found)
        => found is { HowFound: SolverHowFound.Installed, Distribution: null } && SolverHomes.HomeOf(found.Path) is { } home
            ? InstallRecord.TryRead(Path.Combine(home, SolverHomes.RecordFile)) : null;

    private static SolverRecipe? RecipeOf(InstallRecord? record)
        => record is null ? null : SolverRecipes.All.FirstOrDefault(r => r.Id == record.Recipe);

    /// <summary>
    /// R-emi3-3 — runs the optional steps of circuitRF's own EMerge install that are due now (their prerequisite is
    /// met, and they are missing, or failed against a prerequisite state that has since changed). Never asks. Empty
    /// when nothing was due. Blocking; call it off the UI thread.
    /// </summary>
    public static IReadOnlyList<OptionalStepsOutcome> FinishIfDue(SolverInstallation? found, SolverInstaller? installer = null)
        => RecordOf(found) is { } record && RecipeOf(record) is { } recipe
            ? (installer ?? new SolverInstaller()).FinishOptional(recipe, record)
            : [];

    /// <summary>
    /// R-emi3-4 — after the Settings button starts the prerequisite's own installer: looks every
    /// <paramref name="poll"/> until it is installed (then finishes, as <see cref="FinishIfDue"/>), until
    /// <paramref name="limit"/>, or until cancelled. The user presses one button and nothing else.
    /// </summary>
    public static async Task<IReadOnlyList<OptionalStepsOutcome>> FinishWhenInstalledAsync(
        Func<SolverInstallation?> find, TimeSpan poll, TimeSpan limit, CancellationToken ct)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            var found = find();
            if (InstallerFor(found) is null) return [];
            var done = await Task.Run(() => FinishIfDue(found), ct).ConfigureAwait(false);
            if (done.Count > 0) return done;
            try { await Task.Delay(poll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return [];
    }

    private static int _baselineSaid;

    /// <summary>
    /// R-emi3-3/4 — what the start of an EMerge run does before meshing: finishes the optional steps that are due
    /// (a <b>Note</b> when they built, a <b>Warning</b> quoting the build's own last line when they did not — the run
    /// goes on, on the baseline), then, on the baseline, adds <see cref="Sentence(SolverInstallation?)"/> as a Note
    /// once per session (the answer is right, only slower). Returns the installation to run: found again when a build
    /// changed it, so the run and its summary see the faster solver.
    /// </summary>
    public static SolverInstallation BeforeRun(SolverInstallation found, List<string> notes, List<string> warnings,
                                               SolverDiscovery? discovery = null, SolverInstaller? installer = null)
    {
        var done = FinishIfDue(found, installer);
        foreach (var o in done)
        {
            if (o.Status == OptionalStepsStatus.Built)
                notes.Add($"Built EMerge's faster sparse solver ({o.Elapsed.TotalSeconds:0} s).");
            else
                warnings.Add($"circuitRF could not build EMerge's faster sparse solver, so this run uses the baseline one: {o.Detail}. " +
                             $"It is not tried again until {o.When} change. The log is {o.LogPath}.");
        }
        if (done.Count > 0) found = (discovery ?? SolverDiscovery.Emerge).Find(out _) ?? found;
        if (found.Emerge is { Tier: EmergeSpeedTier.Baseline } && Interlocked.Exchange(ref _baselineSaid, 1) == 0
            && Sentence(found) is { } sentence)
            notes.Add(sentence);
        return found;
    }

    /// <summary>A test's fresh session: the baseline Note is said again.</summary>
    internal static void ResetSessionForTests() => Interlocked.Exchange(ref _baselineSaid, 0);
}

/// <summary>
/// brief-emi-3 R-emi3-5 — how many threads an EMerge run's solvers use, and the environment that says so. Every
/// sparse solver EMerge can pick, and its own Rust kernels, read the count from a variable of their own, the ones
/// EMerge's <c>emerge_config</c> sets. <b>Measured</b> (src/Design/RESOLVED.md, EMI-3): the macOS solver ran the Cavity
/// in half the time on all ten cores as on one, and the wheel-only solver in a quarter.
/// </summary>
public static class EmergeThreads
{
    /// <summary>The variables, in the order they are written. Each is read by someone, measured: the macOS solver
    /// follows only <c>VECLIB_MAXIMUM_THREADS</c> (<c>OMP_NUM_THREADS</c> alone left it on every core), and the wheel-only
    /// solver ran on about one core with its variable unset. The x64 vendor library reads <c>OMP_NUM_THREADS</c> when
    /// its own variable is unset, so that one is not spelled here.</summary>
    public static IReadOnlyList<string> Variables { get; } =
    [
        "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "VECLIB_MAXIMUM_THREADS", "RSLAB_NUM_THREADS", "RAYON_NUM_THREADS",
    ];

    /// <summary>The thread count: the Cores preference (<c>EmMaxCores</c>), clamped to the logical count, or every
    /// physical core when it is unset or unusable.</summary>
    public static int Count(int? maxCores, int physicalCores, int logicalCores)
        => maxCores is { } c && c >= 1 ? Math.Min(c, Math.Max(1, logicalCores)) : Math.Max(1, physicalCores);

    /// <summary>The environment a run adds (a pure function of its inputs; the run passes
    /// <c>PhysicalCores.Count</c> and <c>Environment.ProcessorCount</c>).</summary>
    public static IReadOnlyDictionary<string, string> Environment(int? maxCores, int physicalCores, int logicalCores)
    {
        string n = Count(maxCores, physicalCores, logicalCores).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Variables.ToDictionary(v => v, _ => n, StringComparer.Ordinal);
    }
}
