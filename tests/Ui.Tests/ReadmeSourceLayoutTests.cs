// ================================================================
//  ReadmeSourceLayoutTests.cs — every `src/…` path ARCHITECTURE.md names actually exists
//  (brief-automation-5-protocol-adapter.md §7's gate). The layers, the firewall paragraph and the
//  source tree moved out of README.md into ARCHITECTURE.md (2026-09-29), and the gate followed them.
//
//  ARCHITECTURE.md describes the source tree in THREE places — the annotated project list under
//  `## Layers`, the firewall's own list, and the `## Source layout` tree — and they drift
//  together. Nothing catches it: a folder that moved leaves three sentences behind, each still
//  perfectly readable and each now wrong.
//
//  A path that no longer exists is the CHEAPEST possible signal that the annotation beside it is
//  also stale. This test cannot check an annotation's prose, and does not pretend to; what it does
//  is fail the moment a described folder is not there, which is when someone reads the sentence
//  next to it. That is the whole claim.
// ================================================================

using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests;

public sealed class ReadmeSourceLayoutTests
{
    /// <summary>
    /// Every <c>src/…</c> folder the source-layout tree draws is a real folder.
    ///
    /// <para>The tree is box-drawing characters and two-column annotations, so the paths are
    /// reconstructed from the INDENTATION rather than read off each line: a line's depth says which
    /// parent it hangs from, exactly as the drawing means it to.</para>
    /// </summary>
    [Fact]
    public void EveryFolderTheSourceLayoutTreeDraws_Exists()
    {
        var missing = new List<string>();

        foreach (string path in SourceLayoutFolders())
            if (!Directory.Exists(Path.Combine(RepoRoot(), path)))
                missing.Add(path);

        Assert.True(missing.Count == 0,
            "ARCHITECTURE.md's source-layout tree names folders that are not there — and the annotation " +
            "beside each is therefore stale too:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The other direction, for <c>src/</c> only: a project folder that exists and the tree does not
    /// draw. The tree deliberately does not list every sub-folder — it is a map, not an inventory —
    /// but a whole PROJECT missing from it is the drift this gate is about, and
    /// <c>src/Diagnostics</c> was missing from all three lists until this test was written.
    /// </summary>
    [Fact]
    public void EveryProjectUnderSrc_IsDrawnInTheSourceLayoutTree()
    {
        var drawn = SourceLayoutFolders()
            .Select(p => p.Replace('\\', '/'))
            .Where(p => p.StartsWith("src/", StringComparison.Ordinal))
            .Select(p => p.Split('/')[1])
            .ToHashSet(StringComparer.Ordinal);

        var actual = Directory.GetDirectories(Path.Combine(RepoRoot(), "src"))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Where(n => Directory.GetFiles(Path.Combine(RepoRoot(), "src", n), "*.csproj").Length > 0);

        var undrawn = actual.Where(n => !drawn.Contains(n)).ToArray();

        Assert.True(undrawn.Length == 0,
            "these projects exist under src/ and ARCHITECTURE.md's source-layout tree does not draw them: " +
            string.Join(", ", undrawn));
    }

    /// <summary>
    /// The firewall paragraph names the projects that reference no UI framework, and the gate that
    /// enforces it is <c>UiFirewallTests.NonUiAssemblies</c>. The two are written eight lines and one
    /// repository apart, so the prose is checked against the LIST rather than against itself: a
    /// project added to the gate and not to the README leaves the README claiming less than is true,
    /// and one removed leaves it claiming more.
    /// </summary>
    [Fact]
    public void TheFirewallParagraph_NamesEveryProjectTheFirewallGates()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "ARCHITECTURE.md"));

        string gate = File.ReadAllText(Path.Combine(
            RepoRoot(), "tests", "Firewall.Tests", "UiFirewallTests.cs"));

        var gated = Regex.Matches(gate, @"\{\s*""(?<name>[A-Za-z.]+)"",\s*""[A-Za-z.]+\.dll""\s*\}")
                         .Select(m => m.Groups["name"].Value)
                         .Distinct(StringComparer.Ordinal)
                         .ToArray();

        Assert.NotEmpty(gated);

        // "CircuitRF.Design" is written in the README as `src/Design`, and RfCore as `RfCore`.
        foreach (string project in gated)
        {
            string spelled = project == "RfCore"
                ? "`RfCore`"
                : $"`src/{project["CircuitRF.".Length..]}`";

            Assert.True(readme.Contains(spelled, StringComparison.Ordinal),
                $"ARCHITECTURE.md's firewall paragraph does not name {spelled}, which tests/Firewall.Tests gates.");
        }

        Assert.Contains($"those {Spell(gated.Length)} assemblies", readme, StringComparison.Ordinal);
    }

    private static string Spell(int n) => n switch
    {
        6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve",
        _ => n.ToString(),
    };

    /// <summary>
    /// The folder paths the <c>## Source layout</c> tree draws, reconstructed from its indentation.
    /// A line names a folder when its first token ends in <c>/</c>; anything else on the line is an
    /// annotation and is ignored.
    /// </summary>
    private static List<string> SourceLayoutFolders()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "ARCHITECTURE.md"));

        int start = readme.IndexOf("## Source layout", StringComparison.Ordinal);
        Assert.True(start >= 0, "ARCHITECTURE.md no longer has a '## Source layout' section.");

        int fence = readme.IndexOf("```", start, StringComparison.Ordinal);
        int end   = readme.IndexOf("```", fence + 3, StringComparison.Ordinal);
        Assert.True(fence >= 0 && end > fence, "the source-layout tree is not in a fenced block.");

        var stack  = new List<(int Depth, string Name)>();
        var found  = new List<string>();

        foreach (string line in readme[(fence + 3)..end].Split('\n'))
        {
            // The name is the first token after the box drawing, and only when it ends in a slash.
            var m = Regex.Match(line, @"^(?<lead>[\s│├└─┌┬]*)(?<name>[A-Za-z0-9_.()-]+)/(\s|$)");
            if (!m.Success) continue;

            int depth = m.Groups["lead"].Value.Length;
            while (stack.Count > 0 && stack[^1].Depth >= depth) stack.RemoveAt(stack.Count - 1);
            stack.Add((depth, m.Groups["name"].Value));

            string path = string.Join('/', stack.Select(e => e.Name));

            // Only the source tree. tools/, packaging/ and docs/ are drawn too, and this gate is
            // scoped to the three lists §7 says drift together.
            if (path.StartsWith("circuitRF/src/", StringComparison.Ordinal))
                found.Add(path["circuitRF/".Length..]);
        }

        // A parse check, not a completeness one (that is EveryProjectUnderSrc_IsDrawnInTheSourceLayoutTree):
        // the tree is a map and was condensed when it moved to ARCHITECTURE.md, but it draws every project.
        int projects = Directory.GetDirectories(Path.Combine(RepoRoot(), "src")).Length;
        Assert.True(found.Count >= projects, $"only {found.Count} src/ folders were read out of the tree.");
        return found;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
