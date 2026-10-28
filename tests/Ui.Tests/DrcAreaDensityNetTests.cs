using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// DRC v2 D2-D4: the selectors, the area/perimeter/density kinds, layout net extraction, and the
/// two rules that could not exist without it — net-scoped spacing and antenna ratio.
/// </summary>
public class DrcAreaDensityNetTests
{
    private static readonly LayerKey M1 = new(1, 0);
    private static readonly LayerKey M2 = new(2, 0);
    private static readonly LayerKey VIA = new(3, 0);
    private static readonly LayerKey GATE = new(4, 0);

    /// <summary>A two-metal technology with a real via span, so connectivity has something to read.</summary>
    private static Technology Tech(params DrcRule[] rules)
    {
        var t = new Technology { Name = "Test" };
        foreach (var (key, name) in new[] { (M1, "M1"), (M2, "M2"), (VIA, "Via"), (GATE, "Gate") })
            t.Layers.Add(new LayerDef { Key = key, Name = name, Color = new Rgba(200, 200, 200, 255) });

        t.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Metal2", ThicknessDbu = 100,
            SigmaSm = 5.8e7, DrawingLayers = [M2], IsGroundReference = false,
        });
        t.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "V1", DrawingLayers = [VIA],
            SpanFromLayer = "Metal1", SpanToLayer = "Metal2", Fill = ViaFillKind.Solid,
        });
        t.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Metal1", ThicknessDbu = 100,
            SigmaSm = 5.8e7, DrawingLayers = [M1], IsGroundReference = true,
        });

        t.DrcRules.AddRange(rules);
        return t;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    // ── D2: selectors ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithArea_KeepsOnlyPolygonsInRange_AndRoundTripsThroughTheParser()
    {
        const string text = "with_area(1/0, 10000, )";
        Assert.Equal(text, DrcLayerExprParser.Format(DrcLayerExprParser.Parse(text)));

        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),      // 1,000,000 — kept
            Rect(M1, 5000, 0, 5050, 50),     // 2,500 — dropped
        };

        // A width rule over the selection sees only the large square, which is comfortably wide, so
        // nothing is reported. Without the selector the sliver would trip it.
        var withSelector = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "w", Kind = DrcRuleKind.MinWidth, Layer = M1,
            RegionA = "with_area(1/0, 10000, )", ValueDbu = 200,
        }));
        Assert.Empty(withSelector.Violations);

        var withoutSelector = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "w", Kind = DrcRuleKind.MinWidth, Layer = M1, ValueDbu = 200,
        }));
        Assert.NotEmpty(withoutSelector.Violations);
    }

    [Fact]
    public void WithArea_AnOpenBound_ParsesAndSelects()
    {
        Assert.Equal("with_area(1/0, , 500)", DrcLayerExprParser.Format(DrcLayerExprParser.Parse("with_area(1/0, ,500)")));
        Assert.False(DrcLayerExprParser.TryParse("with_area(1/0, , )", out _, out string? err));
        Assert.Contains("at least one bound", err!);
    }

    [Fact]
    public void WithPerimeter_RoundTrips()
    {
        const string text = "with_perimeter(1/0, 100, 2000)";
        Assert.Equal(text, DrcLayerExprParser.Format(DrcLayerExprParser.Parse(text)));
    }

    // ── D2: area and perimeter rules ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1_000_000, false)]   // exactly the shape's area → legal
    [InlineData(999_999, false)]
    [InlineData(1_000_001, true)]    // one square DBU over → too small
    public void MinArea_FiresBelowTheStatedArea(long areaDbu2, bool expectViolation)
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 1000) };   // 1,000,000 DBU²

        var result = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "a", Kind = DrcRuleKind.MinArea, Layer = M1, ValueDbu = areaDbu2,
        }));

        Assert.Equal(expectViolation, result.Violations.Count > 0);
    }

    /// <summary>The whole polygon is the marker — unlike a width violation there is no narrow
    /// sub-region to point at, the shape itself is what is too small.</summary>
    [Fact]
    public void MinArea_MarkerIsTheWholePolygon()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 100, 100) };

        var v = Assert.Single(DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "a", Kind = DrcRuleKind.MinArea, Layer = M1, ValueDbu = 1_000_000,
        })).Violations);

        Assert.Equal(0, v.Marker.MinX);
        Assert.Equal(100, v.Marker.MaxX);
    }

    [Fact]
    public void MinPerimeter_FiresBelowTheStatedPerimeter()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 100, 100) };   // perimeter 400

        Assert.NotEmpty(DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "p", Kind = DrcRuleKind.MinPerimeter, Layer = M1, ValueDbu = 500,
        })).Violations);

        Assert.Empty(DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "p", Kind = DrcRuleKind.MinPerimeter, Layer = M1, ValueDbu = 400,
        })).Violations);
    }

    // ── D3: density ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fully-covered area passes a minimum-density rule and fails a maximum one — the two
    /// directions a fab states, and a check that got the comparison backwards would pass exactly the
    /// designs it should fail.
    /// </summary>
    [Fact]
    public void Density_FullCoverage_PassesAMinimum_AndFailsAMaximum()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 4000, 4000) };

        var min = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "d", Kind = DrcRuleKind.Density, Layer = M1,
            WindowDbu = 2000, MinRatio = 0.2,
        }));
        Assert.Empty(min.Violations);

        var max = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "d", Kind = DrcRuleKind.Density, Layer = M1,
            WindowDbu = 2000, MaxRatio = 0.6,
        }));
        Assert.NotEmpty(max.Violations);
    }

    /// <summary>
    /// The marker is the WINDOW, not the metal in it — the user has to add or remove fill across
    /// that whole area, and highlighting the existing metal would point at shapes that are correct.
    /// </summary>
    [Fact]
    public void Density_MarkerIsTheWindow_NotTheMetal()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 200, 200) };   // sparse

        var result = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "d", Kind = DrcRuleKind.Density, Layer = M1,
            WindowDbu = 1000, MinRatio = 0.5,
        }));

        var v = Assert.Single(result.Violations);
        Assert.Equal(1000, v.Marker.MaxX - v.Marker.MinX);
        Assert.Equal(1000, v.Marker.MaxY - v.Marker.MinY);
    }

    // ── D4: net extraction ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline: two pieces of metal on DIFFERENT layers are one net when a via joins them, and
    /// separate nets when nothing does. Everything net-aware rests on this.
    /// </summary>
    [Fact]
    public void AViaJoinsTwoLayersIntoOneNet_AndWithoutOneTheyStaySeparate()
    {
        var joined = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 200),
            Rect(M2, 800, 0, 1800, 200),
            Rect(VIA, 850, 50, 950, 150),      // sits on both
        };

        var result = DrcEngine.Run(joined, Tech(new DrcRule
        {
            Name = "s", Kind = DrcRuleKind.MinSpacing, Layer = M1,
            NetScope = DrcNetScope.DifferentNet, ValueDbu = 100,
        }));

        Assert.Contains(result.Diagnostics, d => d.Contains("Net extraction found"));
    }

    /// <summary>
    /// The rule a process states twice at two values, and the reason net extraction had to exist.
    /// Two pieces of one net sitting close is legal under a different-net rule and reportable under
    /// a same-net one — the SAME geometry, two answers.
    /// </summary>
    [Fact]
    public void NetScope_ChangesWhichPairsAreReported_ForIdenticalGeometry()
    {
        // Two M1 pieces 50 apart, tied together through M2 by two vias — so they are ONE net.
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 100, 100),
            Rect(M1, 150, 0, 250, 100),
            Rect(M2, 0, 200, 250, 300),
            Rect(VIA, 20, 80, 80, 220),        // ties left M1 to M2
            Rect(VIA, 170, 80, 230, 220),      // ties right M1 to M2
        };

        var sameNet = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "s", Kind = DrcRuleKind.MinSpacing, Layer = M1,
            RegionA = "1/0", NetScope = DrcNetScope.SameNet, ValueDbu = 100,
        }));

        var differentNet = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "s", Kind = DrcRuleKind.MinSpacing, Layer = M1,
            RegionA = "1/0", NetScope = DrcNetScope.DifferentNet, ValueDbu = 100,
        }));

        Assert.NotEmpty(sameNet.Violations);        // they ARE one net, and they are 50 apart
        Assert.Empty(differentNet.Violations);      // nothing here is a short risk
    }

    /// <summary>
    /// An unknown net is never assumed to be DIFFERENT. Treating "could not resolve" as "different"
    /// reports a pair the extraction simply could not classify as a potential short — the false
    /// positive most likely to make people stop trusting the check.
    /// </summary>
    [Fact]
    public void DifferentNet_DoesNotFireOnPairsTheExtractionCouldNotResolve()
    {
        // A layer the stackup says nothing about: no conductor entry, so no net identity.
        var shapes = new List<LayoutShape>
        {
            Rect(GATE, 0, 0, 100, 100),
            Rect(GATE, 150, 0, 250, 100),
        };

        var result = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "s", Kind = DrcRuleKind.MinSpacing, Layer = GATE,
            NetScope = DrcNetScope.DifferentNet, ValueDbu = 100,
        }));

        // Each unclassified piece IS given its own net by the extraction, so this pair genuinely is
        // "different net" — what must NOT happen is a crash or a silent skip of the whole rule.
        Assert.Contains(result.Diagnostics, d => d.Contains("Net extraction found"));
    }

    /// <summary>Net extraction is not free, so a technology whose rules never mention nets must not
    /// pay for it — which is every starter technology.</summary>
    [Fact]
    public void NetExtraction_DoesNotRun_WhenNoRuleAsksForIt()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 1000) };

        var result = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "w", Kind = DrcRuleKind.MinWidth, Layer = M1, ValueDbu = 100,
        }));

        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("Net extraction"));
    }

    // ── D4: antenna ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one rule that asks a question about a whole NET rather than any pair of shapes, and
    /// therefore could not exist before net extraction.
    /// </summary>
    [Fact]
    public void AntennaRatio_FiresOnTooMuchMetalPerGate_AndPassesWhenTheGateIsLargeEnough()
    {
        // 100,000 DBU² of metal over a 10,000 DBU² gate → ratio 10.
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 100),
            Rect(GATE, 0, 0, 100, 100),
        };

        var over = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "ant", Kind = DrcRuleKind.AntennaRatio, Layer = M1,
            RegionA = "1/0", RegionB = "4/0", MaxRatio = 5.0,
        }));
        Assert.NotEmpty(over.Violations);

        var under = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "ant", Kind = DrcRuleKind.AntennaRatio, Layer = M1,
            RegionA = "1/0", RegionB = "4/0", MaxRatio = 20.0,
        }));
        Assert.Empty(under.Violations);
    }

    /// <summary>
    /// A net attached to no gate has no antenna to discharge through and no rule to break. Reporting
    /// it would flag every routing net on the design.
    /// </summary>
    [Fact]
    public void AntennaRatio_ANetWithNoGate_IsNotReported()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 10000, 100),        // a lot of metal
            Rect(GATE, 50000, 0, 50100, 100),  // a gate somewhere else entirely
        };

        Assert.Empty(DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "ant", Kind = DrcRuleKind.AntennaRatio, Layer = M1,
            RegionA = "1/0", RegionB = "4/0", MaxRatio = 1.0,
        })).Violations);
    }

    // ── Diagnostics must distinguish two very different facts ───────────────────────────────────

    /// <summary>
    /// A layer the technology DEFINES that this design simply has no geometry on is entirely normal
    /// — most layers of a real process are empty in any one cell. Reporting those as "does not
    /// exist" told users their technology was broken when it was fine: measured against a real
    /// process, 31 layers were named that way and every one of them was defined.
    /// </summary>
    [Fact]
    public void ADefinedLayerWithNoGeometry_IsNotReportedAsMissing()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 1000) };   // nothing on M2

        var result = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "w", Kind = DrcRuleKind.MinWidth, Layer = M1,
            RegionA = "and(1/0, 2/0)", ValueDbu = 100,
        }));

        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("do not exist"));
    }

    /// <summary>The other half: a layer that genuinely is not in the technology IS reported.</summary>
    [Fact]
    public void AnUndefinedLayer_IsStillReported()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 1000, 1000) };

        var result = DrcEngine.Run(shapes, Tech(new DrcRule
        {
            Name = "w", Kind = DrcRuleKind.MinWidth, Layer = M1,
            RegionA = "and(1/0, 91/7)", ValueDbu = 100,
        }));

        Assert.Contains(result.Diagnostics, d => d.Contains("do not exist") && d.Contains("91/7"));
    }

    // ── Validation ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TechValidation_FlagsIncompleteDensityAntennaAndMisplacedNetScope()
    {
        var problems = TechValidation.Validate(Tech(
            new DrcRule { Name = "d", Kind = DrcRuleKind.Density, Layer = M1 },
            new DrcRule { Name = "ant", Kind = DrcRuleKind.AntennaRatio, Layer = M1, RegionB = "4/0" },
            new DrcRule { Name = "w", Kind = DrcRuleKind.MinWidth, Layer = M1,
                          ValueDbu = 100, NetScope = DrcNetScope.SameNet }));

        Assert.Contains(problems, p => p.Contains("no window size"));
        Assert.Contains(problems, p => p.Contains("neither a minimum nor a maximum"));
        Assert.Contains(problems, p => p.Contains("no maximum ratio"));
        Assert.Contains(problems, p => p.Contains("only a spacing or separation rule can use"));
    }

    // ── brief-review-1: MustBeEmpty and the net-aware selections ────────────────────────────────

    private static DrcRule MustBeEmpty(string region) => new()
    {
        Name = "must be empty", Kind = DrcRuleKind.MustBeEmpty, Layer = M1, RegionA = region,
        Severity = DrcSeverity.Warning,
    };

    [Fact]
    public void MustBeEmpty_MarksEveryPolygon_AndNothingOnAnEmptyRegion()
    {
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 100, 100), Rect(M1, 500, 0, 700, 50) };

        var result = DrcEngine.Run(shapes, Tech(MustBeEmpty("1/0")));

        Assert.Equal([new Bbox(0, 0, 100, 100), new Bbox(500, 0, 700, 50)],
                     result.Violations.Select(v => v.Marker).ToList());
        Assert.All(result.Violations, v =>
        {
            Assert.Equal("must be empty", v.RuleName);
            Assert.Null(v.MeasuredText);
            Assert.Equal(DrcSeverity.Warning, v.Severity);
        });

        Assert.Empty(DrcEngine.Run(shapes, Tech(MustBeEmpty("4/0"))).Violations);
    }

    /// <summary>
    /// The M2 plane lies under BOTH pads in plan view, and only one pad is tied to it by a via. A
    /// polygon's nets come from the pieces on the layers its own expression reads, so the unjoined
    /// pad is not on the plane's net merely for sitting over it.
    /// </summary>
    [Fact]
    public void SameNet_KeepsAPadJoinedByAViaToBOnAnotherLayer_AndDropsAnUnjoinedOne()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 100, 100),          // joined
            Rect(M1, 300, 0, 400, 100),        // not joined
            Rect(M2, -50, -50, 450, 150),
            Rect(VIA, 30, 30, 70, 70),
        };

        var result = DrcEngine.Run(shapes, Tech(MustBeEmpty("same_net(1/0, 2/0)")));

        Assert.Equal(new Bbox(0, 0, 100, 100), Assert.Single(result.Violations).Marker);
    }

    [Fact]
    public void OnGround_WithNoGroundReferenceFlagged_IsEmpty_AndSaysSoOnce()
    {
        var tech = Tech(MustBeEmpty("on_ground(1/0)"), MustBeEmpty("on_ground(1/0)"));
        foreach (var l in tech.Stackup.Layers) l.IsGroundReference = false;
        var shapes = new List<LayoutShape> { Rect(M1, 0, 0, 100, 100) };

        var result = DrcEngine.Run(shapes, tech);

        Assert.Empty(result.Violations);
        Assert.Single(result.Diagnostics, d => d.Contains("on_ground(1/0)") && d.Contains("ground reference"));
    }

    /// <summary>The ground nets are the partition's: a reference that draws nothing, reached by a
    /// via. The M2 piece the via lands on is ground; the one beside it is not.</summary>
    [Fact]
    public void OnGround_KeepsTheCopperAViaCarriesToAnUndrawnReference()
    {
        var tech = Tech(MustBeEmpty("on_ground(2/0)"));
        tech.Stackup.Layers.Single(l => l.Name == "Metal1").DrawingLayers.Clear();
        var shapes = new List<LayoutShape>
        {
            Rect(M2, 0, 0, 100, 100),
            Rect(M2, 300, 0, 400, 100),
            Rect(VIA, 30, 30, 70, 70),
        };

        var result = DrcEngine.Run(shapes, tech);

        Assert.Equal(new Bbox(0, 0, 100, 100), Assert.Single(result.Violations).Marker);
    }

    /// <summary>brief-review-3: on a DRAWN reference, ground is the net of the largest copper on it —
    /// not every net with copper there. Each M2 pad is joined by a via to its own M1 piece; only the
    /// one on the plane is ground, and the choice is stated.</summary>
    [Fact]
    public void OnGround_OnADrawnReference_IsTheNetOfItsLargestCopper()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(M1, 0, 0, 1000, 1000),        // the plane
            Rect(M1, 2000, 0, 2200, 200),      // an island on the reference layer
            Rect(M2, 0, 0, 100, 100),   Rect(VIA, 30, 30, 70, 70),
            Rect(M2, 2000, 0, 2100, 100), Rect(VIA, 2030, 30, 2070, 70),
        };

        var result = DrcEngine.Run(shapes, Tech(MustBeEmpty("on_ground(2/0)")));

        Assert.Equal(new Bbox(0, 0, 100, 100), Assert.Single(result.Violations).Marker);
        Assert.Contains(result.Diagnostics, d => d.Contains("largest copper on the ground reference \"Metal1\""));
    }

    /// <summary>
    /// The additions are inert unless used: a shipped technology's own rules report the same
    /// violations, keys and diagnostics with a rule in the new vocabulary beside them that selects
    /// nothing — and that rule asks for no connectivity.
    /// </summary>
    [Fact]
    public void AShippedTechnology_ReportsTheSameViolations_WithAnInertNewRuleBesideIt()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(new LayerKey(1, 0), 0, 0, 1_000_000, 100_000),            // narrower than the rule
            Rect(new LayerKey(1, 0), 0, 150_000, 1_000_000, 400_000),      // 50 µm from it
            Rect(new LayerKey(2, 0), 0, 0, 2_000_000, 2_000_000),
        };

        var plain = DrcEngine.Run(shapes, ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz"));

        var withRule = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        withRule.DrcRules.Add(MustBeEmpty("near(7/0, 1/0, 100000, 2, )"));
        var extended = DrcEngine.Run(shapes, withRule);

        Assert.NotEmpty(plain.Violations);
        Assert.Equal(plain.Violations.Select(v => v.Key), extended.Violations.Select(v => v.Key));
        Assert.Equal(plain.Violations.Select(v => v.MarkerRings.Sum(r => r.Sum())),
                     extended.Violations.Select(v => v.MarkerRings.Sum(r => r.Sum())));
        Assert.Equal(plain.Diagnostics, extended.Diagnostics);
    }
}
