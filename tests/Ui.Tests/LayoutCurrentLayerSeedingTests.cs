using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Where the layer combo lands when nobody has picked a layer</b> — owner-reported, 2026-09-09, on
/// a workspace whose technology came from a Gerber import.
///
/// <para>Two symptoms, one cause. The layout editor kept opening on
/// <c>GERB_00_PCB_Fabrication_Details</c>, a layer whose technology entry says neither Visible nor
/// Selectable; and the Via toolbar button was permanently disabled on a board whose stackup declares a
/// perfectly ordinary through-drill.</para>
///
/// <para>The cause was that <c>RebuildAvailableLayers</c> could not tell a layer the user PICKED from
/// one it had seeded itself. Every construction site applies a technology on the line AFTER the
/// constructor, so the constructor's own rebuild ran with none and seeded from the no-technology
/// fallback set, whose keys are placeholders (1/0 … 4/0). The next rebuild found a real layer numbered
/// 1/0 and restored it as though it had been chosen — and on a Gerber-imported board, drawing layer 1
/// is the fabrication-details layer, because copper starts at 2. The Via tool then refused, correctly,
/// on a layer no via entry claims; and the move that exists to fix that could only run from arming the
/// tool, which the refusal had disabled.</para>
/// </summary>
public class LayoutCurrentLayerSeedingTests
{
    private static readonly LayerKey FabDetails = new(1, 0);
    private static readonly LayerKey TopCu      = new(2, 0);
    private static readonly LayerKey Inner1     = new(3, 0);
    private static readonly LayerKey BottomCu   = new(7, 0);
    private static readonly LayerKey Drill      = new(21, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };

    /// <summary>The shape of the reported <c>.ctech</c>: layer numbering from a Gerber import, so the
    /// fabrication-details layer is 1/0 and copper starts at 2/0; every layer but the top copper
    /// switched off for both visibility and selection; one via entry, on a drill layer sorted last.</summary>
    private static Technology GerberImportedBoard() => new()
    {
        Name = "board",
        Layers =
        [
            new LayerDef { Key = TopCu,      Name = "Top Copper",    ZOrder = 0,    Color = new Rgba(0x62, 0x93, 0xD9) },
            new LayerDef { Key = Inner1,     Name = "Inner 1",       ZOrder = 10,   Color = new Rgba(0xD9, 0xAD, 0x62), Visible = false, Selectable = false },
            new LayerDef { Key = BottomCu,   Name = "Bottom Copper", ZOrder = 50,   Color = new Rgba(0xD9, 0x62, 0xCD), Visible = false, Selectable = false },
            new LayerDef { Key = FabDetails, Name = "FAB_DETAILS",   ZOrder = 1000, Color = new Rgba(0xD9, 0x62, 0xD9), Visible = false, Selectable = false },
            new LayerDef { Key = Drill,      Name = "Drill",         ZOrder = 1020, Color = new Rgba(0x71, 0xD9, 0x62), Visible = false, Selectable = false },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Top Copper",    ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [TopCu] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Dielectric 1",  ThicknessDbu = 355_600, Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Inner 1",       ThicknessDbu = 18_000, SigmaSm = 5.8e7, DrawingLayers = [Inner1] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Dielectric 2",  ThicknessDbu = 355_600, Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Bottom Copper", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [BottomCu] },
                new StackupLayer { Kind = StackupKind.Via, Name = "Drill", DrawingLayers = [Drill],
                                   SpanFromLayer = "Top Copper", SpanToLayer = "Bottom Copper" },
            ],
        },
    };

    private static TechResolution Resolved(Technology? tech) =>
        new(tech, "/board.ctech", TechResolutionSource.LayoutRef, tech is null ? ["could not load"] : []);

    /// <summary>Every <c>new LayoutEditorViewModel(...)</c> applies a resolution on the next line, so
    /// this is the sequence a real open performs — and the one that used to land on 1/0.</summary>
    private static LayoutEditorViewModel OpenedOn(Technology? tech)
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(Resolved(tech));
        return vm;
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpeningALayout_SeedsTheTopConductor_NotTheLayerThatSharesThePlaceholderNumber()
    {
        var vm = OpenedOn(GerberImportedBoard());

        Assert.Equal(TopCu, vm.CurrentLayerKey);
        Assert.Equal("Top Copper", vm.CurrentLayerItem!.Name);
    }

    /// <summary>"Topmost" means the stackup's top-to-bottom order, not ZOrder. A board technology paints
    /// Bottom Copper beneath Top Copper (the starter PCB technology does exactly this), and seeding by
    /// ZOrder opened every new layout on the bottom copper.</summary>
    [Fact]
    public void Seeding_TakesTheTopmostConductorInStackupOrder_NotTheLowestZOrder()
    {
        var tech = GerberImportedBoard();
        tech.Layers.First(l => l.Key == TopCu).ZOrder = 60;
        tech.Layers.First(l => l.Key == BottomCu).Visible = true;
        tech.Layers.First(l => l.Key == BottomCu).Selectable = true;

        Assert.Equal(TopCu, OpenedOn(tech).CurrentLayerKey);
    }

    /// <summary>The floor, stated on its own: a seeded layer must be one the user can see and click.
    /// Drawing onto a hidden, unselectable layer produces artwork that cannot be found afterwards.</summary>
    [Fact]
    public void SeedingNeverLandsOnALayerThatIsHiddenOrLockedAgainstSelection()
    {
        var tech = GerberImportedBoard();
        // Switch the top copper off too, so the only usable layer is one sorted well below it.
        tech.Layers.First(l => l.Key == TopCu).Visible = false;
        tech.Layers.First(l => l.Key == Drill).Visible = true;
        tech.Layers.First(l => l.Key == Drill).Selectable = true;

        var vm = OpenedOn(tech);

        var seeded = tech.Layers.First(l => l.Key == vm.CurrentLayerKey);
        Assert.True(seeded.Visible);
        Assert.True(seeded.Selectable);
        Assert.Equal(Drill, vm.CurrentLayerKey);
    }

    /// <summary>A pick is a pick: the seeding rules describe where to land when nobody has chosen, and
    /// they must not second-guess someone who deliberately selects a locked or hidden layer.</summary>
    [Fact]
    public void AnExplicitPickOfAHiddenLayerIsKept()
    {
        var vm = OpenedOn(GerberImportedBoard());
        vm.CurrentLayerItem = vm.AvailableLayers.First(l => l.Key == FabDetails);

        Assert.Equal(FabDetails, vm.CurrentLayerKey);

        vm.ApplyTechResolution(Resolved(GerberImportedBoard()));
        Assert.Equal(FabDetails, vm.CurrentLayerKey);
    }

    /// <summary>The second symptom. The open <c>.ctech</c> editor pushes through this same seam on
    /// every commit, and a load that fails resolves to a null technology — which used to drag the
    /// current layer onto a placeholder key and pin it there once the technology came back.</summary>
    [Fact]
    public void ATechnologyThatFailsToResolveAndComesBack_ReturnsTheUserToTheirOwnLayer()
    {
        var vm = OpenedOn(GerberImportedBoard());
        vm.CurrentLayerItem = vm.AvailableLayers.First(l => l.Key == BottomCu);

        vm.ApplyTechResolution(Resolved(null));
        vm.ApplyTechResolution(Resolved(GerberImportedBoard()));

        Assert.Equal(BottomCu, vm.CurrentLayerKey);
        Assert.Equal("Bottom Copper", vm.CurrentLayerItem!.Name);
    }

    /// <summary>A pick that the new technology genuinely no longer declares falls back to the seeding
    /// rule rather than to whatever sorts first — the pre-existing "never throw" contract, kept.</summary>
    [Fact]
    public void APickThatTheNewTechnologyDoesNotDeclare_FallsBackToTheSeedingRule()
    {
        var vm = OpenedOn(GerberImportedBoard());
        vm.CurrentLayerItem = vm.AvailableLayers.First(l => l.Key == BottomCu);

        var trimmed = GerberImportedBoard();
        trimmed.Layers.RemoveAll(l => l.Key == BottomCu);
        trimmed.Stackup.Layers.RemoveAll(l => l.DrawingLayers.Contains(BottomCu));

        var exception = Record.Exception(() => vm.ApplyTechResolution(Resolved(trimmed)));

        Assert.Null(exception);
        Assert.Equal(TopCu, vm.CurrentLayerKey);
    }

    // ── The Via tool ────────────────────────────────────────────────────────────────────────────

    /// <summary>One via entry is no choice at all, so the button must not be the thing that blocks the
    /// move that would satisfy it. Arming it was the only route to <c>MoveToTheOnlyViaLayer</c>, and
    /// the button that arms it was disabled — a deadlock the user could only leave by finding the
    /// drill layer in the combo by hand.</summary>
    [Fact]
    public void TheViaTool_IsEnabledOnASoleViaLayerTechnology_EvenFromACopperLayer()
    {
        var vm = OpenedOn(GerberImportedBoard());
        Assert.Equal(TopCu, vm.CurrentLayerKey);

        Assert.True(vm.ViaToolAvailability.CanExecute);
        Assert.Contains("Drill", vm.ViaToolTipText, StringComparison.Ordinal);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;
        Assert.Equal(Drill, vm.CurrentLayerKey);
    }

    [Fact]
    public void TheViaTool_PlacesOnTheDrillLayer_WhenArmedFromACopperLayer()
    {
        var vm = OpenedOn(GerberImportedBoard());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        vm.OnPointerPressed(4000, 7000, KeyModifiers.None);

        var via = Assert.IsType<ViaShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(Drill, via.Layer);
    }

    /// <summary>The layer combo stays live while a tool is armed, so enabling the button on the
    /// "arming moves there" clause opens a second door onto the inert shape the refusal exists to
    /// prevent: a via bound to no via entry, which draws perfectly and does nothing in DRC, in EM and
    /// in every export. The commit re-runs the move and checks the LAYER, not the button — so a via
    /// still lands on the one layer that can carry a span, wherever the combo had wandered to.</summary>
    [Fact]
    public void TheViaTool_StillPlacesOnTheDrillLayer_WhenTheComboMovedOffItWhileArmed()
    {
        var vm = OpenedOn(GerberImportedBoard());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;
        Assert.Equal(Drill, vm.CurrentLayerKey);

        vm.CurrentLayerItem = vm.AvailableLayers.First(l => l.Key == TopCu);
        vm.OnPointerPressed(4000, 7000, KeyModifiers.None);

        var via = Assert.IsType<ViaShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(Drill, via.Layer);
    }

    /// <summary>Two via entries means the drawing layer IS the span choice, so nothing here may pick
    /// one — the arming move declines, the button stays refused, and a click places nothing. This is
    /// the boundary of the "one via layer is no choice at all" clause above.</summary>
    [Fact]
    public void TwoViaEntries_StillRefuse_AndPlaceNothing()
    {
        var tech = GerberImportedBoard();
        tech.Layers.Add(new LayerDef { Key = new LayerKey(22, 0), Name = "Blind Drill", ZOrder = 1021, Color = new Rgba(0x40, 0x40, 0x40) });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "Blind", DrawingLayers = [new LayerKey(22, 0)],
            SpanFromLayer = "Top Copper", SpanToLayer = "Inner 1",
        });

        var vm = OpenedOn(tech);
        Assert.Equal(TopCu, vm.CurrentLayerKey);
        Assert.False(vm.ViaToolAvailability.CanExecute);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;
        Assert.Equal(TopCu, vm.CurrentLayerKey);

        vm.OnPointerPressed(4000, 7000, KeyModifiers.None);
        Assert.Empty(vm.Model.Shapes);
    }
}
