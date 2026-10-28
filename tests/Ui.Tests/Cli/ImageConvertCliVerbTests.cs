// brief-img-11-cli-and-mcp.md §4 — `circuitrf convert` reads a picture through ImageTrace and nothing else: run as a
// PROCESS it writes the bytes the in-process trace writes, a .clay and a .gds alike; it never guesses a scale (D6);
// and a picture coordinate is pixels and says so.
//
// Every call is a process (~1 s each): the claims are about the command line a caller actually runs.

using System.Diagnostics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Tests.Imaging;
using CircuitRF.Ui.Tests.Em3d;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class ImageConvertCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-im11-convert-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>The same picture in two sibling folders, and one technology file beside both — so every relative
    /// reference the two writes make is the same text.</summary>
    private (string A, string B, string Tech) TwoCopies(RasterImage picture)
    {
        string a = Path.Combine(_root, "a", "board.png"), b = Path.Combine(_root, "b", "board.png"), tech = Path.Combine(_root, "tech.ctech");
        foreach (string p in new[] { a, b })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Pictures.Png(picture));
        }
        TechPersistence.SaveToFile(tech, TracePictures.Tech());
        return (a, b, tech);
    }

    private static ImageTraceRun InProcess(string picture, string tech, string parent) => ImageTrace.Run(new ImageTraceInput
    {
        Source = ImageSource.FromFile(picture).Source!, Technology = TechPersistence.LoadFromFile(tech), TechnologyPath = tech,
        Scale = ImageScaleStatement.Stated(20e-6),
    }, ImageTraceTarget.NewCell(parent, "board"));

    private static string NoTime(string text) =>
        Regex.Replace(text, "\"CreatedUtc\"\\s*:\\s*\"[^\"]*\"", "\"CreatedUtc\": \"\"", RegexOptions.IgnoreCase);

    [Fact]
    public void APictureToClay_WritesTheInProcessTracesBytes()
    {
        var (a, b, tech) = TwoCopies(TracePictures.TwoLayerBoard());
        var (exit, _, stderr) = Cli("convert", a, "-o", Path.Combine(_root, "a", "out"), "--to", "clay", "--scale", "20um/px", "--tech", tech);
        Assert.True(exit == 0, stderr);
        Assert.Contains("--layer-map \"#", stderr);   // the automatic map, printed so it can be pinned

        var run = InProcess(b, tech, Path.Combine(_root, "b", "out"));
        Assert.True(run.Ok, run.Refusal);
        string cli = File.ReadAllText(Path.Combine(_root, "a", "out", "board", "layout", "board.clay"));
        Assert.Equal(NoTime(File.ReadAllText(run.LayoutPath!)), NoTime(cli));
        Assert.NotEqual(NoTime(cli), cli);   // the exclusion is the provenance time, and it was there to exclude
    }

    /// <summary>brief-dfr29-1 gate 6 — the corrections the dialog makes are the records the CLI's flags spell: the same
    /// picture with the same corrections writes the same .clay, and the same .ctech copy beside it, both ways.</summary>
    [Fact]
    public async Task TheDialogsCorrections_AndTheCliFlags_WriteTheSameBytes()
    {
        var (a, b, tech) = TwoCopies(Pictures.Draw(400, 300, c =>
        {
            using var copper = Pictures.Fill(new SkiaSharp.SKColor(0xc8, 0x75, 0x33));
            using var hole = Pictures.Fill(SkiaSharp.SKColors.White);
            c.DrawRect(SkiaSharp.SKRect.Create(20, 20, 360, 260), copper);
            foreach (int x in new[] { 100, 200, 300 }) c.DrawCircle(x, 150, 15, hole);
        }));

        // The dialog, on copy b: the scale by two points, a hole made non-plated, copper and an outline drawn.
        var vm = new CircuitRF.Ui.Recognition.ImageSourceViewModel(Path.Combine(_root, "b"),
            [new CircuitRF.Ui.Recognition.ImageTechnologyChoice("tech", tech)], 0, ImageSource.FromFile(b).Source!, makeSchematic: false,
            new CircuitRF.Ui.Tests.Recognition.RecordingImageRunner(DrawingKind.Layout), debounce: TimeSpan.Zero,
            presetDirectory: Path.Combine(_root, "presets"));
        await vm.Recognition;
        vm.Scale.StartTwoPointsCommand.Execute(null);
        vm.Scale.Click(new PixelPoint(20, 20));
        vm.Scale.Click(new PixelPoint(380, 20));
        vm.Scale.DistanceText = "3.6 mm";
        vm.Scale.ApplyDistanceCommand.Execute(null);
        await vm.Recognition;
        vm.EveryOneLikeIt = false;
        vm.SelectedItem = vm.Overlay.HitTest(200, 150, 2, vm.Shows);
        vm.HoleIsNotPlatedCommand.Execute(null);
        vm.DrawTool = CircuitRF.Ui.Recognition.PictureDrawTool.Copper;
        vm.DrawLayer = "Bottom Copper";
        vm.DrawRectangle(new PixelPoint(40.5, 60), new PixelPoint(140, 110.5));
        vm.DrawTool = CircuitRF.Ui.Recognition.PictureDrawTool.Outline;
        vm.DrawRectangle(new PixelPoint(5, 5), new PixelPoint(395, 295));
        // A hand via with a drill typed (brief-dfr30-1 gate 2): spelled --via x,y,0.6mm.
        vm.DrawTool = CircuitRF.Ui.Recognition.PictureDrawTool.Via;
        vm.ViaDrill = vm.ViaDrillChoices.Single(c => c.Source == CircuitRF.Ui.Recognition.ViaDrillSource.Other);
        vm.OtherDrillText = "0.6 mm";
        vm.PlaceVia(new PixelPoint(300, 60));
        // An area painted with the Fill tool: spelled --fill "x,y=<conductor>".
        vm.DrawTool = CircuitRF.Ui.Recognition.PictureDrawTool.Fill;
        vm.DrawLayer = "Bottom Copper";
        vm.PaintAt(new PixelPoint(100, 150));
        await vm.Recognition;
        var input = vm.BuildTraceInput(TechPersistence.LoadFromFile(tech), null);
        Assert.Equal(5, input.Corrections.Count);
        Assert.Equal("--via 300px,60px,0.6mm", input.Corrections[^2].Spelling);
        Assert.Equal("--fill \"100px,150px=Bottom Copper\"", input.Corrections[^1].Spelling);
        var run = ImageTrace.Run(input, ImageTraceTarget.NewCell(Path.Combine(_root, "b", "out"), "board"));
        Assert.True(run.Ok, run.Refusal);

        // The CLI, on copy a, with each correction's own spelling.
        var args = new List<string> { "convert", a, "-o", Path.Combine(_root, "a", "out"), "--to", "clay", "--scale-points", "20,20,380,20=3.6 mm",
                                      "--tech", tech };
        foreach (var fix in input.Corrections)
        {
            string spelled = fix.Spelling;
            int gap = spelled.IndexOf(' ');
            args.Add(spelled[..gap]);
            args.Add(spelled[(gap + 1)..].Trim('"'));
        }
        var (exit, _, stderr) = Cli([.. args]);
        Assert.True(exit == 0, stderr);

        Assert.Equal(NoTime(File.ReadAllText(run.LayoutPath!)), NoTime(File.ReadAllText(Path.Combine(_root, "a", "out", "board", "layout", "board.clay"))));
        Assert.Equal(File.ReadAllBytes(run.TechnologyCopyPath!), File.ReadAllBytes(Path.Combine(_root, "a", "out", "board", "board.ctech")));
    }

    [Fact]
    public void APictureToGdsii_WritesWhatTheTracedCellExportsTo()
    {
        var (a, b, tech) = TwoCopies(TracePictures.TwoLayerBoard());
        string viaCli = Path.Combine(_root, "a", "board.gds");
        var (exit, _, stderr) = Cli("convert", a, "-o", viaCli, "--scale", "20um/px", "--tech", tech);
        Assert.True(exit == 0, stderr);

        var run = InProcess(b, tech, Path.Combine(_root, "b", "out"));
        string viaApp = Path.Combine(_root, "b", "board.gds");
        var view = LayoutPersistence.LoadFromFile(run.LayoutPath!);
        GdsiiExport.Write(viaApp, GdsiiExport.Analyze(run.CellDir!, TechPersistence.LoadFromFile(tech), view.DbuPerMicron, view));
        Assert.Equal(WithoutGdsiiTimestamps(File.ReadAllBytes(viaApp)), WithoutGdsiiTimestamps(File.ReadAllBytes(viaCli)));
    }

    [Fact]
    public void TheScale_IsNeverAGuess_AndAPictureCoordinateIsPixels()
    {
        var (a, _, tech) = TwoCopies(TracePictures.TwoLayerBoard());
        string target = Path.Combine(_root, "a", "out");

        var unstated = Cli("convert", a, "-o", target, "--to", "clay", "--tech", tech);
        Assert.Equal(1, unstated.Exit);
        Assert.Contains("The picture states no scale", unstated.StdErr);   // the dialog's D6 sentence
        Assert.Contains("--scale <length>/px", unstated.StdErr);
        Assert.False(Directory.Exists(target));

        var bare = Cli("convert", a, "-o", target, "--to", "clay", "--scale", "20um/px", "--tech", tech, "--region", "0,0,200,150");
        Assert.Equal(1, bare.Exit);
        Assert.Contains("'0px'", bare.StdErr);

        // Two land patterns are below D6's three: --scale auto lists what there is, with its support.
        string pairs = Path.Combine(_root, "pairs.png");
        File.WriteAllBytes(pairs, Pictures.Png(TracePictures.Pairs(25, ("0603", 20, 20), ("0603", 220, 160))));
        var auto = Cli("convert", pairs, "-o", target, "--to", "clay", "--scale", "auto", "--tech", tech);
        Assert.Equal(1, auto.Exit);
        Assert.Contains("at least 3 parts agree", auto.StdErr);
        Assert.Contains("support 2", auto.StdErr);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The BgnLib/BgnStr timestamps zeroed — <c>ConvertCliVerbTests</c>' exclusion, for its reason.</summary>
    private static byte[] WithoutGdsiiTimestamps(byte[] file)
    {
        var copy = (byte[])file.Clone();
        for (int i = 0; i + 4 <= copy.Length;)
        {
            int length = (copy[i] << 8) | copy[i + 1];
            if (length < 4 || i + length > copy.Length) break;
            if ((GdsiiRecordType)copy[i + 2] is GdsiiRecordType.BgnLib or GdsiiRecordType.BgnStr) Array.Clear(copy, i + 4, length - 4);
            i += length;
        }
        return copy;
    }

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
