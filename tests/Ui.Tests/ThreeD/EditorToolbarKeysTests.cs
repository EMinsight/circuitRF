// ================================================================
//  EditorToolbarKeysTests.cs — 3D editor toolbar round: the pane's keys reach it wherever focus is in the view (a
//  toolbar click took focus, and 1 did nothing until the canvas was clicked), and the draw tools' order and Sheet glyph.
//  No headless Avalonia host here, so both read the view's source.
// ================================================================

using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class EditorToolbarKeysTests
{
    /// <summary>A toolbar click hands focus back to the pane, a re-activated tab focuses it, and a key bubbling to the view
    /// unused is forwarded to it — never Delete/Backspace, never from a text field.</summary>
    [Fact]
    public void Keys_ReachThePane_WhereverFocusIsInTheView()
    {
        string cs = Read("C3dEditorView.axaml.cs");
        Assert.Contains("Toolbar.AddHandler(Button.ClickEvent, OnToolbarClick", cs, StringComparison.Ordinal);
        Assert.Contains("AddHandler(KeyDownEvent, OnViewKeyBubble, RoutingStrategies.Bubble)", cs, StringComparison.Ordinal);
        Assert.Contains("ActivationFocusRequested += OnActivationFocusRequested", cs, StringComparison.Ordinal);
        string bubble = cs[cs.IndexOf("private void OnViewKeyBubble", StringComparison.Ordinal)..];
        bubble = bubble[..bubble.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("Key.Delete or Key.Back", bubble, StringComparison.Ordinal);
        Assert.Contains("IsTextEntry", bubble, StringComparison.Ordinal);
        Assert.Contains("Pane.ForwardKey(e)", bubble, StringComparison.Ordinal);
    }

    /// <summary>Box, Cylinder, Sphere, then Sheet — in the toolbar and the Shift+A popup — and the Sheet is the layout's
    /// rectangle. brief-em3d-102 gate 10: the sphere is third, drawn, on E, and listed after Cylinder in both 3D ▸ Draw menus.</summary>
    [Fact]
    public void Toolbar_CylinderFollowsBox_AndSheetIsARectangle()
    {
        string xaml = Read("C3dEditorView.axaml");
        int box = xaml.IndexOf("IsBoxArmed", StringComparison.Ordinal);
        int cyl = xaml.IndexOf("IsCylinderArmed", StringComparison.Ordinal);
        int sph = xaml.IndexOf("IsSphereArmed", StringComparison.Ordinal);
        int sheet = xaml.IndexOf("IsSheetArmed", StringComparison.Ordinal);
        Assert.True(box > 0 && box < cyl && cyl < sph && sph < sheet);
        Assert.Contains("Viewer3DPathGlyph.Sphere", xaml[sph..xaml.IndexOf("</ToggleButton>", sph, StringComparison.Ordinal)], StringComparison.Ordinal);
        string sheetButton = xaml[sheet..xaml.IndexOf("</ToggleButton>", sheet, StringComparison.Ordinal)];
        Assert.Contains("Kind=\"RectangleOutline\"", sheetButton, StringComparison.Ordinal);

        var kinds = C3dEditorViewModel.DrawTools.Select(t => t.Kind).ToList();
        Assert.Equal([C3dToolKind.Box, C3dToolKind.Cylinder, C3dToolKind.Sphere, C3dToolKind.Sheet], kinds.Take(4));
        Assert.Equal(('E', nameof(CircuitRF.Ui.Viewer3D.Viewer3DPathGlyph.Sphere)),
                     C3dEditorViewModel.DrawTools.Where(t => t.Kind == C3dToolKind.Sphere).Select(t => (t.Letter, t.Icon)).Single());
        string window = File.ReadAllText(ReadPath(Path.Combine("..", "WorkspaceWindow.axaml")));
        // The native menu and the in-window one: the item after Cylinder's is the sphere's.
        foreach (var (cylinder, sphere) in new[] { ("Header=\"Cylinder\"", "Header=\"Sphere\""), ("Header=\"C_ylinder\"", "Header=\"Sph_ere\"") })
        {
            int at = window.IndexOf(cylinder, StringComparison.Ordinal);
            Assert.True(at > 0, cylinder);
            Assert.Equal(window.IndexOf("Header=", at + cylinder.Length, StringComparison.Ordinal), window.IndexOf(sphere, at, StringComparison.Ordinal));
        }
        Assert.Equal("RectangleOutline", C3dEditorViewModel.DrawTools.Single(t => t.Kind == C3dToolKind.Sheet).Icon);
    }

    /// <summary>The cylinder is drawn, not the icon set's Database stack: in the toolbar, the Shift+A popup and the 3D ▸ Draw
    /// menu; its path parses and sits inside Material's 24-unit box with the same 2-unit margin as the icons beside it.</summary>
    [Fact]
    public void Cylinder_IsTheDrawnGlyph_Everywhere()
    {
        string xaml = Read("C3dEditorView.axaml");
        int cyl = xaml.IndexOf("IsCylinderArmed", StringComparison.Ordinal);
        Assert.Contains("Viewer3DPathGlyph.Cylinder", xaml[cyl..xaml.IndexOf("</ToggleButton>", cyl, StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Equal(nameof(CircuitRF.Ui.Viewer3D.Viewer3DPathGlyph.Cylinder), C3dEditorViewModel.DrawTools.Single(t => t.Kind == C3dToolKind.Cylinder).Icon);
        string window = File.ReadAllText(ReadPath(Path.Combine("..", "WorkspaceWindow.axaml")));
        Assert.DoesNotContain("Kind=\"Database\"", xaml + window, StringComparison.Ordinal);
        Assert.Contains("Viewer3DPathGlyph.Cylinder", window, StringComparison.Ordinal);

        using var path = SkiaSharp.SKPath.ParseSvgPathData(CircuitRF.Ui.Viewer3D.Viewer3DPathGlyph.Cylinder);
        Assert.NotNull(path);
        var b = path.Bounds;
        Assert.True(b.Left >= 3.9f && b.Top >= 1.7f && b.Right <= 20.1f && b.Bottom <= 22.3f, b.ToString());
    }

    /// <summary>3D icons round: Box is the outlined cube (as the cylinder and sphere are outlined solids); Object mode, which used
    /// that cube, is the drawn cube with its corners marked; the Isometric view is the drawn cube with its three visible faces
    /// filled. Each in the toolbar and, where one exists, the 3D menu; both drawn paths parse inside the 24-unit box.</summary>
    [Fact]
    public void Box_IsTheOutlinedCube_ObjectModeAndIsometric_AreTheDrawnCubes()
    {
        string xaml = Read("C3dEditorView.axaml");
        string Button(string marker)
        {
            int at = xaml.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at > 0, marker);
            int end = xaml.IndexOf("Button>", at, StringComparison.Ordinal);
            return xaml[at..end];
        }
        Assert.Contains("Kind=\"CubeOutline\"", Button("IsBoxArmed"), StringComparison.Ordinal);
        Assert.Contains("Viewer3DPathGlyph.ObjectMode", Button("IsObjectMode"), StringComparison.Ordinal);
        Assert.Contains("Viewer3DPathGlyph.Isometric", Button("StandardView3D.Iso}"), StringComparison.Ordinal);
        Assert.Equal("CubeOutline", C3dEditorViewModel.DrawTools.Single(t => t.Kind == C3dToolKind.Box).Icon);

        string window = File.ReadAllText(ReadPath(Path.Combine("..", "WorkspaceWindow.axaml")));
        Assert.DoesNotContain("CubeUnfolded", xaml + window, StringComparison.Ordinal);
        string Item(string header)
        {
            int at = window.IndexOf(header, StringComparison.Ordinal);
            Assert.True(at > 0, header);
            return window[at..window.IndexOf("</MenuItem>", at, StringComparison.Ordinal)];
        }
        Assert.Contains("Viewer3DPathGlyph.ObjectMode", Item("Header=\"_Object\""), StringComparison.Ordinal);
        Assert.Contains("Kind=\"CubeOutline\"", Item("Header=\"_Box\""), StringComparison.Ordinal);

        foreach (string data in new[] { CircuitRF.Ui.Viewer3D.Viewer3DPathGlyph.ObjectMode, CircuitRF.Ui.Viewer3D.Viewer3DPathGlyph.Isometric })
        {
            using var path = SkiaSharp.SKPath.ParseSvgPathData(data);
            Assert.NotNull(path);
            var b = path.Bounds;
            Assert.True(b.Left >= 2 && b.Top >= 1 && b.Right <= 22 && b.Bottom <= 23, b.ToString());
        }
    }

    private static string Read(string file) => File.ReadAllText(ReadPath(file));

    private static string ReadPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        return Path.Combine(root, "src", "Ui", "Views", "ThreeD", file);
    }
}
