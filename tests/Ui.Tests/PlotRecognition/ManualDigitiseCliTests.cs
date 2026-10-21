// brief-pdg-4-digitise-by-hand.md §4, §5 — `circuitrf recognize` reading a plot by hand, run as a PROCESS: one run end to
// end (a box, two typed ranges and one Find curve click) writes the CSV the in-process reading writes, byte for byte —
// the dialog and the CLI build the same ManualPlot and ManualCurve records, and there is no second path.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class ManualDigitiseCliTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    [Fact]
    public void ByHand_FromTheCommandLine_WritesTheCsvTheInProcessReadingWrites()
    {
        string png = pictures.Picture("s21lowpass");
        var source = ImageSource.FromFile(png).Source!;
        // The test's own knowledge of where the plot is and what its axes say; the run under test reads neither.
        var f = PlotReading.FindPlots(source).Single();
        var r = PlotPictureSet.Read(null, png).Plots[0].Rect!;
        static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        double x0 = Math.Round(f.Left, 2), y0 = Math.Round(f.Top, 2), x1 = Math.Round(f.Right, 2), y1 = Math.Round(f.Bottom, 2);
        double xLo = Math.Round(r.X!.ValueAt(f.Left) / 1e8) / 10, xHi = Math.Round(r.X.ValueAt(f.Right) / 1e8) / 10;
        double yLo = Math.Round(r.Y!.ValueAt(f.Bottom), 1), yHi = Math.Round(r.Y.ValueAt(f.Top), 1);
        double sx = Math.Round(f.Left + 0.4 * (f.Right - f.Left)), sy = Math.Round(r.Y.PixelAt(20 * Math.Log10(
            PlotPictureSet.LowPassS21(r.X.ValueAt(sx)).Magnitude)));

        string csv = Path.Combine(pictures.Dir, "cli", "by-hand.csv");
        var result = CliProcess.Run(PalaceBackendTests.RepoRoot(), [],
            ["recognize", png, "--manual-box", $"{N(x0)}px,{N(y0)}px,{N(x1)}px,{N(y1)}px", "--x-range", $"{N(xLo)}GHz..{N(xHi)}GHz",
             "--y-range", $"{N(yLo)}..{N(yHi)}", "--follow", $"S21={N(sx)},{N(sy)}", "--csv", csv]);
        output.WriteLine($"exit {result.ExitCode}\n{result.StdOut}\n{result.StdErr}");
        Assert.True(result.ExitCode == 0, result.StdErr);

        var plot = new ManualPlot(0, ManualQuad.Rectangle(x0, y0, x1, y1), new ManualAxis(xLo * 1e9, xHi * 1e9, AxisScale.Linear),
                                  new ManualAxis(yLo, yHi, AxisScale.Linear), null, 1e9, true);
        var followed = ManualCurves.Follow(source.Raster, plot, (sx, sy));
        Assert.True(followed.Ok, followed.Refusal);
        var session = PlotReading.Read([new PlotPictureInput(source, Path.GetFileName(png))], new PlotReadingOptions
        {
            Manual = [plot], ManualCurves = [new ManualCurve("S21", 1, followed.Fixpoints, PlotSide.Left, PlotAssignment.None, followed.Rgb)],
        });
        Assert.Equal(PlotCsv.Write(session, TraceFit.Session(session, [])), File.ReadAllText(csv));
    }
}
