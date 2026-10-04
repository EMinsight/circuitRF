// brief-em3d-110 R-em3d110-2 — THE REALISTIC VIEW ON THE CPU: a Scene3DFramePlan planned realistic, executed pass for pass as a GPU
// backend executes it (MetalViewer3DBackend.RenderInto), with every fragment shaded by the C# reference the shader is checked against.
// `render x.c3d --look realistic` draws through here, so a picture needs no window and no GPU.
//
// What it is NOT is a second renderer. The plan — which objects, which pipeline, the draw order, the translucent sort, the shadow map's
// window, the occlusion's reach, the ground, the backdrop's ray, every uniform — is Scene3DFramePlan's, the same object the GPU
// backends read. The triangles are rasterised by SoftwareRaster (brief 89's rasteriser, factored out for this) under the GPU's top-left
// rule. Each fragment is the WGSL entry point's arithmetic, calling Pbr.cs (fs_pbr: Pbr.Shade; fs_field_lit: Pbr.FieldSheen and
// Pbr.LitField), ToneCurve.cs and PrefilteredEnvironment's sampler of the same bytes the GPU samples. Where the WGSL does arithmetic of
// its own (shadow_vis, the horizon occlusion and its blur, ground_hit, fs_backdrop, field_colour, fs_color), it is transcribed below
// line for line, its constants read from Shadows.cs, the reference both sides are scanned against.
//
// The passes, in the GPU's order:
//   1. the key light's shadow map (ShadowDraws, depth only, the shadow bias) at the plan's ShadowSize;
//   2. the occlusion: the Pbr draws' (and the ground's) distance along the view ray into a float target, the horizon pass and its blur,
//      each into an R8 target — quantised to 1/255 as R8 stores it;
//   3. the colour pass: the plan's Draws in order into an RGBA8 target, a write rounded to 1/255 as UNORM8 rounds it, blending what the
//      pipeline blends. A run of opaque draws (Pbr, Opaque, Field) is resolved by depth first and shaded once per pixel — the same
//      result, since such a draw neither reads nor blends the target, and a fragment it discards is decided before the depth write.
//
// DETERMINISTIC (R-em3d110-2d): each pass runs in bands of BandRows rows, in parallel; a band owns its rows outright and visits the
// draws and their triangles in the plan's order, so every pixel is computed by one thread in one order whatever the thread count, and
// no sum crosses a band. Counters are summed per band with integer adds. The same plan gives the same bytes on 1 thread or 16.

using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Render.Scene3D.Look;

/// <summary>What a CPU realistic frame did, counted (the gates hold these, not times).</summary>
public sealed class RealisticCounters
{
    /// <summary>Fragments shaded in the colour pass: one per pixel a full-screen pass covers, one per visible pixel of an opaque run, one
    /// per fragment of a blended draw.</summary>
    public long SamplesShaded;
    /// <summary>Depth texels the shadow pass wrote.</summary>
    public long ShadowTexelsWritten;
    /// <summary>Pixels the horizon occlusion was computed for (a point the prepass drew).</summary>
    public long OcclusionPixels;
    /// <summary>The colour pass's draws executed, and those of a pipeline this mirror does not draw (a grid, an image, the selection's).</summary>
    public int Draws, SkippedDraws;
    /// <summary>The bands each pass ran in, and the threads asked for (0: the runtime's choice).</summary>
    public int Bands, Threads;
}

/// <summary>A CPU frame at the plan's size: premultiplied RGBA8, rows top first; per pixel, what the last depth-writing fragment was
/// (<see cref="RealisticPicture.FieldSurface"/>, an object id, or 0 for none) and the depth buffer.</summary>
public sealed record RealisticFrame(int Width, int Height, byte[] Rgba, int[] Surface, float[] Depth, RealisticCounters Counters);

/// <summary>A picture brought down to its size: premultiplied RGBA8 (straight when written: <see cref="PictureResample.Unpremultiply"/>).</summary>
public sealed record RealisticShot(byte[] Rgba, int Width, int Height, int Supersample, bool Transparent, RealisticCounters Counters);

public static class RealisticPicture
{
    /// <summary>The rows a band holds: the unit of parallel work, of progress and of cancellation.</summary>
    public const int BandRows = 16;

    /// <summary>The environment variable a test (or a user) caps the threads with; unset or 0, the runtime decides.</summary>
    public const string ThreadsVariable = "CRF_RASTER_THREADS";

    /// <summary>What <see cref="RealisticFrame.Surface"/> holds for a field fragment.</summary>
    public const int FieldSurface = -1;

    /// <summary>
    /// The picture of <paramref name="scene"/> through <paramref name="view"/> (realistic, its environment ready) at
    /// <paramref name="width"/> × <paramref name="height"/>: planned as Export Picture plans it (no hover, no selection) at
    /// <paramref name="supersample"/> × the size (the largest of 4, 2, 1 at most that within <see cref="FieldPicture.MaxSide"/>), drawn
    /// here, and brought down by <see cref="PictureResample.Downsample"/>. <paramref name="scale"/> is the picture's pixels per window
    /// pixel, which scales the occlusion's cap in pixels as Export Picture's does.
    /// </summary>
    public static RealisticShot Take(Scene3DModel scene, Viewer3DViewState view, Scene3DFieldGeometry? field, int width, int height,
                                     int supersample = PictureResample.DefaultFactor, bool transparent = false, float scale = 1,
                                     RunControl? control = null, int threads = 0)
    {
        int ss = PictureResample.FactorFor(width, height, supersample);
        var plan = new Scene3DFramePlan();
        var none = Scene3DOverlay.None;
        plan.Plan(scene, view, width * ss, height * ss, flipY: false, pick: false, none, none, none, field, export: true,
                  transparent: transparent, pixelScale: scale * ss);
        var frame = Draw(plan, scene, view, field, control, threads);
        control?.Token.ThrowIfCancellationRequested();
        var rgba = ss > 1 ? PictureResample.Downsample(frame.Rgba, frame.Width, frame.Height, ss) : frame.Rgba;
        return new RealisticShot(rgba, width, height, ss, plan.Transparent, frame.Counters);
    }

    /// <summary>
    /// Executes <paramref name="plan"/> — planned realistic for <paramref name="scene"/>, <paramref name="view"/> and
    /// <paramref name="field"/>, with flipY false — and returns the frame. Progress is a stage per pass, ticked per band; a cancelled
    /// <paramref name="control"/> throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static RealisticFrame Draw(Scene3DFramePlan plan, Scene3DModel scene, Viewer3DViewState view, Scene3DFieldGeometry? field,
                                      RunControl? control = null, int threads = 0)
    {
        if (!plan.Realistic || view.Environment is not { } env)
            throw new ArgumentException("The plan was not planned realistic, or the view has no environment.", nameof(plan));
        if (threads <= 0 && int.TryParse(Environment.GetEnvironmentVariable(ThreadsVariable), out int t) && t > 0) threads = t;
        var f = new Frame(plan, scene, view, env, field, control, threads);
        f.Run();
        return new RealisticFrame(f.W, f.H, f.Colour, f.Surface, f.Depth, f.Counters);
    }

    // ── one frame ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Frame
    {
        public readonly int W, H;
        public readonly byte[] Colour;
        public readonly float[] Depth;
        public readonly int[] Surface;
        public readonly RealisticCounters Counters = new();

        private readonly Scene3DFramePlan _plan;
        private readonly Scene3DModel _scene;
        private readonly PrefilteredEnvironment _env;
        private readonly Scene3DFieldGeometry? _field;
        private readonly RunControl? _control;
        private readonly ParallelOptions _parallel;
        private readonly PbrMaterial[] _table;
        private readonly PbrLighting _light;
        private readonly U _u;

        // the passes' own targets
        private float[] _shadow = [];
        private int _shadowSize;
        private readonly byte[] _aoBlur;
        private float[]? _fieldNormals;

        public Frame(Scene3DFramePlan plan, Scene3DModel scene, Viewer3DViewState view, PrefilteredEnvironment env, Scene3DFieldGeometry? field,
                     RunControl? control, int threads)
        {
            _plan = plan; _scene = scene; _env = env; _field = field; _control = control;
            W = Math.Max(1, plan.Width); H = Math.Max(1, plan.Height);
            Colour = new byte[4 * W * H];
            Depth = new float[W * H];
            Surface = new int[W * H];
            _aoBlur = new byte[W * H];
            _parallel = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : -1, CancellationToken = control?.Token ?? default };
            Counters.Threads = threads;
            _u = new U(plan.Uniforms);
            // the appearance table: the scene's rows, zero past them (Pbr.Table's rule)
            _table = new PbrMaterial[Pbr.TableRows];
            for (int k = 0; k < Math.Min(scene.Appearances.Length, Pbr.TableRows); k++) _table[k] = PbrMaterial.Of(scene.Appearances[k]);
            _light = new PbrLighting(_u.Lk.X, _u.Lk.Y, view.Look.RotationDegrees, _u.Key, _u.KeyC);
        }

        public void Run()
        {
            if (_plan.ShadowDrawCount > 0 && _plan.ShadowSize > 0) ShadowPass();
            if (_plan.Occlusion) OcclusionPass();
            ColourPass();
        }

        private void Bands(int rows, string stage, Action<int, int> band)
        {
            int n = (rows + BandRows - 1) / BandRows;
            Counters.Bands += n;
            _control?.BeginStage(stage, n, "bands");
            Parallel.For(0, n, _parallel, b =>
            {
                if (_parallel.CancellationToken.IsCancellationRequested) return;
                band(b * BandRows, Math.Min(rows, (b + 1) * BandRows));
                _control?.TickStage();
            });
            _parallel.CancellationToken.ThrowIfCancellationRequested();
        }

        // ── geometry: a draw's triangles, transformed once and rasterised by every band ─────────────────────────────────────

        private Geometry Prepare(in Scene3DDraw d, Matrix4x4 vp, int size, bool shadow = false, int sizeH = 0)
        {
            sizeH = sizeH > 0 ? sizeH : size;
            var slot = _plan.Transforms.AsSpan(Scene3DFramePlan.TransformFloats * Math.Clamp(d.Transform, 0, Math.Max(0, _plan.TransformCount - 1)),
                                               Scene3DFramePlan.TransformFloats);
            var m = Row(slot);
            uint idOffset = BitConverter.SingleToUInt32Bits(slot[16]);
            uint layer = Math.Min(BitConverter.SingleToUInt32Bits(slot[17]), (uint)FieldUniforms.MaxLayers - 1);
            var g = new Geometry { Pipeline = d.Pipeline, Layer = (int)layer };
            bool fieldDraw = d.Buffer == Scene3DBuffer.Field;
            int tris = d.Count / 3;
            g.World = new Vector3[3 * tris];
            var clip = new Vector4[3 * tris];
            if (fieldDraw)
            {
                var fv = _field!.Vertices;
                g.Re = new Vector3[3 * tris]; g.Im = new Vector3[3 * tris];
                bool lit = d.Pipeline == Scene3DPipeline.FieldLit;
                if (lit) { _fieldNormals ??= FieldShading.Normals(fv, _field.Layers); g.Normal = new Vector3[3 * tris]; }
                for (int k = 0; k < 3 * tris; k++)
                {
                    ref var v = ref fv[d.First + k];
                    var p = new Vector3(v.X, v.Y, v.Z);              // vs_field: no per-draw transform
                    g.World[k] = p;
                    clip[k] = Vector4.Transform(new Vector4(p, 1), vp);
                    g.Re[k] = new Vector3(v.R0, v.R1, v.R2); g.Im[k] = new Vector3(v.I0, v.I1, v.I2);
                    if (lit) { int q = FieldShading.Floats * (d.First + k); g.Normal![k] = new Vector3(_fieldNormals![q], _fieldNormals[q + 1], _fieldNormals[q + 2]); }
                }
            }
            else
            {
                var verts = _scene.Vertices;
                var idx = _scene.Indices;
                bool pbr = d.Pipeline is Scene3DPipeline.Pbr or Scene3DPipeline.PbrTranslucent;
                var shade = _scene.ShadeVertices;
                if (!shadow) { g.Rgba = new uint[3 * tris]; g.Id = new uint[tris]; }
                if (pbr) { g.Normal = new Vector3[3 * tris]; g.Slot = new uint[tris]; }
                for (int k = 0; k < 3 * tris; k++)
                {
                    uint vi = idx[d.First + k];
                    ref var v = ref verts[vi];
                    var p = Vector3.Transform(new Vector3(v.X, v.Y, v.Z), m);
                    g.World[k] = p;
                    clip[k] = Vector4.Transform(new Vector4(p, 1), vp);
                    if (shadow) continue;
                    g.Rgba![k] = v.Rgba;
                    if (k % 3 == 0) g.Id![k / 3] = v.Id + idOffset;
                    if (!pbr) continue;
                    var s = vi < shade.Length ? shade[vi] : default;
                    g.Normal![k] = Vector3.TransformNormal(new Vector3(s.Nx, s.Ny, s.Nz), m);
                    if (k % 3 == 0) g.Slot![k / 3] = s.Slot;              // flat: the provoking (first) vertex's
                }
            }
            var bias = shadow ? Scene3DFramePlan.ShadowBias : Scene3DFramePlan.DepthBias(d.Tie);
            g.Build(clip, tris, size, sizeH, bias);
            return g;
        }

        // ── 1. the shadow map ────────────────────────────────────────────────────────────────────────────────────────────

        private void ShadowPass()
        {
            int s = _shadowSize = _plan.ShadowSize;
            _shadow = new float[s * s];
            Array.Fill(_shadow, 1f);
            var lvp = Row(_plan.Uniforms.AsSpan(Scene3DFramePlan.LookAt + Scene3DFramePlan.LightingAt, 16));
            var draws = new List<Geometry>();
            for (int i = 0; i < _plan.ShadowDrawCount; i++)
            {
                ref var d = ref _plan.ShadowDraws[i];
                if (d.Buffer != Scene3DBuffer.Scene || d.Count < 3) continue;
                draws.Add(Prepare(d, lvp, s, shadow: true));
            }
            long written = 0;
            Bands(s, "shadow", (lo, hi) =>
            {
                long n = 0;
                foreach (var g in draws)
                    for (int p = 0; p < g.Prims.Length; p++)
                    {
                        ref var pr = ref g.Prims[p];
                        if (pr.RowHi <= lo || pr.RowLo >= hi) continue;
                        var v = new DepthOnly(this, g, p, _shadow);
                        SoftwareRaster.Triangle(g.Xyz.AsSpan(9 * p, 9), s, s, lo, hi, RasterFill.TopLeft, ref v);
                        n += v.Written;
                    }
                Interlocked.Add(ref written, n);
            });
            Counters.ShadowTexelsWritten = written;
        }

        /// <summary>vs_shadow / fs_depth: the depth, nothing else; what the section plane cuts away casts nothing.</summary>
        private struct DepthOnly(Frame f, Geometry g, int prim, float[] depth) : IRasterVisitor
        {
            public long Written;

            public void Visit(int index, int i, int j, double w0, double w1, double w2, double z)
            {
                if (z < 0 || z > 1) return;
                var a = g.Weights(prim, w0, w1, w2);
                if (f._u.Clipped(g.WorldAt(prim, a))) return;
                float zb = g.Biased(prim, z);
                if (!(zb <= depth[index])) return;
                depth[index] = zb;
                Written++;
            }
        }

        /// <summary>shadow_vis: how much of the key light reaches <paramref name="w"/> on a surface of normal <paramref name="n"/>.</summary>
        private float ShadowVisibility(Vector3 w, Vector3 n)
        {
            if (_u.Ls.X < 0.5f || _shadowSize == 0) return 1;
            var c = Vector4.Transform(new Vector4(w + n * _u.Lr.W, 1), _u.Lvp);
            float ux = c.X * 0.5f + 0.5f, uy = 0.5f - c.Y * 0.5f;
            if (ux < 0 || ux > 1 || uy < 0 || uy > 1 || c.Z <= 0) return 1;
            float nf = MathF.Min(Vector3.Dot(n, _u.Lf3), -Shadows.MinSlopeCos);
            float gx = -Vector3.Dot(n, _u.Lr3) / nf, gy = -Vector3.Dot(n, _u.Lu3) / nf;
            float z = MathF.Min(c.Z, 1);
            float lit = 0;
            var taps = Shadows.Poisson;
            for (int k = 0; k < taps.Length; k++)
            {
                var o = taps[k] * _u.Ls.Y;
                float tx = ux + o.X * _u.Ls.Z, ty = uy - o.Y * _u.Ls.Z;
                lit += Compare(tx, ty, MathF.Min(z + (gx * o.X + gy * o.Y) * _u.Ls.W, 1));
            }
            return lit / taps.Length;
        }

        /// <summary>textureSampleCompareLevel through a linear, clamp-to-edge, LessEqual comparison sampler: the four texels around
        /// (u, v) each compared (lit where the reference is at or before the stored depth), then blended bilinearly.</summary>
        private float Compare(float u, float v, float reference)
        {
            int s = _shadowSize;
            float x = u * s - 0.5f, y = v * s - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            float At(int i, int j)
            {
                i = Math.Clamp(i, 0, s - 1); j = Math.Clamp(j, 0, s - 1);
                return reference <= _shadow[j * s + i] ? 1f : 0f;
            }
            float a = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * fx;
            float b = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * fx;
            return a + (b - a) * fy;
        }

        // ── 2. the occlusion ─────────────────────────────────────────────────────────────────────────────────────────────

        private float[] _aoDepth = [];

        private void OcclusionPass()
        {
            _aoDepth = new float[W * H];
            Array.Fill(_aoDepth, Occlusion.Empty);
            var depth = new float[W * H];
            Array.Fill(depth, 1f);
            var draws = new List<Geometry>();
            for (int i = 0; i < _plan.DrawCount; i++)
                if (_plan.Draws[i].Pipeline == Scene3DPipeline.Pbr && _plan.Draws[i].Count >= 3)
                {
                    // the prepass's encoder sets no depth bias: every draw at 0
                    var d = _plan.Draws[i] with { Tie = Scene3DDepthTie.None };
                    draws.Add(Prepare(d, _u.Vp, W, sizeH: H));
                }
            Bands(H, "occlusion", (lo, hi) =>
            {
                foreach (var g in draws)
                    for (int p = 0; p < g.Prims.Length; p++)
                    {
                        ref var pr = ref g.Prims[p];
                        if (pr.RowHi <= lo || pr.RowLo >= hi) continue;
                        var v = new Prepass(this, g, p, depth);
                        SoftwareRaster.Triangle(g.Xyz.AsSpan(9 * p, 9), W, H, lo, hi, RasterFill.TopLeft, ref v);
                    }
                if (!_plan.GroundDrawn) return;
                for (int j = lo; j < hi; j++)
                    for (int i = 0; i < W; i++)
                    {
                        var h = GroundHit(i, j);
                        if (h.R >= 1) continue;
                        int idx = j * W + i;
                        if (!(h.Depth <= depth[idx])) continue;
                        depth[idx] = h.Depth;
                        _aoDepth[idx] = h.T;
                    }
            });
            var raw = new byte[W * H];
            long pixels = 0;
            Bands(H, "occlusion", (lo, hi) =>
            {
                long n = 0;
                for (int j = lo; j < hi; j++)
                    for (int i = 0; i < W; i++)
                    {
                        raw[j * W + i] = R8(Horizon(i, j, ref n));
                    }
                Interlocked.Add(ref pixels, n);
            });
            Counters.OcclusionPixels = pixels;
            Bands(H, "occlusion", (lo, hi) =>
            {
                for (int j = lo; j < hi; j++)
                    for (int i = 0; i < W; i++) _aoBlur[j * W + i] = R8(Blur(i, j, raw));
            });
        }

        /// <summary>fs_prepass: each opaque material's distance along the view's ray.</summary>
        private struct Prepass(Frame f, Geometry g, int prim, float[] depth) : IRasterVisitor
        {
            public void Visit(int index, int i, int j, double w0, double w1, double w2, double z)
            {
                if (z < 0 || z > 1) return;
                var a = g.Weights(prim, w0, w1, w2);
                var w = g.WorldAt(prim, a);
                if (f._u.Clipped(w)) return;
                float zb = g.Biased(prim, z);
                if (!(zb <= depth[index])) return;
                depth[index] = zb;
                f._aoDepth[index] = Vector3.Dot(w - f._u.Aro, f._u.Ard);
            }
        }

        /// <summary>ao_point: pixel (x, y)'s point, w 1, or w 0 where the prepass drew nothing.</summary>
        private Vector4 AoPoint(int x, int y)
        {
            float t = _aoDepth[y * W + x];
            if (t > Occlusion.Empty * 0.5f) return Vector4.Zero;
            float nx = (x + 0.5f) / W * 2 - 1, ny = 1 - (y + 0.5f) / H * 2;
            var o = _u.Aro + _u.Arox * nx + _u.Aroy * ny;
            var d = _u.Ard + _u.Ardx * nx + _u.Ardy * ny;
            return new Vector4(o + d * t, 1);
        }

        /// <summary>ao_normal: the surface's normal at p from its neighbours, the nearer on each axis, facing the viewer.</summary>
        private Vector3 AoNormal(int x, int y, Vector3 p)
        {
            Vector4 l = default, r = default, a = default, b = default;
            if (x > 0) l = AoPoint(x - 1, y);
            if (x < W - 1) r = AoPoint(x + 1, y);
            if (y > 0) a = AoPoint(x, y - 1);
            if (y < H - 1) b = AoPoint(x, y + 1);
            static Vector3 Xyz(Vector4 q) => new(q.X, q.Y, q.Z);
            var dx = Vector3.Zero;
            if (r.W > 0 && (l.W == 0 || (Xyz(r) - p).Length() < (p - Xyz(l)).Length())) dx = Xyz(r) - p;
            else if (l.W > 0) dx = p - Xyz(l);
            var dy = Vector3.Zero;
            if (b.W > 0 && (a.W == 0 || (Xyz(b) - p).Length() < (p - Xyz(a)).Length())) dy = Xyz(b) - p;
            else if (a.W > 0) dy = p - Xyz(a);
            var n = Vector3.Cross(dx, dy);
            var ray = _u.Ard;
            if (Vector3.Dot(n, n) < 1e-36f) return -ray;
            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, ray) > 0) n = -n;
            return n;
        }

        /// <summary>fs_ao: 1 open, 0 fully occluded — the horizon of each of Occlusion.Directions directions walked out to the radius.</summary>
        private float Horizon(int x, int y, ref long count)
        {
            var p4 = AoPoint(x, y);
            if (p4.W == 0) return 1;
            count++;
            var p = new Vector3(p4.X, p4.Y, p4.Z);
            var n = AoNormal(x, y, p);
            float t = _aoDepth[y * W + x];
            float wpp = (_u.Arox + _u.Ardx * t).Length() * 2 / W;
            float rpx = Math.Clamp(_u.Ao.Y / MathF.Max(wpp, 1e-30f), 1, Occlusion.MaxPixels * MathF.Max(_u.Ao.W, 1));
            uint cell = (uint)(x & 3) * 4u + (uint)(y & 3);
            float turn = (cell + 0.5f) / 16;
            float step0 = (((cell * 5u) & 15u) + 0.5f) / 16;
            float r2 = _u.Ao.Y * _u.Ao.Y;
            float occ = 0;
            for (int d = 0; d < Occlusion.Directions; d++)
            {
                float ang = (d + turn) * 2 * MathF.PI / Occlusion.Directions;
                float cx = MathF.Cos(ang), cy = MathF.Sin(ang);
                float h = 0;
                for (int k = 0; k < Occlusion.Steps; k++)
                {
                    float s = rpx * ((k + step0) / Occlusion.Steps);
                    int qx = Math.Clamp(x + (int)MathF.Round(cx * s), 0, W - 1), qy = Math.Clamp(y + (int)MathF.Round(cy * s), 0, H - 1);
                    var q = AoPoint(qx, qy);
                    if (q.W == 0) continue;
                    var v = new Vector3(q.X, q.Y, q.Z) - p;
                    float len2 = Vector3.Dot(v, v);
                    if (len2 <= 0) continue;
                    float fall = Math.Clamp(1 - len2 / r2, 0, 1);
                    h = MathF.Max(h, (Vector3.Dot(n, v) / MathF.Sqrt(len2) - Occlusion.Bias) * fall);
                }
                occ += h;
            }
            return Math.Clamp(1 - occ / (Occlusion.Directions * (1 - Occlusion.Bias)), 0, 1);
        }

        /// <summary>fs_ao_blur: the 4 × 4 window, a neighbour on another surface left out.</summary>
        private float Blur(int x, int y, byte[] raw)
        {
            var c4 = AoPoint(x, y);
            if (c4.W == 0) return 1;
            var c = new Vector3(c4.X, c4.Y, c4.Z);
            float sum = 0, n = 0;
            for (int j = -2; j < 2; j++)
                for (int k = -2; k < 2; k++)
                {
                    int qx = Math.Clamp(x + k, 0, W - 1), qy = Math.Clamp(y + j, 0, H - 1);
                    var p = AoPoint(qx, qy);
                    if (p.W == 0 || (new Vector3(p.X, p.Y, p.Z) - c).Length() > _u.Ao.Z) continue;
                    sum += raw[qy * W + qx] / 255f;
                    n += 1;
                }
            return n > 0 ? sum / n : 1;
        }

        /// <summary>occlusion_at: the blurred occlusion at a pixel (1 with none).</summary>
        private float OcclusionAt(int index) => _u.Ao.X < 0.5f ? 1 : _aoBlur[index] / 255f;

        // ── the ground ───────────────────────────────────────────────────────────────────────────────────────────────────

        private readonly record struct Hit(Vector3 W, float T, float R, float Depth);

        /// <summary>ground_hit at pixel (x, y)'s centre: where the view's ray meets the ground's plane, its parameter (the depth along the
        /// view), its distance from the centre in radii (1 or more: missed), and its window depth.</summary>
        private Hit GroundHit(int x, int y)
        {
            float nx = (x + 0.5f) / W * 2 - 1, ny = 1 - (y + 0.5f) / H * 2;
            var o = _u.Aro + _u.Arox * nx + _u.Aroy * ny;
            var d = _u.Ard + _u.Ardx * nx + _u.Ardy * ny;
            if (MathF.Abs(d.Z) < 1e-30f || _u.Gnd.W <= 0) return new Hit(default, 0, 2, 1);
            float t = (_u.Gnd.Z - o.Z) / d.Z;
            var w = o + d * t;
            if (Vector3.Dot(_u.Ardx, _u.Ardx) > 0 && t <= 0) return new Hit(w, t, 2, 1);
            float r = new Vector2(w.X - _u.Gnd.X, w.Y - _u.Gnd.Y).Length() / _u.Gnd.W;
            var c = Vector4.Transform(new Vector4(w, 1), _u.Vp);
            return new Hit(w, t, r, Math.Clamp(c.Z / c.W, 0, 1));
        }

        // ── 3. the colour pass ───────────────────────────────────────────────────────────────────────────────────────────

        private enum Kind { Skip, Opaque, Blend, Screen, Lines }

        private void ColourPass()
        {
            var (cr, cg, cb) = _plan.Clear;
            byte r8 = Unorm(cr), g8 = Unorm(cg), b8 = Unorm(cb), a8 = (byte)(_plan.Transparent ? 0 : 255);
            for (int k = 0; k < W * H; k++)
            {
                Colour[4 * k] = r8; Colour[4 * k + 1] = g8; Colour[4 * k + 2] = b8; Colour[4 * k + 3] = a8;
            }
            Array.Fill(Depth, 1f);

            var draws = new List<(Kind Kind, Geometry? G, int Index)>();
            for (int i = 0; i < _plan.DrawCount; i++)
            {
                var d = _plan.Draws[i];
                var kind = d.Pipeline switch
                {
                    Scene3DPipeline.Pbr or Scene3DPipeline.Opaque => d.Buffer == Scene3DBuffer.Scene ? Kind.Opaque : Kind.Skip,
                    Scene3DPipeline.Field => _field is not null ? Kind.Opaque : Kind.Skip,
                    Scene3DPipeline.PbrTranslucent or Scene3DPipeline.Translucent => d.Buffer == Scene3DBuffer.Scene ? Kind.Blend : Kind.Skip,
                    Scene3DPipeline.FieldBlend or Scene3DPipeline.FieldLit => _field is not null ? Kind.Blend : Kind.Skip,
                    Scene3DPipeline.Backdrop or Scene3DPipeline.Ground => Kind.Screen,
                    Scene3DPipeline.Lines => d.Buffer == Scene3DBuffer.SceneLines ? Kind.Lines : Kind.Skip,
                    _ => Kind.Skip,
                };
                if (kind == Kind.Skip) { Counters.SkippedDraws++; continue; }
                Counters.Draws++;
                if (kind is Kind.Opaque or Kind.Blend)
                {
                    if (d.Count < 3) continue;
                    if (d.Buffer == Scene3DBuffer.Field && d.First + d.Count > _field!.Vertices.Length) continue;
                    draws.Add((kind, Prepare(d, _u.Vp, W, sizeH: H), i));
                }
                else if (kind == Kind.Lines) draws.Add((kind, PrepareLines(d), i));
                else draws.Add((kind, null, i));
            }

            long shaded = 0;
            Bands(H, "draw", (lo, hi) =>
            {
                long n = 0;
                var owner = new (int Draw, int Prim)[(hi - lo) * W];
                int at = 0;
                while (at < draws.Count)
                {
                    var (kind, g, index) = draws[at];
                    if (kind == Kind.Opaque)
                    {
                        // a run of opaque draws: resolved by depth, then shaded once a pixel
                        int end = at;
                        Array.Fill(owner, (-1, -1));
                        while (end < draws.Count && draws[end].Kind == Kind.Opaque)
                        {
                            var ge = draws[end].G!;
                            for (int p = 0; p < ge.Prims.Length; p++)
                            {
                                ref var pr = ref ge.Prims[p];
                                if (pr.RowHi <= lo || pr.RowLo >= hi) continue;
                                var v = new Resolve(this, ge, end, p, owner, lo);
                                SoftwareRaster.Triangle(ge.Xyz.AsSpan(9 * p, 9), W, H, lo, hi, RasterFill.TopLeft, ref v);
                            }
                            end++;
                        }
                        for (int j = lo; j < hi; j++)
                            for (int i = 0; i < W; i++)
                            {
                                var (dr, pr) = owner[(j - lo) * W + i];
                                if (dr < 0) continue;
                                var ge = draws[dr].G!;
                                var (w0, w1, w2) = SoftwareRaster.Barycentric(ge.Xyz.AsSpan(9 * pr, 9), i + 0.5, j + 0.5);
                                Write(j * W + i, Fragment(ge, pr, w0, w1, w2, j * W + i), blend: Blend.None);
                                Surface[j * W + i] = ge.Re is not null ? FieldSurface : (int)ge.Id![ge.Prims[pr].Tri];
                                n++;
                            }
                        at = end;
                        continue;
                    }
                    if (kind == Kind.Blend)
                    {
                        for (int p = 0; p < g!.Prims.Length; p++)
                        {
                            ref var pr = ref g.Prims[p];
                            if (pr.RowHi <= lo || pr.RowLo >= hi) continue;
                            var v = new Immediate(this, g, p);
                            SoftwareRaster.Triangle(g.Xyz.AsSpan(9 * p, 9), W, H, lo, hi, RasterFill.TopLeft, ref v);
                            n += v.Shaded;
                        }
                    }
                    else if (kind == Kind.Lines) n += DrawLines(g!, lo, hi);
                    else n += FullScreen(_plan.Draws[index].Pipeline, lo, hi);
                    at++;
                }
                Interlocked.Add(ref shaded, n);
            });
            Counters.SamplesShaded = shaded;
        }

        /// <summary>An opaque run's depth test: the last fragment to pass (LessEqual, written) owns the pixel; a fragment its shader would
        /// discard is decided here, before the write, as the GPU decides it.</summary>
        private struct Resolve(Frame f, Geometry g, int draw, int prim, (int, int)[] owner, int rowLo) : IRasterVisitor
        {
            public void Visit(int index, int i, int j, double w0, double w1, double w2, double z)
            {
                if (z < 0 || z > 1) return;
                if (f.Discards(g, prim, w0, w1, w2)) return;
                float zb = g.Biased(prim, z);
                if (!(zb <= f.Depth[index])) return;
                f.Depth[index] = zb;
                owner[(j - rowLo) * f.W + i] = (draw, prim);
            }
        }

        /// <summary>A blended draw, fragment by fragment in the GPU's order: depth tested, shaded, blended, and (a field) its depth written.</summary>
        private struct Immediate(Frame f, Geometry g, int prim) : IRasterVisitor
        {
            public long Shaded;

            public void Visit(int index, int i, int j, double w0, double w1, double w2, double z)
            {
                if (z < 0 || z > 1) return;
                if (f.Discards(g, prim, w0, w1, w2)) return;
                float zb = g.Biased(prim, z);
                if (!(zb <= f.Depth[index])) return;
                var c = f.Fragment(g, prim, w0, w1, w2, index);
                Shaded++;
                bool field = g.Re is not null;
                f.Write(index, c, g.Pipeline == Scene3DPipeline.PbrTranslucent ? Blend.Premultiplied : Blend.Alpha);
                if (!field) return;                                         // FieldBlend and FieldLit write depth; glass does not
                f.Depth[index] = zb;
                f.Surface[index] = FieldSurface;
            }
        }

        /// <summary>Whether a fragment's shader discards it: the section plane (a field's slice on its own plane excepted), and fs_color's
        /// wireframe face (alpha 0: drawn only hovered or selected, which a picture never is).</summary>
        private bool Discards(Geometry g, int prim, double w0, double w1, double w2)
        {
            var a = g.Weights(prim, w0, w1, w2);
            if (g.Re is not null) return _u.FieldBlock(g.Layer).Unclipped < 0.5f && _u.Clipped(g.WorldAt(prim, a));
            if (_u.Clipped(g.WorldAt(prim, a))) return true;
            return g.Pipeline is Scene3DPipeline.Opaque or Scene3DPipeline.Translucent && g.ColourAt(prim, a).W == 0;
        }

        /// <summary>The fragment shader of <paramref name="g"/>'s pipeline at a sample: what it returns (premultiplied for the Pbr pipelines,
        /// straight otherwise, as each WGSL entry point returns it).</summary>
        private Vector4 Fragment(Geometry g, int prim, double w0, double w1, double w2, int index)
        {
            var a = g.Weights(prim, w0, w1, w2);
            var world = g.WorldAt(prim, a);
            var v = Vector3.Normalize(_u.Eye - world);
            switch (g.Pipeline)
            {
                case Scene3DPipeline.Pbr:
                case Scene3DPipeline.PbrTranslucent:
                {
                    int t = g.Prims[prim].Tri;
                    uint slot = g.Slot![t];
                    var m = _table[slot & Scene3DShadeVertex.SlotMask];
                    var n = Vector3.Normalize(g.NormalAt(prim, a));
                    if (!g.Prims[prim].Front) n = (_u.Flags & Scene3DFramePlan.FlagCapBackFaces) != 0 ? v : -n;
                    float occ = g.Pipeline == Scene3DPipeline.Pbr ? OcclusionAt(index) : 1;
                    bool keyOn = Vector3.Dot(n, _u.Key) > 0 && Vector3.Dot(_u.KeyC, _u.KeyC) > 0;
                    float vis = keyOn ? ShadowVisibility(world, n) : 1;
                    float cov = (slot & Scene3DShadeVertex.StatedAlpha) != 0 ? g.ColourAt(prim, a).W : 1;
                    return Pbr.Shade(m, n, v, _env, _light, cov, occ, vis);
                }
                case Scene3DPipeline.Opaque:
                case Scene3DPipeline.Translucent:
                {
                    // fs_color: the face's own normal (the derivatives of the world position), shaded 0.3 + 0.7|n·v|
                    var col = g.ColourAt(prim, a);
                    var rgb = new Vector3(col.X, col.Y, col.Z);
                    var fn = g.FaceNormal(g.Prims[prim].Tri);
                    float d = MathF.Abs(Vector3.Dot(fn, v));
                    var c = rgb * (0.3f + 0.7f * d);
                    if (!g.Prims[prim].Front && (_u.Flags & Scene3DFramePlan.FlagCapBackFaces) != 0) c = rgb * 0.8f;
                    return new Vector4(c, col.W);
                }
                case Scene3DPipeline.FieldLit:
                {
                    var c = FieldColour(g, prim, a);
                    var n = Vector3.Normalize(g.NormalAt(prim, a));
                    if (Vector3.Dot(n, v) < 0) n = -n;
                    return new Vector4(Pbr.LitField(c, Pbr.FieldSheen(n, v, _env, _light)), 1 - _u.Lk1.W);
                }
                default:
                    return new Vector4(FieldColour(g, prim, a), 1 - _u.Lk1.W);  // fs_field: Field, FieldBlend
            }
        }

        /// <summary>field_colour: the value under a fragment, through its block's range, as its colour map's colour (a display value).</summary>
        private Vector3 FieldColour(Geometry g, int prim, Vector3 a)
        {
            var b = _u.FieldBlock(g.Layer);
            var re = g.ReAt(prim, a);
            var im = g.ImAt(prim, a);
            int mode = (int)(b.Mode + 0.5f);
            float val = mode switch
            {
                0 => MathF.Sqrt(Vector3.Dot(re, re) + Vector3.Dot(im, im)),
                1 => (re * b.Cos - im * b.Sin).Length(),
                2 => re.X,
                3 => re.X * b.Cos - im.X * b.Sin,
                _ => MathF.Sqrt(re.X * re.X + im.X * im.X),
            };
            if (b.Db > 0.5f) val = 20 * 0.30102999566f * MathF.Log2(MathF.Max(MathF.Abs(val), 1e-30f));
            float t = Math.Clamp((val - b.Lo) / MathF.Max(b.Hi - b.Lo, 1e-30f), 0, 1);
            return b.Map(t);
        }

        private int FullScreen(Scene3DPipeline pipeline, int lo, int hi)
        {
            int n = 0;
            for (int j = lo; j < hi; j++)
                for (int i = 0; i < W; i++)
                {
                    int idx = j * W + i;
                    if (pipeline == Scene3DPipeline.Backdrop)
                    {
                        Write(idx, Backdrop(i, j), Blend.None);
                        n++;
                        continue;
                    }
                    // fs_ground: black, with the light it loses as alpha; its depth tested, not written
                    var h = GroundHit(i, j);
                    if (h.R >= 1 || !(h.Depth <= Depth[idx])) continue;
                    float fade = 1 - SmoothStep(Look.Ground.FadeStart, 1, h.R);
                    float share = _u.Lu.W;
                    float lost = 1 - (share * ShadowVisibility(h.W, Vector3.UnitZ) + (1 - share) * OcclusionAt(idx));
                    Write(idx, new Vector4(0, 0, 0, Math.Clamp(lost * fade, 0, 1)), Blend.Alpha);
                    n++;
                }
            return n;
        }

        /// <summary>fs_backdrop: a vertical gradient (top of the view to bottom), or the environment along the pixel's ray, exposed and
        /// tone-mapped as a surface is.</summary>
        private Vector4 Backdrop(int x, int y)
        {
            float nx = (x + 0.5f) / W * 2 - 1, ny = 1 - (y + 0.5f) / H * 2;
            if (_u.Lk1.X < 1.5f)
            {
                float t = Math.Clamp(ny * 0.5f + 0.5f, 0, 1);
                return new Vector4(Vector3.Lerp(_u.Bg1, _u.Bg0, t), 1);
            }
            var d = _u.Bd + _u.Bdx * nx + _u.Bdy * ny;
            var c = _env.SampleLevel(Pbr.OctEncode(_light.ToEnvironment(d)), 0) * _u.Lk.Y;
            return new Vector4(ToneCurve.Display(c, _u.Lk.X), 1);
        }

        // ── lines (a shown chrome row's edges) ───────────────────────────────────────────────────────────────────────────

        private Geometry PrepareLines(in Scene3DDraw d)
        {
            var slot = _plan.Transforms.AsSpan(Scene3DFramePlan.TransformFloats * Math.Clamp(d.Transform, 0, Math.Max(0, _plan.TransformCount - 1)),
                                               Scene3DFramePlan.TransformFloats);
            var m = Row(slot);
            var lv = _scene.LineVertices;
            int n = Math.Max(0, Math.Min(d.Count, lv.Length - d.First)) / 2 * 2;
            var g = new Geometry { Pipeline = d.Pipeline, World = new Vector3[n], Rgba = new uint[n], Xyz = new double[3 * n] };
            for (int k = 0; k < n; k++)
            {
                ref var v = ref lv[d.First + k];
                var p = Vector3.Transform(new Vector3(v.X, v.Y, v.Z), m);
                var c = Vector4.Transform(new Vector4(p, 1), _u.Vp);
                g.World[k] = p;
                g.Rgba[k] = v.Rgba;
                g.Xyz[3 * k] = (c.X / c.W * 0.5 + 0.5) * W;
                g.Xyz[3 * k + 1] = (0.5 - c.Y / c.W * 0.5) * H;
                g.Xyz[3 * k + 2] = c.W > 0 ? c.Z / c.W : -1;
            }
            return g;
        }

        /// <summary>fs_line over a one-pixel line: one pixel per step along its longer axis, depth tested and written, its colour unshaded.</summary>
        private int DrawLines(Geometry g, int lo, int hi)
        {
            int count = 0;
            for (int k = 0; k + 1 < g.World.Length; k += 2)
            {
                double x0 = g.Xyz[3 * k], y0 = g.Xyz[3 * k + 1], z0 = g.Xyz[3 * k + 2];
                double x1 = g.Xyz[3 * k + 3], y1 = g.Xyz[3 * k + 4], z1 = g.Xyz[3 * k + 5];
                if (z0 < 0 || z1 < 0 || z0 > 1 || z1 > 1) continue;
                int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
                var c = Rgba(g.Rgba![k]);
                for (int s = 0; s <= steps; s++)
                {
                    double f = steps == 0 ? 0 : (double)s / steps;
                    int i = (int)Math.Floor(x0 + f * (x1 - x0)), j = (int)Math.Floor(y0 + f * (y1 - y0));
                    if (i < 0 || i >= W || j < lo || j >= hi) continue;
                    var w = Vector3.Lerp(g.World[k], g.World[k + 1], (float)f);
                    if (_u.Clipped(w)) continue;
                    int idx = j * W + i;
                    float z = (float)(z0 + f * (z1 - z0));
                    if (!(z <= Depth[idx])) continue;
                    Depth[idx] = z;
                    Write(idx, new Vector4(c.X, c.Y, c.Z, 1), Blend.None);
                    count++;
                }
            }
            return count;
        }

        // ── the target ───────────────────────────────────────────────────────────────────────────────────────────────────

        private enum Blend
        {
            None,
            /// <summary>RGB src-alpha, 1 − src-alpha; alpha one, 1 − src-alpha.</summary>
            Alpha,
            /// <summary>RGB one, 1 − src-alpha; alpha one, 1 − src-alpha (PbrTranslucent).</summary>
            Premultiplied,
        }

        private void Write(int index, Vector4 c, Blend blend)
        {
            int o = 4 * index;
            if (blend == Blend.None)
            {
                Colour[o] = Unorm(c.X); Colour[o + 1] = Unorm(c.Y); Colour[o + 2] = Unorm(c.Z); Colour[o + 3] = Unorm(c.W);
                return;
            }
            float a = c.W, k = 1 - a;
            float s = blend == Blend.Alpha ? a : 1;
            Colour[o] = Unorm(c.X * s + Colour[o] / 255f * k);
            Colour[o + 1] = Unorm(c.Y * s + Colour[o + 1] / 255f * k);
            Colour[o + 2] = Unorm(c.Z * s + Colour[o + 2] / 255f * k);
            Colour[o + 3] = Unorm(a + Colour[o + 3] / 255f * k);
        }
    }

    // ── a draw's triangles ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One rasterised triangle: the original triangle it is (or, clipped at the near plane, part of), its corners' clip w, its
    /// depth bias, the rows it spans and whether it faces the viewer.</summary>
    private struct Prim
    {
        public int Tri;
        public double W0, W1, W2;
        public float Bias;
        public int RowLo, RowHi;
        public bool Front, Perspective;
        /// <summary>Clipped: each corner as weights over the original triangle's corners.</summary>
        public bool Clipped;
        public Vector3 B0, B1, B2;
    }

    private sealed class Geometry
    {
        public Scene3DPipeline Pipeline;
        public int Layer;
        public Prim[] Prims = [];
        /// <summary>Each prim's corners in sample space: x, y, window depth.</summary>
        public double[] Xyz = [];
        /// <summary>Per ORIGINAL triangle corner (3 a triangle): world position, and what the draw's shader reads.</summary>
        public Vector3[] World = [];
        public Vector3[]? Normal, Re, Im;
        public uint[]? Rgba;
        /// <summary>Per original triangle: its object's id and its appearance slot (flat).</summary>
        public uint[]? Id, Slot;

        /// <summary>The prims of <paramref name="tris"/> triangles from their corners' clip positions: near-clipped, projected to a
        /// <paramref name="w"/> × <paramref name="h"/> target, biased as the GPU biases a draw of this tie.</summary>
        public void Build(Vector4[] clip, int tris, int w, int h, (float Constant, float Slope, float Clamp) bias)
        {
            var prims = new List<Prim>(tris);
            var xyz = new List<double>(9 * tris);
            Span<Vector4> pc = stackalloc Vector4[4];
            Span<Vector3> pb = stackalloc Vector3[4];
            for (int t = 0; t < tris; t++)
            {
                Vector4 c0 = clip[3 * t], c1 = clip[3 * t + 1], c2 = clip[3 * t + 2];
                if (c0.Z >= 0 && c1.Z >= 0 && c2.Z >= 0)
                {
                    Add(t, c0, c1, c2, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, clipped: false);
                    continue;
                }
                // clip against the near plane, z ≥ 0 (Metal's and D3D's clip space): a triangle becomes nothing, one or two
                int n = 0;
                Vector4[] cs = [c0, c1, c2];
                Vector3[] bs = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
                for (int k = 0; k < 3; k++)
                {
                    var a = cs[k]; var b = cs[(k + 1) % 3];
                    var ba = bs[k]; var bb = bs[(k + 1) % 3];
                    if (a.Z >= 0) { pc[n] = a; pb[n] = ba; n++; }
                    if ((a.Z >= 0) != (b.Z >= 0))
                    {
                        float s = a.Z / (a.Z - b.Z);
                        pc[n] = Vector4.Lerp(a, b, s); pb[n] = Vector3.Lerp(ba, bb, s); n++;
                    }
                }
                if (n >= 3) Add(t, pc[0], pc[1], pc[2], pb[0], pb[1], pb[2], clipped: true);
                if (n == 4) Add(t, pc[0], pc[2], pc[3], pb[0], pb[2], pb[3], clipped: true);
            }
            Prims = [.. prims];
            Xyz = [.. xyz];

            void Add(int t, Vector4 a, Vector4 b, Vector4 c, Vector3 ba, Vector3 bb, Vector3 bc, bool clipped)
            {
                if (!(a.W > 0 && b.W > 0 && c.W > 0)) return;
                double ax = (a.X / a.W * 0.5 + 0.5) * w, ay = (0.5 - a.Y / a.W * 0.5) * h, az = a.Z / a.W;
                double bx = (b.X / b.W * 0.5 + 0.5) * w, by = (0.5 - b.Y / b.W * 0.5) * h, bz = b.Z / b.W;
                double cx = (c.X / c.W * 0.5 + 0.5) * w, cy = (0.5 - c.Y / c.W * 0.5) * h, cz = c.Z / c.W;
                double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (!(Math.Abs(area) > 1e-12) || !double.IsFinite(area)) return;
                // the polygon offset: constant × the format's resolvable step at the triangle's deepest point, plus the slope factor
                // × the depth's steepest change per pixel, clamped
                double dzdx = ((bz - az) * (cy - ay) - (cz - az) * (by - ay)) / area;
                double dzdy = ((cz - az) * (bx - ax) - (bz - az) * (cx - ax)) / area;
                double maxZ = Math.Max(Math.Abs(az), Math.Max(Math.Abs(bz), Math.Abs(cz)));
                double r = maxZ > 0 ? Math.ScaleB(1.0, Math.ILogB(maxZ) - 23) : Math.ScaleB(1.0, -149);
                double o = bias.Constant * r + bias.Slope * Math.Max(Math.Abs(dzdx), Math.Abs(dzdy));
                if (bias.Clamp > 0) o = Math.Min(o, bias.Clamp);
                else if (bias.Clamp < 0) o = Math.Max(o, bias.Clamp);
                prims.Add(new Prim
                {
                    Tri = t, W0 = a.W, W1 = b.W, W2 = c.W, Bias = (float)o, Clipped = clipped, B0 = ba, B1 = bb, B2 = bc,
                    RowLo = (int)Math.Max(0, Math.Floor(Math.Min(ay, Math.Min(by, cy)) - 0.5)),
                    RowHi = (int)Math.Min(h, Math.Ceiling(Math.Max(ay, Math.Max(by, cy)) - 0.5) + 1),
                    // counter-clockwise in NDC (y up) faces the viewer; sample space runs y down, so its area is negative
                    Front = area < 0,
                    Perspective = a.W != 1 || b.W != 1 || c.W != 1,
                });
                xyz.AddRange([ax, ay, az, bx, by, bz, cx, cy, cz]);
            }
        }

        /// <summary>A sample's depth with the prim's bias, clamped to the depth range, as the depth test sees it.</summary>
        public float Biased(int prim, double z) => (float)Math.Clamp(z + Prims[prim].Bias, 0, 1);

        /// <summary>A sample's screen-space weights as weights over the original triangle's corners: perspective-corrected (the GPU's
        /// 1/w), then through the clipped corners.</summary>
        public Vector3 Weights(int prim, double w0, double w1, double w2)
        {
            ref var p = ref Prims[prim];
            if (p.Perspective) (w0, w1, w2) = SoftwareRaster.Perspective(w0, w1, w2, p.W0, p.W1, p.W2);
            var a = new Vector3((float)w0, (float)w1, (float)w2);
            return p.Clipped ? p.B0 * a.X + p.B1 * a.Y + p.B2 * a.Z : a;
        }

        /// <summary>An attribute of the prim's original triangle at weights <paramref name="a"/>.</summary>
        public Vector3 WorldAt(int prim, Vector3 a) => At(World, Prims[prim].Tri, a);
        public Vector3 NormalAt(int prim, Vector3 a) => At(Normal!, Prims[prim].Tri, a);
        public Vector3 ReAt(int prim, Vector3 a) => At(Re!, Prims[prim].Tri, a);
        public Vector3 ImAt(int prim, Vector3 a) => At(Im!, Prims[prim].Tri, a);

        private static Vector3 At(Vector3[] v, int tri, Vector3 a)
        {
            int k = 3 * tri;
            return v[k] * a.X + v[k + 1] * a.Y + v[k + 2] * a.Z;
        }

        /// <summary>The vertex colour, interpolated (RGBA8 unorm → 0–1).</summary>
        public Vector4 ColourAt(int prim, Vector3 a)
        {
            int k = 3 * Prims[prim].Tri;
            return Rgba(Rgba![k]) * a.X + Rgba(Rgba[k + 1]) * a.Y + Rgba(Rgba[k + 2]) * a.Z;
        }

        /// <summary>The original triangle's face normal (unit; its sign is not read).</summary>
        public Vector3 FaceNormal(int tri)
        {
            var n = Vector3.Cross(World[3 * tri + 1] - World[3 * tri], World[3 * tri + 2] - World[3 * tri]);
            float l = n.Length();
            return l > 0 ? n / l : Vector3.UnitZ;
        }
    }

    // ── the uniform block, read ─────────────────────────────────────────────────────────────────────────────────────────

    private readonly struct FieldBlockU
    {
        public readonly float Cos, Sin, Lo, Hi, Mode, Db, Stops, Unclipped;
        private readonly float[] _u;
        private readonly int _at;

        public FieldBlockU(float[] u, int at)
        {
            _u = u; _at = at;
            Cos = u[at]; Sin = u[at + 1]; Lo = u[at + 2]; Hi = u[at + 3];
            Mode = u[at + 4]; Db = u[at + 5]; Stops = u[at + 6]; Unclipped = u[at + 7];
        }

        /// <summary>colour_map: the stops' colour at t, linear between the two around it.</summary>
        public Vector3 Map(float t)
        {
            int n = (int)(Stops + 0.5f);
            float[] u = _u;
            int at = _at;
            Vector3 Stop(int k) => new(u[at + 9 + 4 * k], u[at + 10 + 4 * k], u[at + 11 + 4 * k]);
            float T(int k) => u[at + 8 + 4 * k];
            var rgb = Stop(0);
            for (int k = 1; k < n; k++)
                if (t <= T(k) || k == n - 1)
                {
                    float w = Math.Clamp((t - T(k - 1)) / MathF.Max(T(k) - T(k - 1), 1e-6f), 0, 1);
                    rgb = Vector3.Lerp(Stop(k - 1), Stop(k), w);
                    break;
                }
            return rgb;
        }
    }

    /// <summary>The uniforms a frame's fragments read, from the plan's block (scene.wgsl's U, by the offsets Scene3DFramePlan writes).</summary>
    private readonly struct U
    {
        private readonly float[] _u;
        public readonly Matrix4x4 Vp, Lvp;
        public readonly Vector3 Eye;
        public readonly Vector4 ClipPlane;
        public readonly uint Flags;
        public readonly Vector4 Lk, Lk1, Ls, Lr, Lu, Ao, Gnd;
        public readonly Vector3 Key, KeyC, Bg0, Bg1, Bd, Bdx, Bdy, Lr3, Lu3, Lf3, Aro, Arox, Aroy, Ard, Ardx, Ardy;

        public U(float[] u)
        {
            _u = u;
            Vp = Row(u.AsSpan(0, 16));
            Eye = new(u[16], u[17], u[18]);
            ClipPlane = new(u[20], u[21], u[22], u[23]);
            Flags = BitConverter.SingleToUInt32Bits(u[26]);
            int l = Scene3DFramePlan.LookAt;
            Lk = V4(u, l); Lk1 = V4(u, l + 4);
            Key = V3(u, l + 8); KeyC = V3(u, l + 12); Bg0 = V3(u, l + 16); Bg1 = V3(u, l + 20);
            Bd = V3(u, l + 24); Bdx = V3(u, l + 28); Bdy = V3(u, l + 32);
            int g = l + Scene3DFramePlan.LightingAt;
            Lvp = Row(u.AsSpan(g, 16));
            Ls = V4(u, g + 16); Lr = V4(u, g + 20); Lu = V4(u, g + 24);
            Lr3 = V3(u, g + 20); Lu3 = V3(u, g + 24); Lf3 = V3(u, g + 28);
            Ao = V4(u, g + 32);
            Aro = V3(u, g + 36); Arox = V3(u, g + 40); Aroy = V3(u, g + 44); Ard = V3(u, g + 48); Ardx = V3(u, g + 52); Ardy = V3(u, g + 56);
            Gnd = V4(u, g + 60);
        }

        /// <summary>clipped(): the section plane is on and the point is on its cut-away side.</summary>
        public bool Clipped(Vector3 w)
            => (Flags & Scene3DFramePlan.FlagClip) != 0 && ClipPlane.X * w.X + ClipPlane.Y * w.Y + ClipPlane.Z * w.Z + ClipPlane.W > 0;

        public FieldBlockU FieldBlock(int layer) => new(_u, Scene3DFramePlan.FieldAt + FieldUniforms.Floats * layer);

        private static Vector3 V3(float[] u, int at) => new(u[at], u[at + 1], u[at + 2]);
        private static Vector4 V4(float[] u, int at) => new(u[at], u[at + 1], u[at + 2], u[at + 3]);
    }

    /// <summary>A WGSL mat4x4f as the plan writes it (Scene3DFramePlan.WriteMatrix: a row-vector matrix, row-major), so that
    /// <c>Vector4.Transform(p, m)</c> is the shader's <c>M · p</c>.</summary>
    private static Matrix4x4 Row(ReadOnlySpan<float> m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                                                                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    private static Vector4 Rgba(uint c) => new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, (c >> 24) / 255f);

    /// <summary>A 0–1 value as UNORM8 stores it: clamped, × 255, rounded to nearest.</summary>
    private static byte Unorm(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    /// <summary>An R8 target's value.</summary>
    private static byte R8(float v) => Unorm(v);

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
