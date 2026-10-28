// brief-img-11-cli-and-mcp.md §4 — `circuitrf recognize` reads a picture through the Create Schematic from Image
// functions and nothing else: run as a PROCESS its .cnl is the in-process reading's, byte for byte, for a schematic
// picture and a layout picture; a picture of no drawing is refused naming --image-kind; the overlay is written even
// when the reading is refused; and src/Cli names nothing of the image pipeline but its entry points, their options and
// results, ImageOverlay and PartsTableCsv (R-im11-6).
//
// Every call is a process (~1 s each): the claims are about the command line a caller actually runs.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using CircuitRF.Ui.Tests.Em3d;
using SkiaSharp;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class ImageRecognizeCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-im11-recognize-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>The picture written into <c>a/</c> and <c>b/</c>, siblings, so a relative reference is the same text.</summary>
    private (string A, string B) TwoCopies(RasterImage picture, string name)
    {
        string a = Path.Combine(_root, "a", name), b = Path.Combine(_root, "b", name);
        foreach (string p in new[] { a, b })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Pictures.Png(picture));
        }
        return (a, b);
    }

    /// <summary>A 1.7 mm copper line across a 12 × 6 mm picture at 20 µm/px — a line that runs off both edges, so
    /// both ends are edge launches.</summary>
    private static RasterImage Line() => Pictures.Draw(600, 300, c =>
    {
        using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
        c.DrawRect(SKRect.Create(0, 108, 600, 84), copper);
    });

    [Fact]
    public void ASchematicPicture_WritesTheInProcessReadingsNetlist()
    {
        var (a, b) = TwoCopies(SchematicImageRecognitionTests.Ladder(), "ladder.png");
        string cnl = Path.Combine(_root, "a", "out", "ladder.cnl");
        var (exit, stdout, stderr) = Cli("recognize", a, "-o", cnl, "--json");
        Assert.True(exit == 0, stderr);

        var reading = SchematicImageRecognition.Circuit(new SchematicImageInput { Source = ImageSource.FromFile(b).Source! });
        Assert.True(reading.Ok, reading.Refusal);
        Assert.Equal(reading.Circuit!.CnlText(Path.Combine(_root, "b", "out")), File.ReadAllText(cnl));

        // R-im11-4: what was read travels with the result — the kind, and a schematic picture's symbols and words.
        var doc = JsonNode.Parse(stdout)!["result"]!["recognize"]!;
        Assert.Equal("schematic", doc["image"]!["kind"]!.GetValue<string>());
        Assert.Equal(reading.Symbols!.Symbols.Count, doc["symbols"]!.AsArray().Count);
        Assert.Contains(doc["words"]!.AsArray(), w => w!["text"]?.GetValue<string>() == "L1");
    }

    [Fact]
    public void ALayoutPicture_WritesTheInProcessReadingsNetlist()
    {
        var (a, b) = TwoCopies(Line(), "line.png");
        string tech = Path.Combine(_root, "tech.ctech");
        TechPersistence.SaveToFile(tech, TracePictures.Tech());
        string cnl = Path.Combine(_root, "a", "out", "line.cnl");
        var (exit, _, stderr) = Cli("recognize", a, "--scale", "20um/px", "--tech", tech, "-o", cnl);
        Assert.True(exit == 0, stderr);

        var reading = ImageRecognition.Circuit(new ImageRecognitionInput
        {
            Trace = new ImageTraceInput
            {
                Source = ImageSource.FromFile(b).Source!, Technology = TechPersistence.LoadFromFile(tech), TechnologyPath = tech,
                Scale = ImageScaleStatement.Stated(20e-6),
            },
        });
        Assert.True(reading.Ok, reading.Refusal);
        Assert.Equal(reading.Circuit!.CnlText(Path.Combine(_root, "b", "out")), File.ReadAllText(cnl));
    }

    [Fact]
    public void ABlankPicture_IsRefusedNamingImageKind()
    {
        string blank = Path.Combine(_root, "blank.png");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(blank, Pictures.Png(Pictures.Draw(200, 200, _ => { })));
        var (exit, _, stderr) = Cli("recognize", blank);
        Assert.Equal(1, exit);
        Assert.Contains("No drawing found", stderr);
        Assert.Contains("--image-kind", stderr);
    }

    [Fact]
    public void TheOverlay_IsWrittenWhenTheReadingIsRefused()
    {
        var (a, _) = TwoCopies(Line(), "line.png");
        string overlay = Path.Combine(_root, "a", "line.overlay.png");
        var (exit, _, stderr) = Cli("recognize", a, "--image-kind", "layout", "--overlay", overlay);   // no scale: refused
        Assert.Equal(1, exit);
        Assert.Contains("states no scale", stderr);
        var drawn = RasterImage.Load(overlay).Image!;
        Assert.Equal((600, 300), (drawn.Width, drawn.Height));
        Assert.NotEqual(Pictures.Png(Line()), File.ReadAllBytes(overlay));   // something was drawn over the picture
    }

    /// <summary>R-im11-6: src/Cli names no type of the image pipeline but the entry points, their options and results,
    /// ImageOverlay and the IM-2 classification the document-kind walk already used.</summary>
    [Fact]
    public void TheVerbs_OwnNoImageProcessing()
    {
        string repo = RepoRoot();
        string[] allowed =
        [
            // IM-2: decode and classify — the document-kind walk, check, find, explain and read already call these.
            "ImageSource", "ImageKind", "ImageKindResult", "DrawingKind", "RasterImage", "ImageProvenance",
            // R-im11-3: the overlay.
            "ImageOverlay",
            // IM-3's entry point, its input and options, its target and its result.
            "ImageTrace", "ImageTraceInput", "ImageTraceOptions", "ImageTraceTarget", "ImageTraceResult", "ImageTraceRun",
            "ImageScaleStatement", "ImageScaleCandidate", "ImageScale", "PixelPoint", "PixelRect",
            "ImageLayerMap", "ImageLayerRow", "ImageLayerRole", "ImageTracePresets",
            // IM-13's: a board photo put square on four corners.
            "BoardPhoto", "BoardPhotoCorners", "BoardPhotoReading",
            // IM-4's.
            "ImageRecognition", "ImageRecognitionInput", "ImageRecognitionTarget", "ImageRecognitionRunOptions",
            "ImageRecognitionCircuit", "ImageRecognitionRun",
            // IM-10's.
            "SchematicImageRecognition", "SchematicImageInput", "SchematicImageOptions", "SchematicImageResult",
            "SchematicImageTarget", "SchematicImageRunOptions", "SchematicImageRun", "SchematicArrange", "CrossingRule",
            "ConnectionOverride", "PixelBox",
            // IMF-2's: the scales a user states (--pen, --text-height, --symbol-scale).
            "PictureProfileOptions",
            // IMF-9's candidate readings, by name: what --profile takes.
            "SchematicImageReadings",
            // brief-dfr29-1's corrections by hand (--via, --hole, --not-via, --copper, --clear, --outline): records the trace
            // takes, built from flags, as the dialog builds them.
            "ImageCorrection", "ImageCorrectionKind",
        ];
        // Names these namespaces share with types elsewhere that src/Cli rightly uses (a component, a fit, a 3D view's
        // Perspective projection), and with an option's member (SchematicImageOptions.LineArt, set in an initializer).
        string[] elsewhere = ["Component", "Components", "Fit", "LineArt", "Perspective"];
        var declared = new[] { "Imaging", Path.Combine("Layout", "Recognition", "Image"), Path.Combine("Schematic", "Recognition") }
            .SelectMany(d => Directory.GetFiles(Path.Combine(repo, "src", "Design", d), "*.cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f),
                @"^(?:public|internal)\s+(?:(?:static|sealed|readonly|abstract|partial)\s+)*(?:record\s+struct|record|class|enum|struct|interface)\s+(\w+)",
                RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.Contains("ColourClusters", declared);   // not vacuous
        Assert.Contains("WireGraph", declared);
        var forbidden = declared.Except(allowed).Except(elsewhere).ToList();

        var hits = Directory.GetFiles(Path.Combine(repo, "src", "Cli"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f =>
            {
                string code = Regex.Replace(Regex.Replace(Regex.Replace(File.ReadAllText(f), @"/\*.*?\*/", "", RegexOptions.Singleline),
                                                          @"//[^\n]*", ""), "\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"");
                // A member access (options.LineArt) is not a type named; a qualified type (Imaging.Foo) is.
                return forbidden.Where(t => Regex.IsMatch(code, $@"(?<![\w.]){t}\b|(?:Imaging|Recognition|Image)\.{t}\b"))
                                .Select(t => $"{Path.GetFileName(f)}: {t}");
            }).ToList();
        Assert.Empty(hits);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var result = CliProcess.Run(RepoRoot(), [], args);
        output.WriteLine($"$ {string.Join(' ', args)} -> {result.ExitCode}\n{result.StdErr}");
        return result;
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
