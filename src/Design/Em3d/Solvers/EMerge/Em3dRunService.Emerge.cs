// brief-emi-4 / brief-emi-5 — EMerge's part of Em3dRunService: its leg of a run. Configure finds EMerge (finishing its faster
// solver first when that is due, EMI-3) and resolves its settings; Prepare lowers the problem (no process); Execute writes the
// input, runs the driver, checks the mesh's memory the moment it exists, and reads the arrays it writes into the S DataSet and
// Touchstone, or the eigenmode table.

using System.Diagnostics;
using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static partial class Em3dRunService
{
    /// <summary>The diagnostic source an EMerge memory refusal carries.</summary>
    public const string EmergeMemoryRefusalSource = "emerge-memory";

    internal sealed class EmergeLeg : SolverLeg
    {
        private EmergeSettings? _settings;
        private EmergeLowering? _lowering;
        private SolverInstallation? _emerge;

        public EmergeLeg() : base(EmergeSolver.Descriptor) { }

        public override void Configure(RunContext run)
        {
            var (readiness, log, setup) = (run.Readiness, run.Log, run.Setup);
            var found = readiness.Single(r => r.Tool == SolverTool.Emerge);
            log.Notes.Add($"Solver: EMerge {found.Installation!.DescribeVersion()} at {found.Installation.Path}.");
            // An untested EMerge runs, and the run says so among its Warnings (R-emi2-6 as the owner amended it).
            if (found.Warning is { } untested) log.Warnings.Add(untested);
            // R-emi3-3 — the faster solver is finished here when it is due, and the run uses what that leaves.
            _emerge = EmergeAccelerator.BeforeRun(found.Installation, log.Notes, log.Warnings);
            if (setup.Problem3D == Em3dProblemType.Eigenmode) log.Notes.Add(EmergeAccelerator.EigenmodeNote);
            _settings = EmergeSettings.Resolve(setup.Emerge);
            if (_settings.Problems() is { Count: > 0 } bad)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("emerge-settings", string.Join(" ", bad)));
        }

        public override void Prepare(Em3dProblem problem, RunContext run)
        {
            if (Stop is not null) return;
            _lowering = EmergeScriptWriter.Write(problem, _settings!);
            if (_lowering.Refusal is { } refused)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("emerge-lowering", refused));
            else if (EmergeScriptWriter.RadiationPatternNote(run.Setup, problem) is { } noPattern)
                run.Log.Notes.Add(noPattern);
        }

        public override Leg Execute(Em3dProblem problem, string snpBase, RunContext run)
            => ExecuteEmerge(problem, snpBase, run, _settings!, _lowering!, _emerge!);
    }

    private static Leg ExecuteEmerge(Em3dProblem problem, string snpBase, RunContext run, EmergeSettings settings,
                                     EmergeLowering lowering, SolverInstallation emerge)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        Leg Failed(string reason) => Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(reason));
        var (setup, log, control) = (run.Setup, run.Log, run.Control);
        var wall = Stopwatch.StartNew();
        bool eigen = problem.Type == Em3dProblemType.Eigenmode;
        long physical = MachineMemory.PhysicalBytes;

        string dir = RunDirectory(run.ResultsRoot, setup, Me);
        try
        {
            // An earlier run's arrays would be read as this one's if this one stopped before writing its own.
            Directory.CreateDirectory(dir);
            foreach (string old in new[] { EmergeResults.FrequencyFile, EmergeResults.SFile, EmergeResults.Z0File,
                                           EmergeResults.ModesFile, EmergeResults.QFile, EmergeResults.RunFile })
                File.Delete(Path.Combine(dir, old));
            EmergeScriptWriter.WriteInto(lowering, dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"EMerge's input could not be written to '{dir}' ({e.Message}).");
        }

        // R-emi5-3 — the memory check on the mesh, the moment the driver has made it: a refusal stops the process tree.
        var tier = emerge.Emerge?.Tier ?? EmergeSpeedTier.Unknown;
        string? tooBig = null;
        var tracker = new EmergeStageTracker(control, eigen)
        {
            OnMeshed = mesh =>
            {
                if (run.Memory.Admit(EmergeMemory.Verdict(mesh, settings, tier, eigen, physical), log) is not { } refusal) return;
                tooBig = refusal;
                throw new OperationCanceledException();
            },
        };
        var psi = EmergeRun.StartInfo(emerge.Path, dir,
                                      EmergeThreads.Environment(run.MaxCores, PhysicalCores.Count, Environment.ProcessorCount));
        PalaceRun.ProcessRun ran;
        try { ran = EmergeRun.Solve(psi, control, run.Ct, tracker, physical); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"EMerge could not be started in '{dir}' ({e.Message}).");
        }
        if (tooBig is not null) return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(EmergeMemoryRefusalSource, tooBig));
        if (ran.Cancelled) return Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
        if (ran.StartFailure is { } start) return Failed($"EMerge could not be started ({start}).");
        if (ran.MemoryNote is not null) log.Notes.Add(ran.MemoryNote);
        string logPath = Path.Combine(dir, EmergeResults.LogFile);
        if (ran.ExitCode != 0)
            return Failed(EmergeLog.DriverError(ran.Tail) is { } said
                ? $"EMerge stopped: {said} Its log is {logPath}."
                : $"EMerge exited with code {ran.ExitCode}: {PalaceRun.Quote(ran.Tail, "Error")}. Its log is {logPath}.");

        var facts = EmergeResults.ReadFacts(dir);
        log.Warnings.AddRange(facts.Warnings);
        control?.BeginStage(ReadingLabel);
        string version = facts.Version ?? emerge.Release ?? "(version unreported)";
        return eigen
            ? FinishEmergeEigen(problem, setup, run.ResultsRoot, dir, facts, tier, version, ran.PeakResident, wall.Elapsed, log)
            : FinishEmergeDriven(problem, setup, run.ResultsRoot, snpBase, dir, settings, lowering, facts, tier, version,
                                 ran.PeakResident, wall, log);
    }

    private static Leg FinishEmergeDriven(Em3dProblem problem, EmSetup setup, string resultsRoot, string snpBase, string dir,
                                          EmergeSettings settings, EmergeLowering lowering, EmergeRunFacts facts, EmergeSpeedTier tier,
                                          string version, long? peak, Stopwatch wall, RunLog log)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var sweep = EmergeResults.ReadDriven(dir, ports.Count, out string? readError);
        if (sweep is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(readError!));
        var requested = FrequenciesHz(problem.Frequency);
        if (sweep.FrequenciesHz.Length != requested.Length ||
            sweep.FrequenciesHz.Where((f, k) => Math.Abs(f - requested[k]) > 1e-9 * Math.Abs(requested[k])).Any())
            return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(
                $"EMerge's {EmergeResults.FrequencyFile} does not list the {requested.Length} frequencies the sweep asked for."));
        sweep = EmergeResults.AtPortReferences(sweep with { FrequenciesHz = (double[])requested.Clone() }, ports, out bool renormalised);
        if (renormalised)
            log.Notes.Add("EMerge referred a port's S-parameters to another impedance than the port's own; the .sNp states one real " +
                          "reference per port, so the whole matrix was renormalised to each port's Z0.");

        string? notConverged = null;
        if (facts.Adaptive)
        {
            log.Notes.Add($"EMerge's adaptive sweep solved the full problem at {Count(facts.Samples)} frequencies and fitted all " +
                          $"{requested.Length} requested points from them (estimated error " +
                          $"{(facts.Error is { } e ? PalaceStageTracker.Sci(e) : "unreported")}, tolerance {PalaceStageTracker.Sci(settings.Tolerance)} on |S|).");
            if (facts.Converged == false)
            {
                notConverged = $"EMerge's adaptive sweep stopped at its limit of {settings.MaxSamples} frequencies with estimated error " +
                               $"{(facts.Error is { } e2 ? PalaceStageTracker.Sci(e2) : "unreported")}, above its tolerance " +
                               $"{PalaceStageTracker.Sci(settings.Tolerance)}.";
                log.Warnings.Add(notConverged + " Raise EMerge.MaxSamples, or loosen EMerge.Tolerance.");
            }
        }

        var data = EmergeResults.BuildDataSet(sweep, ports, facts);
        string? npyPath = WriteNpy(resultsRoot, NpyKey(setup, Me), data, log);
        string? snpPath = snpBase + $".s{ports.Count}p";
        string? snpError;
        try
        {
            snpError = EmergeResults.WriteSnp(data, snpBase, setup, problem, lowering, settings, facts, version, wall.Elapsed, peak,
                                              DateTimeOffset.Now);
        }
        catch (Exception e) { snpError = e.Message; }
        if (snpError is not null)
        {
            log.Errors.Add($"The .snp could not be written to '{snpPath}': {snpError}");
            snpPath = null;
        }

        // R-emi5-4c — the one line a user reads first of what the run cost, the last thing the run says.
        log.Notes.Add(EmergeCompletionSummary(version, tier, facts, peak, setup, wall.Elapsed, eigenmode: false));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, snpPath, "EMerge " + version, NotConverged: notConverged);
    }

    /// <summary>R-emi5-4b — the modes, as Palace's eigenmode run reports them (f, Q), with what EMerge does not report left out.</summary>
    private static Leg FinishEmergeEigen(Em3dProblem problem, EmSetup setup, string resultsRoot, string dir, EmergeRunFacts facts,
                                         EmergeSpeedTier tier, string version, long? peak, TimeSpan wall, RunLog log)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        var read = EmergeResults.ReadModes(dir, out string? error);
        if (read is not { } arrays) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(error!));
        var modes = EmergeResults.FilterModes(arrays.Frequencies, arrays.Q, problem.EigenmodeTargetHz);
        if (modes.Kept.Count < problem.EigenmodeCount)
            log.Warnings.Add($"EMerge found {modes.Kept.Count} mode(s) at or above {Fmt(modes.FloorHz / 1e9)} GHz where " +
                             $"{problem.EigenmodeCount} were asked for.");

        var notes = new List<string>
        {
            "f is each mode's resonant frequency (the real part of EMerge's complex eigenfrequency). Q is EMerge's Q, Re f / 2 Im f: " +
            "the LOADED Q, with every loss in the problem, each lumped port's resistance included.",
            "EMerge reports no port's external Q and not where a mode's energy sits, so this result has no Q_unloaded and no " +
            "participation; Palace's eigenmode run reports both.",
        };
        if (modes.Dropped > 0)
            notes.Add($"{modes.Dropped} of EMerge's {arrays.Frequencies.Length} solution(s) were below {Fmt(modes.FloorHz / 1e9)} GHz " +
                      $"({EmergeResults.ModeFloorMargin * 100:0} % under the search start, {Fmt(problem.EigenmodeTargetHz / 1e9)} GHz) " +
                      "and were dropped: EMerge's eigensolver looks either side of the start, where Palace reports only modes above " +
                      "it, and near 0 Hz it can return solutions that are not resonances.");
        log.Notes.AddRange(notes);

        var data = Em3dEigenResult.Build(modes.Kept, [], null, [], null, notes);
        void Scalar(string name, double? v)
        {
            if (v is { } x) data.AddToGroup(EmergeResults.Group, name, RfCore.Data.DataCube.Scalar(x));
        }
        Scalar("Tetrahedra", facts.Tetrahedra);
        Scalar("DegreesOfFreedom", facts.Unknowns);
        log.Notes.Add($"EMerge found {modes.Kept.Count} mode(s) at or above {Fmt(modes.FloorHz / 1e9)} GHz on {Count(facts.Tetrahedra)} " +
                      $"tetrahedra, {Count(facts.Unknowns)} unknowns.");

        string? npyPath = WriteNpy(resultsRoot, NpyKey(setup, Me), data, log);
        log.Notes.Add(EmergeCompletionSummary(version, tier, facts, peak, setup, wall, eigenmode: true));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "EMerge " + version);
    }

    /// <summary>
    /// R-emi5-4c — "EMerge done in 6 s · EMerge 3.0.0b2 · fast sparse solver · 15,124 tetrahedra · 109,566 unknowns · adaptive
    /// sweep built from 11 full solves · peak memory 2.6 GB · preset Standard.", every figure one the run read, and one it did not
    /// read left out rather than guessed.
    /// </summary>
    public static string EmergeCompletionSummary(string version, EmergeSpeedTier tier, EmergeRunFacts facts, long? peakBytes,
                                                 EmSetup setup, TimeSpan wall, bool eigenmode)
    {
        var parts = new List<string> { $"EMerge done in {Duration(wall)}", $"EMerge {version}" };
        if (eigenmode) parts.Add("eigenmode solve on one core");
        else if (tier == EmergeSpeedTier.Fast) parts.Add("fast sparse solver");
        else if (tier == EmergeSpeedTier.Baseline) parts.Add("baseline sparse solver, one core");
        if (facts.Tetrahedra is { } t) parts.Add($"{t.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra");
        if (facts.Unknowns is { } u) parts.Add($"{u.ToString("N0", CultureInfo.InvariantCulture)} unknowns");
        if (!eigenmode && facts.Samples is { } n)
            parts.Add(facts.Adaptive ? $"adaptive sweep built from {n} full solve{(n == 1 ? "" : "s")}" : $"{n} frequencies solved");
        if (peakBytes is { } m) parts.Add($"peak memory {MachineMemory.Format(m)}");
        parts.Add($"preset {setup.Emerge?.Quality ?? PalaceQuality.Standard}");
        return string.Join(" · ", parts) + ".";
    }
}
