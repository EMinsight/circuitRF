// ================================================================
//  PropertiesPanelTreeBorrowTests.cs
//
//  Owner report, 2026-10-05: after deleting files in the Project Tree, clicking a component in an
//  already-open schematic left the Properties Inspector blank, however many times anything was
//  clicked, until the workspace was closed and reopened; the same had been seen in a .c3d view.
//
//  A Project Tree selection BORROWS the Properties panel, and every PropertiesTool context setter
//  detaches every other context on its way past. The schematic never left DocumentDock.ActiveDockable,
//  so nothing re-routed it: its canvas handler did not re-assert Properties at all, and Dock's focus
//  signal returned early on "same document". A non-cell tree selection — a folder click, or the
//  selection a tree refresh clears after a delete — did not even borrow: it called SetActiveCell(null)
//  and blanked the panel outright.
//
//  Driven through a real WorkspaceViewModel and its real PropertiesTool, not a mirror of the wiring.
// ================================================================

using System.Reflection;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PropertiesPanelTreeBorrowTests
{
    private static (WorkspaceViewModel Ws, SchematicDocument Doc, EditableComponent R1, EditableComponent R2) Open()
    {
        var model = new SchematicEditModel();
        var r1 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R1", X = 0,   Y = 0 };
        var r2 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R2", X = 200, Y = 0 };
        model.Components.Add(r1);
        model.Components.Add(r2);
        var doc = new SchematicDocument("top", new SchematicViewModel(model));

        var ws = new WorkspaceViewModel();
        Invoke(ws, "ActivateDocument", doc, false);       // the tab becomes the active document
        return (ws, doc, r1, r2);
    }

    private static void Invoke(WorkspaceViewModel ws, string name, params object?[] args)
        => typeof(WorkspaceViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(ws, args);

    private static string? Shown(WorkspaceViewModel ws) => ws.Factory.PropertiesTool!.EditorVm.IsEmptyState
        ? null
        : ws.Factory.PropertiesTool!.EditorVm.StagedInstanceName;

    [Fact]
    public void ATreeSelectionWithNothingToShow_LeavesTheSchematicInspectorFollowingTheCanvas()
    {
        var (ws, doc, r1, r2) = Open();
        doc.ViewModel.Selection.SelectOne(r1.Id);
        Assert.Equal("R1", Shown(ws));

        // A folder click, or a refresh clearing the selection after a delete — no click on the canvas
        // follows. Both handlers, in the order ProjectTreeTool.SelectedItem's setter runs them.
        ws.OnTreeSelectionChanged(null);
        Invoke(ws, "OnProjectTreeSelectionChanged");

        doc.ViewModel.Selection.SelectOne(r2.Id);
        Assert.Equal("R2", Shown(ws));
    }

    [Fact]
    public void ABorrowedPanel_IsReclaimedWhenTheUserClicksBackIntoTheSchematic()
    {
        var (ws, doc, r1, r2) = Open();
        string probe = Path.Combine(Path.GetTempPath(), "crf-borrow-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(probe, "x");
        try
        {
            // A Known File shows its info — a real borrow, and it must SURVIVE the second selection handler.
            var node = new ProjectTreeNode(NodeKind.OtherFile, Path.GetFileName(probe), probe, Path.GetFileName(probe));
            ws.OnTreeSelectionChanged(new ProjectTreeNodeViewModel(node, new ProjectTreeFilterState()));
            Invoke(ws, "OnProjectTreeSelectionChanged");
            Assert.True(ws.Factory.PropertiesTool!.IsFileInfoActive);

            doc.ViewModel.Selection.SelectOne(r1.Id);
            Assert.Null(Shown(ws));                        // borrowed: the schematic is not shown meanwhile

            Invoke(ws, "OnSchematicCanvasInteracted", doc); // canvas GotFocus
            Assert.Equal("R1", Shown(ws));

            doc.ViewModel.Selection.SelectOne(r2.Id);      // attached again, not merely refreshed once
            Assert.Equal("R2", Shown(ws));
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }
}
