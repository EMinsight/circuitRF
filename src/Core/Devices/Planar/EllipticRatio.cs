namespace CircuitRF.Core.Devices.Planar;

/// <summary>
/// K(k)/K(k′), the ratio every conformal-mapping line formula is written in, by the arithmetic–geometric
/// mean: K(k) = π / (2·AGM(1, k′)), so K(k)/K(k′) = AGM(1, k) / AGM(1, k′). Exact to machine precision,
/// not one of the piecewise approximations (brief-artsch-1 R-as1-1).
///
/// <para>The modulus AND its complement are both inputs. Each formula that calls this can state k′
/// directly (sech/tanh for the stripline, √((b−a)(b+a))/b for the coplanar line), and computing it here as
/// √(1 − k²) would lose every digit of k′ exactly where a wide line drives k to 1.</para>
/// </summary>
public static class EllipticRatio
{
    /// <summary>K(k)/K(k′) for a modulus <paramref name="k"/> and complement <paramref name="kp"/>
    /// (k² + k′² = 1). 0 at k = 0, +∞ at k′ = 0.</summary>
    public static double Of(double k, double kp)
    {
        if (kp <= 0) return double.PositiveInfinity;
        if (k <= 0) return 0.0;
        return Agm(1.0, k) / Agm(1.0, kp);
    }

    /// <summary>The arithmetic–geometric mean, iterated until the two means agree to rounding.</summary>
    public static double Agm(double a, double b)
    {
        for (int i = 0; i < 64 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return 0.5 * (a + b);
    }
}
