using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>A ColorView's Fluent template fixes its own tab control at 350 × 338 with the preview strip below it, so a Width or
/// Height set on the control smaller than that clips the picker at the side and the bottom (the Look panel's and the appearance
/// editor's were 300 × 340). Every ColorView is left at its natural size, as ColorPickerDialog's always was.</summary>
public sealed class ColorViewSizeTests
{
    [Fact]
    public void NoColorViewIsGivenASize()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "circuitrf.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var offenders = new List<string>();
        int seen = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Ui"), "*.axaml", SearchOption.AllDirectories))
        {
            string text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"<cp:ColorView\b[^>]*>", RegexOptions.Singleline))
            {
                seen++;
                if (Regex.IsMatch(m.Value, @"\s(Width|Height)=")) offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            }
        }
        Assert.True(seen >= 3, $"expected the three ColorViews, found {seen}");
        Assert.Empty(offenders);
    }
}
