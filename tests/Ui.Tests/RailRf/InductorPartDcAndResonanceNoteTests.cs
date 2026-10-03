using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using NumFlat;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

/// <summary>
/// Field report, 2026-10-02: an inductor part across a rail is a SHORT at DC and the DC answer has to
/// say so; and a resonance search that probed a crossing and set it aside must not report both "no
/// sign change" and "points added at the resonances located".
/// </summary>
public sealed class InductorPartDcAndResonanceNoteTests
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);

    private static Technology Board()
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = 35 * DbuPerMicron, SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = 35 * DbuPerMicron, SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true },
        ];
        return tech;
    }

    private static RailDcResult Solve(string partNumber)
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "IND", StatedInductanceHenries = 6e-9, EsrOhms = 0.02 });
        library.Rows.Add(new PartLibraryRow { PartNumber = "CAP", CapacitanceFarads = 100e-9, StatedInductanceHenries = 0, EsrOhms = 0.02 });

        var doc = new RailDocument { Name = "dc short" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3, SeriesResistanceOhms = 0.01 });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 1e-3 });
        rail.Parts.Add(new RailPart { Refdes = "X1", PartNumber = partNumber });
        doc.Rails.Add(rail);

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(),
            Shapes = [new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(2) },
                      new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(2) }],
            Pads =
            [
                new PlacedPin("BT1", "1", "VDD", Mm(1), Mm(1), PinSource.BoardNetlist),
                new PlacedPin("U1", "VDD", "VDD", Mm(29), Mm(1), PinSource.BoardNetlist),
                new PlacedPin("X1", "1", "VDD", Mm(15), Mm(1), PinSource.BoardNetlist),
                new PlacedPin("X1", "2", "GND", Mm(16), Mm(1), PinSource.BoardNetlist),
            ],
            PartLibrary = library,
        });
        Assert.Null(run.Refusal);
        return run.Rails[0];
    }

    /// <summary>The inductor part draws amps at DC and the result names it as a finding; the same board
    /// with a capacitor in its place carries no DC path and says nothing.</summary>
    [Fact]
    public void AnInductorPartIsADcShort_ACapacitorIsNot()
    {
        var shorted = Solve("IND");
        string finding = Assert.Single(shorted.Findings, f => f.Contains("X1 is an inductor part"));
        Assert.Contains("short to the return", finding);
        // Its DC impedance is said first, as its ESR — not as 0 Ω (field report, 2026-10-03).
        Assert.Contains("at DC its impedance is its ESR", finding);
        Assert.Contains("R out and L out belong on that source row", finding);
        // 3.3 V across 10 mΩ of source, 20 mΩ of ESR and a few mΩ of copper: just under 110 A.
        double port = Assert.Single(shorted.Ports).VoltageV;
        Assert.InRange(port, 1.5, 2.3);

        var decoupled = Solve("CAP");
        Assert.DoesNotContain(decoupled.Findings, f => f.Contains("inductor part"));
        Assert.InRange(Assert.Single(decoupled.Ports).VoltageV, 3.29, 3.3);
    }

    /// <summary>10 mΩ across 6 nH + 20 mΩ ∥ 100 nF + 20 mΩ: the reactance crosses zero near 6.5 MHz with
    /// a Q far below 1. The search probes it and sets it aside, and neither sentence may then claim
    /// "no sign change" or "added at the resonances located".</summary>
    [Fact]
    public void ACrossingSetAside_IsSaidAsOne_InBothSentences()
    {
        static Complex Z(double f)
        {
            double w = 2 * Math.PI * f;
            var zl = new Complex(0.02, w * 6e-9);
            var zc = new Complex(0.02, -1 / (w * 100e-9));
            return 1 / (1 / 0.01 + 1 / zl + 1 / zc);
        }
        var z0 = new Complex(50, 0);
        double[] grid = [.. Enumerable.Range(0, 201).Select(i => 1e4 * Math.Pow(2e8 / 1e4, i / 200.0))];
        Mat<Complex> SolveAt(double f)
        {
            var s = new Mat<Complex>(1, 1);
            s[0, 0] = (Z(f) - z0) / (Z(f) + z0);
            return s;
        }

        var sweep = PdnAdaptiveSweep.Run(grid, z0, PdnSamplingSettings.Default, SolveAt);

        Assert.Empty(sweep.Resonances);
        Assert.NotEmpty(sweep.AddedHz);
        string all = string.Join(" ", sweep.Notes);
        Assert.DoesNotContain("does not change sign", all);
        Assert.DoesNotContain("resonances the search located", all);
        Assert.Contains("probed and set aside", all);
        Assert.Contains("set it aside as no resonance", all);
    }
}
