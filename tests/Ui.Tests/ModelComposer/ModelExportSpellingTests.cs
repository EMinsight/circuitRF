using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Brief MC-30: a Verilog-A export asked for by a flag is refused naming the spelling that works, and <c>model explain</c>
/// on an Active document lists both export spellings (R-mc30-7); an ANN Id that under-fits a large training set says the
/// next size up on the status line (R-mc30-4, D3).
/// </summary>
public sealed class ModelExportSpellingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-mc30-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Theory]
    [InlineData("--veriloga")]
    [InlineData("--format", "va")]
    public void AVerilogAFlag_IsRefused_NamingTheExtension(params string[] flag)
    {
        var run = ModelCliFixture.Run(_root, ["model", "export", "fet.cmc", .. flag]);
        output.WriteLine(run.StdErr);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("the extension of -o picks the format: -o fet.va", run.StdErr);
    }

    [Fact]
    public void Explain_OnAnActiveDocument_ListsBothExportSpellings()
    {
        Assert.Equal(0, ModelCliFixture.Run(_root, ["model", "new", "fet.cmc", "--kind", "active", "--network", "gan-two-shell"]).ExitCode);
        var run = ModelCliFixture.Run(_root, ["model", "explain", "fet.cmc"]);
        output.WriteLine(run.StdOut);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("circuitrf model export fet.cmc -o fet.cmodel", run.StdOut);
        Assert.Contains("circuitrf model export fet.cmc -o fet.va", run.StdOut);
    }

    [Fact]
    public void TheUnderFitSentence_NeedsBothALargeErrorAndALargeSet()
    {
        static FitErrorRecord E(int train, double held) => new(train, 0, 0, train / 9, 0, held);
        Assert.Equal("2×8 under-fits 24,000 points: try 2×16, or a Table.",
                     CurrentFit.UnderFit(Constituent.AnnMethod, [8, 8], E(24000, 0.03)));
        Assert.Null(CurrentFit.UnderFit(Constituent.AnnMethod, [8, 8], E(24000, 0.015)));
        Assert.Null(CurrentFit.UnderFit(Constituent.AnnMethod, [8, 8], E(1500, 0.03)));
        Assert.Null(CurrentFit.UnderFit(Constituent.TableMethod, [8, 8], E(24000, 0.03)));
    }
}
