// brief-emi-4 — EMerge's part of Em3dRunService: its leg of a run. Its settings and its lowering are in; starting EMerge
// and reading what it writes are EMI-5's, so the leg writes the input into its run directory and then refuses, saying so.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static partial class Em3dRunService
{
    internal sealed class EmergeLeg : SolverLeg
    {
        private EmergeSettings? _settings;
        private EmergeLowering? _lowering;

        public EmergeLeg() : base(EmergeSolver.Descriptor) { }

        public override void Configure(RunContext run)
        {
            var (readiness, log, setup) = (run.Readiness, run.Log, run.Setup);
            var found = readiness.Single(r => r.Tool == SolverTool.Emerge);
            log.Notes.Add($"Solver: EMerge {found.Installation!.DescribeVersion()} at {found.Installation.Path}.");
            // An untested EMerge runs, and the run says so among its Warnings (R-emi2-6 as the owner amended it).
            if (found.Warning is { } untested) log.Warnings.Add(untested);
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
        {
            string dir = RunDirectory(run.ResultsRoot, run.Setup, Solver.Id);
            EmergeScriptWriter.WriteInto(_lowering!, dir);
            run.Log.Files[EmergeSolver.NotStartedYet] = Path.Combine(dir, EmergeScriptWriter.ProblemFile);
            return Leg.Failed(Solver.Id, EmRunStatus.Refused, EmDiagnostics.Forwarded("emerge-not-started", EmergeSolver.NotStartedYet));
        }
    }
}
