using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.Tests.Composed;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// One feature case of brief-modelcomposer-26's compiled gates: a model built in memory, the instance parameters both
/// placements carry, the run's ambient, and the DC grid and S-parameter biases it is compared at.
/// </summary>
public sealed record VaCase(string Name, ComposedModel Model, string Instance = "", double? TempC = null,
                            double[]? Vgs = null, double[]? Vds = null, (double Vgs, double Vds)[]? Biases = null)
{
    /// <summary>A grid reaching outside the pentagon on every side — left, right, below, above and beyond its oblique edge
    /// — so guided extrapolation is exercised.</summary>
    public double[] GridVgs => Vgs ?? [-4, -2, -0.8, 0.8, 1.4];
    public double[] GridVds => Vds ?? [-0.5, 0.5, 4, 9.5, 11.5];

    /// <summary>Three biases; the last lies beyond the pentagon's oblique edge, where the continuation's curvature term
    /// enters the small-signal Jacobian.</summary>
    public (double Vgs, double Vds)[] SBiases => Biases ?? [(-1.5, 2), (-0.6, 6), (0.6, 9.5)];

    public override string ToString() => Name;
}

/// <summary>The cases, each built from <see cref="ComposedTestModels"/> or <see cref="RuntimeTestModels"/>.</summary>
public static class VaTestModels
{
    public static readonly string[] Inputs = ["vgs", "vds"];

    public static IEnumerable<VaCase> All()
    {
        yield return new VaCase("Equation", ComposedTestModels.Model());
        yield return new VaCase("AnnTanh", AnnModel(AnnActivation.Tanh));
        yield return new VaCase("AnnSigmoid", AnnModel(AnnActivation.Sigmoid));
        yield return new VaCase("AsinhGate", AsinhGate());
        // The equation model is continuous through vds = 0 (Id ∝ tanh(α·vds), the gate branches each other's image), so
        // the sweep can cross it exactly; the image of a point with vds < 0 is what is evaluated.
        yield return new VaCase("Exchange", ComposedTestModels.Model() with
        {
            Core = ComposedTestModels.Model().Core with { Symmetry = DrainSourceSymmetry.Exchange },
        }, Vgs: [-4, -2, -0.8, 0.8], Vds: [-11, -2, -0.3, 0, 0.3, 2, 11], Biases: [(-1.5, 2), (-0.6, -4), (0.6, 9.5)]);
        yield return new VaCase("Drift", Drift());
        yield return new VaCase("Range50", Ranged(), TempC: 50);
        yield return new VaCase("Range150Clamped", Ranged(), TempC: 150);
        // N0 = 4 to N = 7: every rule the network carries (fixed, perFinger, perFingerInverse) at a factor that is not a
        // power of two.
        yield return new VaCase("N7", ComposedTestModels.Model(), Instance: "N=7");
        yield return new VaCase("M2", ComposedTestModels.Model(), Instance: "m=2");
    }

    public static IEnumerable<object[]> Cases() => All().Select(c => new object[] { c });

    /// <summary>Every constituent an ANN over the pentagon, all with one activation.</summary>
    public static ComposedModel AnnModel(AnnActivation activation)
    {
        var m = RuntimeTestModels.AnnModel(RuntimeTestModels.Pentagon);
        return m with
        {
            Core = m.Core with
            {
                Id = m.Core.Id with { Activation = activation },
                Ig = m.Core.Ig with { Activation = activation },
                Qg = m.Core.Qg with { Activation = activation },
                Qd = m.Core.Qd with { Activation = activation },
            },
        };
    }

    /// <summary>The gate current through the asinh transform: the gate networks compute <c>u</c> up to about 8, and the
    /// current is <c>1 nA·sinh(u)</c>.</summary>
    public static ComposedModel AsinhGate()
    {
        var m = AnnModel(AnnActivation.Tanh);
        var arrays = new SortedDictionary<string, DataCube>(m.Arrays.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        foreach (var (net, name) in new[] { (RuntimeTestModels.IgGsNet, "IgGs"), (RuntimeTestModels.IgGdNet, "IgGd") })
            foreach (var kv in (net with { YScale = 8, Activation = AnnActivation.Sigmoid }).Arrays(name)) arrays[kv.Key] = kv.Value;
        return m with
        {
            Core = m.Core with
            {
                Ig = m.Core.Ig with { Activation = AnnActivation.Sigmoid, Transform = new OutputTransform(OutputTransform.Asinh, 1e-9) },
            },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 1.0, ["IgGs"] = 1e-3, ["IgGd"] = 1e-3 }),
            Arrays = arrays,
        };
    }

    // One-input pieces, increasing, and a drift network over (vgd, Id) that depends on Id (TrapInputsTests' pieces).
    private static readonly TestNet Piece1 = new([0.0], [0.5], [new double[,] { { 1.0 }, { 0.6 } }, new double[,] { { 0.7, 0.4 } }],
                                                 [[0.2, -0.3], [0.0]], 0.0, 1e-13);
    private static readonly TestNet DriftNet = new([-3.0, 0.02], [0.4, 40.0],
                                                   [new double[,] { { 0.5, 1.0 }, { -0.3, 0.8 } }, new double[,] { { 0.9, 0.6 } }],
                                                   [[0.1, 0.2], [0.0]], 0.0, 2e-13);

    /// <summary>The equation currents with drift charges whose boxes are narrower than the sweep, so every piece is also
    /// continued beyond its box.</summary>
    public static ComposedModel Drift()
    {
        var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (string key in (string[])["QGS", "QGD", "QDS"]) foreach (var kv in Piece1.Arrays(key)) arrays[kv.Key] = kv.Value;
        foreach (var kv in DriftNet.Arrays("Qdrift")) arrays[kv.Key] = kv.Value;
        var domain = TrainingDomain.Rectangle(-3, 1, 0, 10) with
        {
            Box = [new("QGS", (-3, 1)), new("QGD", (-11, 1)), new("QDS", (0, 10)), new("Qdrift.vgd", (-11, 1)), new("Qdrift.Id", (0, 0.05))],
        };
        var m = ComposedTestModels.Model();
        return m with
        {
            Core = m.Core with { Qg = Constituent.Drift(true, domain), Qd = Constituent.Drift(false, domain, 0.3) },
            Arrays = arrays,
        };
    }

    /// <summary>A three-input drain current over vgs, vds and T.</summary>
    private static readonly TestNet IdT = new(
        [-0.5, 5.0, 50.0], [0.8, 0.25, 0.01],
        [new double[,] { { 1.6, 0.0, -0.2 }, { 0.0, 1.2, 0.0 }, { 1.6, 1.2, -0.1 } }, new double[,] { { 0.6, 0.4, 0.5 }, { 0.3, 0.8, 0.2 } },
         new double[,] { { 1.0, 0.7 } }],
        [[1.6, -1.0, 0.2], [0.1, -0.2], [0.0]],
        0.03, 0.02);

    /// <summary>A model over 0–100 °C: the drain current an ANN of (vgs, vds, T) over the pentagon with T's interval in
    /// its box, the rest equations. Run at 150 °C it is evaluated at 100 °C, and says so.</summary>
    public static ComposedModel Ranged()
    {
        var m = ComposedTestModels.Model(ModelTemperature.Range(0, 100));
        var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (var kv in IdT.Arrays("Id")) arrays[kv.Key] = kv.Value;
        var domain = RuntimeTestModels.Pentagon with { Box = [new("T", (0, 100))] };
        return m with
        {
            Core = m.Core with { Id = Constituent.Ann("Id", ["vgs", "vds", "T"], domain) },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 1.0 }),
            Arrays = arrays,
        };
    }

    /// <summary>The maximum-size network: four hidden layers of 64, seeded weights, as the drain current.</summary>
    public static ComposedModel MaximumAnn()
    {
        var rng = new Random(7);
        int[] widths = [2, 64, 64, 64, 64, 1];
        var w = new double[widths.Length - 1][,];
        var b = new double[widths.Length - 1][];
        for (int k = 0; k < w.Length; k++)
        {
            w[k] = new double[widths[k + 1], widths[k]];
            b[k] = new double[widths[k + 1]];
            double scale = 1.0 / Math.Sqrt(widths[k]);
            for (int r = 0; r < widths[k + 1]; r++)
            {
                b[k][r] = 0.1 * (rng.NextDouble() - 0.5);
                for (int c = 0; c < widths[k]; c++) w[k][r, c] = scale * (rng.NextDouble() - 0.5);
            }
        }
        var net = new TestNet([-0.5, 5.0], [0.8, 0.25], w, b, 0.05, 0.02);
        var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (var kv in net.Arrays("Id")) arrays[kv.Key] = kv.Value;
        var m = ComposedTestModels.Model();
        return m with
        {
            Core = m.Core with { Id = Constituent.Ann("Id", Inputs, RuntimeTestModels.Pentagon) },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 1.0 }),
            Arrays = arrays,
        };
    }
}
