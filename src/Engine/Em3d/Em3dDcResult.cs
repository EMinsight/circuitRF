namespace CircuitRF.Engine.Em3d;

/// <summary>
/// brief-emdc-1 R-emdc1-1 — the 0 Hz row of a 3D problem, solved by circuitRF as steady conduction (src/Design/Em3d/Em3dDcSolve.cs).
/// <paramref name="S"/> is N×N and real, in the order of <see cref="Em3dProblem.Ports"/> by number, referred to each port's own
/// Re Z₀; null when refused, and then <paramref name="Refusal"/> is the one sentence saying why there is no DC row.
/// <paramref name="Notes"/> say what was solved: the contacts, the ties, the lumped elements, the element count and the time.
/// </summary>
public sealed record Em3dDcResult(double[,]? S, string? Refusal, IReadOnlyList<string> Notes, int Elements, double ElapsedMs);
