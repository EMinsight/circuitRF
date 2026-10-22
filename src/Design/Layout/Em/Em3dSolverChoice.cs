// brief-emi-1 R-emi1-1a — WHICH 3D solver, and which a setup asks for.
//
// Em3dSolverId names one solver; Em3dSolverChoice is what a setup's Solver3D holds: nothing (a planar setup), one
// solver, or a comparison of several with one of them the reference. What a solver IS — its name, its result token,
// what it can solve, what it needs installed, how it runs — is its descriptor's, in src/Design/Em3d/Solvers/<Name>/,
// and Em3dSolvers is the one list of them. Nothing outside the registry, the descriptors and the .cem/.c3d format
// names a solver by its id: a question about a setup's solver is asked of the choice (Runs, Solvers, IsCompare) or of
// a descriptor's Capabilities.

using CircuitRF.Design.Em3d;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// One 3D solver. <b>Closed and without a "both" member</b>: several solvers on one problem is an
/// <see cref="Em3dSolverChoice"/>, not a solver. Its member names are the spellings a <c>.c3d</c>'s field plot stores.
/// </summary>
public enum Em3dSolverId { Palace, OpenEms, Emerge }

/// <summary>
/// <b>brief-em3d-3 R-em3d3-3 / brief-emi-1 R-emi1-1a — which 3D solver(s) a setup runs, if any.</b>
/// <see cref="None"/> is a planar setup exactly as every <c>.cem</c> before 3D was; one solver makes the setup a 3D one;
/// a <see cref="Compare"/> runs each of several on one generated problem, in order, and compares them against
/// <see cref="Reference"/>.
///
/// <para><b>A separate field, never an <see cref="EmAnalysisKind"/> member</b> (overview §1g, R-em3d3-3a). <c>Auto</c>
/// picks among the planar kernels and must never land on a 3D solver: one needs an installed program, can take an hour,
/// and would change the number an existing <c>.cem</c> produces.</para>
///
/// <para>Immutable, with value equality. An <see cref="Em3dSolverId"/> converts to the one-solver choice.</para>
/// </summary>
public sealed class Em3dSolverChoice : IEquatable<Em3dSolverChoice>, IJsonSpelled
{
    /// <summary>A planar setup: no 3D solver.</summary>
    public static Em3dSolverChoice None { get; } = new([], default);

    private Em3dSolverChoice(IReadOnlyList<Em3dSolverId> solvers, Em3dSolverId reference)
    {
        Solvers = solvers;
        Reference = reference;
    }

    /// <summary>The solvers this setup runs, in the order a run runs them. Empty for <see cref="None"/>.</summary>
    public IReadOnlyList<Em3dSolverId> Solvers { get; }

    /// <summary>The one solver, or a comparison's reference. Meaningless on <see cref="None"/>.</summary>
    public Em3dSolverId Reference { get; }

    /// <summary>True for a planar setup.</summary>
    public bool IsNone => Solvers.Count == 0;

    /// <summary>True when several solvers run on one problem and are compared.</summary>
    public bool IsCompare => Solvers.Count > 1;

    /// <summary>Whether <paramref name="solver"/> is one this setup runs.</summary>
    public bool Runs(Em3dSolverId solver) => Solvers.Contains(solver);

    /// <summary>The setup runs <paramref name="solver"/> alone.</summary>
    public static Em3dSolverChoice One(Em3dSolverId solver) => new([solver], solver);

    /// <summary>
    /// The setup runs each of <paramref name="solvers"/> on one problem, in that order, and compares them against
    /// <paramref name="reference"/>, which must be one of them.
    /// </summary>
    public static Em3dSolverChoice Compare(IReadOnlyList<Em3dSolverId> solvers, Em3dSolverId reference)
    {
        ArgumentNullException.ThrowIfNull(solvers);
        if (solvers.Count < 2 || solvers.Distinct().Count() != solvers.Count)
            throw new ArgumentException("A comparison names at least two different solvers.", nameof(solvers));
        if (!solvers.Contains(reference))
            throw new ArgumentException("A comparison's reference is one of its solvers.", nameof(reference));
        return new([.. solvers], reference);
    }

    public static implicit operator Em3dSolverChoice(Em3dSolverId solver) => One(solver);

    public bool Equals(Em3dSolverChoice? other)
        => other is not null && Reference == other.Reference && Solvers.SequenceEqual(other.Solvers);

    public override bool Equals(object? obj) => Equals(obj as Em3dSolverChoice);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Reference);
        foreach (var s in Solvers) h.Add(s);
        return h.ToHashCode();
    }

    public static bool operator ==(Em3dSolverChoice? a, Em3dSolverChoice? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(Em3dSolverChoice? a, Em3dSolverChoice? b) => !(a == b);

    /// <summary>The <c>.cem</c>'s spelling: <c>None</c>, <c>Palace</c>, <c>OpenEms</c>, <c>Both</c>.</summary>
    public override string ToString() => Em3dSolvers.Spelling(this);

    /// <summary>What the key <c>Solver3D</c> may hold, as <c>circuitrf reference em-setup</c> lists it.</summary>
    public static IReadOnlyList<string> JsonSpellings => [Em3dSolvers.Spelling(None), .. Em3dSolvers.Spellings];
}
