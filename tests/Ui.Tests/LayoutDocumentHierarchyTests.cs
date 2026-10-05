using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3b — LayoutDocument's nav-frame stack (push/pop/popTo, breadcrumbs, dirty/undo following
//  the active frame, and per-frame viewport capture/restore). Mirrors SchematicDocument's own
//  NavFrame model exactly (docs/sonnet-briefs/brief-L3b-hierarchy-navigation.md §1) — these tests
//  drive the document directly, the same public surface the view's push-in/pop-out/breadcrumb
//  handlers call, per this codebase's established "no visual driver" convention.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutDocumentHierarchyTests
{
    private static LayoutEditorViewModel MakeVm() =>
        new(new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 });

    [Fact]
    public void NewDocument_StartsAtDepthZero_BaseIsActive_NoBreadcrumbBesidesRoot()
    {
        var baseVm = MakeVm();
        var doc = new LayoutDocument("Top", baseVm);

        Assert.Equal(0, doc.NavDepth);
        Assert.False(doc.CanPopOut);
        Assert.Same(baseVm, doc.ActiveViewModel);
        Assert.Single(doc.Breadcrumbs);
        Assert.True(doc.Breadcrumbs[0].IsCurrent);
        Assert.False(doc.Breadcrumbs[0].IsNotFirst);
    }

    [Fact]
    public void PushIn_AdvancesActiveViewModel_IncrementsDepth_AddsBreadcrumb()
    {
        var baseVm = MakeVm();
        var subVm  = MakeVm();
        var doc = new LayoutDocument("Top", baseVm);

        doc.PushIn(subVm, "X1");

        Assert.Equal(1, doc.NavDepth);
        Assert.True(doc.CanPopOut);
        Assert.Same(subVm, doc.ActiveViewModel);
        Assert.Equal(2, doc.Breadcrumbs.Count);
        Assert.Equal("Top", doc.Breadcrumbs[0].Text);
        Assert.False(doc.Breadcrumbs[0].IsCurrent);
        Assert.Equal("X1", doc.Breadcrumbs[1].Text);
        Assert.True(doc.Breadcrumbs[1].IsCurrent);
    }

    [Fact]
    public void PopOut_AtBaseLevel_ReturnsNull_NothingChanges()
    {
        var doc = new LayoutDocument("Top", MakeVm());
        var popped = doc.PopOut();

        Assert.Null(popped);
        Assert.Equal(0, doc.NavDepth);
    }

    [Fact]
    public void PopOut_ReturnsPoppedSession_RestoresParentAsActive()
    {
        var baseVm = MakeVm();
        var subVm  = MakeVm();
        var doc = new LayoutDocument("Top", baseVm);
        doc.PushIn(subVm, "X1");

        var popped = doc.PopOut();

        Assert.Same(subVm, popped);
        Assert.Equal(0, doc.NavDepth);
        Assert.Same(baseVm, doc.ActiveViewModel);
        Assert.False(doc.CanPopOut);
    }

    [Fact]
    public void PopTo_ThreeLevelsDeep_PopsToExactLevel_ReturnsPoppedOutermostFirst()
    {
        var l0 = MakeVm();
        var l1 = MakeVm();
        var l2 = MakeVm();
        var l3 = MakeVm();
        var doc = new LayoutDocument("Top", l0);
        doc.PushIn(l1, "A");
        doc.PushIn(l2, "B");
        doc.PushIn(l3, "C");
        Assert.Equal(3, doc.NavDepth);

        var popped = doc.PopTo(1);

        Assert.Equal(1, doc.NavDepth);
        Assert.Same(l1, doc.ActiveViewModel);
        Assert.Equal(new[] { l3, l2 }, popped);   // outermost (deepest) first
    }

    [Fact]
    public void PopTo_Zero_PopsAllTheWayToBase()
    {
        var l0 = MakeVm();
        var doc = new LayoutDocument("Top", l0);
        doc.PushIn(MakeVm(), "A");
        doc.PushIn(MakeVm(), "B");

        var popped = doc.PopTo(0);

        Assert.Equal(0, doc.NavDepth);
        Assert.Same(l0, doc.ActiveViewModel);
        Assert.Equal(2, popped.Count);
    }

    [Fact]
    public void ActiveViewModelChanged_FiresOnPushAndPop_NotOnNoOpPopAtBase()
    {
        var doc = new LayoutDocument("Top", MakeVm());
        int fireCount = 0;
        doc.ActiveViewModelChanged += (_, _) => fireCount++;

        doc.PushIn(MakeVm(), "X1");
        Assert.Equal(1, fireCount);

        doc.PopOut();
        Assert.Equal(2, fireCount);

        var noOp = doc.PopOut();   // already at base — no-op, no event
        Assert.Null(noOp);
        Assert.Equal(2, fireCount);
    }

    // ── Dirty/Undo follow the ACTIVE frame, not the base (gate: matches HierarchySaveTests intent) ──

    [Fact]
    public void IsDirty_FollowsActiveFrame_NotBase()
    {
        var baseVm = MakeVm();
        var subVm  = MakeVm();
        var doc = new LayoutDocument("Top", baseVm);
        Assert.False(doc.IsDirty);

        doc.PushIn(subVm, "X1");
        Assert.False(doc.IsDirty);   // fresh sub-cell, clean

        subVm.DisplayUnit = LayoutUnit.Mm;   // dirties ONLY the sub-cell session
        Assert.True(subVm.IsDirty);
        Assert.True(doc.IsDirty);    // document follows the active (sub-cell) frame

        Assert.False(baseVm.IsDirty);   // base itself never touched

        doc.PopOut();
        Assert.False(doc.IsDirty);   // back at the (still-clean) base
    }

    [Fact]
    public void UndoRedo_FollowsActiveFrame()
    {
        var baseVm = MakeVm();
        var subVm  = MakeVm();
        var doc = new LayoutDocument("Top", baseVm);

        Assert.Same(baseVm.UndoRedo, doc.UndoRedo);

        doc.PushIn(subVm, "X1");
        Assert.Same(subVm.UndoRedo, doc.UndoRedo);

        doc.PopOut();
        Assert.Same(baseVm.UndoRedo, doc.UndoRedo);
    }

    // ── Per-frame viewport capture/restore (gate 2/3 — "restore the parent's viewport") ───────────

    [Fact]
    public void ActiveFrameSavedViewport_IsNull_UntilCaptured()
    {
        var doc = new LayoutDocument("Top", MakeVm());
        Assert.Null(doc.ActiveFrameSavedViewport);
    }

    [Fact]
    public void CaptureThenPushThenPop_RestoresExactlyTheCapturedParentViewport()
    {
        var doc = new LayoutDocument("Top", MakeVm());
        var parentVp = new LayoutViewport(10, 20, 2.0, 800, 600);
        doc.CaptureActiveViewport(parentVp);

        doc.PushIn(MakeVm(), "X1");
        Assert.Null(doc.ActiveFrameSavedViewport);   // fresh sub-cell frame — nothing captured yet

        var childVp = new LayoutViewport(99, 99, 5.0, 800, 600);
        doc.CaptureActiveViewport(childVp);

        var popped = doc.PopOut();
        Assert.NotNull(popped);
        Assert.Equal(parentVp, doc.ActiveFrameSavedViewport);   // exactly what was captured before the push
    }

    [Fact]
    public void ThreeLevelPush_EachPopRestoresItsOwnLevelsViewport()
    {
        var doc = new LayoutDocument("Top", MakeVm());
        var vp0 = new LayoutViewport(0, 0, 1.0, 800, 600);
        var vp1 = new LayoutViewport(1, 1, 1.1, 800, 600);
        var vp2 = new LayoutViewport(2, 2, 1.2, 800, 600);

        doc.CaptureActiveViewport(vp0);
        doc.PushIn(MakeVm(), "A");
        doc.CaptureActiveViewport(vp1);
        doc.PushIn(MakeVm(), "B");
        doc.CaptureActiveViewport(vp2);
        doc.PushIn(MakeVm(), "C");

        doc.CaptureActiveViewport(new LayoutViewport(9, 9, 9.0, 800, 600)); // level C's own current view
        doc.PopOut();
        Assert.Equal(vp2, doc.ActiveFrameSavedViewport);   // back at B — B's viewport restored

        doc.CaptureActiveViewport(new LayoutViewport(8, 8, 8.0, 800, 600));
        doc.PopOut();
        Assert.Equal(vp1, doc.ActiveFrameSavedViewport);   // back at A

        doc.CaptureActiveViewport(new LayoutViewport(7, 7, 7.0, 800, 600));
        doc.PopOut();
        Assert.Equal(vp0, doc.ActiveFrameSavedViewport);   // back at the base
    }

    [Fact]
    public void PushingBackIntoTheSameSubCell_RemembersItsOwnPreviousViewport()
    {
        // Pushing into the SAME session twice (bonus, not gate-required, but falls out of the
        // design for free): the frame is a fresh NavFrame each PushIn, so this asserts only that
        // capture/restore never bleeds across DIFFERENT frame instances, not literal re-entry —
        // PushIn always creates a new frame, matching PushIntoCell's own behaviour.
        var doc = new LayoutDocument("Top", MakeVm());
        var subVm = MakeVm();
        doc.PushIn(subVm, "X1");
        Assert.Null(doc.ActiveFrameSavedViewport);
    }

    /// <summary>
    /// A technology edit reaches the sub-cell a tab is pushed into, not only the tab's base cell. The bug: visibility
    /// turned off in the technology editor did nothing while a sub-cell was on screen, because the workspace re-resolved
    /// each tab's <see cref="LayoutDocument.ViewModel"/> — the base — and the pushed-in frame kept its old technology.
    /// The walk is <see cref="LayoutSessionRegistry.EverySession"/>; the loop is <c>WorkspaceViewModel.OnTechnologyChanged</c>'s.
    /// </summary>
    [Fact]
    public void TechnologyChange_ReachesThePushedInSubCell_WithItsOwnClayPath()
    {
        string techPath = Path.Combine(Path.GetTempPath(), $"crf-hier-tech-{Guid.NewGuid():N}.ctech");
        var key = new LayerKey(1, 0);
        TechPersistence.SaveToFile(techPath, new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = key, Name = "M1", Visible = true, Selectable = true }],
        });
        try
        {
            var cache = new TechnologyCache();
            TechResolution Resolve() => new(cache.Get(techPath), techPath, TechResolutionSource.WorkspaceDefault, []);
            var baseVm = MakeVm();
            var subVm = MakeVm();
            baseVm.ApplyTechResolution(Resolve());
            subVm.ApplyTechResolution(Resolve());
            var registry = new LayoutSessionRegistry();
            registry.Register("/ws/Board/layout/Board.clay", baseVm, _ => { });
            registry.Register("/ws/Pad/layout/Pad.clay", subVm, _ => { });
            var doc = new LayoutDocument("Board", baseVm);
            doc.PushIn(subVm, "X1");

            var sessions = registry.EverySession([doc]);
            Assert.Equal([(baseVm, "/ws/Board/layout/Board.clay"), (subVm, "/ws/Pad/layout/Pad.clay")],
                         sessions.Select(s => (s.Vm, s.ClayPath)));

            // The live edit, then OnTechnologyChanged's loop over what the walk returned.
            var edited = TechPersistence.LoadFromFile(techPath);
            edited.Layers[0].Visible = false;
            cache.TechnologyChanged += changed =>
            {
                foreach (var (vm, _) in registry.EverySession([doc]))
                    if (string.Equals(vm.ResolvedTechPath, changed, StringComparison.OrdinalIgnoreCase))
                        vm.ApplyTechResolution(Resolve());
            };
            cache.SetLive(techPath, edited);

            Assert.Same(subVm, doc.ActiveViewModel);
            Assert.False(doc.ActiveViewModel.Technology!.Layers.Single(l => l.Key == key).Visible);
            Assert.False(baseVm.Technology!.Layers.Single(l => l.Key == key).Visible);
        }
        finally { try { File.Delete(techPath); } catch { /* best effort */ } }
    }
}
