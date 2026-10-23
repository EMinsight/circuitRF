// ================================================================
//  `render --outline-text` (and `plot --outline-text`): an SVG's text as glyph outlines, so a viewer
//  that does not have IBM Plex Sans draws the letters circuitRF drew instead of substituting a face.
//  The outlining is SvgTextOutliner (src/Render); the verbs only ask for it.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Render;
using SkiaSharp;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

public sealed class OutlineTextTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-outline-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static readonly Regex TextRun = new(@"<text(?<attrs>[^>]*)>.*?</text>", RegexOptions.Singleline);
    private static readonly Regex OutlineGroup = new(@"<g fill=""[^""]*""(?: transform=""[^""]*"")?>(?:<path d=""(?<d>[^""]*)""/>)?</g>");

    /// <summary>
    /// Every run becomes an outline group, nothing else in the file changes, and each group's ink lies
    /// inside its own run's box — so the glyphs are where Skia placed the text, not at an origin.
    /// </summary>
    [Fact]
    public void OutlinedSvg_HasNoText_ChangesNothingElse_AndPutsEachGlyphRunWhereItsTextWas()
    {
        Directory.CreateDirectory(_root);
        string csch = Path.Combine(RepoRoot(), "examples", "Envelope", "NR20_ACPR", "schematic", "NR20_ACPR.csch");
        string plain = Path.Combine(_root, "plain.svg"), outlined = Path.Combine(_root, "outlined.svg");

        Assert.Equal(0, RunCli("render", csch, "-o", plain).ExitCode);
        var (exit, stdout, stderr) = RunCli("render", csch, "-o", outlined, "--outline-text");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        string a = File.ReadAllText(plain), b = File.ReadAllText(outlined);
        var runs = TextRun.Matches(a);
        var groups = OutlineGroup.Matches(b);
        Assert.NotEmpty(runs);
        Assert.DoesNotContain("<text", b);
        Assert.Equal(runs.Count, groups.Count);
        Assert.Equal(TextRun.Replace(a, "@"), OutlineGroup.Replace(b, "@"));

        for (int i = 0; i < runs.Count; i++)
        {
            string attrs = runs[i].Groups["attrs"].Value;
            float size = Number(attrs, "font-size")[0];
            float[] xs = Number(attrs, "x");
            float y = Number(attrs, "y")[0];
            string d = groups[i].Groups["d"].Value;
            if (d.Length == 0) continue;                       // a run of spaces has no ink

            using var path = SKPath.ParseSvgPathData(d);
            var ink = path.Bounds;
            Assert.InRange(ink.Left,   xs[0]  - 0.2f * size, xs[^1] + size);
            Assert.InRange(ink.Right,  xs[0],                xs[^1] + 1.2f * size);
            Assert.InRange(ink.Top,    y - 1.2f * size,      y);
            Assert.InRange(ink.Bottom, y - 0.8f * size,      y + 0.4f * size);
        }
    }

    /// <summary>A PDF embeds its fonts and a PNG has no text: the flag is refused there, never ignored.</summary>
    [Fact]
    public void OutlineText_OnARasterOutput_IsRefused()
    {
        string csch = Path.Combine(RepoRoot(), "examples", "Envelope", "NR20_ACPR", "schematic", "NR20_ACPR.csch");
        var (exit, _, stderr) = RunCli("render", csch, "-o", Path.Combine(_root, "x.png"), "--outline-text");

        Assert.Equal(1, exit);
        Assert.Contains("--outline-text applies to svg only", stderr);
        Assert.False(File.Exists(Path.Combine(_root, "x.png")));
    }

    /// <summary>A family circuitRF does not embed stays as text, reported once; a Plex run beside it is outlined.</summary>
    [Fact]
    public void AFamilyThatIsNotEmbedded_IsLeftAsText_AndReported()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="60">
            	<text fill="#000000" font-size="14" font-family="IBM Plex Sans" x="10, 18" y="20">
            			Ab
            	</text>
            	<text fill="#000000" font-size="14" font-family="Lucida Grande" x="10" y="40">
            			▲
            	</text>
            </svg>
            """;

        string result = SvgTextOutliner.Outline(svg, out var unresolved);

        Assert.Equal(["Lucida Grande"], unresolved);
        Assert.Single(TextRun.Matches(result));
        Assert.Contains("Lucida Grande", result);
        Assert.Matches(@"<g fill=""#000000""><path d=""[^""]+""/></g>", result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static float[] Number(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"(?:^|\s){Regex.Escape(name)}=""(?<v>[^""]*)""");
        return m.Groups["v"].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(OutlineTextTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
