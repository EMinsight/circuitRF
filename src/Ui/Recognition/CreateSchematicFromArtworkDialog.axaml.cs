using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CircuitRF.Ui.Recognition;

/// <summary>
/// Design ▸ Create Schematic from Artwork… (brief-artsch-8-gui-command.md R-as8-2). Non-modal. The window owns only
/// what needs a window — the file pickers — and hands each to the view model as a function; everything the dialog
/// decides is <see cref="CreateSchematicFromArtworkViewModel"/>'s, so it is tested without one.
/// </summary>
public partial class CreateSchematicFromArtworkDialog : Window
{
    public CreateSchematicFromArtworkDialog() => InitializeComponent();

    public CreateSchematicFromArtworkDialog(CreateSchematicFromArtworkViewModel vm) : this()
    {
        DataContext = vm;
        vm.PickFileAsync = title => PickOpen(title, null);
        vm.BrowseModelAsync = () => PickOpen("Touchstone model", new FilePickerFileType("Touchstone") { Patterns = ["*.s2p", "*.S2P"] });
        vm.PickImportPathAsync = () => PickOpen("Import parts table", new FilePickerFileType("CSV") { Patterns = ["*.csv"] });
        vm.PickExportPathAsync = PickExport;
        Closed += (_, _) => vm.Close();
    }

    private async Task<string?> PickOpen(string title, FilePickerFileType? type)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Create Schematic from Artwork — {title}",
            AllowMultiple = false,
            FileTypeFilter = type is null
                ? [new FilePickerFileType("All Files") { Patterns = ["*.*"] }]
                : [type, new FilePickerFileType("All Files") { Patterns = ["*.*"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickExport()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create Schematic from Artwork — Export parts table",
            SuggestedFileName = "parts.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        return file?.TryGetLocalPath();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
