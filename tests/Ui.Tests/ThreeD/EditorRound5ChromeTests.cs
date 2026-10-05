// ================================================================
//  EditorRound5ChromeTests.cs — the 3D editor's fifth round of owner feedback, the chrome half: the 2D overlay over the
//  3D pane clips to the pane (a selected wire, edge or gizmo projected past its edge was painted over the object tree
//  and toolbar), and the shell reports the 3D menu's visibility only when it FLIPS — the macOS window repaints the menu
//  bar on that, because AppKit draws a top-level item's hidden flag late.
// ================================================================

using System.Reflection;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class EditorRound5ChromeTests
{
    [Fact]
    public void TheOverlay_ClipsToThePane()
        => Assert.True(new Viewer3DOverlay().ClipToBounds);

    [Fact]
    public void The3DMenuVisibility_IsReportedOnlyWhenItFlips()
    {
        var vm = new WorkspaceViewModel();
        int flips = 0;
        vm.ThreeDMenuVisibilityChanged += () => flips++;

        vm.RefreshThreeDMenu();
        Assert.Equal(0, flips);                                   // hidden, and still hidden

        var focused = typeof(WorkspaceViewModel).GetField("_focusedWindowDocument", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // The read-only viewer's document, whose uninitialized ViewModel reads as "no pane" — the editor's dereferences its
        // ViewModel to find the pane, which an uninitialized document does not have.
        focused.SetValue(vm, RuntimeHelpers.GetUninitializedObject(typeof(Viewer3DDocument)));
        vm.RefreshThreeDMenu();
        vm.RefreshThreeDMenu();
        Assert.Equal(1, flips);                                   // shown once, however often it is re-raised

        focused.SetValue(vm, null);
        vm.RefreshThreeDMenu();
        Assert.Equal(2, flips);                                   // and hidden again
    }
}
