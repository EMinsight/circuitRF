// brief-em3d-109 — field plots in the realistic view. On a copper block on a matte plate, with a Faces-style plot on the block's top
// (two triangles, the value the x coordinate) and a clip-plane slice: gate 1 Exact is the colour map's colour per pixel, the default
// view's, and unmoved by shadows, occlusion, exposure and every studio (Metal); 2 Lit only lightens and keeps the hue, and is the C#
// reference's (Metal); 3 Glow leaves the field exact and dims the model and the background by GlowDim (Metal); 4 a 50 % field is the
// blend of its colour and the copper under it (Metal); 5 the indicator — its text, the view model's, an exported picture's under the
// legend and alone in its corner, and the one constant; 6 a slice inside glass shows through it (Metal); 7 FieldStyle and FieldOpacity
// never reach a run, and check reads them. Also: the Lit field's normals (brief 104's function on the field's own triangles), uploaded
// once per field geometry and never for Exact. Pixel gates run on macOS only; D3D11 and Vulkan are compiled only, as briefs 106/107 say.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class FieldPlotsRealisticTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em109-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public FieldPlotsRealisticTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── the fixture ──────────────────────────────────────────────────────────────────────────────────────────────────

    private const double Mm = 1e-3;
    private const int W = 480, H = 360;

    private static Em3dSolid Box(string name, string material, Em3dRole role, double x0, double y0, double z0, double x1, double y1, double z1)
        => new(name, material, role, new Em3dBox(new(x0 * Mm, y0 * Mm, z0 * Mm), new(x1 * Mm, y1 * Mm, z1 * Mm)), 0);

    private static Scene3DModel Scene(bool glass = false)
    {
        var a = Em3dBoundaryKind.Absorbing;
        Em3dSolid[] solids = glass
            ? [Box("plate", "Copper", Em3dRole.Conductor, 0, 0, -0.5, 10, 10, 0), Box("glass", "Silica", Em3dRole.Dielectric, 2, 2, 0, 8, 8, 3)]
            : [Box("plate", "Copper", Em3dRole.Conductor, 0, 0, -0.5, 10, 10, 0), Box("block", "Copper", Em3dRole.Conductor, 2, 2, 0, 8, 8, 1)];
        var problem = new Em3dProblem(solids, [], [], [], new Em3dAirBox(new(-0.05, -0.05, -0.05), new(0.05, 0.05, 0.05), new Em3dFaces(a, a, a, a, a, a)),
                                      new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        TechAppearance? Look(string n) => n switch
        {
            "plate" => new TechAppearance { BaseColor = "#a0a0a0", Metallic = 0, Roughness = 1 },
            "glass" => new TechAppearance { BaseColor = "#ffffff", Metallic = 0, Roughness = 0.05, Transmission = 0.95 },
            _ => new TechAppearance { BaseColor = "#b87333", Metallic = 1, Roughness = 0.35 },
        };
        return Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false,
            Appearance: n => new AppearanceOverride(Look(n)!, [])));
    }

    /// <summary>Layer 0: the block's top (z 1 mm), two triangles, the value its x in mm (range 2 to 8). Layer 1: a slice on y = 5 mm
    /// through the block's middle, x 3–7, z 0.2–0.8 (or, <paramref name="glass"/>, through the glass, z 0.5–2.5), one value.</summary>
    private static Scene3DFieldGeometry Field(bool glass = false, bool slice = true, long version = 1)
    {
        FieldVertex Top(double x, double y) => new() { X = (float)(x * Mm), Y = (float)(y * Mm), Z = (float)(1 * Mm), R0 = (float)x };
        FieldVertex Cut(double x, double z) => new() { X = (float)(x * Mm), Y = (float)(5 * Mm), Z = (float)(z * Mm), R0 = 3.5f };
        double z0 = glass ? 0.5 : 0.2, z1 = glass ? 2.5 : 0.8;
        var v = new List<FieldVertex>();
        if (!glass) v.AddRange([Top(2, 2), Top(8, 2), Top(8, 8), Top(2, 2), Top(8, 8), Top(2, 8)]);
        int first = v.Count;
        if (slice) v.AddRange([Cut(3, z0), Cut(7, z0), Cut(7, z1), Cut(3, z0), Cut(7, z1), Cut(3, z1)]);
        var layers = new List<FieldLayerRange>();
        if (first > 0) layers.Add(new(0, first, 0));
        if (v.Count > first) layers.Add(new(first, v.Count - first, 1));
        return new Scene3DFieldGeometry([.. v], version, -1, layers);
    }

    private static readonly ColorMap3D Map = ColorMap3D.CoolWarm;

    /// <summary>A FieldUniforms block by hand: a real scalar (mode 2), range lo..hi, <see cref="Map"/>'s stops.</summary>
    private static void Block(Span<float> u, float lo, float hi, bool unclipped)
    {
        u.Clear();
        u[0] = 1; u[2] = lo; u[3] = hi; u[4] = 2; u[6] = Map.Stops.Count; u[7] = unclipped ? 1 : 0;
        for (int i = 0; i < Map.Stops.Count; i++)
        {
            var (t, r, g, b) = Map.Stops[i];
            u[8 + 4 * i] = t; u[9 + 4 * i] = r / 255f; u[10 + 4 * i] = g / 255f; u[11 + 4 * i] = b / 255f;
        }
    }

    private static Viewer3DViewState View(Scene3DModel scene, C3dLook? look, Action<Viewer3DViewState>? camera = null, bool realistic = true,
                                          bool field = true)
    {
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / (float)H, Projection3D.Orthographic) };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        view.Realistic = realistic;
        var lk = RealisticLook.From(look);
        view.Look = lk;
        view.Environment = EnvironmentPrefilter.Studio(lk.Studio);
        view.ShowField = field;
        Block(view.Field.AsSpan(0, FieldUniforms.Floats), 2, 8, unclipped: false);
        Block(view.Field.AsSpan(FieldUniforms.Floats, FieldUniforms.Floats), 2, 8, unclipped: true);
        (camera ?? TopView)(view);
        return view;
    }

    private static void TopView(Viewer3DViewState v) => v.Camera.SetStandardView(StandardView3D.Top);

    /// <summary>From the south-west and above, looking at the block's top at a slant: where a sheen shows.</summary>
    private static void Oblique(Viewer3DViewState v) => (v.Camera.Yaw, v.Camera.Pitch) = (-MathF.PI * 0.6f, 0.45f);

    private sealed record Shot(byte[] Rgba, Viewer3DViewState View, Scene3DFramePlan Plan, Viewer3DSession Session)
    {
        public (int R, int G, int B) At(int x, int y) { int k = 4 * (y * W + x); return (Rgba[k], Rgba[k + 1], Rgba[k + 2]); }

        public (float X, float Y) Project(Vector3 world)
        {
            var (x, y, visible) = View.Camera.Project(world, W, H);
            Assert.True(visible);
            return (x, y);
        }

        public void Save(string name)
        {
            string? dir = Environment.GetEnvironmentVariable("CRF_FIELD109_PNG");
            if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, name + ".png"), new FieldPictureShot(Rgba, W, H, 1, [], null, false).Png());
        }
    }

    /// <summary>The scene drawn by the real Metal backend (live path), with <paramref name="field"/>.</summary>
    private static Shot Metal(Scene3DModel scene, Viewer3DViewState view, Scene3DFieldGeometry? field)
    {
        using var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        metal.CreateOffscreenImages(W, H, 1);
        var session = new Viewer3DSession(() => metal) { ShadeStream = view.Realistic, Environment = view.Realistic ? view.Environment : null };
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        plan.Plan(scene, view, W, H, metal.FlipY, pick: false, none, none, none, field);
        Assert.Equal(view.Realistic, plan.Realistic);
        session.Frame(0, plan, 1, scene, none, none, none, false, field);
        return new Shot(metal.ReadImage(0), view, plan, session);
    }

    /// <summary>The pixels of the block's top in a top view, two pixels in from its edges: each with the world x at its centre.</summary>
    private static IEnumerable<(int X, int Y, double Xmm)> TopPixels(Shot s)
    {
        var (ax, ay) = s.Project(V(2, 2, 1));
        var (bx, by) = s.Project(V(8, 8, 1));
        int x0 = (int)MathF.Ceiling(MathF.Min(ax, bx)) + 2, x1 = (int)MathF.Floor(MathF.Max(ax, bx)) - 3;
        int y0 = (int)MathF.Ceiling(MathF.Min(ay, by)) + 2, y1 = (int)MathF.Floor(MathF.Max(ay, by)) - 3;
        Assert.True(x1 - x0 > 100 && y1 - y0 > 100, $"the block's top spans ({x0}..{x1}, {y0}..{y1})");
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                yield return (x, y, 2 + (x + 0.5 - ax) / (bx - ax) * 6);
    }

    /// <summary>Pixels whose whole 3 × 3 neighbourhood differs between <paramref name="with"/> and <paramref name="without"/> the field:
    /// field pixels, away from its edges.</summary>
    private static List<(int X, int Y)> FieldPixels(Shot with, Shot without)
    {
        var mask = new List<(int, int)>();
        for (int y = 1; y < H - 1; y++)
            for (int x = 1; x < W - 1; x++)
            {
                bool all = true;
                for (int j = -1; j <= 1 && all; j++)
                    for (int i = -1; i <= 1 && all; i++)
                        all = with.At(x + i, y + j) != without.At(x + i, y + j);
                if (all) mask.Add((x, y));
            }
        return mask;
    }

    private static Vector3 V(double x, double y, double z) => new((float)(x * Mm), (float)(y * Mm), (float)(z * Mm));
    private static int Diff((int R, int G, int B) a, (int R, int G, int B) b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    // ── 1. Exact is exact ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_Metal_ExactIsTheColourMapPerPixel_TheDefaultViews_AndUnmovedByLightingExposureAndEveryStudio()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Scene();
        var field = Field();
        var exact = Metal(scene, View(scene, null), field);
        exact.Save("gate1-exact");
        int n = 0;
        foreach (var (x, y, xmm) in TopPixels(exact))
        {
            var (r, g, b) = Map.Sample((float)((xmm - 2) / 6));
            Assert.True(Diff(exact.At(x, y), (r, g, b)) <= 1, $"({x}, {y}) at x = {xmm:F3} mm: {exact.At(x, y)} against the map's ({r}, {g}, {b})");
            n++;
        }
        Assert.True(n > 10_000);

        var pixels = TopPixels(exact).Select(p => (p.X, p.Y)).ToList();
        void Same(Shot other, string what)
        {
            foreach (var (x, y) in pixels) Assert.True(exact.At(x, y) == other.At(x, y), $"{what}: ({x}, {y}) {other.At(x, y)} against {exact.At(x, y)}");
        }
        Same(Metal(scene, View(scene, null, realistic: false), field), "the default view");
        Same(Metal(scene, View(scene, new C3dLook { Shadows = false, AmbientOcclusion = false, Ground = false }), field), "no shadows or occlusion");
        Same(Metal(scene, View(scene, new C3dLook { Exposure = 3 }), field), "+3 EV");
        Same(Metal(scene, View(scene, new C3dLook { Exposure = -3 }), field), "-3 EV");
        Same(Metal(scene, View(scene, new C3dLook { Environment = "HighKey" }), field), "High key");
        Same(Metal(scene, View(scene, new C3dLook { Environment = "Dark", Background = "Environment" }), field), "Dark");
    }

    // ── 2. Lit only lightens ──────────────────────────────────────────────────────────────────────────────────────────

    private static double Hue((int R, int G, int B) c, out double saturation)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        saturation = max > 0 ? d / max : 0;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d : max == g ? 2 + (b - r) / d : 4 + (r - g) / d;
        return (h * 60 + 360) % 360;
    }

    [Fact]
    public void Gate2_Metal_LitOnlyLightens_KeepsTheHue_AndIsTheReferences()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Scene();
        var field = Field();
        var lit = new C3dLook { FieldStyle = "Lit" };
        var exact = Metal(scene, View(scene, null, Oblique), field);
        var shone = Metal(scene, View(scene, lit, Oblique), field);
        var bare = Metal(scene, View(scene, null, Oblique, field: false), field);
        shone.Save("gate2-lit");
        var mask = FieldPixels(exact, bare);
        Assert.True(mask.Count > 5_000, $"{mask.Count} field pixels");
        Assert.True(shone.Plan.FieldNormals && shone.Session.FieldNormalUploads == 1);
        int lifted = 0, hues = 0;
        foreach (var (x, y) in mask)
        {
            var e = exact.At(x, y);
            var l = shone.At(x, y);
            Assert.True(l.R >= e.R && l.G >= e.G && l.B >= e.B, $"({x}, {y}): Lit {l} against Exact {e}");
            lifted = Math.Max(lifted, Diff(l, e));
            double he = Hue(e, out double se), hl = Hue(l, out double sl);
            if (se <= 0.2 || sl <= 0.2) continue;
            double dh = Math.Abs(he - hl);
            Assert.True(Math.Min(dh, 360 - dh) <= 6, $"({x}, {y}): hue {hl:F1}° against {he:F1}°");
            hues++;
        }
        Assert.True(lifted >= 8, $"the sheen lifted no pixel by more than {lifted}");
        Assert.True(hues > 1_000, $"{hues} saturated pixels compared");

        // From straight above the GPU's Lit is the reference's (Pbr.FieldSheen, Pbr.LitField) at each pixel's point of the block's top.
        var top = Metal(scene, View(scene, lit), field);
        var view = top.View;
        var light = view.Look.Lighting(view.Environment!);
        var eye = top.Plan.Uniforms.AsSpan(16, 3);
        var eyeAt = new Vector3(eye[0], eye[1], eye[2]);
        var (ax, ay) = top.Project(V(2, 2, 1));
        var (bx, by) = top.Project(V(8, 8, 1));
        foreach (var (x, y, xmm) in TopPixels(top).Where(p => (p.X + p.Y) % 7 == 0))
        {
            double ymm = 2 + (y + 0.5 - ay) / (by - ay) * 6;
            var world = V(xmm, ymm, 1);
            var (r, g, b) = Map.Sample((float)((xmm - 2) / 6));
            float s = Pbr.FieldSheen(Vector3.UnitZ, Vector3.Normalize(eyeAt - world), view.Environment!, light);
            var c = Pbr.LitField(new Vector3(r, g, b) / 255f, s) * 255f;
            var want = ((int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z));
            Assert.True(Diff(top.At(x, y), want) <= 2, $"({x}, {y}): GPU {top.At(x, y)} against the reference {want}");
        }
    }

    // ── 3. Glow ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A display pixel back to the linear radiance before exposure's curve: the sRGB decode, then the PBR Neutral curve undone in
    /// its toe and linear parts (where the peak is below its compression start, which the caller asserts).</summary>
    private static Vector3 Unshown((int R, int G, int B) c)
    {
        var d = new Vector3(ToneCurve.SrgbDecode(c.R / 255f), ToneCurve.SrgbDecode(c.G / 255f), ToneCurve.SrgbDecode(c.B / 255f));
        Assert.True(MathF.Max(d.X, MathF.Max(d.Y, d.Z)) < ToneCurve.StartCompression, $"{c} is in the curve's shoulder");
        float m = MathF.Min(d.X, MathF.Min(d.Y, d.Z));
        float toe = ToneCurve.ToeSlope * ToneCurve.ToeBreak * ToneCurve.ToeBreak;
        float x = m < toe ? MathF.Sqrt(m / ToneCurve.ToeSlope) : m + ToneCurve.ToeOffset;
        return d + new Vector3(x - m);
    }

    private static double Ev(Vector3 a, Vector3 b) => Math.Log2(Vector3.Dot(a, Pbr.Luma) / Vector3.Dot(b, Pbr.Luma));

    [Fact]
    public void Gate3_Metal_GlowLeavesTheFieldExact_AndDimsTheModelAndTheBackgroundByGlowDim()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Scene();
        var field = Field();
        C3dLook Look(string? style) => new() { FieldStyle = style, Shadows = false, AmbientOcclusion = false, Ground = false, Background = "#909090" };
        var exact = Metal(scene, View(scene, Look(null), Oblique), field);
        var glow = Metal(scene, View(scene, Look("Glow"), Oblique), field);
        var bare = Metal(scene, View(scene, Look(null), Oblique, field: false), field);
        glow.Save("gate3-glow");
        var mask = FieldPixels(exact, bare);
        Assert.True(mask.Count > 5_000);
        foreach (var (x, y) in mask) Assert.True(exact.At(x, y) == glow.At(x, y), $"({x}, {y}): Glow {glow.At(x, y)} against Exact {exact.At(x, y)}");
        Assert.False(glow.Plan.FieldNormals);

        // a point of the matte plate, well clear of the block, and the background's corner
        var (px, py) = exact.Project(V(9.3, 0.7, 0));
        var plate = (X: (int)px, Y: (int)py);
        double dim = Ev(Unshown(glow.At(plate.X, plate.Y)), Unshown(exact.At(plate.X, plate.Y)));
        Assert.True(Math.Abs(dim - RealisticLook.GlowDim) <= 0.2, $"the plate went {dim:F2} EV ({exact.At(plate.X, plate.Y)} to {glow.At(plate.X, plate.Y)})");
        // the background is a display colour, never tone-mapped: decoded only
        Vector3 Decoded((int R, int G, int B) c) => new(ToneCurve.SrgbDecode(c.R / 255f), ToneCurve.SrgbDecode(c.G / 255f), ToneCurve.SrgbDecode(c.B / 255f));
        double bg = Ev(Decoded(glow.At(1, 1)), Decoded(exact.At(1, 1)));
        Assert.True(Math.Abs(bg - RealisticLook.GlowDim) <= 0.2, $"the background went {bg:F2} EV ({exact.At(1, 1)} to {glow.At(1, 1)})");
        // Glow carries no indicator, Lit does
        Assert.Null(RealisticLook.From(Look("Glow")).FieldIndicator);
    }

    // ── 4. opacity ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_Metal_AtFiftyPercentAFieldPixelIsTheBlendOfItsColourAndTheCopperUnderIt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Scene();
        var field = Field(slice: false);
        uint block = scene.Objects.First(o => o.Name == "block").Id;
        Viewer3DViewState Covered(C3dLook? look, bool shown = true)
        {
            var v = View(scene, look, field: shown);
            v.FieldCovered = new bool[scene.Objects.Length];
            v.FieldCovered[block - 1] = true;         // a Surfaces/Faces plot stands in for the faces it is painted on
            return v;
        }
        var colour = Metal(scene, Covered(null), field);
        var copper = Metal(scene, Covered(null, shown: false), field);
        var half = Metal(scene, Covered(new C3dLook { FieldOpacity = 50 }), field);
        half.Save("gate4-half");
        var nothing = Metal(scene, Covered(new C3dLook { FieldOpacity = 0 }), field);
        Assert.Equal(Scene3DPipeline.FieldBlend, half.Plan.Draws.Take(half.Plan.DrawCount).Single(d => d.Buffer == Scene3DBuffer.Field).Pipeline);
        int n = 0, apart = 0;
        foreach (var (x, y, _) in TopPixels(colour))
        {
            var f = colour.At(x, y);
            var c = copper.At(x, y);
            var want = ((f.R + c.R) / 2.0, (f.G + c.G) / 2.0, (f.B + c.B) / 2.0);
            var got = half.At(x, y);
            double off = Math.Max(Math.Abs(got.R - want.Item1), Math.Max(Math.Abs(got.G - want.Item2), Math.Abs(got.B - want.Item3)));
            Assert.True(off <= 2, $"({x}, {y}): {got} against the blend {want} of {f} and {c}");
            // the covered block is drawn under the field below 100 %: at 0 the copper is all that shows
            Assert.True(Diff(nothing.At(x, y), c) <= 1, $"({x}, {y}): at 0 % {nothing.At(x, y)} against the copper {c}");
            if (Diff(f, c) > 20) apart++;
            n++;
        }
        Assert.True(n > 10_000 && apart > n / 2, $"{apart} of {n} pixels tell the field from the copper");
    }

    // ── 5. the indicator ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_TheIndicator_ItsText_TheViewsAndAPicturesUnderTheLegendOrAlone_AndOneSpelling()
    {
        Assert.Null(RealisticLook.From(null).FieldIndicator);
        Assert.Null(RealisticLook.From(new C3dLook { FieldStyle = "Glow" }).FieldIndicator);
        Assert.Equal("Lit Fields", RealisticLook.From(new C3dLook { FieldStyle = "Lit" }).FieldIndicator);
        Assert.Equal("Blended Fields", RealisticLook.From(new C3dLook { FieldOpacity = 50 }).FieldIndicator);
        Assert.Equal("Blended Fields", RealisticLook.From(new C3dLook { FieldStyle = "Glow", FieldOpacity = 50 }).FieldIndicator);
        Assert.Equal("Lit, Blended Fields", RealisticLook.From(new C3dLook { FieldStyle = "lit", FieldOpacity = 50 }).FieldIndicator);

        var vm = OpenCavity();
        var v = vm.Viewer;
        Assert.True(v.ShowField && v.FieldDrawn.Vertices.Length > 0);
        Assert.Null(v.FieldIndicator);                                 // the default view: the colours are the legend's
        v.EnvironmentFor = (s, _) => EnvironmentPrefilter.Studio(s);
        v.IsRealistic = true;
        Until(() => v.View.Environment is not null, "the studio was never made");
        Assert.Null(v.FieldIndicator);                                 // Exact
        var expected = new (string? Style, double? Opacity, string? Label)[]
        {
            ("Lit", null, "Lit Fields"), (null, 50, "Blended Fields"), ("Lit", 50, "Lit, Blended Fields"), ("Glow", null, null), (null, null, null),
        };
        foreach (var (style, opacity, label) in expected)
        {
            Assert.True(vm.ChangeLook("test", l => { l.FieldStyle = style; l.FieldOpacity = opacity; }));
            Assert.Equal(label, v.FieldIndicator);
            foreach (bool on in (bool[])[true, false])
            {
                // not tied to the legend or the caption option
                v.ExportLegend = v.ExportCaption = on;
                Assert.Equal(label, v.FieldIndicator);
                var shot = v.CapturePicture(300, 200, 1, out string? error)!;
                Assert.Null(error);
                Assert.Equal(label, shot.Indicator);
                Assert.Equal(on, shot.Legends.Count > 0);
            }
        }
        v.IsRealistic = false;
        Assert.True(vm.ChangeLook("test", l => l.FieldStyle = "Lit"));
        Assert.Null(v.FieldIndicator);                                 // a Look's style means nothing outside the realistic view

        // a picture: the label is painted under the legend stack, or alone in the top right corner with no legend
        var legend = new FieldPictureLegend(["|E|", "Field1"], ColorMap3D.Viridis, null);
        var mid = Enumerable.Repeat((byte)128, 400 * 300 * 4).ToArray();
        int Ink(SKBitmap a, SKBitmap b, SKRectI r)
        {
            int k = 0;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                    if (a.GetPixel(x, y) != b.GetPixel(x, y)) k++;
            return k;
        }
        float stack = 8 + (8 * 2 + 12 + 16 * (2 + 1));   // pad + the legend's height: its two lines, the bar and the range line
        using (var with = FieldPicture.Compose(mid, 400, 300, 1, [legend], "cap", false, indicator: "Lit Fields"))
        using (var without = FieldPicture.Compose(mid, 400, 300, 1, [legend], "cap", false))
        {
            var under = new SKRectI(200, (int)stack, 400, (int)stack + 20);
            Assert.True(Ink(with, without, under) > 20, "nothing was painted under the legend");
            Assert.Equal(0, Ink(with, without, new SKRectI(0, 0, 400, (int)stack)));             // the legend itself untouched
        }
        using (var with = FieldPicture.Compose(mid, 400, 300, 1, [], null, false, indicator: "Lit Fields"))
        using (var without = FieldPicture.Compose(mid, 400, 300, 1, [], null, false))
        {
            Assert.True(Ink(with, without, new SKRectI(300, 0, 400, 24)) > 20, "nothing was painted in the corner");
            Assert.Equal(0, Ink(with, without, new SKRectI(0, 30, 400, 300)));
        }

        // one spelling: no source file spells a label out
        string src = Path.Combine(RepoRoot(), "src");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string code = Regex.Replace(File.ReadAllText(file), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            Assert.DoesNotMatch(@"""(Lit|Blended|Lit, Blended) Fields""", code);
        }
    }

    // ── 6. through glass ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_Metal_ASliceInsideGlassShowsThroughIt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Scene(glass: true);
        var field = Field(glass: true);
        void South(Viewer3DViewState v) => (v.Camera.Yaw, v.Camera.Pitch) = (-MathF.PI / 2, 0.35f);
        var shown = Metal(scene, View(scene, null, South), field);
        var hidden = Metal(scene, View(scene, null, South, field: false), field);
        shown.Save("gate6-glass");
        var (x, y) = shown.Project(V(5, 5, 1.5));
        int d = Diff(shown.At((int)x, (int)y), hidden.At((int)x, (int)y));
        Assert.True(d > 20, $"the slice changed the pixel inside the glass by {d}: {shown.At((int)x, (int)y)} against {hidden.At((int)x, (int)y)}");
        // the glass is drawn over it: the pixel is not the bare colour map's
        var (r, g, b) = Map.Sample((3.5f - 2) / 6);
        Assert.True(Diff(shown.At((int)x, (int)y), (r, g, b)) > 2, "the slice was drawn over the glass, not seen through it");
    }

    // ── 7. never a run's ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_FieldStyleAndOpacity_AreSavedAndChecked_AndAbsentFromARun()
    {
        var doc = new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = "Copper", Min = new(0, 0, 0), Size = new(1000, 1000, 1000) }],
            Look = new C3dLook { FieldStyle = "Lit", FieldOpacity = 50 },
        };
        string saved = C3dPersistence.SerializeLook(doc.Look);
        Assert.Contains("\"FieldStyle\": \"Lit\"", saved);
        Assert.Contains("\"FieldOpacity\": 50", saved);
        var back = C3dPersistence.DeserializeLook(saved)!;
        Assert.Equal(("Lit", 50.0), (back.FieldStyle, back.FieldOpacity!.Value));
        Assert.Contains(nameof(C3dLook.FieldStyle), C3dLook.Keys);
        Assert.Contains(nameof(C3dLook.FieldOpacity), C3dLook.Keys);
        Assert.False(new C3dLook { FieldOpacity = 100 }.IsEmpty);
        Assert.Equal(("Lit", 50.0), (doc.Look.Clone().FieldStyle, doc.Look.Clone().FieldOpacity!.Value));

        string run = C3dPersistence.SerializeForRun(doc);
        Assert.DoesNotContain("FieldStyle", run);
        Assert.DoesNotContain("FieldOpacity", run);
        Assert.Equal(("Lit", 50.0), (doc.Look.FieldStyle, doc.Look.FieldOpacity!.Value));        // the document keeps it

        string[] Codes(C3dLook look) => [.. C3dValidation.Validate(new C3dDocument { Look = look }).Select(d => d.Id).Where(c => c.StartsWith("c3d.look"))];
        Assert.Empty(Codes(new C3dLook { FieldStyle = "glow", FieldOpacity = 0 }));
        Assert.Equal(["c3d.look.field-style"], Codes(new C3dLook { FieldStyle = "Shiny" }));
        Assert.Equal(["c3d.look.field-style"], Codes(new C3dLook { FieldStyle = "Lit,Glow" }));
        Assert.Equal(["c3d.look.range"], Codes(new C3dLook { FieldOpacity = 150 }));
    }

    // ── the normals ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLitFieldsNormals_AreThePlanesOnASlice_SmoothAcrossACurve_AndMadeOncePerGeometry()
    {
        var slice = Field(slice: true);
        var n = FieldShading.Normals(slice.Vertices, slice.Layers);
        for (int v = 0; v < 6; v++) Assert.True(Math.Abs(Math.Abs(n[3 * v + 2]) - 1) < 1e-6, $"top vertex {v}: z {n[3 * v + 2]}");
        for (int v = 6; v < 12; v++) Assert.True(Math.Abs(Math.Abs(n[3 * v + 1]) - 1) < 1e-6, $"slice vertex {v}: y {n[3 * v + 1]}");

        // a quarter cylinder of radius 1 in 8 facets, as a triangle list: the shared rim vertices take the radial direction
        var arc = new List<FieldVertex>();
        FieldVertex P(int k, float z) { double a = k * Math.PI / 16; return new() { X = (float)Math.Cos(a), Y = (float)Math.Sin(a), Z = z }; }
        for (int k = 0; k < 8; k++) arc.AddRange([P(k, 0), P(k + 1, 0), P(k + 1, 1), P(k, 0), P(k + 1, 1), P(k, 1)]);
        var nc = FieldShading.Normals([.. arc], [new FieldLayerRange(0, arc.Count, 0)]);
        for (int v = 0; v < arc.Count; v++)
        {
            var radial = Vector3.Normalize(new Vector3(arc[v].X, arc[v].Y, 0));
            var got = new Vector3(nc[3 * v], nc[3 * v + 1], nc[3 * v + 2]);
            bool inner = Math.Abs(Math.Atan2(arc[v].Y, arc[v].X)) > 1e-6 && Math.Abs(Math.Atan2(arc[v].Y, arc[v].X) - Math.PI / 2) > 1e-6;
            if (inner) Assert.True(MathF.Abs(MathF.Abs(Vector3.Dot(got, radial)) - 1) < 1e-5, $"vertex {v}: {got} against {radial}");
        }

        // the session makes them for a Lit frame only, once per field geometry, and lets them go
        var scene = Scene();
        var fake = new PatchRecordingBackend();
        var session = new Viewer3DSession(() => fake) { ShadeStream = true };
        session.EnsureBackend();
        var none = Scene3DOverlay.None;
        void Frame(C3dLook? look, Scene3DFieldGeometry f)
        {
            var view = View(scene, look);
            session.Environment = view.Environment;
            var plan = new Scene3DFramePlan();
            plan.Plan(scene, view, W, H, false, false, none, none, none, f);
            session.Frame(0, plan, 1, scene, none, none, none, false, f);
        }
        var field = Field();
        long built = FieldShading.Built;
        Frame(null, field);
        Frame(new C3dLook { FieldStyle = "Glow", FieldOpacity = 40 }, field);
        Assert.Equal((0, built), (fake.FieldNormalUploads, FieldShading.Built));
        Frame(new C3dLook { FieldStyle = "Lit" }, field);
        Frame(new C3dLook { FieldStyle = "Lit", Exposure = 1 }, field);
        Assert.Equal((1, 1), (fake.FieldNormalUploads, session.FieldNormalUploads));
        Frame(new C3dLook { FieldStyle = "Lit" }, Field(slice: false, version: 2));   // a new geometry: made again
        Assert.Equal(2, fake.FieldNormalUploads);
        Frame(null, field);                                                       // Exact: released (an empty upload, not counted)
        Frame(new C3dLook { FieldStyle = "Lit" }, field);                          // and made again when Lit comes back
        Assert.Equal((3, 3), (fake.FieldNormalUploads, session.FieldNormalUploads));
    }

    [Fact]
    public void TheShadersFieldConstants_AreTheReferences()
    {
        string wgsl = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Viewer3D", "Shaders", "scene.wgsl"));
        float Const(string name)
            => float.Parse(Regex.Match(wgsl, $@"^const {name}: f32 = ([0-9.eE+\-]+);", RegexOptions.Multiline).Groups[1].Value,
                           System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Pbr.FieldSheenRoughness, Const("FIELD_SHEEN_ROUGHNESS"));
        Assert.Equal(ToneCurve.SrgbDecodeBreak, Const("SRGB_DECODE_BREAK"));
        // the luminance weights are written once in each, as literals beside each other
        Assert.Equal(2, Regex.Matches(wgsl, @"vec3f\(0\.2126, 0\.7152, 0\.0722\)").Count);
        Assert.Equal(new Vector3(0.2126f, 0.7152f, 0.0722f), Pbr.Luma);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private C3dEditorViewModel OpenCavity()
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology { Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }] });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        var none = new EmAirBoxFace(0, null);
        var setup = new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolver.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");
        string run = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), vm.ActiveRunSetup!, Em3dSolver.Palace);
        string cavity = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");
        foreach (string f in Directory.EnumerateFiles(cavity, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(run, Path.GetRelativePath(cavity, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(run);
        vm.RunFinished();
        Until(() => vm.Viewer.FieldsAvailable && vm.Viewer.FieldSolutions.Count > 0, "the run's fields were never read");
        vm.NewFieldPlot();
        Until(() => vm.Viewer.LayerNamed("Field1") is { Builds: > 0, Scale: not null, Building: false, Item: not null }, "Field1 was never drawn");
        return vm;
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
