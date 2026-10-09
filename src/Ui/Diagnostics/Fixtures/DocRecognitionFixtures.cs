using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Recognition;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// Design ▸ Create Schematic from Artwork…, on the shipped <c>Artwork to Schematic</c> example — the board that
/// example's README walks through, with its placement file and bill of materials given, so the parts table carries
/// every row a reader's copy does.
/// </summary>
/// <remarks>
/// <b>The recognition is the real one</b> (<see cref="ArtworkRecognitionRunner"/>), awaited before the capture, so
/// the table and the report strip are what the dialog shows a few hundred milliseconds after it opens.
///
/// <para><b>The companion paths are given RELATIVE to the example</b>, read with the working directory moved there
/// for the two reads and put back: an absolute path in the text boxes would print this machine's folder into a
/// committed figure.</para>
///
/// <para>The window's <c>Styles</c> are carried across onto its detached content, as
/// <see cref="DocRailFixtures"/> does: the field labels and the table's header buttons are style classes, and a
/// detached control silently falls back to the defaults without them.</para>
/// </remarks>
public static class DocRecognitionFixtures
{
    private const string ExampleFolder = "Artwork to Schematic";

    public static FigureScene CreateSchematicFromArtwork()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the Create Schematic from Artwork figure has no document.");
        string ws = Path.Combine(root, ExampleFolder);
        string clay = Path.Combine(ws, "Board", "Board", "layout", "Board.clay");

        var runner = ArtworkRecognitionRunner.Instance;
        var vm = new CreateSchematicFromArtworkViewModel(runner.Load(clay), [], runner, debounce: TimeSpan.Zero);

        string cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = ws;
            vm.PlacementPath = Path.Combine("fab", "Board.pos");
            vm.BomPath = Path.Combine("fab", "Board-bom.csv");
        }
        finally { Environment.CurrentDirectory = cwd; }
        vm.Recognition.GetAwaiter().GetResult();

        if (vm.Rows.Count == 0)
            throw new InvalidOperationException($"Recognising '{clay}' produced no parts ({vm.Status}), so the figure would show an empty table.");

        var window = new CreateSchematicFromArtworkDialog(vm);
        var content = window.Content as Control
            ?? throw new InvalidOperationException("CreateSchematicFromArtworkDialog has no content control to capture.");
        var styles = window.Styles.ToList();
        window.Content = null;
        window.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        content.DataContext = vm;
        // The frame sizes the control it is handed; handed the dialog's own Grid, that size would swallow the Grid's
        // margin and the content would run to the frame's edges, which the dialog never does.
        return new FigureScene(new Panel { Children = { content } });
    }
}
