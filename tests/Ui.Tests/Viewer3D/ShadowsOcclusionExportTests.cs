// brief-em3d-107 — shadows, contact shading, the ground, and the realistic picture export. Gate 1 a box's shadow on its plate (Metal);
// 2 an orbit renders no shadow pass (a counter, no GPU); 3 glass casts none; 4 occlusion in an inside corner (Metal); 5 a field's colour
// is untouched in a shadow and a corner (Metal); 6 the transparent picture's alpha and its straight colour (Metal); 7 supersampling's
// sizes and the downsample filter; 8 an idle realistic view asks for no frames. Pixel gates run on macOS only; D3D11 and Vulkan are
// generated-shader currency only (Viewer3DFrameGateTests.Gate1b), as brief 106 recorded.

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class ShadowsOcclusionExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-shadow-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public ShadowsOcclusionExportTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── scenes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private const double Mm = 1e-3;

    private static Em3dSolid Box(string name, double x0, double y0, double z0, double x1, double y1, double z1)
        => new(name, "Copper", Em3dRole.Conductor, new Em3dBox(new(x0 * Mm, y0 * Mm, z0 * Mm), new(x1 * Mm, y1 * Mm, z1 * Mm)), 0);

    private static Em3dProblem Problem(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dProblem(solids, [], [], [], new Em3dAirBox(new(-0.05, -0.05, -0.05), new(0.05, 0.05, 0.05), new Em3dFaces(a, a, a, a, a, a)),
                               new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
    }

    /// <summary>A matte grey: what a shadow and a corner are read on.</summary>
    private static readonly TechAppearance Matte = new() { BaseColor = "#a0a0a0", Metallic = 0, Roughness = 1 };

    private static Scene3DModel Build(Em3dProblem problem, Func<string, TechAppearance?> look)
        => Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false,
            Appearance: n => look(n) is { } a ? new AppearanceOverride(a, []) : null));

    private static Viewer3DViewState View(Scene3DModel scene, C3dLook? look, int w, int h, Action<Viewer3DViewState>? camera = null)
    {
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        view.Realistic = true;
        var lk = RealisticLook.From(look);
        view.Look = lk;
        view.Environment = EnvironmentPrefilter.Studio(lk.Studio);
        camera?.Invoke(view);
        return view;
    }

    private sealed record Shot(byte[] Rgba, int W, int H, Viewer3DViewState View, Scene3DFramePlan Plan, Scene3DModel Scene)
    {
        public (int R, int G, int B, int A) At(Vector3 world)
        {
            var (x, y, visible) = View.Camera.Project(world, W, H);
            Assert.True(visible && x >= 0 && y >= 0 && x < W && y < H, $"{world} is off the picture at ({x}, {y})");
            return At((int)x, (int)y);
        }

        public (int R, int G, int B, int A) At(int x, int y)
        {
            int k = 4 * (y * W + x);
            return (Rgba[k], Rgba[k + 1], Rgba[k + 2], Rgba[k + 3]);
        }

        public void Save(string name)
        {
            string? dir = Environment.GetEnvironmentVariable("CRF_SHADOW_PNG");
            if (string.IsNullOrEmpty(dir)) return;
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), new FieldPictureShot(Rgba, W, H, 1, [], null, false) { Transparent = Plan.Transparent }.Png());
        }
    }

    /// <summary><paramref name="scene"/> drawn realistic by the real Metal backend: the live path (Frame, then the image read back), or a
    /// picture (RenderPixels) when <paramref name="export"/>.</summary>
    private static Shot Metal(Scene3DModel scene, Viewer3DViewState view, int w, int h, bool export = false, bool transparent = false,
                              Scene3DFieldGeometry? field = null)
    {
        using var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        metal.CreateOffscreenImages(w, h, 1);
        var session = new Viewer3DSession(() => metal) { ShadeStream = true, Environment = view.Environment };
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        plan.Plan(scene, view, w, h, metal.FlipY, pick: false, none, none, none, field, export: export, transparent: transparent);
        Assert.True(plan.Realistic);
        byte[] rgba;
        if (export) rgba = session.RenderPixels(plan, scene, none, none, none, field)!;
        else
        {
            session.Frame(0, plan, 1, scene, none, none, none, false, field);
            rgba = metal.ReadImage(0);
        }
        return new Shot(rgba, w, h, view, plan, scene);
    }

    private static int Diff((int R, int G, int B, int A) a, (int R, int G, int B, int A) b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static int Lum((int R, int G, int B, int A) c) => (int)Math.Round(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);

    /// <summary>The key light's world direction (toward the light) for <paramref name="view"/>'s Look.</summary>
    private static Vector3 Key(Viewer3DViewState view) => view.Look.Lighting(view.Environment!).KeyDirectionWorld;

    /// <summary>Where the ray from <paramref name="p"/> toward the light meets the plane z = <paramref name="z"/>, followed backwards: the
    /// point on that plane in <paramref name="p"/>'s shadow.</summary>
    private static Vector3 ShadowOf(Vector3 p, float z, Vector3 key) => p - key * ((p.Z - z) / key.Z);

    private static Vector3 V(double x, double y, double z) => new((float)(x * Mm), (float)(y * Mm), (float)(z * Mm));

    private static void Top(Viewer3DViewState v)
    {
        v.Camera.Projection = Projection3D.Orthographic;
        v.Camera.SetStandardView(StandardView3D.Top);
    }

    // ── 1. a shadow ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A box on a plate, the Dark studio's key from above at 60°, seen from straight above: a plate point in the box's shadow is
    /// darker than its mirror image across the box (same distance, toward the light) by more than 20/255; with Shadows false the two
    /// match within 2/255. Occlusion and the ground are off, so only the shadow differs between the two points.</summary>
    [Fact]
    public void Gate1_Metal_ABoxShadowsItsPlate_AndShadowsFalseRemovesIt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Build(Problem(Box("plate", 0, 0, 0, 6, 6, 0.3), Box("box", 2.5, 2.5, 0.3, 3.5, 3.5, 2.3)), _ => Matte);
        (int Shadowed, int Lit) Read(bool shadows)
        {
            var view = View(scene, new C3dLook { Environment = "Dark", Shadows = shadows, AmbientOcclusion = false, Ground = false }, 480, 360, Top);
            var shot = Metal(scene, view, 480, 360);
            shot.Save(shadows ? "gate1" : "gate1-off");
            var p = ShadowOf(V(3, 3, 1.8), (float)(0.3 * Mm), Key(view));
            var mirror = new Vector3(2 * V(3, 3, 0).X - p.X, 2 * V(3, 3, 0).Y - p.Y, p.Z);
            Assert.True(MathF.Abs(p.X - V(3, 3, 0).X) > 0.6f * Mm || MathF.Abs(p.Y - V(3, 3, 0).Y) > 0.6f * Mm, $"{p} is under the box");
            return (Lum(shot.At(p)), Lum(shot.At(mirror)));
        }
        var on = Read(true);
        Assert.True(on.Lit - on.Shadowed > 20, $"shadowed {on.Shadowed}, lit {on.Lit}");
        var off = Read(false);
        Assert.True(Math.Abs(off.Lit - off.Shadowed) <= 2, $"no shadows: {off.Shadowed} vs {off.Lit}");
    }

    // ── 2. an orbit renders no shadow pass ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ThreeOrbitStepsRenderNoShadowPass_ALookRotationRendersOne()
    {
        var scene = Build(Problem(Box("plate", 0, 0, 0, 6, 6, 0.3), Box("box", 2.5, 2.5, 0.3, 3.5, 3.5, 2.3)), _ => Matte);
        var view = View(scene, new C3dLook(), 400, 300);
        var fake = new PatchRecordingBackend();
        using var session = new Viewer3DSession(() => fake) { ShadeStream = true, Environment = view.Environment };
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        void Frame(bool orbiting = false)
        {
            plan.Plan(scene, view, 400, 300, false, false, none, none, none);
            session.Frame(0, plan, 1, scene, none, none, none, orbiting);
        }
        Frame();
        Assert.True(plan.ShadowDrawCount > 0 && plan.ShadowPass);
        Assert.Equal(1, session.ShadowPasses);
        for (int k = 0; k < 3; k++)
        {
            view.Camera.Orbit(40, 15);
            Frame(orbiting: true);
            Assert.False(plan.ShadowPass);
        }
        Assert.Equal(1, session.ShadowPasses);

        view.Look = RealisticLook.From(new C3dLook { Rotation = 75 });
        Frame();
        Assert.Equal(2, session.ShadowPasses);
        Frame();
        Assert.Equal(2, session.ShadowPasses);

        // and what the map depends on besides: an object hidden, then a picture's larger map
        view.Visible[scene.Objects.Single(o => o.Name == "box").Id - 1] = false;
        Frame();
        Assert.Equal(3, session.ShadowPasses);
        // the section plane: fs_depth discards what it cuts away, so turning it on and moving it re-render the map; moving it while it is
        // off does not
        view.Clip.Axis = ClipAxis3D.X;
        view.Clip.Offset = 1.5e-3f;
        Frame();
        Assert.Equal(3, session.ShadowPasses);
        view.Clip.Enabled = true;
        Frame();
        Assert.Equal(4, session.ShadowPasses);
        view.Clip.Offset = 2.5e-3f;
        Frame();
        Assert.Equal(5, session.ShadowPasses);
        view.Clip.Enabled = false;
        plan.Plan(scene, view, 400, 300, false, false, none, none, none, export: true);
        Assert.Equal(Shadows.ExportMapSize, plan.ShadowSize);
        session.RenderPixels(plan, scene, none, none, none, null);
        Assert.Equal(6, session.ShadowPasses);
    }

    // ── 3. glass casts none ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A slab floating over the plate: at Transmission 0.9 (glass, owner decision D1) it is no caster and its shadow point
    /// matches its mirror within 2/255; at 0.2 it is one, and its shadow point is darker by more than 20/255.</summary>
    [Fact]
    public void Gate3_Metal_ATransmission09SlabCastsNoShadow_At02ItDoes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        foreach (double t in (double[])[0.9, 0.2])
        {
            var slab = new TechAppearance { BaseColor = "#ffffff", Metallic = 0, Roughness = 0.6, Transmission = t };
            var scene = Build(Problem(Box("plate", 0, 0, 0, 6, 6, 0.3), Box("slab", 2.5, 2.5, 3, 3.5, 3.5, 3.2)), n => n == "slab" ? slab : Matte);
            var view = View(scene, new C3dLook { Environment = "Dark", AmbientOcclusion = false, Ground = false }, 480, 360, Top);
            var shot = Metal(scene, view, 480, 360);
            shot.Save($"gate3-{t}");
            uint id = scene.Objects.Single(o => o.Name == "slab").Id;
            Assert.Equal(t < Shadows.CasterTransmissionLimit, shot.Plan.ShadowDraws.Take(shot.Plan.ShadowDrawCount).Any(d => d.Object == id));
            var p = ShadowOf(V(3, 3, 3.1), (float)(0.3 * Mm), Key(view));
            var mirror = new Vector3(2 * V(3, 3, 0).X - p.X, 2 * V(3, 3, 0).Y - p.Y, p.Z);
            int shadowed = Lum(shot.At(p)), lit = Lum(shot.At(mirror));
            if (t >= Shadows.CasterTransmissionLimit) Assert.True(Math.Abs(lit - shadowed) <= 2, $"T {t}: {shadowed} vs {lit}");
            else Assert.True(lit - shadowed > 20, $"T {t}: shadowed {shadowed}, lit {lit}");
        }
    }

    // ── 4. contact shading ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An L (a floor and a wall on it), seen orthographically from the open side: the floor's inside corner is darker than its
    /// open face with occlusion on, and the two match within 2/255 with it off (same normal, same view direction, no shadows).</summary>
    [Fact]
    public void Gate4_Metal_TheInsideCornerIsDarker_AndEqualWithoutOcclusion()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Build(Problem(Box("floor", 0, 0, 0, 6, 6, 1), Box("wall", 5, 0, 1, 6, 6, 4)), _ => Matte);
        (int Corner, int Open) Read(bool occlusion)
        {
            var view = View(scene, new C3dLook { AmbientOcclusion = occlusion, Shadows = false, Ground = false }, 480, 360, v =>
            {
                v.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 480 / 360f, Projection3D.Orthographic);
                (v.Camera.Yaw, v.Camera.Pitch) = (MathF.PI, 0.5f);
            });
            var shot = Metal(scene, view, 480, 360);
            shot.Save(occlusion ? "gate4" : "gate4-off");
            return (Lum(shot.At(V(4.97, 3, 1))), Lum(shot.At(V(1.5, 3, 1))));
        }
        var on = Read(true);
        Assert.True(on.Open - on.Corner > 3, $"corner {on.Corner}, open {on.Open}");
        var off = Read(false);
        Assert.True(Math.Abs(off.Open - off.Corner) <= 2, $"no occlusion: corner {off.Corner}, open {off.Open}");
    }

    // ── 5. fields untouched ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A field lying on the plate where the box's shadow falls and up to the box's foot: both pixels read EXACTLY the colour map's
    /// colour (overview rule 2). Without the field the same two pixels are visibly shadowed and occluded, so the test is not vacuous.</summary>
    [Fact]
    public void Gate5_Metal_AFieldPixelInAShadowAndACornerReadsExactlyItsColourMapColour()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Build(Problem(Box("plate", 0, 0, 0, 6, 6, 0.3), Box("box", 2.5, 2.5, 0.3, 3.5, 3.5, 2.3)), _ => Matte);
        void Camera(Viewer3DViewState v)
        {
            v.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 480 / 360f, Projection3D.Orthographic);
            (v.Camera.Yaw, v.Camera.Pitch) = (-MathF.PI / 2, 0.6f);         // from the south, looking north at the box's south face
        }
        var look = new C3dLook { Environment = "Dark", Ground = false };
        var probe = View(scene, look, 480, 360, Camera);
        var inShadow = ShadowOf(V(3, 3, 2.3), (float)(0.3 * Mm), Key(probe));
        var corner = V(3, 2.47, 0.3);

        // non-vacuous: shadowed and occluded without the field
        var lit = Metal(scene, View(scene, look, 480, 360, Camera), 480, 360);
        var flat = Metal(scene, View(scene, new C3dLook { Environment = "Dark", Ground = false, Shadows = false, AmbientOcclusion = false }, 480, 360, Camera), 480, 360);
        Assert.True(Lum(flat.At(inShadow)) - Lum(lit.At(inShadow)) > 10, $"shadow {Lum(lit.At(inShadow))} vs {Lum(flat.At(inShadow))}");
        Assert.True(Lum(flat.At(corner)) - Lum(lit.At(corner)) > 3, $"corner {Lum(lit.At(corner))} vs {Lum(flat.At(corner))}");

        // the field: two triangles on the plate's top, a real scalar of 0.5 under a one-colour map (0.2, 0.4, 0.6)
        float z = (float)(0.3 * Mm);
        FieldVertex F(double x, double y) => new() { X = (float)(x * Mm), Y = (float)(y * Mm), Z = z, R0 = 0.5f };
        var field = new Scene3DFieldGeometry([F(1, 0.5), F(5, 0.5), F(5, 2.5), F(1, 0.5), F(5, 2.5), F(1, 2.5)], 1);
        var view = View(scene, look, 480, 360, Camera);
        view.ShowField = true;
        float[] block = [1, 0, 0, 1, 2, 0, 2, 0, 0, 0.2f, 0.4f, 0.6f, 1, 0.2f, 0.4f, 0.6f];
        block.CopyTo(view.Field.AsSpan());
        var shot = Metal(scene, view, 480, 360, field: field);
        shot.Save("gate5");
        Assert.Equal((51, 102, 153, 255), shot.At(inShadow));
        Assert.Equal((51, 102, 153, 255), shot.At(corner));
    }

    // ── 6. the transparent picture ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A box with the ground, drawn as a transparent picture: alpha 0 far from it, between 1 and 254 in its shadow on the ground,
    /// 255 on it — and the picture's plan carries no hover and no selection (R-em3d107-5e).</summary>
    [Fact]
    public void Gate6_Metal_ATransparentPicture_HasNothingFarAway_ASoftGroundShadow_AndAnOpaqueBox()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Build(Problem(Box("box", 0, 0, 0, 1, 1, 1)), _ => Matte);
        var view = View(scene, new C3dLook(), 480, 360);
        uint box = scene.Objects.Single(o => o.Name == "box").Id;
        view.Hovered = box;
        view.Selection = [Scene3DItem.OfObject(box)];
        var shot = Metal(scene, view, 480, 360, export: true, transparent: true);
        shot.Save("gate6");
        Assert.True(shot.Plan.Transparent && shot.Plan.GroundDrawn);
        Assert.DoesNotContain(shot.Plan.Draws.Take(shot.Plan.DrawCount), d => d.Pipeline is Scene3DPipeline.Edges or Scene3DPipeline.OnTop or Scene3DPipeline.Backdrop);
        var bits = System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(shot.Plan.Uniforms.AsSpan());
        Assert.Equal((0u, 0u), (bits[24], bits[28]));
        Assert.Equal(0, shot.At(4, 4).A);
        Assert.Equal(255, shot.At(V(0.5, 0.5, 1)).A);
        int shadow = shot.At(ShadowOf(V(0.5, 0.5, 0.7), 0, Key(view))).A;
        Assert.InRange(shadow, 1, 254);
    }

    /// <summary>A 50 % grey slab of Transmission 0.5 over nothing: its pixel, straightened, composites over black and over white back to
    /// what the view draws over those backgrounds, within 1/255 — the straight colour is right, not just the premultiplied one.</summary>
    [Fact]
    public void Gate6_Metal_AGreyTransmissionSlabOverNothing_StraightensBackToItsColour()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var grey = new TechAppearance { BaseColor = "#808080", Metallic = 0, Roughness = 0.5, Transmission = 0.5 };
        var scene = Build(Problem(Box("slab", 0, 0, 0, 4, 4, 0.2)), _ => grey);
        var centre = V(2, 2, 0.2);
        (int R, int G, int B, int A) Over(string? background, bool transparent)
        {
            var view = View(scene, new C3dLook { Background = background, Ground = false }, 320, 240);
            var shot = Metal(scene, view, 320, 240, export: true, transparent: transparent);
            return shot.At(centre);
        }
        var pm = Over(null, transparent: true);
        Assert.InRange(pm.A, 60, 200);
        var px = new[] { (byte)pm.R, (byte)pm.G, (byte)pm.B, (byte)pm.A };
        PictureResample.Unpremultiply(px);
        float a = pm.A / 255f;
        var black = Over("#000000", false);
        var white = Over("#ffffff", false);
        for (int c = 0; c < 3; c++)
        {
            int[] onBlack = [black.R, black.G, black.B], onWhite = [white.R, white.G, white.B];
            Assert.True(Math.Abs(px[c] * a - onBlack[c]) <= 1, $"channel {c}: straight {px[c]} × {a} over black vs {onBlack[c]}");
            Assert.True(Math.Abs(px[c] * a + 255 * (1 - a) - onWhite[c]) <= 1, $"channel {c}: straight {px[c]} × {a} over white vs {onWhite[c]}");
        }
    }

    // ── 7. supersampling ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_TheDownsampleFilter_OnKnownPatterns()
    {
        // a flat colour stays exactly itself; a one-pixel checker becomes its mean; energy is kept
        foreach (int k in (int[])[2, 4])
        {
            int w = 8 * k, h = 4 * k;
            var flat = new byte[w * h * 4];
            for (int i = 0; i < flat.Length; i += 4) (flat[i], flat[i + 1], flat[i + 2], flat[i + 3]) = (12, 200, 77, 255);
            Assert.All(Chunks(PictureResample.Downsample(flat, w, h, k)), p => Assert.Equal((12, 200, 77, 255), p));
            var checker = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)((x + y) % 2 == 0 ? 255 : 0);
                    int i = 4 * (y * w + x);
                    (checker[i], checker[i + 1], checker[i + 2], checker[i + 3]) = (v, v, v, 255);
                }
            // away from the picture's edge, where the tent's clamped taps repeat the border
            var down2 = PictureResample.Downsample(checker, w, h, k);
            for (int y = 1; y < 3; y++)
                for (int x = 1; x < 7; x++) Assert.InRange(down2[4 * (y * 8 + x)], 127, 128);
            Assert.Equal(1f, PictureResample.Weights(k).Sum(), 5);
        }
        // a step: monotonic, with the tent's ramp across the edge
        var step = new byte[16 * 2 * 4];
        for (int y = 0; y < 2; y++) for (int x = 8; x < 16; x++) step[4 * (y * 16 + x)] = 255;
        var down = Chunks(PictureResample.Downsample(step, 16, 2, 2)).Take(8).Select(p => p.R).ToArray();
        Assert.Equal([0, 0, 0, 32, 223, 255, 255, 255], down);
        // straightening: a premultiplied 50 % grey at half cover, a transparent pixel, an opaque one
        byte[] pm = [64, 64, 64, 128, 9, 9, 9, 0, 10, 20, 30, 255];
        PictureResample.Unpremultiply(pm);
        Assert.Equal([128, 128, 128, 128, 0, 0, 0, 0, 10, 20, 30, 255], pm);

        static IEnumerable<(int R, int G, int B, int A)> Chunks(byte[] b)
        {
            for (int i = 0; i < b.Length; i += 4) yield return (b[i], b[i + 1], b[i + 2], b[i + 3]);
        }
    }

    [Fact]
    public void Gate7_APictureIsTheRequestedSize_AtOneTwoAndFourTimes()
    {
        var vm = Open(new C3dDocument { Objects = [new C3dBox { Name = "b", Material = "Copper", Min = new(0, 0, 0), Size = new(1000, 1000, 1000) }] });
        var fake = (PatchRecordingBackend)vm.Viewer.Session.Backend!;
        vm.Viewer.EnvironmentFor = (s, _) => EnvironmentPrefilter.Studio(s);
        vm.Viewer.IsRealistic = true;
        Pump(() => vm.Viewer.View.Environment is not null);
        foreach (int k in PictureResample.Factors)
        {
            var shot = vm.Viewer.CapturePicture(300, 200, 2, out string? error, transparent: true, supersample: k);
            Assert.Null(error);
            Assert.Equal((600, 400, k, true), (shot!.Width, shot.Height, shot.Supersample, shot.Transparent));
            Assert.Equal(600 * 400 * 4, shot.Rgba.Length);
            Assert.Equal((600 * k, 400 * k), fake.LastPixels);
            // the occlusion's cap in pixels grows with the picture (2 × the window, k × supersampled), so its reach in the world is the view's
            Assert.Equal(2f * k, fake.LastOcclusionScale);
        }
        // Copy draws once, at its size: supersampling is Export Picture's
        Assert.Equal(1, vm.Viewer.CapturePicture(300, 200, 2, out _)!.Supersample);
        // the cap: a picture whose drawn size would pass MaxSide supersamples less
        Assert.Equal(2, Viewer3DViewModel.SupersampleFor(FieldPicture.MaxSide / 3, 100, 4));
        Assert.Equal(1, Viewer3DViewModel.SupersampleFor(FieldPicture.MaxSide, 100, 4));
        // the default view's picture is drawn once at its size, and is never transparent
        vm.Viewer.IsRealistic = false;
        var plain = vm.Viewer.CapturePicture(300, 200, 2, out _, transparent: true)!;
        Assert.Equal((600, 400, 1, false), (plain.Width, plain.Height, plain.Supersample, plain.Transparent));
    }

    // ── 8. idle ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>After a realistic frame, with nothing changing, the view asks for no further frame (overview §1e: no accumulation, no
    /// animated noise) — counted over a simulated idle period of many dispatcher turns.</summary>
    [Fact]
    public void Gate8_AnIdleRealisticViewRequestsNoFrames()
    {
        var vm = Open(new C3dDocument { Objects = [new C3dBox { Name = "b", Material = "Copper", Min = new(0, 0, 0), Size = new(1000, 1000, 1000) }] });
        vm.Viewer.EnvironmentFor = (s, _) => EnvironmentPrefilter.Studio(s);
        vm.Viewer.IsRealistic = true;
        Pump(() => vm.Viewer.View.Environment is not null);
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        plan.Plan(vm.Viewer.Scene, vm.Viewer.View, 400, 300, false, false, none, none, none);
        vm.Viewer.Session.Frame(0, plan, 1, vm.Viewer.Scene, none, none, none, false);
        Assert.True(plan.Realistic && plan.Occlusion);
        int requested = 0;
        vm.Viewer.FrameRequested += () => requested++;
        for (int turn = 0; turn < 200; turn++)
        {
            while (_posted.TryDequeue(out var a)) a();
            Thread.Yield();
        }
        Assert.Equal(0, requested);
    }

    // ── the shader's constants are the reference's ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheShadersShadowAndOcclusionConstants_AreTheReferences()
    {
        string wgsl = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Viewer3D", "Shaders", "scene.wgsl"));
        double F(string name) => double.Parse(Regex.Match(wgsl, $@"^const {name}: (?:f32|u32) = ([0-9.eE+\-]+)u?;", RegexOptions.Multiline).Groups[1].Value,
                                              CultureInfo.InvariantCulture);
        Assert.Equal(Shadows.MinSlopeCos, (float)F("SHADOW_MIN_COS"));
        Assert.Equal(Shadows.Poisson.Length, (int)F("SHADOW_TAPS"));
        Assert.Equal(Occlusion.Directions, (int)F("AO_DIRECTIONS"));
        Assert.Equal(Occlusion.Steps, (int)F("AO_STEPS"));
        Assert.Equal(Occlusion.Bias, (float)F("AO_BIAS"));
        Assert.Equal(Occlusion.MaxPixels, (float)F("AO_MAX_PIXELS"));
        Assert.Equal(Occlusion.Empty, (float)F("AO_EMPTY"));
        Assert.Equal(Ground.FadeStart, (float)F("GROUND_FADE"));
        var taps = Regex.Matches(wgsl[wgsl.IndexOf("var<private> POISSON", StringComparison.Ordinal)..], @"vec2f\(([-0-9.]+), ([-0-9.]+)\)")
                        .Take(16).Select(m => new Vector2(float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                                          float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal(Shadows.Poisson, taps);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Pump(Func<bool> until)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return until();
        }, TimeSpan.FromSeconds(60)), "never settled");

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Pump(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested);
        return vm;
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
