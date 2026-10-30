using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-dfr31-1 — the two engine halves of floating metal and connected bodies: the elimination of a floating body from a
// Maxwell matrix against its closed form (gate 3, no Palace), and the body grouping's contact rules and cost (gate 6).
public sealed class StaticBodiesEngineTests
{
    /// <summary>
    /// Three conductors with every capacitor drawn by hand: A–M 3 pF, M–B 6 pF, A–B 1 pF, A to ground 2 pF, B 4 pF, M none.
    /// M floating carries no charge, so its potential divides between A and B, and A–B becomes 1 pF plus 3 pF in series with
    /// 6 pF (2 pF): C_red = [[2+1+2, −3], [−3, 4+3]] pF.
    /// </summary>
    [Fact]
    public void Gate3_AFloatingBody_IsEliminatedToTheClosedForm()
    {
        const double p = 1e-12;
        double cam = 3 * p, cmb = 6 * p, cab = 1 * p, ca0 = 2 * p, cb0 = 4 * p;
        // Maxwell: diagonal = every capacitor on the conductor, off-diagonal = −the capacitor between.
        var c = new double[,]
        {
            { ca0 + cab + cam, -cab,             -cam       },
            { -cab,            cb0 + cab + cmb,  -cmb       },
            { -cam,            -cmb,             cam + cmb  },
        };
        var red = Em3dStaticResult.EliminateFloating(c, [false, false, true]);

        double series = cam * cmb / (cam + cmb);
        double ab = cab + series;
        Assert.Equal(2, red.GetLength(0));
        Assert.Equal(-ab, red[0, 1], 1e-24);
        Assert.Equal(-ab, red[1, 0], 1e-24);
        Assert.Equal(ca0 + ab, red[0, 0], 1e-24);
        Assert.Equal(cb0 + ab, red[1, 1], 1e-24);

        var m = Em3dStaticResult.MutualFromMaxwell(red);
        Assert.Equal(ca0, m[0, 0], 1e-24);
        Assert.Equal(cb0, m[1, 1], 1e-24);
        Assert.Equal(ab, m[0, 1], 1e-24);
    }

    [Fact]
    public void Bodies_JoinThroughABarrel_NeverThroughDielectric_AndNameTheLargestMember()
    {
        static Em3dSolid Slab(string n, double x0, double x1, double z0, double z1)
            => new(n, "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(
                [new(x0, 0), new(x1, 0), new(x1, 1e-3), new(x0, 1e-3)], [], z0, z1), 1);
        var solids = new List<Em3dSolid>
        {
            Slab("top/line", 0, 4e-3, 1.0e-3, 1.035e-3),
            Slab("bot/pad", 3e-3, 5e-3, 0, 35e-6),          // under the line's end, a dielectric away
            Slab("bot/other", 6e-3, 9e-3, 0, 35e-6),
            Slab("bot/touching", 9e-3, 10e-3, 0, 35e-6),    // shares bot/other's edge
            new("air", "air", Em3dRole.Air, new Em3dBox(new(0, 0, 0), new(1, 1, 1)), 0),
        };
        var apart = Em3dBodies.Group(solids, [], null, null);
        Assert.Equal(-1, apart.BodyOf("air"));
        Assert.NotEqual(apart.BodyOf("top/line"), apart.BodyOf("bot/pad"));
        Assert.Equal(apart.BodyOf("bot/other"), apart.BodyOf("bot/touching"));
        Assert.Equal("body:bot/other", apart.Bodies[apart.BodyOf("bot/other")].Name);

        solids.Add(new Em3dSolid("via/1", "cu", Em3dRole.Conductor, new Em3dCylinder(new(3.5e-3, 0.5e-3, 0), new(3.5e-3, 0.5e-3, 1.035e-3), 1.5e-4), 2));
        var nets = new Dictionary<string, string[]> { ["top/line"] = ["RF"] };
        var joined = Em3dBodies.Group(solids, [], null, n => nets.GetValueOrDefault(n) ?? []);
        int b = joined.BodyOf("top/line");
        Assert.Equal(b, joined.BodyOf("bot/pad"));
        Assert.Equal(b, joined.BodyOf("via/1"));
        Assert.Equal("RF", joined.Bodies[b].Name);
    }

    /// <summary>
    /// Gate 6 — 20,000 objects: a 100 × 50 grid of vias, each with a pad on two levels and a pad on a third that touches no
    /// barrel, plus a plane on each level with a clearance hole round every via. Grouped once; the COUNT of footprint pairs
    /// examined is held linear in the object count (a quadratic scan is 2·10⁸), not the time.
    /// </summary>
    [Fact]
    public void Gate6_GroupingTwentyThousandObjects_ExaminesALinearNumberOfPairs()
    {
        const double pitch = 1e-3, r = 1.5e-4, pad = 3e-4;
        var solids = new List<Em3dSolid>();
        static List<Point2> Square(double cx, double cy, double h)
            => [new(cx - h, cy - h), new(cx + h, cy - h), new(cx + h, cy + h), new(cx - h, cy + h)];
        int nx = 100, ny = 50;
        var holes = new List<IReadOnlyList<Point2>>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                double x = (i + 0.5) * pitch, y = (j + 0.5) * pitch;
                holes.Add(Square(x, y, 4.5e-4));
                solids.Add(new($"via/{i}/{j}", "cu", Em3dRole.Conductor, new Em3dCylinder(new(x, y, 0), new(x, y, 2e-3), r), 2));
                solids.Add(new($"top/{i}/{j}", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(Square(x, y, pad), [], 2e-3, 2.035e-3), 1));
                solids.Add(new($"bot/{i}/{j}", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(Square(x, y, pad), [], -35e-6, 0), 1));
                solids.Add(new($"mid/{i}/{j}", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(Square(x + 0.5 * pitch, y, 1e-4), [], 1e-3, 1.035e-3), 1));
            }
        List<Point2> outline = [new(0, 0), new(nx * pitch, 0), new(nx * pitch, ny * pitch), new(0, ny * pitch)];
        solids.Add(new("plane/top", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(outline, holes, 2e-3, 2.035e-3), 1));
        solids.Add(new("plane/bot", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(outline, holes, -35e-6, 0), 1));
        int n = solids.Count;
        Assert.True(n >= 20_000, $"{n} objects");

        var g = Em3dBodies.Group(solids, [], null, null);

        // Each via with its two pads is one body, each mid pad is alone, and the planes (cleared round every via) are two.
        Assert.Equal(nx * ny * 2 + 2, g.Bodies.Count);
        Assert.Equal(g.BodyOf("via/7/9"), g.BodyOf("top/7/9"));
        Assert.Equal(g.BodyOf("via/7/9"), g.BodyOf("bot/7/9"));
        Assert.NotEqual(g.BodyOf("plane/top"), g.BodyOf("via/7/9"));
        Assert.True(g.PairVisits < 20L * n, $"{g.PairVisits} pair visits for {n} objects");
    }
}
