using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-4 R-to4-8: Push writes each owning document in one undo step of its own.</summary>
public sealed class TuningPushTests
{
    private static string Param(SchematicEditModel m, string inst, string name)
        => m.Components.Single(c => c.InstanceName == inst).Parameters.Single(p => p.Name == name).Expression;

    private static string VarRow(SchematicEditModel m, string name)
        => m.Components.SelectMany(c => c.Parameters).Single(p => p.Name == name).Expression;

    private static void Set(TuningPanelFixture f, string key, string text)
    {
        var row = f.Row(key);
        row.ValueBoxText = text;
        row.CommitValueText();
    }

    [Fact]
    public void TopVarAndSubCell_EachDocumentOneUndoStep_BothDirty_InheritedGainsOverride()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned(["R1.R", "Wline", "X2.Rbias", "DUT:R3.R"], on: true);
        f.Top.UndoRedo.MarkSaved();

        Set(f, "R1.R", "60");
        Set(f, "Wline", "350");
        Set(f, "X2.Rbias", "1.5");
        Set(f, "DUT:R3.R", "12");
        Assert.False(f.Top.UndoRedo.IsModified);        // tuning alone dirties nothing
        Assert.False(f.Dut.UndoRedo.IsModified);

        f.Panel.PushCommand.Execute(null);

        Assert.Equal(("60", "350", "12"), (Param(f.Top.EditModel, "R1", "R"), VarRow(f.Top.EditModel, "Wline"), Param(f.DutModel, "R3", "R")));
        var x2 = f.Top.EditModel.Components.Single(c => c.InstanceName == "X2").Parameters.Single(p => p.Name == "Rbias");
        Assert.Equal(("1.5", "kOhm"), (x2.Expression, x2.Unit));
        Assert.True(f.Top.UndoRedo.IsModified);
        Assert.True(f.Dut.UndoRedo.IsModified);
        Assert.Equal([f.DutModel], f.TabsOpened);       // the sub-cell was asked to have a tab
        Assert.Equal("Pushed 4 values · DUT: 1", f.Panel.StatusText);

        // One step per document, and undo in the top document touches only the top document.
        f.Top.UndoRedo.Undo();
        Assert.False(f.Top.UndoRedo.IsModified);
        Assert.Equal(("50", "300"), (Param(f.Top.EditModel, "R1", "R"), VarRow(f.Top.EditModel, "Wline")));
        Assert.DoesNotContain(f.Top.EditModel.Components.Single(c => c.InstanceName == "X2").Parameters, p => p.Name == "Rbias");
        Assert.Equal("12", Param(f.DutModel, "R3", "R"));

        f.Dut.UndoRedo.Undo();
        Assert.False(f.Dut.UndoRedo.IsModified);
        Assert.Equal("10", Param(f.DutModel, "R3", "R"));
    }

    [Fact]
    public void ReadOnlyOwner_IsSkippedAndReported()
    {
        var f = new TuningPanelFixture(workspaceRoot: "/work/ws");   // DUT lives in another workspace
        f.Panel.SetTuned(["R1.R", "DUT:R3.R"], on: true);
        Set(f, "R1.R", "60");
        Set(f, "DUT:R3.R", "12");

        f.Panel.PushCommand.Execute(null);

        Assert.Equal("60", Param(f.Top.EditModel, "R1", "R"));
        Assert.Equal("10", Param(f.DutModel, "R3", "R"));
        Assert.Empty(f.TabsOpened);
        Assert.Equal("Pushed 1 value · skipped 1 read-only", f.Panel.StatusText);
    }

    [Fact]
    public void ASessionNeverPushed_LeavesEveryDocumentClean()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned(["R1.R", "DUT:R3.R"], on: true);
        f.Top.UndoRedo.MarkSaved();
        f.Panel.CreateSession = (_, _) => new TuneSession(
            (_, _, _) => new CircuitRF.Design.Circuit.RunResult(
                CircuitRF.Design.Circuit.RunStatus.Success, "ok", grouped: TuningDisplayFixture.Results(1)),
            null, a => a());

        f.Panel.StartCommand.Execute(null);
        Set(f, "R1.R", "60");
        Set(f, "DUT:R3.R", "12");
        f.Panel.StopCommand.Execute(null);

        Assert.False(f.Top.UndoRedo.IsModified);
        Assert.False(f.Dut.UndoRedo.IsModified);
        Assert.Empty(f.TabsOpened);
    }

    /// <summary>The shell's half: a sub-cell given a tab by Push does not take focus from the bench.</summary>
    [Fact]
    public void BackgroundTab_IsAddedWithoutBecomingActive()
    {
        var factory = new CircuitRfDockFactory();
        factory.CreateLayout();
        var active = factory.DocumentDock!.ActiveDockable;
        var doc = new StubDocument("DUT.csch", StubDocument.StubKind.Schematic);

        factory.OpenDocumentInBackground(doc);

        Assert.Contains(doc, factory.DocumentDock.VisibleDockables!);
        Assert.Same(active, factory.DocumentDock.ActiveDockable);
        Assert.Contains("OpenDocumentInBackground", TuningPanelFollowsFocusTests.WorkspaceTuningSource());
    }
}
