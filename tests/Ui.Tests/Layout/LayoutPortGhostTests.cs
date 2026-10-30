// Owner request, 2026-08-09: "the port needs to have a ghost rendering when user places it using the
// Port button (in the toolbar). The ghosts snapping (and sizes) also needs to render live."
//
// The Port tool had a live SNAP MARKER while hovering but no ghost, so the port itself — its inferred
// direction, the width bar spanning the metal, the arrow — was invisible until after the click. The
// ghost is now built by the SAME method the commit uses (TryBuildPortPlacement), so the two cannot
// disagree about where it lands, which way it faces, or how wide it is.

using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortGhostTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private const long Tol = 300 * Dbu;

    /// <summary>A 20 x 2.9 mm run of metal with its low-x/low-y corner at the origin.</summary>
    private static LayoutEditorViewModel Fixture()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });
        return new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Port };
    }

    private static void Hover(LayoutEditorViewModel vm, long x, long y, KeyModifiers mods = KeyModifiers.None) =>
        vm.OnPointerMoved(x, y, leftDown: false, mods, 0, 0, Tol);

    private static LabelShape? Ghost(LayoutEditorViewModel vm) =>
        vm.Overlay.InProgressPrimitive as LabelShape;

    [Fact]
    public void HoveringOverMetal_ShowsAPortGhost_WhereTheClickWouldLand()
    {
        var vm = Fixture();
        Assert.Null(Ghost(vm));

        // Near the low-x/low-y corner, inside the snap tolerance of it.
        Hover(vm, 90 * Dbu, 80 * Dbu);

        var ghost = Ghost(vm);
        Assert.NotNull(ghost);
        Assert.True(ghost!.IsPort);
        Assert.Equal(0, ghost.X);
        Assert.Equal(0, ghost.Y);
    }

    [Fact]
    public void TheGhostCarriesTheDirectionTheCommitWouldInfer()
    {
        // The whole point of a ghost is that the ARROW is visible before the click. A ghost with no
        // direction draws no arrow at all (the same rule a committed port follows), so this is the
        // assertion that says the ghost is actually informative rather than merely present.
        var vm = Fixture();
        Hover(vm, 90 * Dbu, 80 * Dbu);
        var ghost = Ghost(vm);

        Assert.NotNull(ghost!.PortDirection);
        Assert.Equal(LayoutPortDirection.FromBbox(
                         new Bbox(0, 0, 20_000 * Dbu, 2_900 * Dbu), 0, 0),
                     ghost.PortDirection);
    }

    [Fact]
    public void OffTheMetal_TheGhostVanishes_BecauseAClickThereCreatesNothing()
    {
        var vm = Fixture();
        Hover(vm, 90 * Dbu, 80 * Dbu);
        Assert.NotNull(Ghost(vm));

        // Well clear of the conductor AND of its snap tolerance.
        Hover(vm, 5_000 * Dbu, 9_000 * Dbu);
        Assert.Null(Ghost(vm));

        // …and a click there genuinely places nothing, which is what the vanishing ghost predicted.
        vm.OnPointerPressed(5_000 * Dbu, 9_000 * Dbu, KeyModifiers.None, 1, 0, 0, Tol);
        Assert.Empty(vm.Model.Shapes.OfType<LabelShape>());
    }

    [Fact]
    public void TheGhostFollowsTheSnappedPoint_NotTheRawCursor()
    {
        var vm = Fixture();

        // Off the grid AND outside the geometry-snap tolerance of every feature — deliberately clear
        // of the corners, the edge midpoints, the edges themselves and the CENTROID (the first
        // version of this fixture sat 275 um from the centroid, so geometry snap correctly won and
        // the test was measuring the wrong thing). SnapDbu is 100 um.
        Hover(vm, 5_137 * Dbu, 1_211 * Dbu);

        var ghost = Ghost(vm);
        Assert.NotNull(ghost);
        Assert.Equal(5_100 * Dbu, ghost!.X);

        // ALONG the trace the grid decides, as it always did. ACROSS it the EDGE does, because this
        // fixture has geometry snap ON (owner, 2026-09-09: "when geometry snap is on, the port should
        // be snapping to the edge for placement and for drags"). 1,211 µm up a 2.9 mm trace is nearer
        // the low-y side, so that is where the ghost sits — and, the ghost being the click's own
        // preview, where the click lands.
        Assert.Equal(0, ghost.Y);
    }

    [Fact]
    public void TheGhostIsExactlyWhatTheClickCommits()
    {
        // The contract that makes the ghost trustworthy: hover, then click at the same point, and
        // compare every field that the ghost is meant to be previewing.
        var vm = Fixture();
        Hover(vm, 90 * Dbu, 80 * Dbu);
        var ghost = Ghost(vm);

        vm.OnPointerPressed(90 * Dbu, 80 * Dbu, KeyModifiers.None, 1, 0, 0, Tol);
        var placed = Assert.Single(vm.Model.Shapes.OfType<LabelShape>());

        Assert.Equal(ghost!.X, placed.X);
        Assert.Equal(ghost.Y, placed.Y);
        Assert.Equal(ghost.Text, placed.Text);
        Assert.Equal(ghost.PortDirection, placed.PortDirection);
        Assert.Equal(ghost.Layer, placed.Layer);
        Assert.True(placed.IsPort);
    }

    /// <summary>R-dfr31-5-2 — on the shipped 10-layer board (a 1 mm label default) a port dropped on a
    /// 300 µm line is at most twice the line tall.</summary>
    [Fact]
    public void APortOnA300umLine_OnThe10LayerBoard_IsAtMost600umTall()
    {
        var tech = CircuitRF.Design.Layout.ShippedTechnologies.Load("pcb-10layer_FR-4_1.7mm_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 10 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 10_000 * Dbu, Y2 = 300 * Dbu });
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Port };
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));
        Assert.Equal(1_000 * Dbu, vm.CurrentLabelHeightDbu);

        vm.OnPointerPressed(5_000 * Dbu, 150 * Dbu, KeyModifiers.None, 1, 0, 0, Tol);

        var port = Assert.Single(vm.Model.Shapes.OfType<LabelShape>());
        Assert.True(port.IsPort);
        Assert.InRange(port.Height, 1, 600 * Dbu);
    }

    [Fact]
    public void CommittingClearsTheGhost_SoNoStalePreviewSurvivesThePlacement()
    {
        var vm = Fixture();
        Hover(vm, 90 * Dbu, 80 * Dbu);
        vm.OnPointerPressed(90 * Dbu, 80 * Dbu, KeyModifiers.None, 1, 0, 0, Tol);

        Assert.Null(Ghost(vm));
    }

    [Fact]
    public void LeavingThePortTool_ClearsTheGhost()
    {
        var vm = Fixture();
        Hover(vm, 90 * Dbu, 80 * Dbu);
        Assert.NotNull(Ghost(vm));

        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;
        Assert.Null(Ghost(vm));
    }

    [Fact]
    public void TheNamePreviewedIsTheNameThatLands_AndAdvancesAfterEachPlacement()
    {
        var vm = Fixture();

        Hover(vm, 90 * Dbu, 80 * Dbu);
        Assert.Equal("P1", Ghost(vm)!.Text);
        vm.OnPointerPressed(90 * Dbu, 80 * Dbu, KeyModifiers.None, 1, 0, 0, Tol);

        Hover(vm, 19_950 * Dbu, 80 * Dbu);
        Assert.Equal("P2", Ghost(vm)!.Text);
    }
}
