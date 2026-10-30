// brief-img-12-acceptance-docs-example.md R-im12-3 — pictures with nothing in them (D16): a photograph-like gradient,
// a blank page, a page of text and a Smith chart — lines, but no circuit: its arcs are wire only in short slanting
// pieces, and a schematic wires across and down. Each is refused with the sentence that says what was seen, through
// the entry points the CLI and the dialog share; and the dialog, reading it with the real runner, shows the picture
// dimmed with that sentence, Create disabled, and the link that reads it as the other kind.
//
// brief-imf-9 R-imf9-3: the schematic reader itself refuses a picture whose kind is none — IMF-1's shaded illustration
// among them — in one sentence, "This picture does not look like a schematic: …", and the kind check alone decides it:
// no candidate reading is run.

using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class ImageNothingHereTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im12-none-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    internal static RasterImage Picture(string name) => name switch
    {
        "gradient" => Gradient(),
        "blank" => Pictures.Draw(480, 360, _ => { }),
        "text" => Pictures.Draw(560, 400, c =>
        {
            string[] lines =
            [
                "The amplifier is biased from a single supply and",
                "draws its drain current through the choke, which",
                "is decoupled at both ends. The output match is a",
                "two-section network laid out on the top layer, and",
                "the board is assembled with standard parts only.",
            ];
            for (int i = 0; i < lines.Length; i++) SchematicPictures.Text(c, lines[i], 24, 60 + 60 * i, 18);
        }),
        "illustration" => ImageSource.FromFile(Path.Combine(FixturePaths.Require(StylePictures.Folder), "illustration", "picture.png")).Source!.Raster,
        "smith" => Pictures.Draw(480, 480, c =>
        {
            using var pen = SchematicPictures.Ink();
            const float cx = 240, cy = 240, r = 200;
            c.DrawCircle(cx, cy, r, pen);
            c.DrawLine(cx - r, cy, cx + r, cy, pen);
            c.Save();
            using (var clip = new SKPath()) { clip.AddCircle(cx, cy, r); c.ClipPath(clip, antialias: true); }
            foreach (float rr in new[] { 0.2f, 0.5f, 1f, 2f, 5f })          // constant resistance
                c.DrawCircle(cx + r * rr / (1 + rr), cy, r / (1 + rr), pen);
            foreach (float x in new[] { 0.2f, 0.5f, 1f, 2f, 5f })           // constant reactance, both halves
            {
                c.DrawCircle(cx + r, cy - r / x, r / x, pen);
                c.DrawCircle(cx + r, cy + r / x, r / x, pen);
            }
            c.Restore();
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static RasterImage Gradient()
    {
        int w = 480, h = 360;
        var rgba = new byte[w * h * 4];
        var rng = new Random(5);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = (double)x / (w - 1), s = (double)y / (h - 1);
                int i = (y * w + x) * 4;
                rgba[i] = Noisy(70 + 130 * t, rng);
                rgba[i + 1] = Noisy(100 + 70 * s, rng);
                rgba[i + 2] = Noisy(170 - 90 * t, rng);
                rgba[i + 3] = 255;
            }
        return new RasterImage(w, h, rgba);
    }

    private static byte Noisy(double v, Random rng)
    {
        double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        return (byte)Math.Clamp(Math.Round(v + 12 * g), 0, 255);
    }

    /// <summary>The sentence each is refused with: what IM-2 saw, or — for line work that holds no circuit — the
    /// schematic reader's.</summary>
    public static TheoryData<string, string> Cases() => new()
    {
        { "gradient", "a photograph-like picture with no straight line work" },
        { "blank", "a blank picture" },
        { "text", "a page of text" },
        { "illustration", "a shaded illustration, with no circuit or board drawn in it" },
        { "smith", SchematicImageReport.CurvedLinesRefusal },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void APictureWithNoCircuit_IsRefusedWithWhatWasSeen(string picture, string sentence)
    {
        var r = SchematicImageRecognition.Circuit(new SchematicImageInput
        {
            Source = ImageSource.FromBytes(Pictures.Png(Picture(picture)), picture + ".png").Source!,
        });
        bool none = picture != "smith";
        Assert.Equal(none ? SchematicImageRecognition.NotASchematic(sentence) : sentence, r.Refusal);
        // The kind check alone: no candidate reading run for a picture of no kind.
        Assert.Equal(none ? 0 : 1, r.Cost.Readings);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheDialog_ShowsItDimmed_WithItsSentence_AndTheWayToReadItAnyway(string picture, string sentence)
    {
        string tech = Path.Combine(_ws, "board.ctech");
        TechPersistence.SaveToFile(tech, TracePictures.Tech());
        var vm = new ImageSourceViewModel(_ws, [new ImageTechnologyChoice("board", tech)], 0,
                                          ImageSource.FromBytes(Pictures.Png(Picture(picture)), picture + ".png").Source!,
                                          makeSchematic: true, ImageRecognitionRunner.Instance, debounce: TimeSpan.Zero,
                                          presetDirectory: Path.Combine(_ws, "presets"))
                 { SchematicOffered = true };   // the reader is withheld from the application, not from its tests
        await vm.Recognition;

        Assert.True(vm.HasFailure);
        Assert.EndsWith(sentence, vm.FailureText);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Equal(vm.FailureText, vm.CreateTip);
        // Read as nothing, either kind is offered; read as a schematic and found no circuit, a layout is.
        bool none = picture != "smith";
        Assert.Equal((none, true), (vm.FailureOffersSchematic, vm.FailureOffersLayout));
    }
}
