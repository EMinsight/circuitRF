// ================================================================
//  SpreadingCopperEscalationTests.cs — Fast's spreading-copper refusal meshes the rail instead
//
//  Field report, 2026-10-01: a rail Fast refused because it reaches its load only through
//  spreading copper had one number-giving remedy, "run Accuracy", and the designer's point was that
//  asking bought nothing. The rule is unchanged — Fast never prices that copper with the trace
//  formula — and the run now meshes THAT rail and says so. The other remedy, marking the copper a
//  trace, stays the user's; a run told not to escalate refuses exactly as before.
// ================================================================

using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class SpreadingCopperEscalationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    private static Technology Board()
    {
        var tech = new Technology { Name = "two-layer" };
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(0.4), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = 5.8e7,
                DrawingLayers = [Bot], IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    /// <summary>A 0.3 mm trace from the source to a choke, L1, feeding a 3 × 3 mm square with the load
    /// on its far side — the current has to cross copper of no trace shape
    /// (<c>PdnRefusalCauseTests</c>' own refused board, run through the rail set).</summary>
    private static RailDcRunResult Run(bool escalate)
    {
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3 });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.03 });
        rail.Parts.Add(new RailPart
        {
            Refdes = "L1", Connection = RailPartConnection.Series, DcResistanceOhms = 0.005,
            TerminalA = new RailPortAnchor { Refdes = "L1", Pin = "1" },
            TerminalB = new RailPortAnchor { Refdes = "L1", Pin = "2" },
        });
        var doc = new RailDocument { Name = "square" };
        doc.Rails.Add(rail);

        PlacedPin Pad(string refdes, string pin, double xMm) =>
            new(refdes, pin, "VDD", Mm(xMm), Mm(0.15), PinSource.BoardNetlist) { Layer = Top };

        return RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(),
            DbuPerMicron = DbuPerMicron,
            Shapes = [Rect(Top, 0, 0, 20, 0.3), Rect(Top, 21, -1.35, 24, 1.65), Rect(Bot, -1, -2, 25, 2)],
            Pads = [Pad("BT1", "1", 0.1), Pad("L1", "1", 19.9), Pad("L1", "2", 21.1), Pad("U1", "VDD", 23.9)],
            EscalateSpreadingCopper = escalate,
        });
    }

    [Fact]
    public void AFastRunMeshesTheRailItRefusedForSpreadingCopper_AndSaysSo()
    {
        var refused = Run(escalate: false);
        Assert.Contains("only through spreading copper", refused.RefusalFor("VDD"), StringComparison.Ordinal);

        var run = Run(escalate: true);
        var rail = Assert.Single(run.Rails);
        Assert.Null(run.RefusalFor("VDD"));
        Assert.Equal(PdnModelKind.Accurate, rail.SolvedBy);
        Assert.StartsWith("Fast could not price the spreading copper on", rail.EscalatedFromFast, StringComparison.Ordinal);
        Assert.EndsWith("so rail 'VDD' was solved with Accuracy, which meshes it.", rail.EscalatedFromFast, StringComparison.Ordinal);
        Assert.Equal(rail.EscalatedFromFast, rail.Notes[0]);
        Assert.Contains(rail.Ports, p => p.DropV is > 0);
        output.WriteLine(rail.EscalatedFromFast);
    }
}
