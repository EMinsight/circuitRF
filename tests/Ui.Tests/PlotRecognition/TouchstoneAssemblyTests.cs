// brief-img-14-sparameter-plots-to-touchstone.md §4 — assembling the file (R-im14-9): the refusal of a trace no evidence
// names.

using System.IO;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class TouchstoneAssemblyTests(ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private TouchstoneAssemblyResult Build(params string[] pngs)
    {
        var s = PlotPictureSet.Read(null, pngs);
        foreach (var l in s.Lines()) output.WriteLine(l);
        var built = TouchstoneAssembly.Build(s);
        foreach (var l in built.Header) output.WriteLine("! " + l);
        return built;
    }



    [Fact]
    public void AnUnassignedTrace_Refuses_NamingIt()
    {
        var built = Build(FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-two-black.png")));
        Assert.False(built.Ok);
        Assert.Equal("unassigned", built.Refusal!.Kind);
        Assert.StartsWith("T1 (plot 1, #", built.Refusal.Sentence);
        Assert.Equal("--trace \"T1=S21:db\"", built.Refusal.Flag);
    }
}
