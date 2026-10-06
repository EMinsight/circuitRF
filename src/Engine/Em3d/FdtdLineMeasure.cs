// brief-em3d-116 R-em3d116-2e — a wave-port terminal's line, measured from its own probes.
//
// From the three voltage planes (spacing h, the middle on the reference plane) and the two current planes between
// them, at the middle plane: E = U, E' = (U_c − U_a)/(2h), H = (I_a + I_b)/2, H' = (I_b − I_a)/h, derivatives along the
// line INTO the structure. For any sum of a forward and a backward wave on a uniform line, V' = −jβZ·I and
// I' = −jβ·V/Z, so β = √(−E'H'/(EH)) and Z = √(EE'/(HH')) — both exact whatever the termination, up to the
// differences' O((βh)²) error. A staircased coax running slow on a Cartesian grid shows here (113-a: ε_eff 2.20 for
// εr 2.1). FdtdPortTransform is not touched: this reads the same DFT.

using System.Numerics;

namespace CircuitRF.Engine.Em3d;

/// <summary>The line's characteristic impedance and effective permittivity at each frequency.</summary>
public sealed record FdtdLineMeasure(double[] FrequenciesHz, Complex[] Z, double[] EpsEff);

public static class FdtdLineProbes
{
    /// <summary>
    /// Z and ε_eff from a terminal's five probes in the run that excited it. <paramref name="spacingM"/> is the voltage
    /// planes' spacing; <paramref name="ua"/> is the plane nearer the source, <paramref name="uc"/> the one nearer the
    /// structure, and the currents flow into the structure.
    /// </summary>
    public static FdtdLineMeasure Measure(FdtdProbe ua, FdtdProbe u, FdtdProbe uc, FdtdProbe ia, FdtdProbe ib,
                                          double spacingM, double[] frequenciesHz)
    {
        var Ua = FdtdPortTransform.Dft(ua, frequenciesHz);
        var U  = FdtdPortTransform.Dft(u, frequenciesHz);
        var Uc = FdtdPortTransform.Dft(uc, frequenciesHz);
        var Ia = FdtdPortTransform.Dft(ia, frequenciesHz);
        var Ib = FdtdPortTransform.Dft(ib, frequenciesHz);
        int n = frequenciesHz.Length;
        var z = new Complex[n];
        var eps = new double[n];
        for (int k = 0; k < n; k++)
        {
            Complex e = U[k], de = (Uc[k] - Ua[k]) / (2 * spacingM);
            Complex h = (Ia[k] + Ib[k]) / 2, dh = (Ib[k] - Ia[k]) / spacingM;
            Complex beta = Complex.Sqrt(-de * dh / (e * h));
            if (beta.Real < 0) beta = -beta;
            Complex zl = Complex.Sqrt(e * de / (h * dh));
            if (zl.Real < 0) zl = -zl;
            double k0 = 2 * Math.PI * frequenciesHz[k] / FdtdGrid.C0;
            z[k] = zl;
            eps[k] = k0 > 0 ? Math.Pow(beta.Real / k0, 2) : double.NaN;
        }
        return new FdtdLineMeasure(frequenciesHz, z, eps);
    }
}
