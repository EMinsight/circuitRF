using Avalonia.Controls;
using Avalonia.Layout;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>The step rail and the selected step's settings. Everything it shows is the view model's.</summary>
public partial class StepsPanelView : UserControl
{
    /// <summary>The app note that walks the data a model needs, from the source simulator to the composer.</summary>
    internal const string DataGuidePage = "app-notes/an04-model-from-a-reference-kit.html";

    public StepsPanelView()
    {
        InitializeComponent();
        ProposalList.DoubleTapped += (_, _) =>
        {
            if (DataContext is ModelComposerViewModel vm && ProposalList.SelectedItem is ProposalRow row)
                vm.ChooseTopologyCommand.Execute(row);
        };
        SymbolButton.Click += (_, _) => OpenSymbolMenu();
        DataGuideButton.Click += (_, _) => DocLauncher.Open(DataGuidePage);
    }

    /// <summary>
    /// The Export step's symbol menu, built when it opens because the choices follow the export folder: one row per
    /// symbol, its glyph and its name as the row's own header — the Smith Chart's Add menu, whose remarks say why the glyph
    /// is not the MenuItem's Icon (the icon presenter scales it back down to the theme's size).
    /// </summary>
    private void OpenSymbolMenu()
    {
        if (DataContext is not ModelComposerViewModel vm || vm.Symbols.Count == 0) return;
        var menu = new MenuFlyout();
        foreach (var symbol in vm.Symbols)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new PaletteGlyphControl
            {
                Kind = Design.Schematic.SymbolKind.ComposedFET, SymbolCellDir = symbol.GlyphRef, Width = 48, Height = 39,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock { Text = symbol.Label, VerticalAlignment = VerticalAlignment.Center });
            menu.Items.Add(new MenuItem { Header = row, Command = vm.ChooseSymbolCommand, CommandParameter = symbol });
        }
        menu.ShowAt(SymbolButton);
    }
}
