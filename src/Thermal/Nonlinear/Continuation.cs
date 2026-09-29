// brief-em3d-77 R-em3d77-5 — a sweep point reached from the last converged one, and thermal runaway as a RESULT.
//
// Between fixed-temperature ends no steady state exists above a current I* (overview §1g); with the metals' tables the limit
// is a fold a little below melting (brief 72 §3.1). Newton near it needs a converged neighbour, the drive-ladder pattern of
// the harmonic-balance sweeps: so a point is solved from the last converged point's state along the straight path between
// the two (λ from 0 to 1), and a λ that does not converge is BISECTED toward the last converged one:
//
//   t = 1; repeat: solve at t from the last converged state —
//     converged:  it becomes the last converged; done if t = 1, else try the lowest known failure again from here (and if
//                 that was the failure itself, no failure is known any more: try t = 1 again);
//     not:        it is the lowest known failure; if [last converged, it] is 0.5 % wide, that is the runaway bracket;
//                 else try the middle.
//
// Running out of solves before the bracket closes is NOT a runaway: the result has no solution and Runaway false.
//
// THE FOLD, PREDICTED (brief-em3d-85). Near the limit λ is a maximum of a smooth function of the hottest temperature θ,
// λ ≈ λ_f − c(θ − θ_f)², so once a failure is known and three converged states lie on the path, a parabola through their
// (θ, λ) predicts λ_f. It is TESTED, never taken: a solve just below it at half a bracket's width, and — if that converges —
// one just above; a pair that converges below and fails above is a bracket like any other. A prediction that does not hold
// (the solve below fails, or the one above converges) is one bisection step, and the next converged state predicts again.
// Bisection alone took eleven solves to close a bracket on an output network's six wires; most of them retried the lowest
// failure from a nearer start.
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
        var path = new List<(double Lambda, double Theta)> { (0, Hottest(last)) };
        double? above = null;          // the second half of a predicted bracket, once its lower half has converged
        int predictedFrom = -1;        // the path's length when the last prediction was made: one prediction per new state
        while (solves < maxSolves)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var s = ConductiveBalance.Solve(at(t), options, system, last.State);
            solves++;
            if (s.Converged)
            {
                lo = t;
                last = s;
                path.Add((t, Hottest(s)));
                if (t >= 1) return new(s, false, 1, 1, s, solves);
                if (above is { } up && up < hi) { t = up; above = null; continue; }      // the prediction's upper half
                above = null;
                // the lowest known failure has now converged from nearer: it was a step too long, not the limit, so no
                // failure is known any more and the target is tried again
                if (t >= hi) hi = 1;
                if (failed is not null && Predict(path, lo, hi, width, ref predictedFrom) is { } pair)
                {
                    (t, above) = pair;
                    continue;
                }
                t = hi;
                continue;
            }
            hi = t;
            failed = s;
            above = null;
            if (width(lo, hi) <= Bracket) return new(null, true, lo, hi, last, solves) { LastFailed = failed };
            if (Predict(path, lo, hi, width, ref predictedFrom) is { } next) (t, above) = next;
            else t = (lo + hi) / 2;
        }
        // out of solves with the bracket still open: no runaway was found, the point simply did not converge
        return new(null, false, lo, hi, last, solves) { LastFailed = failed };
    }

    /// <summary>The hottest temperature of a state: its wires' when it has any, else the mesh's, °C.</summary>
    private static double Hottest(ElectrothermalSolution s)
        => s.WireTemperature.Length > 0 ? s.WireTemperature.Max(w => w.Length > 0 ? w.Max() : double.NegativeInfinity) : s.Thermal.Temperature.Max();

    /// <summary>
    /// The fold a parabola λ(θ) through the path's last three converged states predicts, as a pair of trial λ just below and
    /// just above it (half a bracket's width each side, inside (<paramref name="lo"/>, <paramref name="hi"/>)); null when the
    /// path has not changed since the last prediction, when the parabola has no maximum ahead of the last state, or when the
    /// maximum lies outside the open bracket.
    /// </summary>
    private static (double Below, double Above)? Predict(List<(double Lambda, double Theta)> path, double lo, double hi,
                                                         Func<double, double, double> width, ref int predictedFrom)
    {
        if (path.Count < 3 || predictedFrom == path.Count) return null;
        predictedFrom = path.Count;
        var (l0, t0) = path[^3];
        var (l1, t1) = path[^2];
        var (l2, t2) = path[^1];
        if (!(t0 < t1 && t1 < t2)) return null;
        // divided differences: λ(θ) = l0 + d1(θ − t0) + d2(θ − t0)(θ − t1)
        double d01 = (l1 - l0) / (t1 - t0), d12 = (l2 - l1) / (t2 - t1), d2 = (d12 - d01) / (t2 - t0);
        if (!(d2 < 0)) return null;
        // λ = c2 θ² + c1 θ + c0: its vertex
        double c2 = d2, c1 = d01 - d2 * (t0 + t1);
        double vertex = -c1 / (2 * c2);
        if (!(vertex > t2) || !double.IsFinite(vertex)) return null;
        double peak = l0 + d01 * (vertex - t0) + d2 * (vertex - t0) * (vertex - t1);
        if (!(peak > lo && peak < hi)) return null;
        // half a bracket's width each side of the peak, in λ: the width function is linear in (hi − lo) near one place
        double probe = 1e-3 * (hi - lo);
        double per = width(peak - probe, peak + probe) / (2 * probe);
        if (!(per > 0) || !double.IsFinite(per)) return null;
        double half = 0.4 * Bracket / per;
        double below = Math.Max(peak - half, lo + 0.1 * (peak - lo)), up = Math.Min(peak + half, hi - 0.1 * (hi - peak));
        return below > lo && up < hi && below < up ? (below, up) : null;
    }
}
