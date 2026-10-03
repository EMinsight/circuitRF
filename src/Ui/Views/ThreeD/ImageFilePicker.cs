// brief-em3d-101 R-em3d101-2a — the one image picker the 3D editor opens (Insert Image…, 3D ▸ Draw ▸ Image…, Replace Image…,
// Resolve Path…, the Inspector's Browse…). Code-behind side of the UI firewall: the view model is handed a delegate, never a
// StorageProvider. Filtered to what SkiaSharp decodes (C3dImages.Extensions).

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

public static class ImageFilePicker
{
    /// <summary>The files chosen (absolute), or none.</summary>
    public static async Task<IReadOnlyList<string>> PickAsync(TopLevel? owner, string title, bool multiple)
    {
        if (owner is null) return [];
        var patterns = C3dImages.Extensions.SelectMany(e => new[] { "*" + e, "*" + e.ToUpperInvariant() }).ToList();
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = patterns }],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
