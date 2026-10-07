using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>TO-1 R-to1-6: tuned values applied in memory elaborate exactly as the same values typed.</summary>
public sealed class TunableOverridesTests
{
    private static NetExtractor.ExtractionResult Extract(SchematicEditModel top, string r3 = "10")
        => NetExtractor.Extract(top, "tb", TuningFixture.Resolver(r3));

    [Fact]
    public void OverridesElaborateLikeTheHandEditedDesign()
    {
        var original = Extract(TuningFixture.Top());
        var tuned = TunableOverrides.Apply(original.TestBench, original.Library, new Dictionary<string, string>
        {
            ["R1.R"]     = "75 Ohm",
            ["Wline"]    = "400 um",
            ["DUT:R3.R"] = "22 Ohm",
            ["X2.Rbias"] = "3 kOhm",   // inherited: gains the override Push would add
        });
        Assert.Null(tuned.Refusal);
        Assert.Empty(tuned.Notes);

        var typed = Extract(TuningFixture.Top(r1: "75", wline: "400", x2Rbias: "3"), r3: "22");

        Assert.Equal(CnlWriter.Write(typed.TestBench, typed.Library), CnlWriter.Write(tuned.TestBench!, tuned.Library!));
        Assert.Equal(Dump(typed.TestBench, typed.Library), Dump(tuned.TestBench!, tuned.Library!));

        // The input is untouched: tuning changes nothing but the copy.
        Assert.Equal(CnlWriter.Write(Extract(TuningFixture.Top()).TestBench, Extract(TuningFixture.Top()).Library),
                     CnlWriter.Write(original.TestBench, original.Library));
    }

    [Fact]
    public void AnUnresolvedKeyIsANoteNotAThrow()
    {
        var x = Extract(TuningFixture.Top());
        var tuned = TunableOverrides.Apply(x.TestBench, x.Library,
            new Dictionary<string, string> { ["R9.R"] = "1 Ohm", ["NoSuchVar"] = "1" });

        Assert.Null(tuned.Refusal);
        Assert.Equal(2, tuned.Notes.Count);
    }

    [Fact]
    public void AVariableSetByBothSetAndATunedValueIsRefused()
    {
        var x = Extract(TuningFixture.Top());
        var tuned = TunableOverrides.Apply(x.TestBench, x.Library,
            new Dictionary<string, string> { ["Wline"] = "400 um" }, setVariables: ["Wline"]);

        Assert.Contains("Wline", tuned.Refusal);
        Assert.Null(tuned.TestBench);
    }

    /// <summary>Every elaborated component's resolved values and every resolved global, as text.</summary>
    private static string Dump(TestBench tb, Library lib)
    {
        using var nl = new Elaborator(lib).Elaborate(tb);
        var lines = nl.Components
            .Select(c => $"{c.InstancePath} {c.ComponentType} " +
                         string.Join(",", c.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")))
            .Concat(nl.ResolvedGlobals.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Value}"));
        return string.Join("\n", lines);
    }
}
