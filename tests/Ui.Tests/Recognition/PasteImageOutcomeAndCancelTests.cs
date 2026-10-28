// brief-imf-9-auto-settings-and-paste.md R-imf9-4, R-imf9-6 — at the view model: Paste's one line in Messages, and a
// paste cancelled mid-read. The read is made slow by the paste's own reader seam blocking until the test lets it go —
// never by a large picture — and then the real reader runs with the token already cancelled, so it stops at its first
// checkpoint.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.ImageAcceptance;
using CircuitRF.Ui.Tests.Imaging;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

/// <summary>A Messages panel with no UI thread: every row as it was left.</summary>
internal sealed class RecordingMessages : IMessageSink
{
    public sealed class Row(string text) : IProgressMessage
    {
        public string Text { get; private set; } = text;
        public MessageLevel? Level { get; private set; }
        public string? ActionLabel { get; private set; }
        public Func<Task>? Action { get; private set; }
        public RunCancellation? Cancellation { get; private set; }

        public void Update(string text, string? counter = null, double? percentComplete = null, bool indeterminate = false) => Text = text;
        public void Finish(MessageLevel level, string outcome, bool keepBar = true) => (Level, Text) = (level, Text + " - " + outcome);
        public void Complete(MessageLevel level, string text) => (Level, Text) = (level, text);

        public void CompleteWithAction(MessageLevel level, string text, string actionLabel, Func<Task> action) =>
            (Level, Text, ActionLabel, Action) = (level, text, actionLabel, action);

        public void BindCancellation(RunCancellation? cancellation) => Cancellation = cancellation;
    }

    public List<(MessageLevel Level, string Text)> Posted { get; } = [];
    public List<Row> Rows { get; } = [];

    public void Post(MessageLevel level, string text, string? filePath = null) { lock (Posted) Posted.Add((level, text)); }

    public IProgressMessage BeginProgress(string text)
    {
        var row = new Row(text);
        lock (Rows) Rows.Add(row);
        return row;
    }

    public void Clear() { }
}

[Collection(ImageEntryPointsCollection.Name)]
public sealed class PasteOutcomeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-imf9-outcome-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>R-imf9-4: one line — what was pasted, and the amplifier standing on its default gain, selected — and the
    /// dialog opened from it shows the paste's own reading, not a second one.</summary>
    [Fact]
    public async Task TheGainBlock_PastesWithOneLine_NamingItsAmplifier_AndTheDialogHoldsTheSameReading()
    {
        var messages = new RecordingMessages();
        var vm = new WorkspaceViewModel { MessagesOverride = messages };
        var doc = new SchematicDocument("TB", new SchematicViewModel(new SchematicEditModel()));
        var source = ReadingPlausibilityTests.Picture("ds-gain-block-2x");

        await vm.PasteSchematicPicture(doc, source);

        var row = Assert.Single(messages.Rows);
        Assert.Empty(messages.Posted);
        Assert.Matches(@"^Pasted \d+ parts?, \d+ nets?, \d+ ports? from the picture — .*1 amplifier with default gain.* \(selected\): open .* in Create Schematic from Image to correct\.$", row.Text);
        Assert.Equal("Correct…", row.ActionLabel);
        var reading = vm.LastPasteReading!;
        var outcome = SchematicPasteOutcome.Of(reading);
        Assert.Equal(outcome.Sentence, row.Text);
        // The parts needing a look are what is selected.
        var selected = doc.ActiveViewModel.Selection.Ids;
        Assert.Equal(outcome.ToCorrect.Count, selected.Count);
        string amp = reading.Circuit!.TestBench.Instances.Single(i => i.Reference == "Amp").InstanceName;
        Assert.Contains(doc.ActiveViewModel.EditModel.Components, c => selected.Contains(c.Id) && c.InstanceName == amp);

        // The dialog the line opens: the same picture, the paste's reading shown as it is.
        var dialog = new ImageSourceViewModel(_dir, [], 0, null, makeSchematic: true, ImageRecognitionRunner.Instance,
                                              debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_dir, "presets")) { SchematicOffered = true };
        dialog.SeedSchematic(source, reading);
        await dialog.Recognition;
        Assert.Same(reading, dialog.SchematicReading);
    }
}

[Collection(ImageEntryPointsCollection.Name)]
public sealed class PasteCancelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-imf9-cancel-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A reader that blocks until let go, then reads with the paste's token — already cancelled by then.</summary>
    private sealed class Gate
    {
        public readonly ManualResetEventSlim Started = new(), Release = new();

        public T Hold<T>(Func<T> read)
        {
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return read();
        }
    }

    [Theory]
    [InlineData("esc")]
    [InlineData("row")]
    public async Task ASchematicPasteCancelledMidRead_ChangesNothing(string how)
    {
        var messages = new RecordingMessages();
        var gate = new Gate();
        var vm = new WorkspaceViewModel
        {
            MessagesOverride = messages,
            PasteSchematicReader = (input, dir, control) => gate.Hold(() => SchematicImageRecognition.PasteDrawing(input, dir, control)),
        };
        var model = new SchematicEditModel();
        var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        model.Components.Add(r1);
        var schematic = new SchematicViewModel(model);
        schematic.Selection.SetAll([r1.Id]);
        var doc = new SchematicDocument("TB", schematic, Path.Combine(_dir, "tb.csch"));

        var paste = vm.PasteSchematicPicture(doc, ReadingPlausibilityTests.Picture("ds-ldo-2x"));
        Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(vm.IsPasteInFlight);
        Assert.False(vm.PasteImageAsSchematicCommand.CanExecute(null));   // no second paste while one reads
        var row = Assert.Single(messages.Rows);
        Assert.Contains("Esc to cancel", row.Text);
        if (how == "esc") Assert.True(vm.CancelPasteInFlight());          // the window marks its Esc handled
        else row.Cancellation!.Cancel();                                   // the row's right-click Cancel
        gate.Release.Set();
        await paste;

        Assert.Equal("Paste cancelled.", row.Text);
        Assert.Equal([r1], model.Components);
        Assert.Empty(model.Wires);
        Assert.Equal([r1.Id], schematic.Selection.Ids);                   // Esc went to the paste, not the selection
        Assert.False(schematic.UndoRedo.CanUndo);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));          // no picture kept beside the schematic
        Assert.False(vm.IsPasteInFlight);
        Assert.False(vm.CancelPasteInFlight());                            // Esc means what it meant before
    }

    [Fact]
    public async Task ALayoutPasteCancelledMidRead_ChangesNothing()
    {
        var messages = new RecordingMessages();
        var gate = new Gate();
        var vm = new WorkspaceViewModel
        {
            MessagesOverride = messages,
            PasteLayoutTracer = (input, control) => gate.Hold(() => ImageTrace.PasteTrace(input, control)),
        };
        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 })
        {
            Technology = TracePictures.Tech(),
        };
        var doc = new LayoutDocument("L", layout);

        var paste = vm.PasteLayoutPicture(doc, ReadingPlausibilityTests.Picture("ds-ldo-2x"));
        Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(vm.CancelPasteInFlight());
        gate.Release.Set();
        await paste;

        Assert.Equal("Paste cancelled.", Assert.Single(messages.Rows).Text);
        Assert.Empty(layout.Model.Shapes);
        Assert.False(layout.UndoRedo.CanUndo);
        Assert.False(vm.IsPasteInFlight);
    }
}
