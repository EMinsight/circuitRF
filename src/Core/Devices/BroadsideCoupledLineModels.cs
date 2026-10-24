using CircuitRF.Core.Devices.Coupled;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// BCLIN — an offset broadside-coupled STRIPLINE pair, 4-port, ground-referenced: strip A on one
/// conductor layer, strip B on the next layer down, both between two ground planes in one homogeneous
/// dielectric.
///
/// <para>Port order is CLIN's (<see cref="CLineModel"/>): 1 = line A near end, 2 = line A far end,
/// 3 = line B near end (beside 1), 4 = line B far end.</para>
///
/// <para>Physics in <see cref="BroadsideCoupledPair"/>: the cross-section's 2×2 Maxwell capacitance
/// matrix from circuitRF's own 2D field solver (<see cref="QuasiStaticSolver2D"/>), solved once per
/// geometry at construction and cached; C_e = C11 + C12, C_o = C11 − C12 (symmetric average when the
/// stack is not symmetric); both modes travel at c/√εr, Z_m = 1/(v·C_m). Conductor loss by the
/// perturbational method on the solved field, dielectric loss π·√εr·tanδ/λ₀.</para>
///
/// <para>Parameters (SI): <c>W</c> strip width (both strips), <c>O</c> lateral centre-to-centre offset
/// (≥ 0), <c>L</c> length, <c>Hs</c> separation between the strips' facing surfaces, <c>Ha</c> strip A's
/// top to the upper ground, <c>Hb</c> strip B's bottom to the lower ground, <c>T</c> strip thickness (with
/// optional <c>Ta</c>/<c>Tb</c> per strip, default T), <c>Er</c>, <c>Sigma</c>, <c>TanD</c>, optional
/// <c>Roughness</c>.</para>
/// </summary>
public sealed class StriplineBroadsideCoupledModel : ComponentModel, IReportsWarnings, IDcShortedTerminals
{
    /// <summary>Lossless at DC (both modes' γ are zero there): each strip a short end to end (1-2, 3-4).</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1), (2, 3)];

    public override int PortCount => 4;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _l, _sigma, _tanD, _rough;
    private readonly MicrostripValidityReporter _reporter;

    /// <summary>The cross-section this instance stamps.</summary>
    public BroadsideGeometry Geometry { get; }

    /// <summary>The solved modes (Ze, Zo, εeff, C matrices).</summary>
    public BroadsideModes Modes { get; }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public StriplineBroadsideCoupledModel(double w, double offset, double l, double hs, double ha, double hb,
        double ta, double tb, double epsR, double sigmaSPerM, double tanD, string instancePath, double roughnessMeters = 0.0)
    {
        if (Refusal(w, offset, l, hs, ha, hb, ta, tb, epsR) is { } why)
            throw new ArgumentException(why.Render());
        _l = l; _sigma = sigmaSPerM; _tanD = tanD; _rough = roughnessMeters;
        _reporter = new MicrostripValidityReporter(instancePath);
        double top = hb + tb + hs + ta + ha;
        Geometry = new BroadsideGeometry(w, offset, hs, hb, ta, tb, epsR, ha, top);
        Modes = BroadsideCoupledPair.Solve(Geometry);
    }

    /// <summary>The coded refusal for a cross-section no broadside stripline pair can have; null when valid.</summary>
    public static Diagnostic? Refusal(double w, double offset, double l, double hs, double ha, double hb,
                                      double ta, double tb, double epsR)
    {
        if (!(w > 0.0))
            return Diagnostic.Create("bclin.width.not-positive", DiagnosticSeverity.Error, "BCLIN: W={w} m must be positive.", ("w", w));
        if (!(offset >= 0.0))
            return Diagnostic.Create("bclin.offset.negative", DiagnosticSeverity.Error,
                "BCLIN: offset O={o} m must not be negative; it is the centre-to-centre distance, and the pair is symmetric in its sign.", ("o", offset));
        if (!(l >= 0.0))
            return Diagnostic.Create("bclin.length.negative", DiagnosticSeverity.Error, "BCLIN: L={l} m must not be negative.", ("l", l));
        if (!(hs > 0.0))
            return Diagnostic.Create("bclin.hs.not-positive", DiagnosticSeverity.Error,
                "BCLIN: strip separation Hs={hs} m must be positive.", ("hs", hs));
        if (!(ha > 0.0) || !(hb > 0.0))
            return Diagnostic.Create("bclin.ground-spacing.not-positive", DiagnosticSeverity.Error,
                "BCLIN: ground spacings Ha={ha} m and Hb={hb} m must both be positive.", ("ha", ha), ("hb", hb));
        if (!(ta >= 0.0) || !(tb >= 0.0))
            return Diagnostic.Create("bclin.thickness.negative", DiagnosticSeverity.Error,
                "BCLIN: strip thicknesses Ta={ta} m and Tb={tb} m must not be negative.", ("ta", ta), ("tb", tb));
        if (!(epsR >= 1.0))
            return Diagnostic.Create("bclin.er.below-one", DiagnosticSeverity.Error,
                "BCLIN: Er={er} is below 1; a dielectric's relative permittivity is at least that of vacuum.", ("er", epsR));
        return null;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double f = omega / (2.0 * Math.PI);
        BroadsideCoupledPair.ReportLimits("BCLIN", Modes, Geometry, f, _reporter);
        var (ge, go) = BroadsideCoupledPair.Gamma(Modes, f, Geometry.EpsR, _sigma, _tanD, _rough);
        CLineModel.StampModalPair(mna, c.Nodes[0], c.Nodes[1], c.Nodes[2], c.Nodes[3],
                                  Modes.Ze, ge * _l, Modes.Zo, go * _l);
    }
}

/// <summary>
/// MBCLIN — a broadside-coupled MICROSTRIP pair, 4-port, ground-referenced: one ground plane below,
/// strip B buried in the dielectric at <c>Hb</c> above it, strip A <c>Hs</c> above strip B, the dielectric
/// <c>Hd</c> thick over the ground, and air above it (no top ground).
///
/// <para>Port order is CLIN's: 1 = line A (upper) near end, 2 = A far end, 3 = line B (buried) near end,
/// 4 = B far end.</para>
///
/// <para>Physics in <see cref="BroadsideCoupledPair"/>: the medium is inhomogeneous, so the field solver
/// runs twice — with the dielectric (C) and all-vacuum (C₀) — and each mode gets its own
/// Z_m = 1/(c·√(C_m·C₀_m)) and εeff,m = C_m/C₀_m: the modes travel at different speeds, which is what
/// limits the directivity. The strips sit at different heights, so the pair is never symmetric and the
/// symmetric-average convention is always in use (reported when the asymmetry exceeds 5 %).</para>
///
/// <para>Parameters (SI): <c>W</c>, <c>O</c>, <c>L</c>, <c>Hb</c> (ground to B's bottom), <c>Hs</c>
/// (B's top to A's bottom), <c>T</c> (optional <c>Ta</c>/<c>Tb</c>), optional <c>Hd</c> (dielectric
/// thickness; absent = <c>Hb + Tb + Hs</c>, i.e. strip A on the top surface), <c>Er</c>, <c>Sigma</c>,
/// <c>TanD</c>, optional <c>Roughness</c>. An optional <c>Ha</c> adds a top ground that far above strip
/// A's top — a covered (shielded) line; with <c>Hd</c> reaching it the pair is BCLIN's stripline, solved
/// the general way (C and C₀ separately), which is how the two parts are held to each other.</para>
/// </summary>
public sealed class MicrostripBroadsideCoupledModel : ComponentModel, IReportsWarnings, IDcShortedTerminals
{
    /// <summary>Lossless at DC (both modes' γ are zero there): each strip a short end to end (1-2, 3-4).</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1), (2, 3)];

    public override int PortCount => 4;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _l, _sigma, _tanD, _rough;
    private readonly MicrostripValidityReporter _reporter;

    public BroadsideGeometry Geometry { get; }
    public BroadsideModes Modes { get; }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    /// <param name="hd">Dielectric thickness; <see cref="double.NaN"/> = strip A on the surface.</param>
    /// <param name="haCover">Strip A's top to an optional top ground; <see cref="double.NaN"/> = open above.</param>
    public MicrostripBroadsideCoupledModel(double w, double offset, double l, double hb, double hs, double ta, double tb,
        double hd, double epsR, double sigmaSPerM, double tanD, string instancePath, double roughnessMeters = 0.0,
        double haCover = double.NaN)
    {
        if (double.IsNaN(hd)) hd = hb + tb + hs;
        if (Refusal(w, offset, l, hb, hs, ta, tb, hd, epsR) is { } why)
            throw new ArgumentException(why.Render());
        if (!double.IsNaN(haCover) && !(haCover > 0.0))
            throw new ArgumentException(Diagnostic.Create("mbclin.cover.not-positive", DiagnosticSeverity.Error,
                "MBCLIN: cover spacing Ha={ha} m must be positive, or absent for an open line.", ("ha", haCover)).Render());
        _l = l; _sigma = sigmaSPerM; _tanD = tanD; _rough = roughnessMeters;
        _reporter = new MicrostripValidityReporter(instancePath);
        Geometry = new BroadsideGeometry(w, offset, hs, hb, ta, tb, epsR, double.IsNaN(haCover) ? null : haCover, hd);
        Modes = BroadsideCoupledPair.Solve(Geometry, useHomogeneity: false);
    }

    public static Diagnostic? Refusal(double w, double offset, double l, double hb, double hs, double ta, double tb,
                                      double hd, double epsR)
    {
        if (!(w > 0.0))
            return Diagnostic.Create("mbclin.width.not-positive", DiagnosticSeverity.Error, "MBCLIN: W={w} m must be positive.", ("w", w));
        if (!(offset >= 0.0))
            return Diagnostic.Create("mbclin.offset.negative", DiagnosticSeverity.Error,
                "MBCLIN: offset O={o} m must not be negative; it is the centre-to-centre distance.", ("o", offset));
        if (!(l >= 0.0))
            return Diagnostic.Create("mbclin.length.negative", DiagnosticSeverity.Error, "MBCLIN: L={l} m must not be negative.", ("l", l));
        if (!(hs > 0.0))
            return Diagnostic.Create("mbclin.hs.not-positive", DiagnosticSeverity.Error,
                "MBCLIN: strip separation Hs={hs} m must be positive.", ("hs", hs));
        if (!(hb > 0.0))
            return Diagnostic.Create("mbclin.hb.not-positive", DiagnosticSeverity.Error,
                "MBCLIN: Hb={hb} m (ground to the buried strip) must be positive.", ("hb", hb));
        if (!(ta >= 0.0) || !(tb >= 0.0))
            return Diagnostic.Create("mbclin.thickness.negative", DiagnosticSeverity.Error,
                "MBCLIN: strip thicknesses Ta={ta} m and Tb={tb} m must not be negative.", ("ta", ta), ("tb", tb));
        if (!(hd >= hb + tb + hs - 1e-12 * (hb + tb + hs)))
            return Diagnostic.Create("mbclin.hd.below-strip-a", DiagnosticSeverity.Error,
                "MBCLIN: dielectric thickness Hd={hd} m is below strip A's bottom at Hb + Tb + Hs = {ya} m; the dielectric " +
                "must reach the upper strip.", ("hd", hd), ("ya", hb + tb + hs));
        if (!(epsR >= 1.0))
            return Diagnostic.Create("mbclin.er.below-one", DiagnosticSeverity.Error,
                "MBCLIN: Er={er} is below 1; a dielectric's relative permittivity is at least that of vacuum.", ("er", epsR));
        return null;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double f = omega / (2.0 * Math.PI);
        BroadsideCoupledPair.ReportLimits("MBCLIN", Modes, Geometry, f, _reporter);
        var (ge, go) = BroadsideCoupledPair.Gamma(Modes, f, Geometry.EpsR, _sigma, _tanD, _rough);
        CLineModel.StampModalPair(mna, c.Nodes[0], c.Nodes[1], c.Nodes[2], c.Nodes[3],
                                  Modes.Ze, ge * _l, Modes.Zo, go * _l);
    }
}
