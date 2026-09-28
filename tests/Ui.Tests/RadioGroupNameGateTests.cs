// ================================================================
//  RadioGroupNameGateTests.cs — a RadioButton's GroupName is shared by the whole WINDOW (Avalonia groups by name per
//  visual root), so a view that can appear more than once in one window must not name its groups. Two 3D editors in one
//  workspace window each had "Equal distances" (bound to !ChamferTwoDistances) and "Two distances" (bound to it) in the
//  group "chamfer": checking one editor's button unchecked the other's, whose write-back checked its partner, which
//  unchecked the first editor's again — forever, on the UI thread. It showed as a hang on opening a workspace with a
//  .c3d open. Radio buttons with no GroupName group with their siblings, which is all any of these pairs needed; only a
//  Window (one instance per root) may name a group. src/Ui/RESOLVED.md records it.
// ================================================================

using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class RadioGroupNameGateTests
{
    [Fact]
    public void OnlyAWindowNamesARadioGroup()
    {
        string ui = Path.Combine(Em3d.PalaceBackendTests.RepoRoot(), "src", "Ui");
        var offenders = new List<string>();
        int windows = 0;
        foreach (string file in Directory.EnumerateFiles(ui, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(file);
            if (!Regex.IsMatch(text, @"\bGroupName\s*=")) continue;
            if (Regex.IsMatch(text, @"^\s*<Window\b", RegexOptions.Multiline)) { windows++; continue; }
            offenders.Add(Path.GetRelativePath(ui, file));
        }
        Assert.True(windows > 0, "the scan found no Window with a named group at all: it is not reading the views");
        Assert.True(offenders.Count == 0, "A view that is not a Window names a radio group (shared across its whole window): "
                                          + string.Join(", ", offenders));
    }
}
