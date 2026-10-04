// brief-em3d-110 R-em3d110-3 — the GPU and the CPU draw the same realistic picture. The only way the two can drift is the WGSL and the C#
// disagreeing (106 §3g scans their constants); this holds the PICTURE: the real Metal backend offscreen and RealisticPicture, the same
// plan, compared per pixel away from a one-pixel band at each depth discontinuity, where the two rasterisers' coverage differs.
//
//    5  a row of spheres (copper, gold, silver, aluminium, a rough dielectric, a clear-coated one, glass), a box on a plate (shadow,
//       occlusion, ground) and a field plane — Exact from an orthographic view, Lit from a perspective one (D1): at least 99 % of
//       pixels within 4/255 a channel, every field pixel within 1/255. macOS only; elsewhere skipped with the reason.
//
// And the rasteriser's work, counted rather than timed: a shadow map's texels, and a frame's shaded samples.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Viewer3D;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Viewer3D;

/// <summary>A gate that needs the Metal backend: skipped, with that reason, off macOS.</summary>
internal sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs the Metal backend, which only macOS has; D3D11 and Vulkan run the same generated shader";
    }
}

[Collection(Viewer3DCollection.Name)]
public sealed class RealisticPictureTests(ITestOutputHelper output)
{
    private const double Mm = 1e-3;
    private const int W = 480, H = 360;

    // ── the fixture ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, TechAppearance> Looks = new()
    {
        ["plate"] = new() { BaseColor = "#a0a0a0", Metallic = 0, Roughness = 1 },
        ["box"] = new() { BaseColor = "#3060c0", Metallic = 0, Roughness = 0.5 },
        ["copper"] = new() { BaseColor = "#f0a080", Metallic = 1, Roughness = 0.25 },
        ["gold"] = new() { BaseColor = "#ffd060", Metallic = 1, Roughness = 0.2 },
        ["silver"] = new() { BaseColor = "#f4f4f2", Metallic = 1, Roughness = 0.15 },
        ["aluminium"] = new() { BaseColor = "#d8dade", Metallic = 1, Roughness = 0.4 },
        ["rough"] = new() { BaseColor = "#40a040", Metallic = 0, Roughness = 0.9 },
        ["coated"] = new() { BaseColor = "#c03030", Metallic = 0, Roughness = 0.6, Clearcoat = 1, ClearcoatRoughness = 0.05 },
        ["glass"] = new() { BaseColor = "#ffffff", Metallic = 0, Roughness = 0.05, Transmission = 1, Ior = 1.5 },
    };

    private static Scene3DModel Scene()
    {
        static Em3dSolid Box(string name, double x0, double y0, double z0, double x1, double y1, double z1)
            => new(name, "Copper", Em3dRole.Conductor, new Em3dBox(new(x0 * Mm, y0 * Mm, z0 * Mm), new(x1 * Mm, y1 * Mm, z1 * Mm)), 0);
        var solids = new List<Em3dSolid> { Box("plate", 0, 0, 0, 16, 8, 0.4), Box("box", 11, 4.5, 0.4, 14, 7, 2.4) };
        string[] row = ["copper", "gold", "silver", "aluminium", "rough", "coated", "glass"];
        for (int k = 0; k < row.Length; k++)
            solids.Add(new Em3dSolid(row[k], "Copper", Em3dRole.Conductor, new Em3dSphere(new((1.5 + 2 * k) * Mm, 2 * Mm, 1.2 * Mm), 0.8 * Mm), 0));
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(solids, [], [], [], new Em3dAirBox(new(-0.05, -0.05, -0.05), new(0.05, 0.05, 0.05), new Em3dFaces(a, a, a, a, a, a)),
                                      new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        return Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false,
            Appearance: n => Looks.TryGetValue(n, out var look) ? new AppearanceOverride(look, []) : null));
    }

    /// <summary>A field plane on the plate's top, left of the box: a real scalar rising along x through a five-stop map.</summary>
    private static Scene3DFieldGeometry Field()
    {
        float z = (float)(0.4 * Mm);
        FieldVertex F(double x, double y) => new() { X = (float)(x * Mm), Y = (float)(y * Mm), Z = z, R0 = (float)((x - 1) / 9) };
        var v = new List<FieldVertex>();
        for (int k = 0; k < 9; k++)
        {
            double x0 = 1 + k, x1 = 2 + k;
            v.AddRange([F(x0, 4.5), F(x1, 4.5), F(x1, 7.5), F(x0, 4.5), F(x1, 7.5), F(x0, 7.5)]);
        }
        return new Scene3DFieldGeometry([.. v], 1);
    }

    private static Viewer3DViewState View(Scene3DModel scene, C3dLook look, bool perspective)
    {
        var view = new Viewer3DViewState
        {
            Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / (float)H, perspective ? Projection3D.Perspective : Projection3D.Orthographic),
        };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        (view.Camera.Yaw, view.Camera.Pitch) = (-1.2f, 0.62f);
        if (perspective) view.Camera.Distance *= 0.8f;
        view.Realistic = true;
        view.Look = RealisticLook.From(look);
        view.Environment = EnvironmentPrefilter.Studio(view.Look.Studio);
        view.ShowField = true;
        // block 0: a real scalar (mode 2) over 0 … 1, five stops
        float[] block = [1, 0, 0, 1, 2, 0, 5, 0,
                         0, 0.1f, 0.1f, 0.6f, 0.25f, 0.1f, 0.6f, 0.9f, 0.5f, 0.2f, 0.8f, 0.2f, 0.75f, 0.95f, 0.85f, 0.1f, 1, 0.9f, 0.2f, 0.1f];
        block.CopyTo(view.Field.AsSpan());
        return view;
    }

    private static (Scene3DFramePlan Plan, byte[] Gpu) Metal(Scene3DModel scene, Viewer3DViewState view, Scene3DFieldGeometry field)
    {
        using var metal = new MetalBackendScope();
        var session = new Viewer3DSession(() => metal.Backend) { ShadeStream = true, Environment = view.Environment };
        session.EnsureBackend();
        // the CPU picture's rows run down, as Metal's framebuffer's do: the plan is not flipped for either
        Assert.False(metal.Backend.FlipY);
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        plan.Plan(scene, view, W, H, flipY: false, pick: false, none, none, none, field, export: true);
        Assert.True(plan.Realistic);
        return (plan, session.RenderPixels(plan, scene, none, none, none, field)!);
    }

    private sealed class MetalBackendScope : IDisposable
    {
        public readonly CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend Backend = new();
        public MetalBackendScope() => Backend.CreateOffscreenImages(W, H, 1);
        public void Dispose() => Backend.Dispose();
    }

    // ── 5. GPU = CPU ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [MacOSFact]
    public void Gate5_TheMetalPictureAndTheCpuPictureAgree_Exact_Orthographic() => Agree(C3dFieldStyle.Exact, perspective: false);

    [MacOSFact]
    public void Gate5_TheMetalPictureAndTheCpuPictureAgree_Lit_Perspective() => Agree(C3dFieldStyle.Lit, perspective: true);

    private void Agree(C3dFieldStyle style, bool perspective)
    {
        var scene = Scene();
        var field = Field();
        var view = View(scene, new C3dLook { FieldStyle = style.ToString() }, perspective);
        var (plan, gpu) = Metal(scene, view, field);
        var cpu = RealisticPicture.Draw(plan, scene, view, field);
        Save($"gate5-{style}", gpu, cpu.Rgba);

        // the one-pixel band at each discontinuity: a pixel whose 8 neighbours do not all show the same surface
        var s = cpu.Surface;
        bool Edge(int x, int y)
        {
            int c = s[y * W + x];
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    int u = Math.Clamp(x + i, 0, W - 1), v = Math.Clamp(y + j, 0, H - 1);
                    if (s[v * W + u] != c) return true;
                }
            return false;
        }
        int compared = 0, within = 0, fieldPixels = 0, worstField = 0, worst = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (Edge(x, y)) continue;
                int k = 4 * (y * W + x), d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(gpu[k + c] - cpu.Rgba[k + c]));
                compared++;
                if (d <= 4) within++;
                worst = Math.Max(worst, d);
                if (s[y * W + x] != RealisticPicture.FieldSurface) continue;
                fieldPixels++;
                worstField = Math.Max(worstField, d);
            }
        double share = within / (double)compared;
        output.WriteLine($"{style}: {compared:N0} pixels compared, {share:P2} within 4/255 (worst {worst}); {fieldPixels:N0} field pixels, worst {worstField}");
        Assert.True(fieldPixels > 1000, "the field is not in the picture");
        Assert.True(share >= 0.99, $"{share:P2} of pixels within 4/255");
        Assert.True(worstField <= 1, $"a field pixel differs by {worstField}/255");
    }

    // ── counters ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The work, counted: every visible pixel shaded once (the opaque run is resolved first), the backdrop's pixels with none of
    /// it skipped, the shadow map written, the occlusion computed where the prepass drew — and the same counts on one thread.</summary>
    [Fact]
    public void TheRasterisersWorkIsCounted_AndIsTheSameOnOneThread()
    {
        var scene = Scene();
        var view = View(scene, new C3dLook { Background = "#202020,#606060" }, perspective: false);
        view.ShowField = false;
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        plan.Plan(scene, view, W, H, false, false, none, none, none, null);         // the live view's 2048² map: the claim is the same
        var a = RealisticPicture.Draw(plan, scene, view, null);
        var b = RealisticPicture.Draw(plan, scene, view, null, threads: 1);
        output.WriteLine($"shaded {a.Counters.SamplesShaded:N0}, shadow texels {a.Counters.ShadowTexelsWritten:N0}, occlusion {a.Counters.OcclusionPixels:N0}");
        Assert.Equal(a.Rgba, b.Rgba);
        Assert.Equal((a.Counters.SamplesShaded, a.Counters.ShadowTexelsWritten, a.Counters.OcclusionPixels),
                     (b.Counters.SamplesShaded, b.Counters.ShadowTexelsWritten, b.Counters.OcclusionPixels));
        Assert.Equal(0, a.Counters.SkippedDraws);
        int surfaces = a.Surface.Count(x => x != 0);
        Assert.True(a.Counters.SamplesShaded >= W * H + surfaces, $"{a.Counters.SamplesShaded} < the backdrop's {W * H} + {surfaces}");
        Assert.True(a.Counters.ShadowTexelsWritten > 0 && a.Counters.OcclusionPixels >= surfaces);
    }

    private static void Save(string name, byte[] gpu, byte[] cpu)
    {
        string? dir = Environment.GetEnvironmentVariable("CRF_SHADOW_PNG");
        if (string.IsNullOrEmpty(dir)) return;
        File.WriteAllBytes(Path.Combine(dir, name + "-gpu.png"), new FieldPictureShot(gpu, W, H, 1, [], null, false).Png());
        File.WriteAllBytes(Path.Combine(dir, name + "-cpu.png"), new FieldPictureShot(cpu, W, H, 1, [], null, false).Png());
    }
}
