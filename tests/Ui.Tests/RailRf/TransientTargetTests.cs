// Designer feedback round 11 — the transient target the model always had, now settable: the window's
// "derive Z from the load" row and `rail --target-transient`. One test per claim.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class TransientTargetTests
{
    /// <summary>
    /// The form opens on the source's voltage, 5 % and the load's current; Set stores ΔV = 5 % × 3.6 V
    /// over ΔI = 35 mA as a TRANSIENT target (Z ≈ 5.14 Ω, not the 103 Ω of V / I), shows the
    /// arithmetic, and the stored target — rise time left out — survives a save and a read.
    /// </summary>
    [Fact]
    public void TheFormDerivesZFromRippleOverTheLoadStep_AndStoresATransientTargetThatRoundTrips()
    {
        var doc = OneRail(volts: 3.6, amps: 0.035);
        var vm = Window(doc);

        Assert.Equal("3.6 V", vm.RippleVoltsEntry);
        Assert.Equal("5 %", vm.RipplePercentEntry);
        Assert.Equal("35 mA", vm.StepCurrentEntry);

        vm.SetTransientTargetCommand.Execute(null);

        var target = doc.Rails[0].ImpedanceTarget!;
        Assert.Equal(RailTargetKind.Transient, target.Kind);
        Assert.Equal(0.18 / 0.035, target.FlatTargetOhms!.Value, 9);
        Assert.Null(target.BandTopHz);
        Assert.Contains("= 180 mV / 35 mA", vm.TransientTargetReadout, StringComparison.Ordinal);
        Assert.Equal("", vm.TransientTargetProblem);

        var back = RailDocumentIo.DeserializeUnvalidated(RailDocumentIo.SerializeUnvalidated(doc));
        Assert.Equal(target, back.Rails[0].ImpedanceTarget);
    }

    /// <summary>
    /// The stored target is the number chosen: editing the source row afterwards does not move it. The
    /// document keeps ΔV and ΔI, not the voltage and percentage they came from.
    /// </summary>
    [Fact]
    public void AStoredTargetDoesNotFollowALaterSourceEdit()
    {
        var doc = OneRail(volts: 3.6, amps: 0.035);
        var vm = Window(doc);
        vm.RiseTimeEntry = "10 ns";
        vm.SetTransientTargetCommand.Execute(null);
        var stored = doc.Rails[0].ImpedanceTarget;
        Assert.Equal(35e6, stored!.BandTopHz!.Value, 3);

        doc.Rails[0].Sources[0] = doc.Rails[0].Sources[0] with { OpenCircuitVoltageV = 5.0 };

        Assert.Equal(stored, doc.Rails[0].ImpedanceTarget);
        Assert.Equal(0.18, doc.Rails[0].ImpedanceTarget!.Transient!.DeltaVVolts, 12);
    }

    private static RailDocument OneRail(double volts, double amps)
    {
        var doc = new RailDocument { Name = "pdn" };
        var rail = new RailSpec { Name = "VDD", NetName = "VDD" };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "U9", Pin = "1" }, OpenCircuitVoltageV = volts });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = amps });
        doc.Rails.Add(rail);
        return doc;
    }

    private static RailRfViewModel Window(RailDocument doc)
    {
        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.SolveFunc = (request, _) => new(null, [], [.. request.Document.Rails.Select(r => r.Name)], []);
        return vm;
    }
}
