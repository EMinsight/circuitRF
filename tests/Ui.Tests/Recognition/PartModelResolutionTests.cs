// brief-artsch-4-parts-and-parts-table.md §4 — a part number resolves to a workspace Touchstone file
// whose name begins with it; a four-port file is refused with a note; no match is the ideal part.

using System;
using System.IO;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PartModelResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as4m-" + Guid.NewGuid().ToString("N")[..12]);

    public PartModelResolutionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllText(Path.Combine(_root, "models", "TESTPN123_series.s2p"),
            "# GHz S MA R 50\n1 0.1 0 0.9 0 0.9 0 0.1 0\n");
        File.WriteAllText(Path.Combine(_root, "models", "TESTPN4_quad.s4p"), "# GHz S MA R 50\n");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void APartNumberResolvesToTheTouchstoneFileNamedForIt()
    {
        var choice = new PartModelResolver(_root).Resolve("testpn123");

        Assert.Equal((PartModelKind.SnP, PartEvidenceSource.File), (choice.Model, choice.Source));
        Assert.Equal("TESTPN123_series.s2p", Path.GetFileName(choice.FilePath));
    }

    [Fact]
    public void AFourPortFileIsRefusedWithANote()
    {
        var choice = new PartModelResolver(_root).Resolve("TESTPN4");

        Assert.Equal(PartModelKind.Ideal, choice.Model);
        Assert.Contains(choice.Notes, n => n.Contains("4 ports", StringComparison.Ordinal));
    }

    [Fact]
    public void NoMatchIsTheIdealPart()
    {
        var choice = new PartModelResolver(_root).Resolve("NOSUCHPART");

        Assert.Equal((PartModelKind.Ideal, null), (choice.Model, choice.FilePath));
        Assert.Empty(choice.Notes);
    }
}
