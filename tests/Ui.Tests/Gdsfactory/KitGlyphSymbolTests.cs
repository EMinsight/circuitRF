using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Gdsfactory;

/// <summary>
/// brief-gdf-4 §3, tier A: a symbol built from a kit's own SVG glyph, on synthetic glyphs. The pins come from
/// the cell's ports; the drawing is the glyph's; what cannot be drawn is counted.
/// </summary>
public sealed class KitGlyphSymbolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-gdf4-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    private static KitGlyphResult Built(string svg, IReadOnlyList<KitGlyphPort> ports)
        => KitGlyphSymbol.TryBuild(svg, ports, out string? problem) ?? throw new Xunit.Sdk.XunitException(problem);

    /// <summary>A 100 × 100 two-terminal glyph: a body with a lead from each side's middle to the box edge.</summary>
    private const string TwoLead = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
          <line x1="0" y1="50" x2="25" y2="50" stroke="black"/>
          <rect x="25" y="35" width="50" height="30" fill="none" stroke="black"/>
          <line x1="75" y1="50" x2="100" y2="50" stroke="black"/>
        </svg>
        """;

    [Fact]
    public void ATwoLeadHorizontalGlyph_PutsItsPinsOnTheLeadEnds()
    {
        var built = Built(TwoLead,
            [new KitGlyphPort("e1", 0, 0, 180), new KitGlyphPort("e2", 10, 0, 0)]);

        var e1 = built.Symbol.Pins.Single(p => p.Name == "e1");
        var e2 = built.Symbol.Pins.Single(p => p.Name == "e2");
        Assert.Equal((-200.0, 0.0), (e1.LocalX, e1.LocalY));
        Assert.Equal((200.0, 0.0), (e2.LocalX, e2.LocalY));
        Assert.Equal(SymbolPinNameAlign.Left, e1.NameAlign);
        Assert.Equal(SymbolPinNameAlign.Right, e2.NameAlign);

        // The lead ends map exactly onto the pins, so nothing had to be carried out to the grid: the glyph's
        // three shapes are all there is.
        Assert.Equal(3, built.Symbol.Primitives.Count);
        Assert.Empty(built.PinsOffLead);
        Assert.Empty(built.Notes);
    }

    [Fact]
    public void FourPortsOnAnEdgeWithTwoLeadEnds_GiveFourPins_AndSayTwoAreNotOnALead()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <line x1="0" y1="30" x2="20" y2="30"/>
              <line x1="0" y1="70" x2="20" y2="70"/>
              <rect x="20" y="10" width="60" height="80" fill="none" stroke="black"/>
            </svg>
            """;
        // Kit y runs up: a is the top port, d the bottom.
        var built = Built(svg,
        [
            new KitGlyphPort("a", 0, 30, 180), new KitGlyphPort("b", 0, 20, 180),
            new KitGlyphPort("c", 0, 10, 180), new KitGlyphPort("d", 0, 0, 180),
        ]);

        var pins = built.Symbol.Pins.OrderBy(p => p.LocalY).ToList();
        Assert.Equal(["a", "b", "c", "d"], pins.Select(p => p.Name));
        Assert.Single(pins.Select(p => p.LocalX).Distinct());
        Assert.All(pins, p => Assert.Equal(0, p.LocalY % 100));
        Assert.Equal(4, pins.Select(p => p.LocalY).Distinct().Count());

        Assert.Equal(2, built.PinsOffLead.Count);
        var note = Assert.Single(built.Notes);
        Assert.True(note.Warning);
        Assert.StartsWith("2 of 4 pin(s) on the left edge are not on a lead", note.Text);
    }

    [Fact]
    public void AnSvgFilter_IsDroppedAndCounted_AndTheRestConverts()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <defs><filter id="glow"><feGaussianBlur stdDeviation="2"/></filter></defs>
              <style>@media (prefers-color-scheme: dark) { line { stroke: white; } }</style>
              <line x1="0" y1="50" x2="25" y2="50"/>
              <path d="M25 50 C 40 20, 60 80, 75 50" fill="none" stroke="black" filter="url(#glow)"/>
              <line x1="75" y1="50" x2="100" y2="50"/>
            </svg>
            """;
        var built = Built(svg,
            [new KitGlyphPort("e1", 0, 0, 180), new KitGlyphPort("e2", 10, 0, 0)]);

        Assert.Equal(new Dictionary<string, int> { ["filter"] = 1 }, built.Dropped);
        Assert.Contains(built.Notes, n => n.Text == "Not drawn from the glyph: filter ×1.");
        Assert.Equal(2, built.Symbol.Primitives.OfType<LinePrimitive>().Count());
        Assert.Single(built.Symbol.Primitives.OfType<CubicCurvePrimitive>());
        Assert.Empty(built.PinsOffLead);
    }

    /// <summary>A kit may say what is filled only through its stylesheet. Its class rules are read for that; its
    /// dark-mode rules are not, and a faint fill keeps its own strength rather than covering the drawing.</summary>
    [Fact]
    public void TheGlyphsClassRules_DecideHowStronglyAShapeIsFilled_AndItsDarkModeRulesDoNot()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <style>.s{fill:none;stroke:#333}.f{fill:#333;fill-opacity:.25}.k{fill:#333}
                @media (prefers-color-scheme:dark){.s{fill:#ccc}}</style>
              <rect class="s" x="10" y="10" width="30" height="30"/>
              <rect class="s f" x="60" y="10" width="30" height="30"/>
              <circle class="k" cx="50" cy="70" r="10"/>
            </svg>
            """;
        var built = Built(svg, []);

        var rects = built.Symbol.Primitives.OfType<RectPrimitive>().ToList();
        Assert.Equal(2, rects.Count);
        Assert.All(rects, r => Assert.False(r.Filled));
        Assert.Equal([0.0, 0.25], rects.Select(SymbolFill.Of));
        Assert.True(Assert.Single(built.Symbol.Primitives.OfType<CirclePrimitive>()).Filled);
    }

    /// <summary>The precedence is the bridge's: it reports a cell's kit <c>.csym</c> and never its glyph of the same stem.</summary>
    [PythonFact]
    public void AKitCsymWithTheSameStem_WinsOverTheGlyph()
    {
        string symbols = Path.Combine(_dir, "kit", "symbols");
        Directory.CreateDirectory(symbols);
        File.WriteAllText(Path.Combine(symbols, "res.svg"), TwoLead);
        File.WriteAllText(Path.Combine(symbols, "res.csym"), "{}");
        File.WriteAllText(Path.Combine(symbols, "cap.svg"), TwoLead);

        var info = new ProcessStartInfo(PythonRunner.Interpreter!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(
            "import sys; from gdsfactory_bridge.bridge import _drawing_dirs, _find_drawing as f; " +
            "d = _drawing_dirs(sys.argv[1], None); l = {}; " +
            "print(f('res', d, l)[0], f('cap', d, l)[0], f('ind', d, l))");
        info.ArgumentList.Add(Path.Combine(_dir, "kit"));
        info.Environment["PYTHONPATH"] = PythonRunner.PackageRoot;

        using var process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd().Trim();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        Assert.Equal("symbol glyph None", stdout);
    }
}
