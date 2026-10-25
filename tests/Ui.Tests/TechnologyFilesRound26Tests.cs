// ================================================================
//  TechnologyFilesRound26Tests.cs — brief-dfr26-3: the shipped ten-layer stackup, a stackup boundary
//  that sits against its ground, a Save As that writes a new identity, and a user-library technology
//  that always has its material libraries.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(UserStateDirectoryCollection.Name)]
public sealed class TechnologyFilesRound26Tests : IDisposable
{
    private const string TenLayer = "pcb-10layer_FR-4_1.7mm_1oz";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-dfr26-3-" + Guid.NewGuid().ToString("N")[..12]);

    public TechnologyFilesRound26Tests()
    {
        Directory.CreateDirectory(_root);
        CircuitRF.Ui.AppDataRoot.RedirectTo(Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── R-dfr26-3-1 ───────────────────────────────────────────────────────────

    [Fact]
    public void TheTenLayerTechnologyIsOffered_Validates_AndHasSixSpansWithResolvableEnds()
    {
        Assert.Contains(TechnologyCatalog.All, e => e.Id == TenLayer);
        var tech = ShippedTechnologies.Load(TenLayer);
        Assert.Empty(TechValidation.Validate(tech));

        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        Assert.Equal(10, conductors.Count);
        Assert.Equal(["Inner 1", "Inner 8"], conductors.Where(c => c.IsGroundReference).Select(c => c.Name));
        Assert.Equal(1_718_000, tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via).Sum(l => l.ThicknessDbu));
        Assert.Equal(BoundaryCondition.Open, tech.Stackup.Bottom);

        var names = conductors.Select(c => c.Name).ToList();
        var spans = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via)
            .Select(v => (names.IndexOf(v.SpanFromLayer!) + 1, names.IndexOf(v.SpanToLayer!) + 1)).ToList();
        Assert.Equal([(1, 10), (1, 2), (2, 3), (3, 8), (8, 9), (9, 10)], spans);
    }

    // ── R-dfr26-3-2 ───────────────────────────────────────────────────────────

    /// <summary>A Ground boundary is a plane under (or over) the outermost entry; it belongs against a
    /// conductor that IS the ground reference, never against a signal layer carrying artwork.</summary>
    [Fact]
    public void EveryShippedGroundBoundarySitsAgainstAGroundReferenceConductor()
    {
        foreach (var entry in ShippedTechnologies.All)
        {
            var stack = ShippedTechnologies.Load(entry).Stackup;
            var layers = stack.Layers.Where(l => l.Kind != StackupKind.Via).ToList();
            if (stack.Top == BoundaryCondition.Ground)
                Assert.True(layers[0] is { Kind: StackupKind.Conductor, IsGroundReference: true }, $"{entry.Id}: top");
            if (stack.Bottom == BoundaryCondition.Ground)
                Assert.True(layers[^1] is { Kind: StackupKind.Conductor, IsGroundReference: true }, $"{entry.Id}: bottom");
        }
    }

    // ── R-dfr26-3-3 ───────────────────────────────────────────────────────────

    /// <summary>The designer's case: a technology saved under a new file name kept the original's Name.</summary>
    [Fact]
    public void SaveAsGivesANewName_AndLibraryReferencesThatResolveFromTheNewPlace()
    {
        string original = WriteWorkspaceTech(Path.Combine(_root, "ws", "board"), "imported_board_gerber");
        var vm = new TechEditorViewModel(original, TechPersistence.LoadFromFile(original));

        string copy = Path.Combine(_root, "elsewhere", "pcb-10layer-mine.ctech");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        vm.SaveAs(copy);

        var saved = TechPersistence.LoadFromFile(copy);   // throws if a library reference does not resolve
        Assert.Equal("pcb-10layer-mine", saved.Name);
        Assert.NotEmpty(saved.LibraryMaterials);
    }

    // ── R-dfr26-3-4 ───────────────────────────────────────────────────────────

    [Fact]
    public void InstallingWithTheGenericLibraryMissingWritesTheBuiltInOne_AndAMissingOtherLibraryRefuses()
    {
        string lonely = WriteTech(Path.Combine(_root, "src"), "lonely", ["generic-materials.cmat"]);
        var ok = TechnologyCatalog.Install(lonely);
        Assert.Null(ok.Refusal);
        Assert.Equal(MaterialLibraries.GenericRawJson(),
            File.ReadAllText(Path.Combine(TechnologyCatalog.UserDirectory, MaterialLibraries.GenericFileName)));
        TechnologyCatalog.Load(ok.Entry!, out string? note);
        Assert.Null(note);

        string other = WriteTech(Path.Combine(_root, "src"), "other", ["house-materials.cmat"]);
        var refused = TechnologyCatalog.Install(other);
        Assert.Null(refused.Entry);
        Assert.Contains("house-materials.cmat", refused.Refusal);
        Assert.False(File.Exists(Path.Combine(TechnologyCatalog.UserDirectory, "other.ctech")));
    }

    /// <summary>An install made before the library travelled: it loads on the built-in library with one note.</summary>
    [Fact]
    public void AnInstalledTechnologyMissingItsGenericLibraryLoadsWithTheBuiltInOneAndANote()
    {
        string path = WriteTech(TechnologyCatalog.UserDirectory, "old-install", ["generic-materials.cmat"]);
        var entry = Assert.Single(TechnologyCatalog.All, e => e.Id == "old-install");

        var tech = TechnologyCatalog.Load(entry, out string? note);
        Assert.NotEmpty(tech.LibraryMaterials);
        Assert.Contains(MaterialLibraries.GenericFileName, note);
        Assert.Single(TechnologyCatalog.LibraryFiles(entry));
        Assert.True(File.Exists(path));
    }

    /// <summary>The editor's Save As into the user folder carries the library beside the copy.</summary>
    [Fact]
    public void SaveAsIntoTheUserFolderCarriesTheLibraryBesideIt()
    {
        string original = WriteWorkspaceTech(Path.Combine(_root, "ws", "board"), "board");
        var vm = new TechEditorViewModel(original, TechPersistence.LoadFromFile(original));

        Directory.CreateDirectory(TechnologyCatalog.UserDirectory);
        string copy = Path.Combine(TechnologyCatalog.UserDirectory, "board-mine.ctech");
        vm.SaveAs(copy);

        Assert.Equal(["generic-materials.cmat"], TechPersistence.LoadFromFile(copy).MaterialLibraries);
        Assert.True(File.Exists(Path.Combine(TechnologyCatalog.UserDirectory, MaterialLibraries.GenericFileName)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A ten-layer copy at <paramref name="dir"/>/<paramref name="stem"/>.ctech naming the generic
    /// library, which sits beside it — the shape of a workspace's own technology.</summary>
    private static string WriteWorkspaceTech(string dir, string stem)
    {
        string path = WriteTech(dir, stem, ["generic-materials.cmat"]);
        File.WriteAllText(Path.Combine(dir, MaterialLibraries.GenericFileName), MaterialLibraries.GenericRawJson());
        return path;
    }

    private static string WriteTech(string dir, string stem, string[] libraries)
    {
        var tech = ShippedTechnologies.Load(TenLayer);
        tech.Name = stem;
        tech.MaterialLibraries = [.. libraries];
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, stem + ".ctech");
        File.WriteAllText(path, TechPersistence.Serialize(tech));
        return path;
    }
}
