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

    // ── The converter on glyphs it once misread (review of brief-gdf-4) ──

    /// <summary>A build that must finish: a regression to an endless loop fails here instead of hanging the run.</summary>
    private static KitGlyphResult BuiltWithin(string svg, IReadOnlyList<KitGlyphPort> ports, int seconds = 10)
    {
        var task = System.Threading.Tasks.Task.Run(() => Built(svg, ports));
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), $"the glyph did not convert within {seconds} s");
        return task.Result;
    }

    private static string Svg(string body) => $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">{body}</svg>""";

    [Fact]
    public void ANumberAfterAClosepath_EndsThePath_AndIsCounted()
    {
        var built = BuiltWithin(Svg("""<path d="M10 10 L20 20 L10 30 Z 5" fill="none" stroke="black"/>"""), []);

        Assert.Single(built.Symbol.Primitives.OfType<PolylinePrimitive>());
        Assert.Equal(1, built.Dropped["path (unreadable data)"]);
    }

    [Fact]
    public void ManyPortsOnAnEdgeWithHalfAsManyLeads_StillPlaceEveryPin()
    {
        string leads = string.Concat(Enumerable.Range(0, 20).Select(i => $"""<line x1="0" y1="{2 + i * 4.8:0.##}" x2="10" y2="{2 + i * 4.8:0.##}"/>"""));
        var ports = Enumerable.Range(0, 40).Select(i => new KitGlyphPort("p" + i, 0, -i, 180)).ToList();

        var built = BuiltWithin(Svg(leads), ports);

        Assert.Equal(40, built.Symbol.Pins.Select(p => p.LocalY).Distinct().Count());
    }

    [Fact]
    public void AFillStatedOnTheRootSvg_IsInheritedByItsShapes()
    {
        var built = Built("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" fill="none" stroke="black">
              <rect x="20" y="20" width="60" height="60"/><circle cx="50" cy="50" r="10"/>
            </svg>
            """, []);

        Assert.Equal(2, built.Symbol.Primitives.Count);
        Assert.All(built.Symbol.Primitives, p => Assert.Equal(0, SymbolFill.Of(p)));
    }

    /// <summary>A frame drawn on the box edge offers no lead ends: from its corners, a side along the top would look
    /// like a lead into the left edge.</summary>
    [Fact]
    public void AFrameOnTheBoxEdge_IsNotALead_SoAPinTakesTheGlyphsRealLead()
    {
        var built = Built(Svg("""
            <path d="M0 0 H100 V100 H0 Z" fill="none" stroke="black"/>
            <line x1="0" y1="50" x2="30" y2="50" stroke="black"/>
            """), [new KitGlyphPort("a", 0, 0, 180), new KitGlyphPort("b", 0, -10, 180)]);

        Assert.Contains(built.Symbol.Pins, p => p.LocalY == 0);
        Assert.Single(built.PinsOffLead);
    }

    [Fact]
    public void AnOutlinePathEndingInACurve_StillDrawsItsClosingEdge()
    {
        var built = Built(Svg("""<path d="M10 50 L50 50 C60 10 90 10 90 50 Z" fill="none" stroke="black"/>"""), []);

        // The closing edge, glyph (90,50)→(10,50): local (160,0)→(−160,0) on the 400-unit symbol a portless glyph gets.
        Assert.Contains(built.Symbol.Primitives.OfType<PolylinePrimitive>(),
            pl => pl.Points.Count == 2 && pl.Points[0][0] > pl.Points[1][0] && pl.Points[0][1] == pl.Points[1][1]);
    }

    /// <summary>The inner subpath of a filled ring is a hole, left open by keyholing it into the outer; and SVG fills
    /// an open subpath as if it were closed.</summary>
    [Fact]
    public void AFilledPathsHoleStaysOpen_AndAnOpenFilledSubpathIsFilled()
    {
        var ring = Built(Svg("""<path fill-rule="evenodd" d="M10 10 H90 V90 H10 Z M40 40 H60 V60 H40 Z" fill="black"/>"""), []);
        var polygon = Assert.Single(ring.Symbol.Primitives.OfType<PolygonPrimitive>());
        Assert.True(polygon.Filled);
        Assert.False(SymbolGeometry.HitTest(polygon, 0, 0, 1));       // the hole's centre
        Assert.True(SymbolGeometry.HitTest(polygon, 0, 120, 1));      // the ring's body

        var open = Built(Svg("""<path d="M10 10 L90 10 L50 90" fill="black"/>"""), []);
        Assert.True(Assert.Single(open.Symbol.Primitives.OfType<PolygonPrimitive>()).Filled);
    }

    /// <summary>Shapes nobody sees in the glyph are not drawn: SVG strokes nothing unless told to.</summary>
    [Fact]
    public void InvisibleShapes_AreNotDrawn()
    {
        var built = Built(Svg("""
            <rect x="0" y="0" width="100" height="100" fill="none" stroke="none"/>
            <rect x="0" y="0" width="100" height="100" fill="transparent"/>
            <rect x="10" y="10" width="80" height="80" opacity="0" fill="black"/>
            <circle cx="50" cy="50" r="10" fill="none" stroke="black"/>
            """), []);

        Assert.IsType<CirclePrimitive>(Assert.Single(built.Symbol.Primitives));
    }

    [Fact]
    public void AStylesheetInDefs_IsReadAndNotReportedAsDropped()
    {
        var built = Built(Svg("""<defs><style>.s{fill:none;stroke:#000}</style></defs><rect class="s" x="20" y="20" width="60" height="60"/>"""), []);

        Assert.Empty(built.Dropped);
        Assert.Equal(0, SymbolFill.Of(Assert.Single(built.Symbol.Primitives)));
    }

    [Fact]
    public void AnUnknownPathCommand_KeepsWhatWasReadBeforeIt_AndIsCounted()
    {
        var built = Built(Svg("""<path d="M10 10 L50 50 L90 10 X" fill="none" stroke="black"/>"""), []);

        Assert.Equal(3, Assert.Single(built.Symbol.Primitives.OfType<PolylinePrimitive>()).Points.Count);
        Assert.Equal(1, built.Dropped["path (unreadable data)"]);
    }

    /// <summary>fill-opacity is inherited, so a child's own value replaces its group's; opacity composes.</summary>
    [Fact]
    public void FillOpacityIsInheritedNotCompounded_WhileOpacityComposes()
    {
        var built = Built(Svg("""
            <g fill-opacity="0.5"><rect fill-opacity="0.5" x="10" y="10" width="30" height="30" fill="black"/></g>
            <g opacity="0.5"><rect fill-opacity="50%" x="60" y="10" width="30" height="30" fill="black"/></g>
            """), []);

        Assert.Equal([0.5, 0.25], built.Symbol.Primitives.Select(SymbolFill.Of));
    }

    /// <summary>A white background written as rgb() is still the background, and a gradient fill — whose definition
    /// is dropped and counted — is not carried as a solid block.</summary>
    [Fact]
    public void AnRgbWhiteBackground_IsNotDrawn_AndAGradientFillIsAnOutline()
    {
        var built = Built(Svg("""
            <rect x="0" y="0" width="100" height="100" fill="rgb(255, 255, 255)"/>
            <rect x="20" y="20" width="60" height="60" fill="url(#g)" stroke="black"/>
            """), []);

        Assert.Equal(0, SymbolFill.Of(Assert.Single(built.Symbol.Primitives)));
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
