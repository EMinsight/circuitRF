using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Tests;

// ── L1 fix §2: live technology editing (brief-L1-fix-path-seams-and-live-tech.md) ──
// WorkspaceViewModel can't be instantiated headlessly (needs the Avalonia runtime — see
// src/Ui/CLAUDE.md's testing notes), so this composes TechnologyCache + TechEditorViewModel +
// LayoutEditorViewModel exactly as WorkspaceViewModel wires them, mirroring the existing L0d gate-3
// "simulated seam" test in TechEditorDocumentTests.cs. Dialog-gated behavior (the close prompt, the
// Reload Technology confirm) is verified via the same "simulate the production switch" pattern
// already established for WorkspaceViewModel-only logic elsewhere in this test suite.

public class LayoutLiveTechnologyTests
{
    private static string TempPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");

    private static Technology FreshTech() => new()
    {
        Name = "Test Tech",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = 1000,
        DefaultFlattenTolDbu = 1000,
        Layers =
        [
            new LayerDef { Key = new LayerKey(1, 0), Name = "Metal1", Color = new Rgba(200, 100, 50), ZOrder = 1 },
            new LayerDef { Key = new LayerKey(2, 0), Name = "Metal2", Color = new Rgba(50, 100, 200), ZOrder = 2 },
        ],
    };

    /// <summary>Composes the same three real types WorkspaceViewModel wires together, with the
    /// SAME event-subscription bodies (OnTechnologyChanged / OnTechLiveChanged / OnTechSaved).</summary>
    private sealed class Harness : IDisposable
    {
        public string TechPath { get; }
        public TechnologyCache Cache { get; } = new();
        public TechEditorViewModel EditorVm { get; }
        public LayoutEditorViewModel LayoutVm { get; }

        public Harness(Technology initial, LayoutView? layoutModel = null)
        {
            TechPath = TempPath();
            TechPersistence.SaveToFile(TechPath, initial);

            LayoutVm = new LayoutEditorViewModel(layoutModel ?? new LayoutView
            {
                DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
            });
            LayoutVm.ApplyTechResolution(new TechResolution(
                Cache.Get(TechPath), TechPath, TechResolutionSource.WorkspaceDefault, []));

            // Mirrors WorkspaceViewModel.OnTechnologyChanged's body.
            Cache.TechnologyChanged += changedPath =>
            {
                if (!string.Equals(LayoutVm.ResolvedTechPath, changedPath, StringComparison.OrdinalIgnoreCase)) return;
                LayoutVm.ApplyTechResolution(new TechResolution(
                    Cache.Get(changedPath), changedPath, TechResolutionSource.WorkspaceDefault, []));
            };

            EditorVm = new TechEditorViewModel(TechPath, TechPersistence.LoadFromFile(TechPath));
            // Mirrors WorkspaceViewModel.OnTechLiveChanged — applied synchronously here (no
            // dispatcher coalescing needed for a test that isn't racing a UI frame).
            EditorVm.TechLiveChanged += (path, clone) => Cache.SetLive(path, clone);
            // Mirrors WorkspaceViewModel.OnTechSaved's body exactly.
            EditorVm.TechSaved += path => Cache.Invalidate(path);
        }

        public void Dispose() { try { System.IO.File.Delete(TechPath); } catch { } }
    }

    // ── Live propagation ──────────────────────────────────────────────────────

    [Fact]
    public void LiveColorEdit_WithoutSaving_PropagatesToOpenLayoutImmediately()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);
    }

    /// <summary>A reorder in the .ctech editor reorders the open layout's Layers list and its layer picker
    /// immediately, unsaved — the list follows the technology's order (designer round 21).</summary>
    [Fact]
    public void LiveLayerReorder_WithoutSaving_ReordersTheOpenLayoutsLayersList()
    {
        // As on a board: the layer listed first paints on top, so ZOrder runs against the list.
        var tech = FreshTech();
        tech.Layers[0].ZOrder = 20;
        tech.Layers[1].ZOrder = 0;
        using var h = new Harness(tech);
        Assert.Equal(["Metal1", "Metal2"], h.LayoutVm.LayerRows.Select(r => r.Name));

        h.EditorVm.MoveLayer(h.EditorVm.Layers[1], -1);

        Assert.Equal(["Metal2", "Metal1"], h.LayoutVm.LayerRows.Select(r => r.Name));
        Assert.Equal(["Metal2", "Metal1"], h.LayoutVm.AvailableLayers.Select(l => l.Name));
    }

    /// <summary>The seam carries colour and FILL — not visibility, which is the layout's own view
    /// state since brief-layout-layers-panel.md.</summary>
    [Fact]
    public void LiveFillEdit_WithoutSaving_PropagatesImmediately()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.FillOpacity = 0.8;
        h.EditorVm.CommitEdit(before, "Change fill");

        Assert.Equal(0.8, h.LayoutVm.Technology!.Layers[0].FillOpacity);
    }

    // ── R-fix-1: always a deep clone, never Working itself ────────────────────

    [Fact]
    public void MutatingWorkingAfterALiveUpdate_DoesNotAffectTheConsumersClone_UndoDoesUpdateIt()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");
        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);

        // Mutate Working directly, simulating the next in-progress edit before it commits — the
        // consumer's already-installed clone must be unaffected.
        h.EditorVm.Working.Layers[0].Color = new Rgba(1, 1, 1);
        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);

        // Undo REPLACES Working wholesale (ApplySnapshot) — the consumer must receive the restored
        // value, not silently keep the old clone forever.
        h.EditorVm.UndoRedo.Undo();
        Assert.Equal(new Rgba(200, 100, 50), h.LayoutVm.Technology!.Layers[0].Color); // original
    }

    // ── Discard reverts ────────────────────────────────────────────────────────

    [Fact]
    public void DiscardWithoutSaving_ClearLive_RevertsOpenLayoutsToTheOnDiskTechnology()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");
        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);

        h.Cache.ClearLive(h.TechPath); // mirrors ConfirmCloseDockable's "Don't Save" branch

        Assert.Equal(new Rgba(200, 100, 50), h.LayoutVm.Technology!.Layers[0].Color);
    }

    // ── Save clears the override ──────────────────────────────────────────────

    [Fact]
    public void Save_ClearsTheLiveOverride_GetReturnsWhatWasSaved()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));

        h.EditorVm.SaveCommand.Execute(null);

        Assert.False(h.Cache.HasLiveOverride(h.TechPath));
        Assert.Equal(new Rgba(9, 8, 7), h.Cache.Get(h.TechPath)!.Layers[0].Color);
    }

    // ── Units are never re-seeded ──────────────────────────────────────────────

    [Fact]
    public void StreamingSeveralLiveEdits_NeverReSeedsDisplayUnitOrSnapDbu()
    {
        var layoutModel = new LayoutView
        {
            DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mil, SnapDbu = 12_345, AngleMode = AngleMode.AnyAngle,
        };
        // The technology's own defaults deliberately differ from the layout's current state.
        using var h = new Harness(FreshTech(), layoutModel);

        for (int i = 0; i < 5; i++)
        {
            var before = h.EditorVm.SnapshotJson();
            h.EditorVm.Layers[0].Layer.ZOrder = i;
            h.EditorVm.CommitEdit(before, $"Edit {i}");
        }

        Assert.Equal(LayoutUnit.Mil, h.LayoutVm.DisplayUnit);
        Assert.Equal(12_345, h.LayoutVm.Model.SnapDbu);
    }

    /// <summary>Designer round 29: the technology's unit changed while a layout is open on it — a layout still on the
    /// old unit follows, one whose unit was chosen in its own toolbar keeps it.</summary>
    [Theory]
    [InlineData(LayoutUnit.Um, LayoutUnit.Mm)]    // on the technology's unit: follows
    [InlineData(LayoutUnit.Mil, LayoutUnit.Mil)]  // chosen in the layout: kept
    public void TheTechnologysUnitEdited_MovesAnOpenLayoutOnlyWhenItWasStillOnTheOldOne(LayoutUnit layoutUnit, LayoutUnit expected)
    {
        var layoutModel = new LayoutView { DbuPerMicron = 1000, DisplayUnit = layoutUnit, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };
        using var h = new Harness(FreshTech(), layoutModel);   // the technology is in µm

        h.EditorVm.DefaultDisplayUnit = LayoutUnit.Mm;

        Assert.Equal(expected, h.LayoutVm.DisplayUnit);
        Assert.Equal(1000, h.LayoutVm.Model.SnapDbu);
    }

    // ── Close prompts (simulated — SaveChangesDialog needs the Avalonia runtime) ──────────

    // Mirrors WorkspaceViewModel.ConfirmCloseDockable's TechDocument branch exactly.
    private static bool SimulateCloseDirtyTechDocument(
        TechnologyCache cache, string path, TechEditorViewModel vm, SaveChangesResult choice)
    {
        switch (choice)
        {
            case SaveChangesResult.Cancel:
                return false;
            case SaveChangesResult.DontSave:
                cache.ClearLive(path);
                return true;
            case SaveChangesResult.Save:
                vm.SaveCommand.Execute(null);
                return !vm.IsDirty;
            default:
                return false;
        }
    }

    [Fact]
    public void SimulatedClose_Cancel_KeepsTheDocumentOpen_AndKeepsTheLiveOverride()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool canProceed = SimulateCloseDirtyTechDocument(h.Cache, h.TechPath, h.EditorVm, SaveChangesResult.Cancel);

        Assert.False(canProceed);
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));
    }

    [Fact]
    public void SimulatedClose_DontSave_ClearsTheOverride()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool canProceed = SimulateCloseDirtyTechDocument(h.Cache, h.TechPath, h.EditorVm, SaveChangesResult.DontSave);

        Assert.True(canProceed);
        Assert.False(h.Cache.HasLiveOverride(h.TechPath));
    }

    // ── Reload guard (simulated) ───────────────────────────────────────────────

    // Mirrors WorkspaceViewModel.ReloadTechnologyAsync's guard exactly.
    private static bool SimulateReloadTechnology(TechnologyCache cache, string path, SaveChangesResult? choiceIfLiveOverride)
    {
        if (cache.HasLiveOverride(path) && choiceIfLiveOverride != SaveChangesResult.Save)
            return false; // Cancel (or dialog unavailable) — leave the override intact

        cache.Invalidate(path);
        return true;
    }

    [Fact]
    public void SimulatedReload_LiveOverridePresent_CancelLeavesOverrideIntact()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool reloaded = SimulateReloadTechnology(h.Cache, h.TechPath, SaveChangesResult.Cancel);

        Assert.False(reloaded);
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));
    }

    [Fact]
    public void SimulatedReload_LiveOverridePresent_ConfirmedDiscard_ClearsOverride()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool reloaded = SimulateReloadTechnology(h.Cache, h.TechPath, SaveChangesResult.Save); // "Discard" button

        Assert.True(reloaded);
        Assert.False(h.Cache.HasLiveOverride(h.TechPath));
    }

    [Fact]
    public void SimulatedReload_NoLiveOverride_NeverPrompts_ReloadsDirectly()
    {
        using var h = new Harness(FreshTech());

        bool reloaded = SimulateReloadTechnology(h.Cache, h.TechPath, choiceIfLiveOverride: null);

        Assert.True(reloaded);
    }
}
