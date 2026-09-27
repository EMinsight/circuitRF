// ================================================================
//  SuggestedFileNameGateTests.cs — a Save picker that states its type (DefaultExtension AND FileTypeChoices) suggests a
//  name WITHOUT the extension. Avalonia's storage provider appends the type's extension itself, so a suggested name
//  that carries it is shown twice — "x.step.step". The fifth time this shipped (the Harmonica testbench, the Match
//  Designer's export, railRF's Save, the Smith chart, then Export STEP) is why it is a scan over every picker in
//  src/Ui rather than one more test of one more picker. src/Ui/RESOLVED.md records the history.
// ================================================================

using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SuggestedFileNameGateTests
{
    /// <summary>Pickers whose name keeps its extension ON PURPOSE, each with the reason written beside it in the source:
    /// a compound name (<c>x.placement.csv</c>) whose inner dot would read as the extension on its own.</summary>
    private static readonly string[] Deliberate = [".placement.csv", ".bom.csv", ".ipc\""];

    [Fact]
    public void ASavePickerThatStatesItsType_SuggestsANameWithoutTheExtension()
    {
        var offenders = new List<string>();
        string ui = Path.Combine(Em3d.PalaceBackendTests.RepoRoot(), "src", "Ui");
        int pickers = 0;
        foreach (string file in Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories))
        {
            string code = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(code, @"new\s+(?:[\w.]+\.)?FilePickerSaveOptions\s*\{"))
            {
                string block = Block(code, m.Index + m.Length);
                if (!block.Contains("DefaultExtension", StringComparison.Ordinal) || !block.Contains("FileTypeChoices", StringComparison.Ordinal))
                    continue;
                pickers++;
                var name = Regex.Match(block, @"SuggestedFileName\s*=\s*(.*?),\s*\n", RegexOptions.Singleline);
                if (!name.Success) continue;
                string expr = name.Groups[1].Value;
                bool literalExtension = Regex.IsMatch(expr, @"""[^""]*\.[A-Za-z][\w]*""") && !Deliberate.Any(d => expr.Contains(d, StringComparison.Ordinal));
                bool keepsTheSourcesExtension = Regex.IsMatch(expr, @"Path\.GetFileName\(");
                if (literalExtension || keepsTheSourcesExtension)
                    offenders.Add($"{Path.GetRelativePath(ui, file)}:{code[..m.Index].Count(c => c == '\n') + 1}: {expr.Trim()}");
            }
        }
        Assert.True(pickers > 20, $"only {pickers} typed Save pickers found: the scan no longer sees them");
        Assert.True(offenders.Count == 0, "Suggested names that carry the extension the picker adds again:\n" + string.Join("\n", offenders));
    }

    /// <summary>The braces' contents from <paramref name="start"/> (just inside the opening brace) to its match.</summary>
    private static string Block(string code, int start)
    {
        int depth = 1, i = start;
        for (; i < code.Length && depth > 0; i++)
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
        return code[start..i];
    }
}
