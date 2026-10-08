using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>brief-tuneopt-5 R-to5-4: "Copy as .cnl" is a <c>preset</c> line that reads back to the same
/// preset, and writes the same bytes again.</summary>
public sealed class PresetCnlTextTests
{
    [Fact]
    public void CopyAsCnl_ParsesBackToTheSamePreset()
    {
        var p = new TuningPreset
        {
            Name = "wide band", Created = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc), Cost = 0.0123,
        };
        p.Values["R1.R"]      = "47 Ohm";
        p.Values["DUT:Wline"] = "212 um";
        p.Values["ZL"]        = "30+52j Ohm";
        p.Values["X1.m"]      = "2";

        string text = TuningPresets.CnlText(p);
        var back = TuningDirectiveText.ReadPresetLine(text);

        Assert.Equal((p.Name, p.Created, p.Cost, p.IsLastTuned), (back.Name, back.Created, back.Cost, back.IsLastTuned));
        Assert.Equal(p.Values, back.Values);
        Assert.Equal(text, TuningPresets.CnlText(back));
    }
}
