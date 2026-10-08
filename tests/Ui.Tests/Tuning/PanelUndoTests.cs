using Avalonia.Input;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>Undo works from the Tuning and Optimizer panels: an edit there says which session it landed
/// on (the shell pins Undo to it), and a floating panel forwards the shell's Undo/Redo keys.</summary>
public sealed class PanelUndoTests
{
    [Fact]
    public void APush_NamesTheSessionItLandedOn()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned("R1.R", true);
        var row = f.Row("R1.R");
        row.ValueBoxText = "70";
        row.CommitValueText();

        SchematicViewModel? pinned = null;
        f.Panel.EditCommitted += vm => pinned = vm;
        f.Panel.PushCommand.Execute(null);

        Assert.Same(f.Top, pinned);
        Assert.True(f.Top.UndoRedo.CanUndo);

        // And the shell subscribes both panels to its undo pin.
        string src = TuningPanelFollowsFocusTests.WorkspaceTuningSource();
        Assert.Contains("panel.EditCommitted     += OnAnalysesEditCommitted;", src);
    }

    [Theory]
    [InlineData(Key.Z, KeyModifiers.Control, false, true)]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, false, false)]
    [InlineData(Key.Y, KeyModifiers.Control, false, false)]
    [InlineData(Key.Z, KeyModifiers.Meta, false, true)]
    [InlineData(Key.Z, KeyModifiers.Meta, true, null)]       // macOS: ⌘Z is the app menu's
    [InlineData(Key.Z, KeyModifiers.None, false, null)]
    public void AFloatingPanel_ForwardsTheShellsUndoRedoGestures(Key key, KeyModifiers mods, bool macOs, bool? undo)
        => Assert.Equal(undo, CrfHostWindow.UndoRedoGesture(key, mods, macOs));
}
