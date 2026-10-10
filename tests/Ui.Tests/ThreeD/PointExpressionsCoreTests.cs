// ================================================================
//  PointExpressionsCoreTests.cs — brief-em3d-132, the core of expressions in a wire's and a polyline's points: the JSON
//  round trip (gate 2), evaluation before anything reads the points (gate 3), the offset and swap writers and the moved
//  renumbering (gate 4), check naming the point (gate 5), and Flatten through the writers (R-em3d132-5). Gate 1, the
//  byte identity of every shipped and fixture .c3d, is TerminalWavePortTests.Gate5.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class PointExpressionsCoreTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pointexpr-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── gate 2: the JSON contract ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_AWireAndBothPolylineForms_RoundTripWithTheirPointExpressions()
    {
        var w = Wire();
        Bind(w, nameof(C3dWire.Points), 1, 2, "h_loop", "Mil");
        Bind(w, nameof(C3dWire.Points), 2, 0, "x_pad + 50um", "Um");
        var flat = new C3dPolyline { Name = "p2", Points = [new(0, 0), new(254 * Um, 0), new(0, 254 * Um)] };
        Bind(flat, nameof(C3dPolyline.Points), 1, 0, "x_v", "Mil");
        var bent = new C3dPolyline { Name = "p3", Points3 = [new(0, 0, 0), new(10 * Um, 0, 5 * Um)] };
        Bind(bent, nameof(C3dPolyline.Points3), 1, 2, "2*t", "Um");
        var doc = new C3dDocument { Objects = [w, flat, bent] };

        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("\t[{ \"Expr\": \"x_pad + 50um\", \"Unit\": \"Um\" }, 20000, 20000]", text);   // a point is still one line
        Assert.Contains("[{ \"Expr\": \"x_v\", \"Unit\": \"Mil\" }, 0]", text);
        Assert.DoesNotContain("\"Value\"", text);
        var back = C3dPersistence.Deserialize(text);
        Assert.Equal(text, C3dPersistence.Serialize(back));
        Assert.Equal(["Points[1]", "Points[2]"], back.Objects[0].Exprs!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new C3dExpr("h_loop", "Mil"), C3dBindings.GetExpr(back.Objects[0], "Points[1]", 2));
        Assert.Equal(new C3dExpr("x_v", "Mil"), C3dBindings.GetExpr(back.Objects[1], "Points[1]", 0));
        Assert.Equal(new C3dExpr("2*t", "Um"), C3dBindings.GetExpr(back.Objects[2], "Points3[1]", 2));

        // An in-memory copy (an undo entry) carries the resolved number beside the expression, as every bound field does.
        var copy = (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));
        Assert.Equal(w.Points, copy.Points);
        Assert.Equal(new C3dExpr("h_loop", "Mil"), C3dBindings.GetExpr(copy, "Points[1]", 2));
    }

    [Fact]
    public void AnExpressionUnderAPolylinesIgnoredPoints_IsARefusalNamingIt()
    {
        string text = """
            { "FormatVersion": 1, "Objects": [ { "$type": "Polyline", "Name": "p",
              "Points": [[{ "Expr": "x_v", "Unit": "Mil" }, 0], [1, 1]], "Points3": [[0, 0, 0], [1, 1, 1]] } ] }
            """;
        var x = Assert.ThrowsAny<Exception>(() => C3dPersistence.Deserialize(text));
        Assert.Contains("Polyline 'p' has Points3", x.Message + x.InnerException?.Message);
        Assert.Contains("Points[0]", x.Message + x.InnerException?.Message);
    }

    // ── gate 3: evaluation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AnApexBoundToHLoop_GivesTwoLoopHeights_AndAPolylineVertexMovesWithXv()
    {
        string ws = Workspace();
        var w = Wire();
        Bind(w, nameof(C3dWire.Points), 1, 2, "h_loop", "Um");
        var pl = new C3dPolyline { Name = "outline", Points = [new(0, 0), new(100 * Um, 0), new(0, 100 * Um)] };
        Bind(pl, nameof(C3dPolyline.Points), 1, 0, "x_v", "Um");
        var doc = new C3dDocument
        {
            Variables = [new C3dVariable { Name = "h_loop", Expression = "200", Unit = "Um" }, new C3dVariable { Name = "x_v", Expression = "120", Unit = "Um" }],
            Objects = [.. Pads(), w, pl],
        };
        string path = WriteC3d(ws, doc);

        double Loop(string h)
        {
            var d = C3dPersistence.LoadFromFile(path);
            d.Variables[0].Expression = h;
            var e = C3dElaborator.ElaborateOnce(d, path, null);
            Assert.True(e.Ok, string.Join(" ", e.Refusals));
            return e.Wires.Single(r => r.Name == "w1").AssemblyLoopHeightM;
        }
        double low = Loop("200"), high = Loop("300");
        Assert.True(high - low > 50e-6, $"{low} → {high}");

        var resolved = C3dPersistence.LoadFromFile(path);
        resolved.Variables[1].Expression = "150";
        Assert.True(C3dResolver.Resolve(resolved, C3dCell.Of(path)).Ok);
        Assert.Equal(150 * Um, ((C3dPolyline)resolved.Objects[^1]).Points[1].U);
    }

    // ── gate 4: the pure functions ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("x_pad", 20, "x_pad + 20um")]                // plain append, in the display unit
    [InlineData("x_pad + 50um", 20, "x_pad + 70um")]         // fold into the trailing literal
    [InlineData("x_pad + 20um", -20, "x_pad")]               // a fold that reaches zero drops the term
    [InlineData("x_pad + 5um", -20, "x_pad - 15um")]         // the sign becomes the operator
    [InlineData("x_pad", -20, "x_pad - 20um")]               // a negative offset is never "+ -20um"
    [InlineData("a < b ? c : d", 20, "(a < b ? c : d) + 20um")]   // a root looser than + is parenthesised
    [InlineData("2*w", 20, "2*w + 20um")]                    // a product is not
    [InlineData("x_pad + 5mil", 20, "x_pad + 5mil + 20um")]  // a fold that is not exact in the literal's unit appends
    public void Gate4_TheOffsetWriter(string expr, long offsetUm, string expected)
        => Assert.Equal(expected, C3dPointExpressions.OffsetExpression(new C3dExpr(expr, "Um"), offsetUm * Um, LayoutUnit.Um, 1000).Expr);

    [Fact]
    public void Gate4_AZeroOffset_LeavesTheTextByteIdentical_AndABareLiteralFoldsInTheSiteUnit()
    {
        var e = new C3dExpr("x_pad+ 50um ", "Um");
        Assert.Same(e, C3dPointExpressions.OffsetExpression(e, 0, LayoutUnit.Mil, 1000));
        Assert.Equal("x + 3", C3dPointExpressions.OffsetExpression(new C3dExpr("x + 2", "Mil"), 25_400, LayoutUnit.Mil, 1000).Expr);
        Assert.Equal("x_pad+ 70um ", C3dPointExpressions.OffsetExpression(e, 20 * Um, LayoutUnit.Um, 1000).Expr);   // spacing kept
    }

    [Theory]
    [InlineData("ey", 1, 0, "ey")]                           // +1 with a zero constant: byte-identical
    [InlineData("ey", -1, 0, "-(ey)")]                       // -1 on an atom
    [InlineData("a*b", -1, 0, "-(a*b)")]                     // -1 on a compound
    [InlineData("t + 5um", -1, 0, "-(t) - 5um")]             // the trailing literal stays outside, sign flipped
    [InlineData("-(-(e))", -1, 0, "-(e)")]                   // a negation is cancelled, not stacked
    [InlineData("-(e)", -1, 0, "e")]
    [InlineData("5um", -1, 0, "-5um")]
    [InlineData("ey", -1, 30, "-(ey) + 30um")]               // the constant through the offset writer
    public void Gate4_TheSwapWriter(string expr, int sign, long offsetUm, string expected)
        => Assert.Equal(expected, C3dPointExpressions.SwapExpression(new C3dExpr(expr, "Um"), sign, offsetUm * Um, LayoutUnit.Um, 1000).Expr);

    [Fact]
    public void Gate4_AQuarterTurnAndItsInverse_ReadBackAsTheOriginalText()
    {
        var w = Wire();
        Bind(w, nameof(C3dWire.Points), 0, 0, "x_pad + 50um", "Um");
        Bind(w, nameof(C3dWire.Points), 0, 1, "ey", "Um");
        Bind(w, nameof(C3dWire.Points), 1, 2, "h_loop", "Mil");
        var before = C3dBindings.Copy(w.Exprs)!;
        var points = w.Points.ToList();

        var turn = C3dTransform.Rotation(C3dAxis.Z, 90).Then(C3dTransform.Translation(new C3dPoint3(30 * Um, -10 * Um, 7 * Um)));
        w.Placement = C3dPlacement.Canonical(turn, out _);
        Assert.Null(C3dWires.BakePlacement(w, LayoutUnit.Um, 1000, out bool exact));
        Assert.True(exact);
        Assert.Equal("-(ey) + 30um", C3dBindings.GetExpr(w, "Points[0]", 0)!.Expr);
        Assert.Equal("x_pad + 40um", C3dBindings.GetExpr(w, "Points[0]", 1)!.Expr);
        Assert.Equal("h_loop + 7um", C3dBindings.GetExpr(w, "Points[1]", 2)!.Expr);

        w.Placement = C3dPlacement.Canonical(turn.Inverse(), out _);
        Assert.Null(C3dWires.BakePlacement(w, LayoutUnit.Um, 1000, out _));
        Assert.Equal(points, w.Points);
        Assert.Equal(before.Keys.Order(), w.Exprs!.Keys.Order());
        foreach (var (key, slots) in before) Assert.Equal(slots, w.Exprs[key]);
    }

    [Fact]
    public void Gate4_AnyOtherTurnOfABoundPoint_IsRefusedByName_AndAnUnboundWireTurns()
    {
        var w = Wire();
        Bind(w, nameof(C3dWire.Points), 1, 2, "h_loop", "Mil");
        w.Placement = new C3dPlacement { Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 30 }] };
        string? why = C3dWires.BakePlacement(w, LayoutUnit.Um, 1000, out _);
        Assert.Contains("'w1' point 2 z holds h_loop", why);
        Assert.Equal(Wire().Points, w.Points);                                // nothing changed

        var free = Wire();
        free.Placement = new C3dPlacement { Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 30 }] };
        Assert.Null(C3dWires.BakePlacement(free, LayoutUnit.Um, 1000, out _));
    }

    /// <summary>Moved from the editor with deb6199f's assertions, generalised over the property: an insert or a remove
    /// carries each later point's entries with it, another list's keys are left alone, and a key of any other shape is
    /// refused rather than guessed at.</summary>
    [Fact]
    public void Gate4_RenumberPointExpressions_FollowsItsPoints_AndRefusesAShapeItCannotRenumber()
    {
        static C3dPolyline With(params string[] keys)
            => new() { Exprs = keys.ToDictionary(k => k, _ => new C3dExpr?[1], StringComparer.Ordinal) };

        var p = With("Points[0]", "Points[1]", "Points[2]", "Offset", "Points3[1]");
        Assert.True(C3dPointExpressions.RenumberPointExpressions(p, nameof(C3dPolyline.Points), from: 1, removed: null));
        Assert.Equal(["Offset", "Points3[1]", "Points[0]", "Points[2]", "Points[3]"], p.Exprs!.Keys.Order(StringComparer.Ordinal));

        p = With("Points[0]", "Points[1]", "Points[2]");
        Assert.True(C3dPointExpressions.RenumberPointExpressions(p, nameof(C3dPolyline.Points), from: 2, removed: 1));
        Assert.Equal(["Points[0]", "Points[1]"], p.Exprs!.Keys.Order(StringComparer.Ordinal));

        p = With("Points3[0]", "Points3[1]");
        Assert.True(C3dPointExpressions.RenumberPointExpressions(p, nameof(C3dPolyline.Points3), from: 1, removed: 0));
        Assert.Equal(["Points3[0]"], p.Exprs!.Keys);

        p = With("Points");
        Assert.False(C3dPointExpressions.RenumberPointExpressions(p, nameof(C3dPolyline.Points), from: 1, removed: null));
        Assert.Equal(["Points"], p.Exprs!.Keys);
    }

    // ── R-em3d132-5: flatten ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Flatten_TurnsABoundWireThroughTheWriters_AndTheParentResolvesIt()
    {
        string ws = Workspace();
        var w = Wire();
        Bind(w, nameof(C3dWire.Points), 1, 2, "h_loop", "Um");
        Bind(w, nameof(C3dWire.Points), 1, 0, "x_mid", "Um");
        var vars = new List<C3dVariable> { new() { Name = "h_loop", Expression = "200", Unit = "Um" }, new() { Name = "x_mid", Expression = "350", Unit = "Um" } };
        WriteC3d(ws, new C3dDocument { Variables = vars, Objects = [w] }, "Bond");
        var parent = new C3dDocument
        {
            Variables = [.. vars.Select(v => new C3dVariable { Name = v.Name, Expression = v.Expression, Unit = v.Unit })],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Bond", Placement = new C3dPlacement { Origin = new(1000 * Um, 0, 0), Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 90 }] } }],
        };
        string path = WriteC3d(ws, parent);
        var e = C3dElaborator.ElaborateOnce(parent, path, null);

        var r = C3dHierarchy.FlattenOne(parent, path, 0, e, e.Technology);
        Assert.Null(r.Refusal);
        var flat = Assert.IsType<C3dWire>(Assert.Single(r.Objects));
        Assert.Equal("h_loop", C3dBindings.GetExpr(flat, "Points[1]", 2)!.Expr);
        Assert.Equal("x_mid", C3dBindings.GetExpr(flat, "Points[1]", 1)!.Expr);
        Assert.Null(C3dBindings.GetExpr(flat, "Points[1]", 0));
        parent.Instances.Clear();
        parent.Objects.Add(flat);
        Assert.True(C3dResolver.Resolve(parent, C3dCell.Of(path)).Ok);
        Assert.Equal(new C3dPoint3(1000 * Um - 20 * Um, 350 * Um, 200 * Um), flat.Points[1]);

        parent.Objects.Clear();
        parent.Instances.Add(new C3dInstance { Name = "U2", CellRef = "../../Bond", Placement = new C3dPlacement { Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 30 }] } });
        var refused = C3dHierarchy.FlattenOne(parent, path, 0, C3dElaborator.ElaborateOnce(parent, path, null), e.Technology);
        Assert.Contains("'w1' point 2", refused.Refusal);
    }

    // ── gate 5: check ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_CheckExits1OnAnUnresolvablePoint_NamingObjectPointAndComponent_AndExplainListsIt()
    {
        string ws = Workspace();
        var w = Wire();
        Bind(w, nameof(C3dWire.Points), 1, 2, "nowhere", "Um");
        Bind(w, nameof(C3dWire.Points), 2, 0, "x_pad", "Um");
        string path = WriteC3d(ws, new C3dDocument { Variables = [new C3dVariable { Name = "x_pad", Expression = "650", Unit = "Um" }], Objects = [.. Pads(), w] });

        var check = RunCli("check", path);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("'w1' point 2 z = nowhere: unknown: nowhere", check.StdErr);

        var explain = RunCli("explain", path);
        Assert.Contains("'w1' point 3 x", explain.StdOut + explain.StdErr);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static C3dWire Wire() => new()
    {
        Name = "w1", Material = "Gold",
        Points = [new(50 * Um, 20 * Um, 20 * Um), new(350 * Um, 20 * Um, 200 * Um), new(650 * Um, 20 * Um, 20 * Um)],
    };

    private static C3dObject[] Pads() =>
    [
        new C3dBox { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) },
        new C3dBox { Name = "lead", Material = "Gold", Min = new(600 * Um, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) },
    ];

    private static void Bind(IC3dBindable owner, string property, int point, int k, string expr, string? unit)
        => C3dBindings.SetExpr(owner, C3dBindings.SpecOf(owner.GetType(), property)!.ElementAt(point), k, new C3dExpr(expr, unit));

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, C3dDocument doc, string cell = "Top")
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(
            typeof(PointExpressionsCoreTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
