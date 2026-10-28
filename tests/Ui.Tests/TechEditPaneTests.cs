using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The layout editor's Technology ▾ ▸ Edit… opens the <c>.ctech</c> as an ordinary document, with no
/// placement of its own (owner request, 2026-10-28). It used to open in a narrow pane right of the
/// layout so layers could be toggled beside the artwork; visibility left the technology on 2026-10-15.
///
/// <para>The rest of this file pins <see cref="CircuitRfDockFactory.SplitDocumentRightOf"/>, the
/// factory's own split, which other tests build their arrangements with. These run headlessly for the
/// reason <see cref="SplitDocumentAreaLayoutTests"/> records: the Dock MVVM model types are plain C#.</para>
/// </summary>
public class TechEditPaneTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string RepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    // ── The affordance ────────────────────────────────────────────────────────

    /// <summary>The ellipsis is the promise that something further opens — the owner asked for it the
    /// same day the command started opening a pane rather than just another tab.</summary>
    [Fact]
    public void TheFlyoutItemIsEditWithAnEllipsis()
    {
        var axaml = RepoFile("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        Assert.Contains("<MenuItem Header=\"Edit…\" Click=\"OnEditTechnologyClick\"/>", axaml);
        Assert.DoesNotContain("<MenuItem Header=\"Edit\" Click=\"OnEditTechnologyClick\"/>", axaml);
    }

    /// <summary>The view asks for the plain open — the same one the project tree and the EM setup
    /// editor use — and hands over no width to split by.</summary>
    [Fact]
    public void TheLayoutEditorOpensTheTechnologyAsAnOrdinaryDocument()
    {
        var src = RepoFile("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        var body = src[src.IndexOf("private void OnEditTechnologyClick", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("private void OnOpenSourceWorkspaceClick", StringComparison.Ordinal)];

        Assert.Contains("OpenTechnologyDocument(techPath)", body);
        Assert.DoesNotContain("Bounds.Width", body);
    }

    // ── The split itself ──────────────────────────────────────────────────────

    [Fact]
    public void TheTechnologyLandsInItsOwnPane_RightOfTheLayout_AtTheAskedProportion()
    {
        var (f, _, host, layout, tech) = Shell();

        Assert.True(f.SplitDocumentRightOf(tech, layout, 0.2));

        // The layout keeps its strip; the technology is in a new document dock beside it.
        Assert.Same(host, layout.Owner);
        Assert.NotSame(host, tech.Owner);
        var pane = Assert.IsAssignableFrom<IDocumentDock>(tech.Owner);

        // Right of, not left of: the wrapper's children are [host, splitter, pane], in that order.
        var wrapper = Assert.IsAssignableFrom<IProportionalDock>(host.Owner);
        Assert.Equal(Orientation.Horizontal, wrapper.Orientation);
        Assert.Equal(
            new IDockable[] { host, pane },
            wrapper.VisibleDockables!.Where(d => d is not IProportionalDockSplitter).ToArray());

        Assert.Equal(0.2, pane.Proportion, 6);
        Assert.Equal(0.8, host.Proportion, 6);
        Assert.Same(tech, pane.ActiveDockable);
    }

    /// <summary>
    /// A pane that outlives its last document is a dead region the user cannot dismiss — the same
    /// reason a RESTORED extra pane is collapsable, and the primary strip is not.
    /// </summary>
    [Fact]
    public void TheNewPaneIsCollapsable_SoClosingItsLastTabGivesTheSpaceBack()
    {
        var (f, _, _, layout, tech) = Shell();
        f.SplitDocumentRightOf(tech, layout, 0.2);

        Assert.True(((IDocumentDock)tech.Owner!).IsCollapsable);
    }

    /// <summary>The arrangement is an ordinary split document area, so it is captured — and therefore
    /// comes back — exactly like one the user made by dragging a tab to the edge.</summary>
    [Fact]
    public void TheSplitIsCaptured_SoItSurvivesAReopen()
    {
        var (f, root, _, layout, tech) = Shell();
        f.SplitDocumentRightOf(tech, layout, 0.2);

        var keys = new Dictionary<IDockable, string>
        {
            [layout] = "layout/board.clay",
            [tech]   = "tech/pcb.ctech",
        };
        var region = DockLayoutCapture.CaptureDocumentRegion(root, d => keys.GetValueOrDefault(d));

        Assert.NotNull(region);
        Assert.Equal("Horizontal", region!.Orientation);
        Assert.Equal(
            new[] { "layout/board.clay", "tech/pcb.ctech" },
            region.Children.Select(c => string.Join(",", c.Documents)).ToArray());
    }

    /// <summary>A neighbour that is not in a document dock has no strip to split; the caller's document
    /// is left exactly where it was rather than being pulled out of one and dropped nowhere.</summary>
    [Fact]
    public void WithNoHostStrip_NothingMoves()
    {
        var (f, _, host, _, tech) = Shell();
        var orphan = new StubDocument("orphan", StubDocument.StubKind.Welcome);

        Assert.False(f.SplitDocumentRightOf(tech, orphan, 0.2));
        Assert.Same(host, tech.Owner);
    }

    /// <summary>
    /// Field report, 2026-09-29: the technology beside a layout could vanish, and Reset Layout did not
    /// bring it back. Reset (and the launch Window Layout preset, and Hide/Show Dockers) re-host only
    /// the PRIMARY strip, so the side pane Edit… had put it in was left out of the new tree — while the
    /// document stayed registered as open, so every later Edit… activated a tab on no screen.
    /// </summary>
    [Fact]
    public void AResetLayout_KeepsTheSidePanesTechnology_InTheTree()
    {
        var (f, _, _, layout, tech) = Shell();
        f.SplitDocumentRightOf(tech, layout, 0.2);

        var rebuilt = f.CreateLayoutPreservingContent(DockLayoutDefaults.For(CircuitRF.Ui.Theming.WindowLayout.ProjectTreeAndLibrary));
        f.InitLayout(rebuilt);

        var docks = DockLayoutCapture.EnumerateDocumentDocks(rebuilt).ToList();
        Assert.Contains(docks, d => d.VisibleDockables!.Contains(tech));
        Assert.Contains(docks, d => d.VisibleDockables!.Contains(layout));
    }

    /// <summary>The real shell, with a layout and a technology open as two tabs of the one strip —
    /// which is where a split starts from.</summary>
    private static (CircuitRfDockFactory Factory, IRootDock Root, IDocumentDock Host,
                    IDockable Layout, IDockable Tech) Shell()
    {
        var f = new CircuitRfDockFactory();

        var root = f.CreateLayout();
        f.InitLayout(root);               // sets every Owner — without it there is no tree to split

        var host = f.DocumentDock!;
        f.RemoveWelcomeStub();

        var layout = new StubDocument("board", StubDocument.StubKind.Welcome);
        var tech   = new StubDocument("pcb", StubDocument.StubKind.Welcome);
        f.OpenDocument(layout);
        f.OpenDocument(tech);

        return (f, root, host, layout, tech);
    }
}
