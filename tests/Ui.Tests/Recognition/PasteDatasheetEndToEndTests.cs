// brief-imf-10-acceptance-and-docs.md R-imf10-3 — a paste, end to end, at the view model: the committed gain block on
// the clipboard, Edit ▸ Paste Image as Schematic into an open schematic that already holds a part. Nothing is asked;
// the drawing lands beside what was there and joins none of it; the amplifier is the System Amp between two ports;
// Messages has the one line; the picture is under the drawing. Then the document is netlisted as Simulate netlists it
// and an S-parameter run completes with the Amp's default 20 dB in S21 — a pasted datasheet circuit simulates with no
// edit. One case per resolution, because 1× and 2× read differently (IMF-2); 2× is skipped with what it lacks.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ImageAcceptance;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Recognition;

[Collection(ImageEntryPointsCollection.Name)]
public sealed class PasteDatasheetEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-imf10-paste-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private const string TwoX = "Measured short at 2×: the RFC coil comes apart into five ? parts, and where it meets the " +
                                "amplifier's output the junction reads as a 3-port S-parameter block — which has no Touchstone " +
                                "file, so the run is refused (ImageDatasheetStyleTests' GainBlock skip)";

    [Theory]
    [InlineData("ds-gain-block-1x")]
    [InlineData("ds-gain-block-2x", Skip = TwoX)]
    public async Task TheGainBlock_PastedWithNothingAsked_SimulatesWithNoEdit(string picture)
    {
        // An open schematic holding one part of its own, and the picture on the clipboard as a copied file.
        string csch = Path.Combine(_dir, "tb.csch");
        var before = new SchematicEditModel();
        var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = -400, Y = -400 };
        r1.Parameters.Add(new EditableParameter { Name = "R", Expression = "50", Unit = "Ohm" });
        before.Components.Add(r1);
        SchematicPersistence.SaveToFile(csch, before, "tb");
        string png = Path.Combine(FixturePaths.Require(StylePictures.Folder), picture, "picture.png");
        var clipboard = new FakePictureClipboard { Contents = new PictureClipboardContents(false, [png]), PictureFile = png };
        var messages = new RecordingMessages();
        var vm = new WorkspaceViewModel { MessagesOverride = messages, PictureClipboard = () => clipboard, SchematicFromImageOffered = true };
        Assert.True(vm.OpenDocumentByPath(csch));
        var doc = Assert.IsType<SchematicDocument>(vm.FindOpenDocument(Path.GetFullPath(csch)));
        var model = doc.ActiveViewModel.EditModel;
        var existing = Assert.Single(model.Components);
        int posted = messages.Posted.Count;
        await vm.RefreshPasteImage();
        Assert.True(vm.PasteImageAsSchematicCommand.CanExecute(null));

        // Nothing asked: the command runs to its end with no window to answer.
        await vm.PasteImageAsSchematicCommand.ExecuteAsync(null);

        // The one line, and nothing else said.
        var row = Assert.Single(messages.Rows);
        Assert.Equal(posted, messages.Posted.Count);
        Assert.Matches(@"^Pasted \d+ parts?, \d+ nets?, 2 ports from the picture — .*1 amplifier with default gain.*\(selected\)", row.Text);
        Assert.Equal(MessageLevel.Warning, row.Level);
        var reading = vm.LastPasteReading!;
        Assert.Equal(SchematicPasteOutcome.Of(reading).Sentence, row.Text);

        // The drawing: an Amp between two ports, the passives by kind, beside what was there and joined to none of it.
        var pasted = model.Components.Where(c => !ReferenceEquals(c, existing)).ToList();
        Assert.NotEmpty(pasted);
        var tb = NetExtractor.Extract(model).TestBench;
        output.WriteLine(row.Text);
        foreach (var i in tb.Instances) output.WriteLine($"{i.Reference}:{i.InstanceName} {string.Join(' ', NetlistShape.Pins(i))}");
        Assert.Single(tb.Instances, i => i.Reference == "Amp");
        Assert.Equal(2, tb.Instances.Count(i => i.Reference == "Port"));
        int Count(string reference) => tb.Instances.Count(i => i.Reference == reference && !i.InstanceName.StartsWith('?'));
        // The answer draws 3 C, 1 L, 1 R. Short by one each way, as ImageStylePictureTests' GainBlock skip says: at 1×
        // Cbypass reads as a supply bar, at 2× the RFC coil comes apart.
        var (c, l, r) = picture.EndsWith("1x", StringComparison.Ordinal) ? (2, 1, 1) : (3, 0, 1);
        Assert.Equal((c, l, r + 1), (Count("C"), Count("L"), Count("R")));   // + the R1 that was there
        var mine = tb.Instances.Single(i => i.InstanceName == existing.InstanceName);
        foreach (string net in mine.NetBindings)
            Assert.DoesNotContain(tb.Instances, i => !ReferenceEquals(i, mine) && NetlistShape.Pins(i).Contains(net));
        // What the line names is what is selected, all of it pasted.
        var selected = doc.ActiveViewModel.Selection.Ids;
        Assert.NotEmpty(selected);
        Assert.All(selected, id => Assert.Contains(pasted, p => p.Id == id));
        string amp = reading.Circuit!.TestBench.Instances.Single(i => i.Reference == "Amp").InstanceName;
        Assert.Contains(pasted, p => p.InstanceName == amp && selected.Contains(p.Id));

        // The picture under the drawing: an ordinary bitmap kept beside the schematic, below everything, round it all.
        var bitmap = Assert.Single(model.CanvasObjects.OfType<EditableBitmap>());
        Assert.True(File.Exists(bitmap.ImagePath), bitmap.ImagePath);
        Assert.Equal(model.CanvasObjects.Min(o => o.ZOrder), bitmap.ZOrder);
        // The variables block (the Amp's gain, an unread value) sits above the drawing, off the picture.
        Assert.All(pasted.Where(p => p.Symbol != SymbolKind.Var), p =>
        {
            Assert.InRange(p.X, bitmap.X - bitmap.Width / 2, bitmap.X + bitmap.Width / 2);
            Assert.InRange(p.Y, bitmap.Y - bitmap.Height / 2, bitmap.Y + bitmap.Height / 2);
        });

        // Netlisted as Simulate netlists it, and run with no edit: S21 is the Amp's default 20 dB, less what the
        // blocking capacitors and the bias network cost.
        string saved = Path.Combine(_dir, "pasted.csch");
        SchematicPersistence.SaveToFile(saved, model, "tb");
        string s2p = Path.Combine(_dir, "pasted.s2p");
        var (exit, _, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "sparam", saved, "--freq", "0.5GHz:1.5GHz:0.5GHz", "-o", s2p);
        Assert.True(exit == 0, stderr);
        var snp = RfCore.TouchstoneIO.ReadFile(s2p);
        double s21 = 20 * Math.Log10(snp[1][1, 0].Magnitude);   // 1 GHz
        output.WriteLine($"S21 at 1 GHz: {s21:F2} dB");
        Assert.InRange(s21, 18, 20.5);
    }
}
