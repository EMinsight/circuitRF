using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-123 gate 2 — a strip's Z₀ and ε_eff through openEMS, at the shipped grid settings, against closed forms
//  produced independently of circuitRF. A lumped port drawn across a strip's full width puts its extents on the strip's
//  edges, inside their thirds pairs; until brief 123 those extents were lines, the edge had three lines with the middle
//  one on it, and the strip read 6 % (stripline) and 4-6 % (microstrip) low in Z₀ — the extent now snaps to the pair's
//  inside line (src/Engine/RESOLVED.md, brief-em3d-123).
//
//  Z₀ and ε_eff come from two lengths (10 and 30 mm): ε_eff from the eigenvalue of T₃₀·T₁₀⁻¹, Z₀ from its eigenvectors as
//  (Z₊ − Z₋)/2 — exact for a port error box that is identity at low frequency, second order in ω otherwise. Z₀ is not
//  identifiable from two lines alone (an ideal transformer in the error boxes commutes with the line), so that assumption
//  is the extraction's; the two lines' own √(B/C) agree with it to about 1 % at 1 GHz.
//
//  The shipped grid puts one cell through a 254 µm substrate, which would swamp the edge, so each dielectric is drawn as
//  thin layers of one material and the .cem states MinCellUm 10 µm (the default, a tenth of the strip, merges them): every
//  interface is a required line, and z is resolved with nothing the product lacks. x and y are the shipped grid.
//  Both run openEMS for well over 5 s: Category=Benchmark.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class OpenEmsThirdsAccuracyTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6, C0 = 299_792_458.0, Eta0 = 376.730313668;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-thirds-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// A zero-thickness-like (1 µm) 800 µm strip centred in 1 mm of εr 2.2 between two grounds, 1-4 GHz, against Cohn's
    /// exact zero-thickness Z₀ (51.18 Ω) and εr.
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Stripline_Z0AndEpsEff_MatchCohn()
    {
        const double w = 800, b = 1000, er = 2.2;
        double k = 1 / Math.Cosh(Math.PI * w / (2 * b));
        double z0 = Eta0 / (4 * Math.Sqrt(er)) * EllipK(k) / EllipK(Math.Sqrt(1 - k * k));
        var side = new EmAirBoxFace(5 * b, Em3dBoundaryKind.Absorbing);
        var wall = new EmAirBoxFace(5 * b, Em3dBoundaryKind.Pec);
        var (dz0, deps) = Measure("stripline", w, Layers(b / 2 - 1, 50), Layers(b / 2, 50), er,
                                  new EmAirBox(side, side, wall, wall, null, new EmAirBoxFace(0, Em3dBoundaryKind.Pec)),
                                  ("1", "4", 7), z0, _ => er);
        // Measured +1.2 to +2.5 % in Z₀ (−6.0 to −7.6 % with the extents on lines), ε_eff within 0.9 %.
        Assert.InRange(dz0, -3, 3.5);
        Assert.InRange(deps, -1.5, 1.5);
    }

    /// <summary>
    /// <c>FdtdGridTests.Microstrip()</c>'s section — 600 µm on 254 µm of εr 9.8, a 1 µm strip, open above — 1-10 GHz,
    /// against Hammerstad–Jensen's zero-thickness Z₀ (30.21 Ω, within 0.2 %) and Kirschning–Jansen's ε_eff(f) (within
    /// 0.6 %).
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Microstrip_Z0AndEpsEff_MatchHammerstadJensenAndKirschningJansen()
    {
        const double w = 600, h = 254, er = 9.8;
        var (z0, e0) = HammerstadJensen(w / h, er);
        var open = new EmAirBoxFace(2000, Em3dBoundaryKind.Absorbing);
        var (dz0, deps) = Measure("microstrip", w, [], Layers(h, 25.4), er, new EmAirBox(open, open, open, open, null, open),
                                  ("1", "10", 10), z0, f => KirschningJansen(w / h, er, e0, f * h * 1e-3 / 1e9));
        // Measured +1.4 to +3.4 % in Z₀ (−4.3 to −6.2 % with the extents on lines), ε_eff +1.1 to +2.3 %.
        Assert.InRange(dz0, -3, 4);
        Assert.InRange(deps, -3, 3);
    }

    /// <summary>Runs both lengths and returns the worst signed Z₀ and ε_eff errors, in percent.</summary>
    private (double Z0, double Eps) Measure(string name, double wUm, double[] aboveUm, double[] belowUm, double er, EmAirBox box,
                                            (string Start, string Stop, int Points) band, double z0Ref, Func<double, double> epsRef)
    {
        var snps = new List<SNP>();
        foreach (double lenMm in new[] { 10.0, 30.0 })
        {
            var (setup, source) = Line(name, wUm, lenMm, aboveUm, belowUm, er, box, band);
            var result = EmRunService.Run(setup, source, Path.Combine(_root, $"{name}-{lenMm}mm"));
            Assert.True(result.Status == EmRunStatus.Ok, result.Error);
            foreach (string n in result.Notes ?? []) if (n.StartsWith("openEMS grid", StringComparison.Ordinal) || n.Contains("snapped", StringComparison.Ordinal) || n.Contains("thirds pair", StringComparison.Ordinal))
                output.WriteLine($"{lenMm} mm: {n}");
            snps.Add(TouchstoneIO.ReadFile(result.SnpPath!));
        }

        double worstZ = 0, worstE = 0, prev = 0;
        var f = snps[0].Frequencies;
        for (int i = 0; i < f.Length; i++)
        {
            var t1 = Abcd(snps[0][i]);
            var t2 = Abcd(snps[1][i]);
            var m = Mul(t2, Inv(t1));
            Complex tr = m[0, 0] + m[1, 1], det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0], sq = Complex.Sqrt(tr * tr - 4 * det);
            Complex la = (tr + sq) / 2, lb = (tr - sq) / 2;
            Complex za = m[0, 1] / (la - m[0, 0]), zb = m[0, 1] / (lb - m[0, 0]);
            if (za.Real < 0) { (la, lb) = (lb, la); (za, zb) = (zb, za); }
            double phase = la.Phase;
            while (phase < prev - Math.PI) phase += 2 * Math.PI;
            prev = phase;
            double eps = Math.Pow(phase / 20e-3 * C0 / (2 * Math.PI * f[i]), 2), z0 = ((za - zb) / 2).Real;
            double dz = (z0 - z0Ref) / z0Ref * 100, de = (eps - epsRef(f[i])) / epsRef(f[i]) * 100;
            if (Math.Abs(dz) > Math.Abs(worstZ)) worstZ = dz;
            if (Math.Abs(de) > Math.Abs(worstE)) worstE = de;
            output.WriteLine($"{f[i] / 1e9,5:F2} GHz  Z0 {z0,7:F3} Ω (ref {z0Ref:F3}, {dz,6:F2} %)  ε_eff {eps,7:F4} (ref {epsRef(f[i]):F4}, {de,6:F2} %)");
        }
        output.WriteLine($"{name}: worst ΔZ0 {worstZ:F2} %, worst Δε_eff {worstE:F2} %");
        return (worstZ, worstE);
    }

    private static double[] Layers(double totalUm, double eachUm)
    {
        int n = (int)Math.Ceiling(totalUm / eachUm - 1e-9);
        return [.. Enumerable.Repeat(totalUm / n, n)];
    }

    /// <summary>A strip of width <paramref name="wUm"/> and length <paramref name="lenMm"/>, a 1 µm sheet with dielectric
    /// layers of one εr above and below it over an undrawn ground (the PEC floor), a lumped port across its full width at
    /// each end.</summary>
    private static (EmSetup, EmLayoutSource) Line(string name, double wUm, double lenMm, double[] aboveUm, double[] belowUm,
                                                  double er, EmAirBox box, (string Start, string Stop, int Points) band)
    {
        string erText = er.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string Dielectric(string n, double um) =>
            $$"""{ "Kind": "Dielectric", "Name": "{{n}}", "ThicknessDbu": {{Math.Round(um * 1000)}}, "Epsr": {{erText}}, "TanD": 0, "Mur": 1, "SigmaSm": 0, "DrawingLayers": [] },""";
        string layers =
            string.Concat(aboveUm.Select((t, i) => Dielectric($"Above{i}", t))) +
            """{ "Kind": "Conductor", "Name": "Strip", "ThicknessDbu": 1000, "Epsr": 1, "TanD": 0, "Mur": 1, "SigmaSm": 58000000, "DrawingLayers": [ { "Layer": 1, "Datatype": 0 } ] },""" +
            string.Concat(belowUm.Select((t, i) => Dielectric($"Below{i}", t))) +
            """{ "Kind": "Conductor", "Name": "Ground", "ThicknessDbu": 35000, "Epsr": 1, "TanD": 0, "Mur": 1, "SigmaSm": 58000000, "DrawingLayers": [ { "Layer": 2, "Datatype": 0 } ], "IsGroundReference": true }""";
        string tech = $$"""
            { "FormatVersion": 1, "Name": "{{name}}", "Layers": [ { "Key": { "Layer": 1, "Datatype": 0 }, "Name": "Strip" } ],
              "Stackup": { "Top": "Open", "Bottom": "Open", "Layers": [ {{layers}} ] } }
            """;
        long half = (long)Math.Round(wUm / 2 * 1000), length = (long)Math.Round(lenMm * 1e6);
        string clay = $$"""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": {{-half}}, "X2": {{length}}, "Y2": {{half}} },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": {{length}}, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var setup = new EmSetup
        {
            Name = name, LayoutRef = name + ".clay",
            Frequency = new FrequencySpec(band.Start, band.Stop, band.Points, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.OpenEms,
            AirBox = box,
            // The default MinCell, a tenth of the strip, would merge the thin layers back into one cell.
            OpenEms = new CemOpenEms { MinCellUm = 10 },
        };
        return (setup, new EmLayoutSource($"/nowhere/{name}.clay", view, TechPersistence.Deserialize(tech), view.DbuPerMicron));
    }

    // ── closed forms ────────────────────────────────────────────────────────────────────────────

    /// <summary>K(k), the complete elliptic integral of the first kind, by the arithmetic-geometric mean.</summary>
    private static double EllipK(double k)
    {
        double a = 1, g = Math.Sqrt(1 - k * k);
        for (int i = 0; i < 30; i++) (a, g) = ((a + g) / 2, Math.Sqrt(a * g));
        return Math.PI / (2 * a);
    }

    private static (double Z0, double EpsEff) HammerstadJensen(double u, double er)
    {
        double a = 1 + Math.Log((Math.Pow(u, 4) + Math.Pow(u / 52, 2)) / (Math.Pow(u, 4) + 0.432)) / 49 + Math.Log(1 + Math.Pow(u / 18.1, 3)) / 18.7;
        double b = 0.564 * Math.Pow((er - 0.9) / (er + 3), 0.053);
        double e = (er + 1) / 2 + (er - 1) / 2 * Math.Pow(1 + 10 / u, -a * b);
        double fu = 6 + (2 * Math.PI - 6) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));
        return (Eta0 / (2 * Math.PI) * Math.Log(fu / u + Math.Sqrt(1 + 4 / (u * u))) / Math.Sqrt(e), e);
    }

    /// <param name="fn">f·h in GHz·mm.</param>
    private static double KirschningJansen(double u, double er, double e0, double fn)
    {
        double p1 = 0.27488 + (0.6315 + 0.525 / Math.Pow(1 + 0.0157 * fn, 20)) * u - 0.065683 * Math.Exp(-8.7513 * u);
        double p2 = 0.33622 * (1 - Math.Exp(-0.03442 * er));
        double p3 = 0.0363 * Math.Exp(-4.6 * u) * (1 - Math.Exp(-Math.Pow(fn / 38.7, 4.97)));
        double p4 = 1 + 2.751 * (1 - Math.Exp(-Math.Pow(er / 15.916, 8)));
        return er - (er - e0) / (1 + p1 * p2 * Math.Pow((0.1844 + p3 * p4) * fn, 1.5763));
    }

    // ── two-port algebra (ABCD, 50 Ω reference) ─────────────────────────────────────────────────

    private static Complex[,] Abcd(NumFlat.Mat<Complex> s)
    {
        Complex s11 = s[0, 0], s12 = s[0, 1], s21 = s[1, 0], s22 = s[1, 1], d = 2 * s21;
        const double z = 50;
        return new Complex[,]
        {
            { ((1 + s11) * (1 - s22) + s12 * s21) / d, z * ((1 + s11) * (1 + s22) - s12 * s21) / d },
            { ((1 - s11) * (1 - s22) - s12 * s21) / d / z, ((1 - s11) * (1 + s22) + s12 * s21) / d },
        };
    }

    private static Complex[,] Mul(Complex[,] a, Complex[,] b) => new Complex[,]
    {
        { a[0, 0] * b[0, 0] + a[0, 1] * b[1, 0], a[0, 0] * b[0, 1] + a[0, 1] * b[1, 1] },
        { a[1, 0] * b[0, 0] + a[1, 1] * b[1, 0], a[1, 0] * b[0, 1] + a[1, 1] * b[1, 1] },
    };

    private static Complex[,] Inv(Complex[,] a)
    {
        Complex det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        return new Complex[,] { { a[1, 1] / det, -a[0, 1] / det }, { -a[1, 0] / det, a[0, 0] / det } };
    }
}
