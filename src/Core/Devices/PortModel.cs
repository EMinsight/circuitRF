using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// RF port / termination. Stamps as a 0 V voltage source between its two nodes
/// (signal node = Nodes[0], reference node = Nodes[1]).
///
/// During S-parameter analysis the engine overrides the RHS for the driven port to
/// 1 V and reads the resulting branch currents to form the port Y-matrix (§9).
/// The port Z0 is the renormalization impedance — not a physical resistor in the network.
///
/// LastBranchIndex: set each time Stamp is called; stable across frequencies because
/// branch allocation order is deterministic (same topology, same stamp order).
/// </summary>
public sealed class PortModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// The branch row/col index assigned on the most recent Stamp call.
    /// Used by SParameterEngine to locate port branch currents in the solution vector.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        LastBranchIndex = mna.AddBranch();
        mna.AddBranchCurrent(LastBranchIndex, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(LastBranchIndex, c.Nodes[0], +Complex.One);
        mna.AddConstraint(LastBranchIndex, c.Nodes[1], -Complex.One);
        mna.AddSourceValue(LastBranchIndex, Complex.Zero); // 0 V by default
    }

    /// <summary>
    /// What a Port or Term presents to a run that is not the S-parameter one — DC, harmonic balance — at
    /// <paramref name="omega"/>: the termination it stands for, an admittance of 1/Z to its reference node (Z is
    /// 50 Ω when unstated, the default the S-parameter engine reads, which loads the network with the same 1/Z).
    /// At DC the real part alone, 1/Re(Z): a reactance has no DC meaning, and the DC and harmonic-balance engines
    /// must agree on the operating point. Never its 0 V drive branch, which would short the node.
    /// Null — no load at all — when <c>Re(Z) ≤ 0</c> (a reactive reference impedance, which the S-parameter
    /// engine also treats apart), and for a Term inside an instantiated sub-cell, which the elaborator warns is
    /// ignored. Until 2026-10-05 a Term loaded nothing outside S-parameters, so a current whose only return was
    /// a Term drove an open circuit.
    /// </summary>
    public static Complex? TerminationAdmittance(ElaboratedComponent c, double omega)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.InstancePath.Contains('.')) return null;
        Complex z = !c.Parameters.TryGetValue("Z", out var v) ? new Complex(50.0, 0)
                  : v.Kind == Expressions.ValueKind.Complex ? v.AsComplex()
                  : new Complex(v.AsReal(), 0);
        if (!(z.Real > 0) || !double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary)) return null;
        return omega == 0 ? new Complex(1.0 / z.Real, 0) : Complex.One / z;
    }
}

/// <summary>Alias for "Term" in .cnl; identical behaviour to PortModel, its termination included (<see cref="PortModel.TerminationAdmittance"/>).</summary>
public sealed class TermModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        LastBranchIndex = mna.AddBranch();
        mna.AddBranchCurrent(LastBranchIndex, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(LastBranchIndex, c.Nodes[0], +Complex.One);
        mna.AddConstraint(LastBranchIndex, c.Nodes[1], -Complex.One);
        mna.AddSourceValue(LastBranchIndex, Complex.Zero);
    }
}
