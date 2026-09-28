// brief-em3d-77 R-em3d77-5 — a sweep point reached from the last converged one, and thermal runaway as a RESULT.
//
// Between fixed-temperature ends no steady state exists above a current I* (overview §1g); with the metals' tables the limit
// is a fold a little below melting (brief 72 §3.1). Newton near it needs a converged neighbour, the drive-ladder pattern of
// the harmonic-balance sweeps: so a point is solved from the last converged point's state along the straight path between
// the two (λ from 0 to 1), and a λ that does not converge is BISECTED toward the last converged one:
//
//   t = 1; repeat: solve at t from the last converged state —
//     converged:  it becomes the last converged; done if t = 1, else try the lowest known failure again from here;
//     not:        it is the lowest known failure; if [last converged, it] is 0.5 % wide, that is the runaway bracket;
//                 else try the middle.
//
// A step that throws (a singular system from a conductor with no path, a table that cannot be read) is not a runaway and is
// not caught here: the caller reports it as the failure it is.

using CircuitRF.Thermal.Electrothermal;

namespace CircuitRF.Thermal.Nonlinear;

/// <summary>A continuation's end: the target's solution, or the runaway bracket and the last point that converged.</summary>
/// <param name="ConvergedAt">λ of <paramref name="LastConverged"/>.</param>
/// <param name="FailedAt">λ of the lowest point that did not converge (1 when the target converged).</param>
public sealed record ContinuationResult(ElectrothermalSolution? Solution, bool Runaway, double ConvergedAt, double FailedAt,
                                        ElectrothermalSolution? LastConverged, int Solves)
{
    /// <summary>The last solve that did not converge: why, in its own notes.</summary>
    public ElectrothermalSolution? LastFailed { get; init; }
}

public static class Continuation
{
    /// <summary>R-em3d77-5 — the bracket's width at which a runaway is declared, relative to the swept value.</summary>
    public const double Bracket = 0.005;

    /// <summary>
    /// The problem <paramref name="at"/>(1), reached from <paramref name="warm"/> — the solution at <paramref name="at"/>(0) — or,
    /// with no warm state, from a cold start at λ = 1, and failing that from a cold solve at λ = 0 (the caller makes that point
    /// easy: its currents at zero, say).
    /// <paramref name="width"/> is the relative width of the bracket [λ₀, λ₁] in the swept value.
    /// </summary>
    public static ContinuationResult Advance(Func<double, ElectrothermalProblem> at, ElectrothermalSystem system, ElectrothermalSolution? warm,
                                             ThermalSolveOptions options, Func<double, double, double> width, int maxSolves = 60)
    {
        ArgumentNullException.ThrowIfNull(at);
        int solves = 0;
        var last = warm;
        double t = 1;
        if (last is null)
        {
            // the target from a cold start first: most points need no ramp, and a ramp costs a solve at λ = 0 as well
            var direct = ConductiveBalance.Solve(at(1), options, system);
            solves++;
            if (direct.Converged) return new(direct, false, 1, 1, direct, solves);
            last = ConductiveBalance.Solve(at(0), options, system);
            solves++;
            if (!last.Converged) return new(null, false, 0, 0, null, solves) { LastFailed = last };
            t = 0.5;                                     // λ = 1 has just failed: bisect at once
        }
        double lo = 0, hi = 1;
        ElectrothermalSolution? failed = null;
        while (solves < maxSolves)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var s = ConductiveBalance.Solve(at(t), options, system, last.State);
            solves++;
            if (s.Converged)
            {
                lo = t;
                last = s;
                if (t >= 1) return new(s, false, 1, 1, s, solves);
                t = hi;
                continue;
            }
            hi = t;
            failed = s;
            if (width(lo, hi) <= Bracket) return new(null, true, lo, hi, last, solves) { LastFailed = failed };
            t = (lo + hi) / 2;
        }
        return new(null, true, lo, hi, last, solves) { LastFailed = failed };
    }
}
