using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// A vendor library's Help: the documents the library came with (<see cref="VendorLibraryDocs"/>),
/// opened with the system's own viewer, and circuitRF's page on vendor libraries after them. Shared by
/// the Library palette and the Parameter Editor, so both offer the same list.
///
/// <para>A menu lists the documents by their path in the library, nearest the root first, each opened
/// with whatever the operating system opens that kind of file with. A library that shipped none —
/// common: an edition can be nothing but model and symbol files, its notes published separately —
/// says so in the menu rather than jumping straight to circuitRF's page, so the user knows to look
/// on the download page for them.</para>
/// </summary>
internal static class VendorLibraryHelp
{
    /// <summary>circuitRF's own page, the last entry of the menu and the answer when there is no document.</summary>
    private static void OpenOwnPage() => DocLauncher.Open("reference/pdk-integration.html", "vendor");

    public static void Open(Control anchor, string? libraryRoot)
    {
        IReadOnlyList<string> docs = libraryRoot is null
            ? []
            : VendorLibraryDocs.Find(libraryRoot, VendorLibraryIndex.Latest(libraryRoot));
        var menu = new ContextMenu();
        if (docs.Count == 0)
            menu.Items.Add(new MenuItem
            {
                Header    = libraryRoot is null ? "This library cannot be found" : "This library came with no documents",
                IsEnabled = false,
            });
        foreach (string doc in docs)
        {
            var item = new MenuItem { Header = Path.GetRelativePath(libraryRoot!, doc).Replace('\\', '/') };
            ToolTip.SetTip(item, doc);
            string path = doc;
            item.Click += (_, _) => DocLauncher.OpenFile(path);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var own = new MenuItem { Header = "Vendor libraries in circuitRF Help" };
        own.Click += (_, _) => OpenOwnPage();
        menu.Items.Add(own);
        menu.Open(anchor);
    }
}
