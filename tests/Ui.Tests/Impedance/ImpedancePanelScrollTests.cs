using System.Diagnostics;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Diagnostics;
using Xunit;

namespace CircuitRF.Ui.Tests.Impedance;

/// <summary>
/// Field report, 2026-09-27. The Impedance panel's bottom half (the verdict tiles, the filter, the rows)
/// could not be reached at an ordinary dock height; and a crash whose cause had been swallowed by the
/// toolkit arrived with an empty trail. Both are pinned here; neither can be seen in pixels headlessly.
/// </summary>
public class ImpedancePanelScrollTests
{
    private static string RepoFile(string rel, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }

    /// <summary>The whole panel scrolls, and the rows are capped by the viewport so they keep
    /// virtualizing inside it (verified on the same structure with a headless Avalonia harness:
    /// the bottom row reachable at a 300-unit dock, 8 of 500 rows realized).</summary>
    [Fact]
    public void ThePanelScrollsAsAWhole_AndTheRowsAreCappedByTheViewport()
    {
        var axaml = RepoFile("src/Ui/Views/Impedance/ImpedanceToolView.axaml");
        var code  = RepoFile("src/Ui/Views/Impedance/ImpedanceToolView.axaml.cs");

        int scroll = axaml.IndexOf("<ScrollViewer x:Name=\"PanelScroll\"", StringComparison.Ordinal);
        int root   = axaml.IndexOf("<Grid x:Name=\"PanelRoot\"", StringComparison.Ordinal);
        Assert.True(scroll >= 0 && root > scroll, "the panel's root grid must sit inside the outer ScrollViewer");
        Assert.Contains("x:Name=\"RowsList\"", axaml);
        Assert.Contains("PanelRoot.MinHeight = viewportHeight", code);
        Assert.Contains("RowsList.MaxHeight", code);
    }

    /// <summary>An exception thrown under a toolkit frame is the kind a binding swallows; one thrown
    /// only in our own code is ours to report and is not noted twice.</summary>
    [Fact]
    public void OnlyAnExceptionThrownUnderTheToolkitIsNotedAsPossiblySwallowed()
    {
        StackFrame[]? underToolkit = null;
        var list = new Avalonia.Collections.AvaloniaList<int>();
        list.CollectionChanged += (_, _) => underToolkit = new StackTrace(false).GetFrames();
        list.Add(1);

        Assert.NotNull(CrashReporter.ToolkitFrames(underToolkit!));
        Assert.Null(CrashReporter.ToolkitFrames(new StackTrace(false).GetFrames()));
    }
}
