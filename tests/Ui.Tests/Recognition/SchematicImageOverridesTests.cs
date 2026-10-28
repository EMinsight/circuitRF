// brief-img-10-schematic-emit.md §4 — a crossing flipped on the picture is held as an override (R-im10-8): it survives a
// re-run, and it reaches the entry point as the point the CLI's --disconnect states.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class SchematicImageOverridesTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-schovr-" + Guid.NewGuid().ToString("N")[..12]);

    public SchematicImageOverridesTests() => Directory.CreateDirectory(_ws);

    public void Dispose()
    {
        if (Directory.Exists(_ws)) Directory.Delete(_ws, recursive: true);
    }

    /// <summary>The ladder, with a wire crossing its main line at a junction dot — connected as read.</summary>
    private static ImageSource Picture() => ImageSource.FromBytes(Pictures.Png(Pictures.Draw(560, 300, c =>
    {
        SchematicImageRecognitionTests.DrawLadder(c);
        using var pen = SchematicPictures.Ink();
        c.DrawLine(350, 96, 350, 184, pen);
        using var dot = Pictures.Fill(SKColors.Black);
        c.DrawCircle(350, 140, 4, dot);
    })), "crossing.png").Source!;

    private static ImageOverlayItem Crossing(ImageSourceViewModel vm) =>
        Assert.Single(vm.Overlay.Items, i => i.Class == ImageOverlayClass.Crossing);

    [Fact]
    public async Task AFlippedCrossing_SurvivesAReRun_AndReachesTheEntryPointAsDisconnectsPoint()
    {
        var runner = new RecordingImageRunner(DrawingKind.Schematic);
        var vm = ImageDialog.Open(_ws, runner, Picture(), makeSchematic: true);
        vm.SchematicOffered = true;   // the reader is withheld from the application, not from its tests
        await vm.Recognition;
        Assert.True(vm.IsSchematicPicture);
        Assert.StartsWith("connected", Crossing(vm).Label);

        vm.ToggleCrossing(Crossing(vm));
        await vm.Recognition;
        Assert.Equal("not connected (yours)", Crossing(vm).Label);

        // Another option changed: the reading runs again, with the override laid over it.
        vm.CrossingIndex = 2;
        await vm.Recognition;
        Assert.Equal("not connected (yours)", Crossing(vm).Label);

        var held = Assert.Single(runner.SchematicReads.Last().Options.Overrides);
        var (cx, cy, _) = Crossing(vm).Circle!.Value;
        Assert.Equal((ConnectionOverrideKind.Disconnect, cx, cy), (held.Kind, held.X, held.Y), new Near());
        var args = ConnectionOverride.ToArguments([held]);
        Assert.Equal(ConnectionOverride.DisconnectFlag, args[0]);
        Assert.Equal(held, ConnectionOverride.Parse(args[0], args[1], out string? error));
        Assert.Null(error);
    }

    private sealed class Near : System.Collections.Generic.IEqualityComparer<(ConnectionOverrideKind, double, double)>
    {
        public bool Equals((ConnectionOverrideKind, double, double) a, (ConnectionOverrideKind, double, double) b) =>
            a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) <= 0.01 && Math.Abs(a.Item3 - b.Item3) <= 0.01;

        public int GetHashCode((ConnectionOverrideKind, double, double) v) => v.Item1.GetHashCode();
    }
}
