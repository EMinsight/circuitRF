using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Views.Layout;
using CircuitRF.Ui.Views.Materials;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Materials chapter's figures: one new material, "My laminate", entered into the LVS example's
/// <c>generic-materials.cmat</c> card by card, and that example's technology on its Materials tab. Every edit is made through
/// the editor's own view model and stays in memory — nothing is saved, so the example's files are never touched.
/// </summary>
public static class DocMaterialsFixtures
{
    private const string Example = "LVS";
    private const string Library = "tech/generic-materials.cmat";
    private const string Technology = "tech/mmic-GaAs_2LM_100um.ctech";

    /// <summary>The new material named, with εr and tan δ: the form from its top, the name and the Dielectric card.</summary>
    public static FigureScene NewMaterial() => Editor("", Dielectric);

    /// <summary>The same laminate made anisotropic: εr in the plane above εr through it.</summary>
    public static FigureScene AnisotropicPermittivity() => Editor("Dielectric", row =>
    {
        Dielectric(row);
        row.IsAnisotropic = true;
        row.TensorXxText = "3.66";
        row.TensorYyText = "3.66";
        row.TensorZzText = "3.5";
    });

    /// <summary>Its thermal values: k, density, specific heat, an anisotropic k and a k(T) table, opened.</summary>
    public static FigureScene Thermal() => Editor("Thermal", row =>
    {
        Dielectric(row);
        row.ThermalKText = "0.62";
        row.DensityText = "1900";
        row.SpecificHeatText = "900";
        row.IsThermalAnisotropic = true;
        row.KTensorXxText = "0.8";
        row.KTensorYyText = "0.8";
        row.KTensorZzText = "0.62";
        row.KTable.AddPointCommand.Execute(null);                 // 20 °C at the constant
        row.KTable.AddPointCommand.Execute(null);                 // 45 °C at the last value
        row.KTable.Points[1].TempText = "150";
        row.KTable.Points[1].ValueText = "0.57";
    }, openTables: true);

    /// <summary>Its look in the realistic view: a body colour and a roughness, the rest taken from the role.</summary>
    public static FigureScene Appearance() => Editor("Appearance", row =>
    {
        Dielectric(row);
        foreach (var (key, value) in new[] { ("BaseColor", "#C9B98A"), ("Roughness", "0.55") })
        {
            var field = row.Appearance.Field(key);
            field.Text = value;
            field.CommitText();
        }
    });

    /// <summary>A right-click on the new material: its context menu, Delete Material. Opened the way a right-click opens it — a
    /// ContextRequested on the row — so the figure fails if the menu does not open.</summary>
    public static FigureScene DeleteMenu()
    {
        var scene = Editor("", Dielectric);
        return new FigureScene(scene.Content)
        {
            Popups = root =>
            {
                var view = root as MaterialsTableView ?? root.GetVisualDescendants().OfType<MaterialsTableView>().First();
                var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "RowList");
                var rowItem = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is MaterialRowViewModel { Name: "My laminate" });
                rowItem.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
                UiArtworkGenerator.Pump();
                var menu = view.RowMenu;
                if (!menu.IsOpen) return [];
                // Headless there is no pointer to place it at, so it opened at the window's origin: re-open it over the row, the
                // way DocFixtures.OpenContextMenu places a menu.
                var at = rowItem.TranslatePoint(new Point(rowItem.Bounds.Width * 0.45, rowItem.Bounds.Height * 0.6), root) ?? default;
                menu.Close();
                menu.HorizontalOffset = at.X;
                menu.VerticalOffset = at.Y;
                menu.Open(list);
                UiArtworkGenerator.Pump();
                return DocFixtures.DescribePopup(menu, rowItem, at);
            },
        };
    }

    /// <summary>The technology editor's Materials tab: the technology's own materials, the library it names, and that
    /// library's rows below, read-only.</summary>
    public static FigureScene TechnologyTab()
    {
        string path = Path.Combine(ExampleRoot(), Technology);
        var tech = TechPersistence.LoadFromFile(path);
        var vm = new TechEditorViewModel(path, tech) { SelectedTabIndex = 4 };
        return new FigureScene(new TechEditorView { DataContext = new TechDocument(tech.Name, vm, vm.FilePath) });
    }

    private static void Dielectric(MaterialRowViewModel row)
    {
        row.EpsrText = "3.5";
        row.TanDText = "0.004";
        row.SourceText = "The supplier's datasheet, stated at 10 GHz.";
    }

    /// <summary>The library's editor with "My laminate" added and filled by <paramref name="fill"/>, its form scrolled so the
    /// card headed <paramref name="card"/> is at the top — or not scrolled, for an empty <paramref name="card"/>.</summary>
    private static FigureScene Editor(string card, Action<MaterialRowViewModel> fill, bool openTables = false)
    {
        string path = Path.Combine(ExampleRoot(), Library);
        var vm = new MaterialsEditorViewModel(path, MaterialLibraryPersistence.LoadFromFile(path));
        // Before the view exists, so the new name's focus request — which would select its text — has no one to answer it.
        vm.Table.Add();
        var row = vm.Table.SelectedRow!;
        row.NameText = "My laminate";
        fill(row);
        return new FigureScene(new MaterialsTableView { DataContext = vm.Table })
        {
            AfterLayout = card.Length == 0 ? null : view => ScrollToCard(view, card, openTables),
        };
    }

    private static void ScrollToCard(Control view, string card, bool openTables)
    {
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "DetailScroll");
        var border = scroll.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("card")
                     && b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("hdr"))?.Text == card);
        if (openTables)
        {
            foreach (var expander in border.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
            scroll.UpdateLayout();
        }
        if (border.TranslatePoint(new Point(0, 0), scroll) is { } top)
            scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + top.Y - 8));
    }

    private static string ExampleRoot()
        => Path.Combine(ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the Materials figures have no document."),
            Example);
}
