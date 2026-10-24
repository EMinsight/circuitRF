using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-2 R-mc2-3: outside its training domain a table or ANN constituent is continued
/// linearly from the nearest domain point, currents with an added conductance — continuous in value and
/// gradient across the boundary, exactly differentiable outside it, finite everywhere, and a DC solve
/// started far outside comes back in. The model mixes both methods over a pentagon with an oblique edge.
/// </summary>
public sealed class GuidedExtrapolationTests(ITestOutputHelper output)
{
    private static readonly ComposedCoreModel Mixed = RuntimeTestModels.Core(RuntimeTestModels.MixedModel());

    /// <summary>The same model under the local law (brief-modelcomposer-29): each added conductance ten times the edge's
    /// own, which the gradient must follow as p_b moves.</summary>
    private static readonly ComposedCoreModel MixedLocal = RuntimeTestModels.Core(RuntimeTestModels.MixedModel() is var m
        ? m with { Extrapolation = ModelExtrapolation.Local(m.Extrapolation!.Gx) } : null!);

    private static (double[] I, double[] Q, double[,] Dg, double[,] Dc) At(ComposedCoreModel core, double vgs, double vds)
    {
        var r = core.Evaluate(new PortVoltages([vgs, vds]));
        return (r.I, r.Q, r.Dg, r.Dc);
    }

    [Theory]
    [InlineData(-1.0, 0.0, 0.0, -1.0)]    // bottom edge
    [InlineData(1.0, 4.0, 1.0, 0.0)]      // right edge
    [InlineData(0.5, 9.0, 2.0, 1.0)]      // the oblique edge
    public void ValueAndGradient_AreContinuousAcrossTheBoundary(double vgs, double vds, double nx, double ny)
    {
        const double eps = 1e-12;
        var a = At(Mixed, vgs - eps * nx, vds - eps * ny);
        var b = At(Mixed, vgs + eps * nx, vds + eps * ny);

        // Relative to each quantity's own size: a value's is what it is plus what its slope moves
        // it by over a volt, so a value passing through zero on the boundary is still judged.
        double gScale = Flat(a.Dg).Max(Math.Abs), cScale = Flat(a.Dc).Max(Math.Abs);
        Close(a.I, b.I, a.I.Max(Math.Abs) + gScale, "i");
        Close(a.Q, b.Q, a.Q.Max(Math.Abs) + cScale, "q");
        Close(Flat(a.Dg), Flat(b.Dg), gScale, "dg");
        Close(Flat(a.Dc), Flat(b.Dc), cScale, "dc");

        static void Close(double[] x, double[] y, double scale, string what)
        {
            for (int k = 0; k < x.Length; k++)
                Assert.True(Math.Abs(x[k] - y[k]) <= 1e-9 * scale, $"{what}[{k}]: {x[k]:R} inside vs {y[k]:R} outside");
        }
    }

    [Theory]
    [InlineData(-1.0, -2.0)]     // beyond an edge
    [InlineData(2.5, 4.0)]
    [InlineData(1.25, 11.34)]    // beyond the oblique edge: the projection's Jacobian is not axis-aligned
    [InlineData(2.2, -3.0)]      // in a vertex's normal cone
    [InlineData(-4.2, 13.0)]
    [InlineData(-4.0, 5.0)]      // beyond the low-vgs edge, where the gate's own conductance is all but zero
    public void OutsideTheDomain_TheJacobian_IsTheExactDerivative(double vgs, double vds)
    {
        foreach (var core in (ComposedCoreModel[])[Mixed, MixedLocal]) Jacobian(core, vgs, vds);
    }

    private static void Jacobian(ComposedCoreModel core, double vgs, double vds)
    {
        var at = At(core, vgs, vds);
        const double h = 1e-6;
        for (int col = 0; col < 2; col++)
        {
            double[] p = [vgs, vds], m = [vgs, vds];
            p[col] += h; m[col] -= h;
            var up = At(core, p[0], p[1]);
            var dn = At(core, m[0], m[1]);
            for (int row = 0; row < 2; row++)
            {
                double fdG = (up.I[row] - dn.I[row]) / (2 * h), fdC = (up.Q[row] - dn.Q[row]) / (2 * h);
                double sG = Math.Max(Math.Abs(at.Dg[row, 0]) + Math.Abs(at.Dg[row, 1]), 1e-9);
                double sC = Math.Max(Math.Abs(at.Dc[row, 0]) + Math.Abs(at.Dc[row, 1]), 1e-24);
                Assert.True(Math.Abs(fdG - at.Dg[row, col]) <= 1e-6 * sG, $"dg[{row},{col}]: {at.Dg[row, col]:R} vs {fdG:R}");
                Assert.True(Math.Abs(fdC - at.Dc[row, col]) <= 1e-6 * sC, $"dc[{row},{col}]: {at.Dc[row, col]:R} vs {fdC:R}");
            }
        }
    }

    /// <summary>
    /// R-mc29-1: a volt beyond the edge where the gate current is flat (IgGs is 1e-4·σ(5·vgs − 4), ~3e-12 S at −3 V), the
    /// local law continues it as the data says — the linear continuation plus a floor-sized guide — where the global law
    /// adds G_x = 0.1 S.
    /// </summary>
    [Fact]
    public void BeyondAQuietEdge_TheLocalLawAddsAlmostNothing()
    {
        double local = At(MixedLocal, -4.0, 5.0).I[0], global = At(Mixed, -4.0, 5.0).I[0];
        output.WriteLine($"Ig at (−4, 5): local {local:E3} A, global {global:E3} A");
        Assert.True(Math.Abs(local) < 1e-6, $"local {local:E3} A");
        Assert.True(Math.Abs(global) > 1e-3, $"global {global:E3} A");
    }

    [Fact]
    public void ASweepOfTenTimesTheDomain_IsFiniteEverywhere()
    {
        // The pentagon spans vgs −3 … 1 and vds 0 … 10; sweep ten extents either side of its centre.
        int points = 0;
        for (int a = 0; a < 100; a++)
            for (int b = 0; b < 100; b++)
            {
                double vgs = -1 + 40 * (a / 49.5 - 1), vds = 5 + 100 * (b / 49.5 - 1);
                var r = At(Mixed, vgs, vds);
                Assert.True(r.I.Concat(r.Q).Concat(Flat(r.Dg)).Concat(Flat(r.Dc)).All(double.IsFinite),
                    $"not finite at vgs={vgs:G4}, vds={vds:G4}");
                points++;
            }
        Assert.Equal(10_000, points);
    }

    [Fact]
    public void ADcSolveStartedFarOutside_ConvergesToTheInDomainAnswer()
    {
        // The ANN's domain is vds 30 … 45 V; the cold start (every node at 0 V) is 2.5 domain extents
        // below it, and no bias ramp is allowed to walk it in. Both solves are taken to a residual far
        // below the default, so what is compared is the operating point and not where Newton stopped.
        string ann  = ComposedTestModels.WriteTemp(RuntimeTestModels.AnnModel(TrainingDomain.Rectangle(-1, 1, 30, 45)));
        string twin = ComposedTestModels.WriteTemp(RuntimeTestModels.EquationTwin());
        const string bench = """
            Vdc:VG vg 0 V=-0.2
            R:RG vg g R=10
            Vdc:VD vd 0 V=40
            R:RD vd d R=20

            """;

        var settings = new AnalysisSettings { DcBiasStepping = DcBiasSteppingMode.Never, NonlinearAbsTol = 1e-12 };
        var a = ComposedFetEquivalenceTests.Dc(bench + $"ComposedFET:X1 g d 0 File=\"{ann}\"\n", settings);
        var b = ComposedFetEquivalenceTests.Dc(bench + $"ComposedFET:X1 g d 0 File=\"{twin}\"\n", settings);

        foreach (string node in (string[])["g", "d", "X1.gi", "X1.di", "X1.si"])
        {
            output.WriteLine($"{node}: {a[node]:R} vs {b[node]:R}");
            Assert.True(Math.Abs(a[node] - b[node]) <= 1e-9 * Math.Max(Math.Abs(b[node]), 1.0), $"{node}: {a[node]:R} vs {b[node]:R}");
        }
        // The answer is inside the domain.
        double vds = a["X1.di"] - a["X1.si"];
        Assert.InRange(vds, 30, 45);
    }

    private static double[] Flat(double[,] m) => [m[0, 0], m[0, 1], m[1, 0], m[1, 1]];
}
