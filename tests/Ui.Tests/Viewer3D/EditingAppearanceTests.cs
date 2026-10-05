// brief-em3d-108 — editing appearance, with live feedback. Gate 1 the swatch (deterministic, a metal is not a dielectric, roughness
// lowers the peak); 2 the Materials dialog's preview (the table changes, nothing is elaborated, tessellated or uploaded as geometry, and
// Cancel puts the table back byte for byte); 3 the display-only reload (an Appearance or a Color edit re-elaborates nothing, a Color
// edit patches vertex colours, and a run before it stays current); 4 a physical edit still takes today's path; 5 the Inspector's group
// (one undo entry over two objects, Clear override, Like for plating); 6 a greyed field's tooltip is the resolver's provenance; 7 the
// Look panel (a rotation drag is uniforms and one undo entry; an environment is prefiltered once and cached); 8 the Show boxes are the
// chrome table's; 9 the picture camera. UI behaviour through the view models, with no window. Pixels were not seen.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Appearance;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class EditingAppearanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-look108-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public EditingAppearanceTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. the swatch ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TheSwatch_IsDeterministic_AMetalIsNotADielectric_AndRoughnessLowersThePeak()
    {
        var env = EnvironmentPrefilter.Studio(C3dStudio.Studio);
        var gold = AppearanceColour.FromSrgb(0xFF, 0xE3, 0x9D);
        AppearanceValues Look(double metallic, double roughness)
            => new(gold, metallic, roughness, 0, 1.5, 0, 0, AppearanceColour.White, double.PositiveInfinity);

        long before = AppearanceSwatch.Evaluations;
        var once = AppearanceSwatch.Shade(Look(1, 0.25), env);
        long one = AppearanceSwatch.Evaluations - before;
        Assert.InRange(one, 1, AppearanceSwatch.Size * AppearanceSwatch.Size);         // a counter, never a timing
        var twice = AppearanceSwatch.Shade(Look(1, 0.25), env);
        var elsewhere = Task.Run(() => AppearanceSwatch.Shade(Look(1, 0.25), env)).Result;
        Assert.Equal(once, twice);
        Assert.Equal(once, elsewhere);

        var dielectric = AppearanceSwatch.Shade(Look(0, 0.25), env);
        Assert.True(once.Zip(dielectric).Max(p => Vector4.Distance(p.First, p.Second)) > 0.1f, "a metal's swatch should differ from a dielectric's");

        float Peak(double roughness) => AppearanceSwatch.Shade(Look(1, roughness), env).Where(p => p.W >= 1).Max(p => MathF.Max(p.X, MathF.Max(p.Y, p.Z)));
        float[] peaks = [Peak(0), Peak(0.25), Peak(0.5), Peak(1)];
        for (int k = 1; k < peaks.Length; k++)
            Assert.True(peaks[k] < peaks[k - 1], $"the brightest pixel should fall with roughness: {string.Join(", ", peaks)}");
    }

    // ── 2. the Materials dialog's live preview ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ADialogRoughnessEdit_ChangesOnlyTheTable_AndCancelRestoresItByteForByte()
    {
        var (vm, fake, techPath, _) = Open();
        Frame(vm);
        float[] table = Pbr.Table(vm.Viewer.Scene.Appearances);
        var adopted = vm.Viewer.Scene;
        long tess = vm.TessellationMisses, lowered = vm.Elaborations, requested = vm.Viewer.Source.Requested;
        long statics = Scene3DBuilder.Tessellations;
        int uploads = fake.SceneUploads;
        long patchBytes = fake.PatchBytes;

        var tech = TechPersistence.LoadFromFile(techPath);
        var picker = new MaterialPickerViewModel([new MaterialSourceSeed("tech.ctech (the technology's own)", null, tech.Materials, null)],
                                                 "tech.ctech", false, "Copper", 0, null);
        picker.AppearancePreview += (_, material, appearance) => vm.Viewer.PreviewAppearance(material, appearance);
        var roughness = picker.Table.Rows.Single(r => r.Name == "Copper").Appearance.Field(nameof(TechAppearance.Roughness));
        roughness.Slider = 0.05;                                            // a drag: previewed
        Frame(vm);
        Assert.NotEqual(table, Pbr.Table(vm.Viewer.Scene.Appearances));
        Assert.Contains(vm.Viewer.Scene.Appearances, a => Math.Abs(a.Roughness - 0.05) < 1e-12);
        roughness.CommitSlider();                                           // written to the dialog's copy, still previewed
        Frame(vm);
        Assert.Contains(vm.Viewer.Scene.Appearances, a => Math.Abs(a.Roughness - 0.05) < 1e-12);

        Assert.Equal((tess, lowered, requested), (vm.TessellationMisses, vm.Elaborations, vm.Viewer.Source.Requested));
        Assert.Equal(statics, Scene3DBuilder.Tessellations);
        Assert.Equal(uploads, fake.SceneUploads);
        Assert.Equal(patchBytes, fake.PatchBytes);                          // geometry bytes: none (the shade stream may be patched)
        Assert.True(vm.Viewer.AppearanceRestyles >= 2);

        vm.Viewer.EndAppearancePreview();                                   // Cancel
        Frame(vm);
        Assert.Same(adopted, vm.Viewer.Scene);
        Assert.Equal(table, Pbr.Table(vm.Viewer.Scene.Appearances));
        Assert.False(vm.Viewer.IsPreviewingAppearance);
    }

    // ── 3. the display-only reload ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AnAppearanceOrColourEdit_ReloadsWithNoElaboration_AColourPatchesVertices_AndARunStaysCurrent()
    {
        var (vm, fake, techPath, cache) = Open();
        Frame(vm);
        // a run taken before the edit, as Simulate keeps one
        var e0 = C3dElaborator.ElaborateOnce(vm.Document, vm.FilePath, Path.Combine(_root, "ws", ".cws"));
        string run = Path.Combine(_root, "ws", "results", "run");
        C3dRunInputs.Take(vm.Document, vm.FilePath, e0.FilesRead).KeepIn(run);
        Assert.False(C3dRunDocument.Check(run, vm.Document, vm.FilePath)!.Stale);

        long tess = vm.TessellationMisses, lowered = vm.Elaborations;
        int uploads = fake.SceneUploads;
        Edit(cache, techPath, t => t.Materials.Single(m => m.Name == "Copper").Appearance = new TechAppearance { Roughness = 0.1 });
        vm.OnTechnologyChanged(techPath);
        Settle(vm);
        Frame(vm);
        Assert.Equal(1, vm.DisplayRebuilds);
        Assert.Contains(vm.Viewer.Scene.Appearances, a => Math.Abs(a.Roughness - 0.1) < 1e-12);
        Assert.Equal((tess, lowered, uploads), (vm.TessellationMisses, vm.Elaborations, fake.SceneUploads));

        long patched = fake.PatchBytes;
        Edit(cache, techPath, t => t.Materials.Single(m => m.Name == "Copper").Color = "#FF0000");
        vm.OnTechnologyChanged(techPath);
        Settle(vm);
        Frame(vm);
        Assert.Equal(2, vm.DisplayRebuilds);
        Assert.Equal((tess, lowered, uploads), (vm.TessellationMisses, vm.Elaborations, fake.SceneUploads));
        var a = vm.SceneObject("a")!;
        uint rgba = vm.Viewer.Scene.Vertices[a.FirstVertex].Rgba;
        Assert.Equal((255u, 0u, 0u), (rgba & 0xFF, (rgba >> 8) & 0xFF, (rgba >> 16) & 0xFF));
        // the two copper boxes' vertices and their feature edges (drawn in their colour), recoloured; nothing else
        var copper = new[] { a.Id, vm.SceneObject("b")!.Id };
        long copperBytes = (long)(a.VertexCount + vm.SceneObject("b")!.VertexCount) * Scene3DVertex.Stride
                         + vm.Viewer.Scene.EdgeBatches.Where(e => copper.Contains(e.ObjectId)).Sum(e => (long)e.VertexCount * Scene3DVertex.Stride);
        Assert.Equal(copperBytes, fake.PatchBytes - patched);

        // a library two technologies name raises one cue per technology: the second, while the first's rebuild is on its way, is
        // measured against that rebuild; and a technology this view never read changes nothing here
        long requested = vm.Viewer.Source.Requested;
        Edit(cache, techPath, t => t.Materials.Single(m => m.Name == "Copper").Appearance = new TechAppearance { Roughness = 0.2 });
        vm.OnTechnologyChanged(techPath);
        vm.OnTechnologyChanged(techPath);
        vm.OnTechnologyChanged(Path.Combine(_root, "ws", "other.ctech"));
        Assert.Equal(requested + 2, vm.Viewer.Source.Requested);
        Settle(vm);
        Assert.Equal(4, vm.DisplayRebuilds);
        Assert.Equal((tess, lowered), (vm.TessellationMisses, vm.Elaborations));

        // the edit saved, as the technology editor's Save writes it: a run before it is still current (brief 105 §5, D18)
        TechPersistence.SaveToFile(techPath, cache.Get(techPath)!);
        Assert.False(C3dRunDocument.Check(run, vm.Document, vm.FilePath)!.Stale);
    }

    // ── 4. a physical edit still takes today's path ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AnEpsrEdit_StillElaborates()
    {
        var (vm, _, techPath, cache) = Open();
        long lowered = vm.Elaborations, requested = vm.Viewer.Source.Requested;
        Edit(cache, techPath, t => t.Materials.Single(m => m.Name == "FR4").Epsr = 3.5);
        vm.OnTechnologyChanged(techPath);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var act)) act();
            return vm.Viewer.Source.Requested > requested && vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the physical edit never re-elaborated");
        Assert.Equal(0, vm.DisplayRebuilds);
        Assert.True(vm.Elaborations > lowered, "an εr edit must elaborate again");
    }

    // ── 5. the Inspector ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_TwoObjects_OneUndoEntry_UndoRemovesBoth_ClearOverride_AndLikeGoldOnCopper()
    {
        var (vm, _, _, _) = Open();
        string forRun = C3dPersistence.SerializeForRun(vm.Document);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id), Scene3DItem.OfObject(vm.SceneObject("b")!.Id)]);
        var group = Assert.IsType<AppearanceEditorViewModel>(vm.Properties.Appearance);
        var roughness = group.Field(nameof(TechAppearance.Roughness));
        int entries = vm.UndoEntries, previews = vm.AppearancePreviews;
        roughness.Slider = 0.15;
        roughness.Slider = 0.1;                                             // a drag: two previews, nothing written
        Assert.Equal(previews + 2, vm.AppearancePreviews);
        Assert.Null(vm.Document.Objects.Single(o => o.Name == "a").Appearance);
        roughness.CommitSlider();                                           // the release
        Assert.Equal(entries + 1, vm.UndoEntries);
        foreach (string name in new[] { "a", "b" })
        {
            var obj = vm.Document.Objects.Single(o => o.Name == name);
            Assert.Equal(0.1, obj.Appearance!.Roughness);
            Assert.Contains("\"Appearance\": {\n", C3dPersistence.SerializeObject(obj).Replace("\r\n", "\n"));
            Assert.Contains("\"Roughness\": 0.1", C3dPersistence.SerializeObject(obj));
        }
        Assert.Equal(forRun, C3dPersistence.SerializeForRun(vm.Document));
        vm.UndoRedo.Undo();
        Assert.All(vm.Document.Objects.Where(o => o.Name is "a" or "b"), o => Assert.Null(o.Appearance));
        Assert.Equal(forRun, C3dPersistence.SerializeForRun(vm.Document));
        Settle(vm);

        // Clear override, then Like Gold on a copper box: it resolves Gold's base colour.
        int a = vm.Document.Objects.FindIndex(o => o.Name == "a");
        Assert.Null(vm.SetAppearance([a], [], nameof(TechAppearance.Roughness), 0.2, "Roughness"));
        Assert.NotNull(vm.Document.Objects[a].Appearance);
        Assert.Null(vm.ClearAppearance([a], [], "Clear"));
        Assert.Null(vm.Document.Objects[a].Appearance);
        Assert.DoesNotContain("Appearance", C3dPersistence.SerializeObject(vm.Document.Objects[a]));
        Settle(vm);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id)]);
        vm.Properties.Appearance!.Like = "Gold";
        Assert.Equal("Gold", vm.Document.Objects[a].Appearance!.Like);
        Settle(vm);
        Assert.Equal(AppearanceColour.FromSrgb(0xFF, 0xE3, 0x9D), vm.Viewer.AppearanceOf(vm.SceneObject("a")!)!.Values.BaseColor);
        Assert.Equal(forRun, C3dPersistence.SerializeForRun(vm.Document));
    }

    // ── 6. provenance, shown ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_AnInheritedFieldsTooltip_IsTheResolversProvenance()
    {
        var (vm, _, techPath, _) = Open(copperLikeGold: true);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id)]);
        var resolved = vm.Viewer.AppearanceOf(vm.SceneObject("a")!)!;
        var group = vm.Properties.Appearance!;
        foreach (var f in group.Fields)
        {
            Assert.False(f.IsStated);
            Assert.Equal(resolved.Provenance[f.Key], f.Provenance);
        }
        Assert.Equal("Like 'Gold'", group.Field(nameof(TechAppearance.Roughness)).Provenance);

        // and in the Materials editor, on the material's row
        var tech = TechPersistence.LoadFromFile(techPath);
        var picker = new MaterialPickerViewModel([new MaterialSourceSeed("tech.ctech (the technology's own)", null, tech.Materials, null)],
                                                 "tech.ctech", false, "Copper", 0, null);
        var row = picker.Table.Rows.Single(r => r.Name == "Copper");
        Assert.Equal(row.ResolvedLook.Provenance[nameof(TechAppearance.Roughness)], row.Appearance.Field(nameof(TechAppearance.Roughness)).Provenance);
        Assert.Equal("Like 'Gold'", row.Appearance.Field(nameof(TechAppearance.Roughness)).Provenance);
    }

    /// <summary>A colour picked in the Inspector: previewed, written once when the picker closes, and undone by one Undo. The view's
    /// Closed handler must not look the field up through DataContext — Avalonia detaches the popup before raising Closed, so it found
    /// nothing, nothing was written, and Undo had nothing to undo (the field is held from Opened instead).</summary>
    [Fact]
    public void Inspector_ABaseColourPicked_IsOneEntry_AndUndoPutsItBack()
    {
        var (vm, _, _, _) = Open();
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("a")!.Id)]);
        var colour = vm.Properties.Appearance!.Field(nameof(TechAppearance.BaseColor));
        int entries = vm.UndoEntries;
        colour.BeginColour();
        colour.PreviewColour(Avalonia.Media.Color.FromRgb(0x12, 0x34, 0x56));
        Assert.Equal(entries, vm.UndoEntries);                              // previewed only
        colour.CommitColour();
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal("#123456", vm.Document.Objects.Single(o => o.Name == "a").Appearance!.BaseColor);
        vm.UndoRedo.Undo();
        Assert.Null(vm.Document.Objects.Single(o => o.Name == "a").Appearance);

        string view = File.ReadAllText(Path.Combine(RepoRootOf(), "src", "Ui", "Views", "Appearance", "AppearanceEditorView.axaml.cs"));
        int at = view.IndexOf("private void OnColourFlyoutClosed", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = view[at..view.IndexOf('}', at)];
        Assert.DoesNotContain("DataContext", body);
    }

    private static string RepoRootOf()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ── 7. the Look panel ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_ARotationDragIsTenUniformWrites_NoGeometry_OneUndoEntry_AndAnEnvironmentIsPrefilteredOnce()
    {
        var (vm, fake, _, _) = Open();
        var panel = vm.LookPanel;
        Assert.False(vm.Viewer.IsRealistic);
        panel.Opened();                                                     // D2: opening turns the realistic view on
        Assert.True(vm.Viewer.IsRealistic);
        Pump(() => vm.Viewer.View.Environment is not null);
        Frame(vm);
        Frame(vm);
        int writes = vm.Viewer.LookWrites, entries = vm.UndoEntries, uploads = fake.SceneUploads, patches = fake.Patches;
        long bytes = fake.Counters.UploadBytesTotal;
        for (int k = 1; k <= 10; k++)
        {
            panel.Rotation = 30 + 7 * k;
            Frame(vm);
        }
        Assert.Equal(writes + 10, vm.Viewer.LookWrites);
        Assert.Equal((uploads, patches, bytes), (fake.SceneUploads, fake.Patches, fake.Counters.UploadBytesTotal));
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Null(vm.Document.Look);                                     // previewed, not written
        panel.CommitDrag();
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(100, vm.Document.Look!.Rotation);
        Assert.True(vm.IsDirty);

        // an environment: prefiltered once, off the UI thread, the old one drawn meanwhile; switching back costs nothing
        EnvironmentPrefilter.Studio(C3dStudio.Studio);
        string hdr = Path.Combine(_root, "ws", "Cell", "3d", "sky-" + Guid.NewGuid().ToString("N")[..6] + ".hdr");
        File.WriteAllBytes(hdr, RadianceHdr.Encode(new RadianceImage(8, 4, [.. Enumerable.Range(0, 8 * 4 * 3).Select(i => 0.3f + (i % 5) * 0.2f)])));
        long runs = EnvironmentPrefilter.Runs;
        var studio = vm.Viewer.View.Environment;
        panel.LoadHdr(hdr);
        Assert.Equal(Path.GetFileName(hdr), vm.Document.Look!.Environment);  // stored relative to the .c3d
        Assert.Same(studio, vm.Viewer.View.Environment);                     // kept until the new one is ready
        Pump(() => vm.Viewer.View.Environment?.Label == Path.GetFileName(hdr));
        Assert.Equal(runs + 1, EnvironmentPrefilter.Runs);
        panel.Environment = panel.EnvironmentChoices[0];                    // back to Studio
        Pump(() => vm.Viewer.View.Environment?.Label == "Studio");
        panel.LoadHdr(hdr);
        Pump(() => vm.Viewer.View.Environment?.Label == Path.GetFileName(hdr));
        Assert.Equal(runs + 1, EnvironmentPrefilter.Runs);
    }

    /// <summary>A number row's ×, as the appearance editor's: back to "not stated" — the default — as one undo entry, and
    /// unavailable while the key is already not stated.</summary>
    [Fact]
    public void LookPanel_TheResetX_PutsANumberBackToItsDefault_AsOneEntry()
    {
        var (vm, _, _, _) = Open();
        var panel = vm.LookPanel;
        panel.Opened();
        Assert.False(panel.ResetNumberCommand.CanExecute(nameof(C3dLook.Exposure)));
        panel.ExposureText = "2.5";
        panel.CommitText(nameof(C3dLook.Exposure));
        Assert.Equal(2.5, vm.Document.Look!.Exposure);
        Assert.True(panel.ResetNumberCommand.CanExecute(nameof(C3dLook.Exposure)));
        int entries = vm.UndoEntries;
        panel.ResetNumberCommand.Execute(nameof(C3dLook.Exposure));
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Null(vm.Document.Look?.Exposure);
        Assert.Equal(C3dLook.DefaultExposure, panel.Exposure);
        Assert.False(panel.ResetNumberCommand.CanExecute(nameof(C3dLook.Exposure)));
    }

    /// <summary>Picking an environment writes the Look and reloads the panel while the ComboBox is still inside its selection change:
    /// the list must keep its INSTANCES and the selection must be one of them, or Avalonia drops it and the combo shows nothing.</summary>
    [Fact]
    public void LookPanel_PickingAnEnvironment_KeepsTheListsInstances_AndSelectsOneOfThem()
    {
        var (vm, _, _, _) = Open();
        var panel = vm.LookPanel;
        panel.Opened();
        var before = panel.EnvironmentChoices.ToList();
        panel.Environment = before[2];                                      // Dark
        Assert.Equal(nameof(C3dStudio.Dark), vm.Document.Look!.Environment);
        Assert.Equal(before, panel.EnvironmentChoices, ReferenceEqualityComparer.Instance);
        Assert.Same(before[2], panel.Environment);
    }

    /// <summary>A background colour is picked INSIDE the panel (a flyout opened from the panel's own flyout was clipped to it and had
    /// no visible way to finish): previewed while it moves, one entry when the other swatch is taken, Done is pressed or the panel
    /// closes. And L in the view toggles the realistic view.</summary>
    [Fact]
    public void LookPanel_ABackgroundColourIsPickedInline_EachFinishIsOneEntry_AndLTogglesTheRealisticView()
    {
        var (vm, _, _, _) = Open();
        var panel = vm.LookPanel;
        panel.Opened();
        panel.BackgroundKind = "Gradient";
        int entries = vm.UndoEntries;
        panel.EditBackgroundCommand.Execute("bottom");
        Assert.True(panel.IsEditingBackground);
        panel.PreviewBackground(top: false, Avalonia.Media.Color.FromRgb(0x10, 0x20, 0x30));
        Assert.Equal(entries, vm.UndoEntries);                              // previewed, not written
        panel.EditBackgroundCommand.Execute("top");                         // the other swatch keeps the first end's colour
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal("top", panel.BackgroundEditing);
        panel.PreviewBackground(top: true, Avalonia.Media.Color.FromRgb(0x40, 0x50, 0x60));
        panel.Closed();                                                     // closing the panel keeps it too
        Assert.False(panel.IsEditingBackground);
        Assert.Equal(entries + 2, vm.UndoEntries);
        Assert.Equal("#405060,#102030", vm.Document.Look!.Background);

        bool on = vm.Viewer.IsRealistic;
        Assert.True(vm.Viewer.HandleKey(Avalonia.Input.Key.L, Avalonia.Input.KeyModifiers.None, gestureInProgress: false));
        Assert.NotEqual(on, vm.Viewer.IsRealistic);
    }

    // ── 8. the Show boxes ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_OneShowBoxPerRowOfTheChromeTable_GeneratedFromIt_EachOneUndoEntry()
    {
        var (vm, _, _, _) = Open();
        Assert.Equal(RealisticLook.Chrome.Select(c => c.Key), vm.LookPanel.ShowRows.Select(r => r.Key));
        var stub = new C3dLookPanelViewModel(vm, [.. RealisticLook.Chrome.Select(c => c.Key), "ShowWidgets"]);
        Assert.Equal(RealisticLook.Chrome.Count + 1, stub.ShowRows.Count);
        Assert.Equal("Widgets", stub.ShowRows[^1].Label);
        Assert.Equal("Air box", stub.ShowRows.Single(r => r.Key == nameof(C3dLook.ShowAirBox)).Label);

        int entries = vm.UndoEntries;
        vm.LookPanel.ShowRows.Single(r => r.Key == nameof(C3dLook.ShowAirBox)).IsChecked = true;
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.True(vm.Document.Look!.ShowAirBox);
        Assert.True(vm.Viewer.View.Look.Shows(Scene3DChrome.AirBox) || !vm.Viewer.IsRealistic);
        vm.UndoRedo.Undo();
        Assert.Null(vm.Document.Look);
        Assert.False(vm.LookPanel.ShowRows.Single(r => r.Key == nameof(C3dLook.ShowAirBox)).IsChecked);
        // a key this build does not read is kept in the file, as an unread key always is
        stub.ShowRows[^1].IsChecked = true;
        Assert.Contains("\"ShowWidgets\": true", C3dPersistence.SerializeLook(vm.Document.Look));
    }

    // ── 9. the picture camera ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_UseThisViewWritesOnce_OrbitingNeverWrites_GoToRestores_ClearRemoves_AndNoRunSeesIt()
    {
        var (vm, _, _, _) = Open();
        vm.Viewer.View.Camera.Orbit(40, -25);
        var wanted = vm.Viewer.View.Camera;
        int entries = vm.UndoEntries;
        Assert.True(vm.UseViewForPictures());
        Assert.Equal(entries + 1, vm.UndoEntries);
        string saved = C3dPersistence.SerializeLook(vm.Document.Look);
        Assert.Contains("\"Camera\"", saved);
        Assert.Empty(C3dValidation.Validate(vm.Document, documentPath: vm.FilePath).Where(d => d.Id == "c3d.look.camera"));

        for (int k = 0; k < 10; k++) vm.Viewer.View.Camera.Orbit(13, 7);    // ten orbit steps
        vm.Viewer.View.Camera.Distance *= 1.7f;
        Assert.Equal(saved, C3dPersistence.SerializeLook(vm.Document.Look));
        Assert.Equal(entries + 1, vm.UndoEntries);

        Assert.True(vm.GoToPictureView());
        var c = vm.Viewer.View.Camera;
        float scale = MathF.Max(wanted.Distance, 1e-9f);
        Assert.True(Vector3.Distance(c.Target, wanted.Target) <= 1e-6f * scale + 1e-12f, $"{c.Target} vs {wanted.Target}");
        Assert.True(Vector3.Distance(c.Back, wanted.Back) <= 1e-6f, $"{c.Back} vs {wanted.Back}");
        Assert.True(MathF.Abs(c.Distance - wanted.Distance) <= 1e-6f * scale);
        Assert.True(MathF.Abs(c.FovY - wanted.FovY) <= 1e-6f);
        Assert.Equal(wanted.Projection, c.Projection);
        Assert.Equal(entries + 1, vm.UndoEntries);                          // going there writes nothing

        Assert.DoesNotContain("Camera", C3dPersistence.SerializeForRun(vm.Document));
        Assert.True(vm.ClearPictureView());
        Assert.Null(vm.Document.Look?.Camera);
        Assert.Equal(entries + 2, vm.UndoEntries);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two copper boxes (a, b) on an FR4 slab, in a workspace whose technology states Copper, Gold (with an appearance) and
    /// FR4, opened with a technology cache as the workspace opens one.</summary>
    private (C3dEditorViewModel Vm, PatchRecordingBackend Fake, string TechPath, TechnologyCache Cache) Open(bool copperLikeGold = false)
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        string techPath = Path.Combine(ws, "tech.ctech");
        TechPersistence.SaveToFile(techPath, new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, Appearance = copperLikeGold ? new TechAppearance { Like = "Gold" } : null },
                new TechMaterial { Name = "Gold", Sigma20 = 4.1e7, Appearance = new TechAppearance { BaseColor = "#FFE39D", Metallic = 1, Roughness = 0.25 } },
                new TechMaterial { Name = "FR4", Epsr = 4.4, TanD = 0.02 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "FR4", Min = new(0, 0, 0), Size = new(4000, 2000, 500) },
                new C3dBox { Name = "a", Material = "Copper", Min = new(500, 500, 500), Size = new(1000, 1000, 100) },
                new C3dBox { Name = "b", Material = "Copper", Min = new(2500, 500, 500), Size = new(1000, 1000, 100) },
            ],
        });
        var cache = new TechnologyCache();
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue, cache);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Viewer.EnvironmentFor = EnvironmentPrefilter.For;
        vm.Start();
        Settle(vm);
        return (vm, fake, techPath, cache);
    }

    /// <summary>A live edit of the technology, as an open technology editor installs one: a deep copy, mutated.</summary>
    private static void Edit(TechnologyCache cache, string techPath, Action<Technology> mutate)
    {
        var t = TechPersistence.Clone(cache.Get(techPath)!);
        mutate(t);
        cache.SetLive(techPath, t);
    }

    private static void Frame(C3dEditorViewModel vm)
    {
        var plan = new Scene3DFramePlan();
        plan.Plan(vm.Viewer.Scene, vm.Viewer.View, 400, 300, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        vm.Viewer.Session.Frame(0, plan, 1, vm.Viewer.Scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
    }

    private void Settle(C3dEditorViewModel vm) => Pump(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested);

    private void Pump(Func<bool> until)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return until();
        }, TimeSpan.FromSeconds(60)), "never settled");
}
