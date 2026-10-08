using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices.Planar;

/// <summary>
/// The stripline's closed forms (brief-artsch-1 R-as1-2; docs/design/planar-line-models.md §3). All in air:
/// the line is TEM in a homogeneous dielectric, so Z₀ = Z₀,air/√εr and ε_eff = εr at every frequency.
///
/// <para><b>Centred, zero thickness — exact.</b> S. B. Cohn, "Characteristic impedance of the shielded-strip
/// transmission line," IRE Trans. MTT 2(2), 52–57, 1954: Z₀,air = (η₀/4)·K(k)/K(k′), k = sech(πW/2b), b the
/// plane spacing.</para>
///
/// <para><b>Thickness — Wheeler.</b> H. A. Wheeler, "Transmission-line properties of a strip line between
/// parallel planes," IEEE Trans. MTT 26(11), 866–876, 1978: the effective width
/// W′ = W + ΔW, ΔW/(b−t) = x/(π(1−x))·{1 − ½ln[(x/(2−x))² + (0.0796x/(W/b + 1.1x))^m]},
/// m = 2/(1 + ⅔·x/(1−x)), x = t/b, into Z₀,air = (η₀/4π)·ln{1 + (4/π)(b−t)/W′·[(8/π)(b−t)/W′ +
/// √(((8/π)(b−t)/W′)² + 6.27)]}. It is applied as the RATIO of Wheeler's thick-strip value to his own
/// thin-strip value, multiplying Cohn's exact one, so t = 0 is exact rather than Wheeler's 0.5 %
/// approximation of it and the thickness effect is Wheeler's.</para>
///
/// <para><b>Offset — the parallel combination.</b> Each half is taken as a centred line of plane spacing
/// 2·Hᵢ + t, and the offset line's capacitance as the sum of the two halves:
/// Z₀ = 2·Z₁Z₂/(Z₁ + Z₂) (B. C. Wadell, "Transmission Line Design Handbook," Artech House, 1991, §3.5.2 —
/// the textbook approximation for an offset stripline). It ignores how the two halves' fringing fields
/// interact at the strip's edge, which grows with the offset: against the field solve it is 1.2 % high at
/// H₂/H₁ = 2 and 3.4 % at 4, which is why <see cref="OffsetRatioRange"/> stops at 2.</para>
/// </summary>
public static class Stripline
{
    public const string ModelName = "SLIN";

    /// <summary>Where the offset combination is within 1.5 % of the field solve.</summary>
    public static readonly ValidityRange OffsetRatioRange = new(1.0, 2.0);
    /// <summary>The field-solve comparison covered W/b from 0.11 to 1.2 and t/b to 0.06 (testdata/planar-lines);
    /// the centred zero-thickness form is exact everywhere, so these bound the thickness and offset terms,
    /// a factor of about two past what was measured.</summary>
    public static readonly ValidityRange WOverBRange = new(0.05, 2.5);
    public static readonly ValidityRange TOverBRange = new(0.0, 0.12);

    /// <summary>Cohn's exact zero-thickness centred line, air, planes <paramref name="b"/> apart.</summary>
    public static double CentredExactAir(double w, double b)
    {
        double x = Math.PI * w / (2 * b);
        return PlanarLineLoss.Eta0 / 4 * EllipticRatio.Of(1 / Math.Cosh(x), Math.Tanh(x));
    }

    /// <summary>Wheeler's 1978 centred line, air: a strip <paramref name="t"/> thick between planes
    /// <paramref name="b"/> apart.</summary>
    public static double WheelerAir(double w, double b, double t)
    {
        double dw = 0;
        if (t > 0)
        {
            double x = t / b;
            double m = 2 / (1 + 2.0 / 3.0 * x / (1 - x));
            double inner = Math.Pow(x / (2 - x), 2) + Math.Pow(0.0796 * x / (w / b + 1.1 * x), m);
            dw = (b - t) * x / (Math.PI * (1 - x)) * (1 - 0.5 * Math.Log(inner));
        }
        double r = (b - t) / (w + dw);
        double q = 8 / Math.PI * r;
        return PlanarLineLoss.Eta0 / (4 * Math.PI) * Math.Log(1 + 4 / Math.PI * r * (q + Math.Sqrt(q * q + 6.27)));
    }

    /// <summary>The centred line, air: Cohn's exact value scaled by Wheeler's thick-to-thin ratio.</summary>
    public static double CentredAir(double w, double b, double t)
        => CentredExactAir(w, b) * (t > 0 ? WheelerAir(w, b, t) / WheelerAir(w, b, 0) : 1.0);

    /// <summary>The (possibly offset) line, air: <paramref name="h1"/> and <paramref name="h2"/> are the
    /// dielectric from the strip's faces to the plane above and below.</summary>
    public static double Z0Air(double w, double h1, double h2, double t)
    {
        double z1 = CentredAir(w, 2 * h1 + t, t);
        double z2 = CentredAir(w, 2 * h2 + t, t);
        return 2 * z1 * z2 / (z1 + z2);
    }
}
