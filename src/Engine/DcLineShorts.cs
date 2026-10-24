using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine;

/// <summary>
/// A line at DC. A lossless line's ω = 0 stamp is its resonance clamp — a purely imaginary ~1/(Z0·1e-9) S — so
/// <see cref="NonlinearDcEngine"/>, which keeps the real part of every stamp, solved it as an OPEN: 1 V through a 3 mm
/// line into 50 Ω read 0 V at the load. A model that states what it is at DC (<see cref="IDcShortedTerminals"/>) is
/// stamped as that instead: each terminal pair shorted by a branch V_a = V_b, exactly as an inductor is at DC.
/// </summary>
internal static class DcLineShorts
{
    /// <summary>Stamps <paramref name="ec"/>'s DC shorts and returns true, or returns false for any other element (the
    /// caller stamps it as usual). A pair on one node is no constraint and takes no branch. No multiplier applies: m
    /// shorts in parallel are one short.</summary>
    public static bool TryStamp(IMnaContext mna, ElaboratedComponent ec)
    {
        if (ec.Model is not IDcShortedTerminals line) return false;
        foreach (var (a, b) in line.DcShortedTerminals)
        {
            int na = ec.Nodes[a], nb = ec.Nodes[b];
            if (na == nb) continue;
            int br = mna.AddBranch();
            mna.AddBranchCurrent(br, na, nb);
            mna.AddConstraint(br, na, Complex.One);
            mna.AddConstraint(br, nb, -Complex.One);
        }
        return true;
    }
}
