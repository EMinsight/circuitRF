// brief-artsch-1 R-as1-5/R-as1-6: the line calculator answers CPWG and SLIN through their models, with the
// cross-section beside them. One test per claim: a CPWG (a gap on the top layer) and a SLIN (an inner layer
// between planes) analyse to exactly their model's numbers, and the width each synthesises analyses back
// to the target; and the trace review's own cross-section solve agrees with both models within 3 % on Z0 on
// the physics-reference geometries — the cross-check AS-5 relies on, recorded in this test's output.

using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Schematic;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class LineCalculatorPlanarTests(ITestOutputHelper output)
{
    private const double F = 10e9;

    private static double P(PlanarLineSubstrateInjection.Binding b, string n)
        => double.Parse(b.Overrides.Single(o => o.Name == n).Expression, CultureInfo.InvariantCulture);

    [Fact]
    public void ACpwg_AnalysesToItsModel_AndItsSynthesisAnalysesBack()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        double g = 0.2e-3;
        var r = LineCalculator.Calculate(tech, new LineCalcRequest("TopCu", [0.4e-3], [50], g, F));
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("CPWG", r.ModelComponent);

        var b = PlanarLineSubstrateInjection.Build(tech, SymbolKind.Cpwg, "Top", null);
        CoplanarLineModel At(double w) => new(w, g, 1e-3, P(b, "H"), P(b, "T"), P(b, "Er"), P(b, "Sigma"), P(b, "TanD"), "x");

        var byWidth = r.Rows[0];
        Assert.Equal(At(byWidth.Model!.WidthM).LineParameters(F), byWidth.Model.Line);
        Assert.Equal("CPWG", byWidth.Model.Component);
        Assert.NotNull(byWidth.CrossSection?.Z0);

        var synth = r.Rows[1].Model!;
        Assert.Equal(50.0, At(synth.WidthM).LineParameters(0).Z0, 6);
    }

    [Fact]
    public void ASlin_AnalysesToItsModel_AndItsSynthesisAnalysesBack()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var r = LineCalculator.Calculate(tech, new LineCalcRequest("Sig3Cu", [0.15e-3], [50], null, F));
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("SLIN", r.ModelComponent);
        Assert.NotNull(r.Stripline);

        var b = PlanarLineSubstrateInjection.Build(tech, SymbolKind.Slin, "Sig3", null);
        StriplineModel At(double w) => new(w, 1e-3, P(b, "H1"), P(b, "H2"), P(b, "T"), P(b, "Er"), P(b, "Sigma"), P(b, "TanD"), "x");

        var byWidth = r.Rows[0];
        Assert.Equal(At(byWidth.Model!.WidthM).LineParameters(F), byWidth.Model.Line);
        Assert.NotNull(byWidth.CrossSection?.Z0);

        Assert.Equal(50.0, At(r.Rows[1].Model!.WidthM).LineParameters(0).Z0, 6);
    }

    /// <summary>The physics-reference geometries (testdata/planar-lines), each drawn on a technology of its
    /// own, through the calculator: the model column against the trace review's cross-section column. A
    /// geometry outside the model's stated range — the 4:1 offset stripline — is recorded, not asserted:
    /// there the model is 3.4 % from the field solve by its own account, and the cross-section 8.2 %.</summary>
    [Fact]
    public void TheCrossSection_AgreesWithBothModels_Within3Percent_OnThePhysicsGeometries()
    {
        foreach (var line in File.ReadLines(Path.Combine(PlanarLinesDir(), "physics-references.txt")))
        {
            if (line.StartsWith('#')) continue;
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double[] n = [.. v.Skip(1).Take(7).Select(t => double.Parse(t, CultureInfo.InvariantCulture))];
            (double w, double a, double b, double t, double er) = (n[0], n[1], n[2], n[3], n[4]);
            if (t <= 0) continue;   // a drawn conductor has a thickness

            bool cpwg = v[0] == "CPWG";
            var tech = cpwg ? Board([("Top", t, false), ("Diel", b, er), ("Gnd", 35e-6, true)])
                            : Board([("Gnd1", 35e-6, true), ("D1", a, er), ("Sig", t, false), ("D2", b, er), ("Gnd2", 35e-6, true)]);
            var r = LineCalculator.Calculate(tech, new LineCalcRequest(cpwg ? "Top" : "Sig", [w], [], cpwg ? a : null, null));
            Assert.True(r.Ok, r.Refusal);
            var row = r.Rows.Single();
            double model = row.Model!.Line.Z0Static, section = row.CrossSection!.Z0!.Value;
            double diff = (section - model) / model;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{v[0]} W={w:G4} {(cpwg ? "G" : "H1")}={a:G4} {(cpwg ? "H" : "H2")}={b:G4} T={t:G4} er={er}: " +
                $"model {model:0.000} Ω, cross-section {section:0.000} Ω, field {n[5]:0.000} Ω, model−section {diff * 100:+0.00;-0.00} %"));
            bool inRange = cpwg || Math.Max(a, b) / Math.Min(a, b) <= 2.0 + 1e-9;
            if (inRange) Assert.True(Math.Abs(diff) < 0.03, $"{line}: model {model} vs cross-section {section}");
            else output.WriteLine("  (outside SLIN's stated offset range: recorded, not asserted)");
        }
    }

    /// <summary>A stackup top to bottom: a conductor is (name, thickness, ground?), a dielectric (name,
    /// thickness, εr). Every conductor draws on a layer of its own name.</summary>
    private static Technology Board(IReadOnlyList<object> rows)
    {
        var tech = new Technology { Name = "Row", DefaultDisplayUnit = LayoutUnit.Um };
        int key = 1;
        foreach (var r in rows)
        {
            if (r is ValueTuple<string, double, bool> c)
            {
                var lk = new LayerKey(key++, 0);
                tech.Layers.Add(new LayerDef { Key = lk, Name = c.Item1, Color = new CircuitRF.Design.Theming.Rgba(200, 100, 50), ZOrder = key });
                tech.Stackup.Layers.Add(new StackupLayer
                {
                    Kind = StackupKind.Conductor, Name = c.Item1, ThicknessDbu = (long)Math.Round(c.Item2 * 1e9),
                    SigmaSm = 5.8e7, IsGroundReference = c.Item3, DrawingLayers = [lk],
                });
            }
            else if (r is ValueTuple<string, double, double> d)
            {
                tech.Stackup.Layers.Add(new StackupLayer
                    { Kind = StackupKind.Dielectric, Name = d.Item1, ThicknessDbu = (long)Math.Round(d.Item2 * 1e9), Epsr = d.Item3 });
            }
        }
        return tech;
    }

    private static string PlanarLinesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, "testdata", "planar-lines");
            if (Directory.Exists(cand)) return cand;
        }
        throw new DirectoryNotFoundException("testdata/planar-lines");
    }
}
