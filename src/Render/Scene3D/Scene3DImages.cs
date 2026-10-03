// brief-em3d-101 R-em3d101-4 — what the 3D view draws an image WITH: a decoded texture and its CPU-built mip levels, keyed by the
// file's absolute path, and the rule for when a backend uploads one.
//
// DECODED ONCE, HERE, FOR ALL THREE BACKENDS. The pixels come from BitmapCache (the one decode cache every editor shares — "share
// the cache, not the model"), are capped at C3dImages.MaxTexturePixels on the long edge, and are reduced to every mip level by
// SkiaSharp in this file, so Metal, D3D11 and Vulkan upload the same levels and none of them generates its own: the three
// pictures stay alike. Levels are RGBA8 UNORM rows, top first, alpha NOT premultiplied — exactly a vertex colour's encoding, so a
// pixel reaches the framebuffer with the value a vertex of that colour would (the sRGB trap brief 69 found once: never an _SRGB
// format on the GPU side).
//
// UPLOADED BY IDENTITY, NEVER PER FRAME AND NEVER PER RE-ELABORATION (R-em3d101-4e). A Scene3DTexture is handed out once per
// path and kept, so every scene built while the file is unchanged holds the SAME object; Scene3DTextureResidency uploads a texture
// it does not hold and releases one no scene holds any more. Moving, hiding, making transparent or Model-toggling an image sheet
// rebuilds the scene and finds the same object: nothing uploads. Refresh (Refresh Image, Resolve Path…) drops the path, so the
// next scene holds a NEW object and that one uploads. Uploads is a counter, not a timing (gate 6).

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CircuitRF.Design.ThreeD;
using SkiaSharp;

namespace CircuitRF.Render.Scene3D;

/// <summary>One mip level: <see cref="Width"/> × <see cref="Height"/> RGBA8 texels, rows top first, alpha not premultiplied.</summary>
public sealed record Scene3DTextureLevel(int Width, int Height, byte[] Rgba);

/// <summary>
/// brief-em3d-101 — a decoded image as the GPU takes it: its size after the cap, whether every pixel is opaque, and its mip levels
/// (made on demand, and kept only while something holds them — an uploaded texture needs them no more). One object per path for as
/// long as the file is not refreshed: what <see cref="Scene3DTextureResidency{T}"/> keys an upload on.
/// </summary>
public sealed class Scene3DTexture
{
    private readonly Func<IReadOnlyList<Scene3DTextureLevel>> _make;
    private WeakReference<IReadOnlyList<Scene3DTextureLevel>>? _levels;

    internal Scene3DTexture(string path, int width, int height, int sourceWidth, int sourceHeight, bool opaque, bool broken,
                            IReadOnlyList<Scene3DTextureLevel> levels, Func<IReadOnlyList<Scene3DTextureLevel>> make)
    {
        (Path, Width, Height, SourceWidth, SourceHeight, Opaque, Broken) = (path, width, height, sourceWidth, sourceHeight, opaque, broken);
        _make = make;
        _levels = new WeakReference<IReadOnlyList<Scene3DTextureLevel>>(levels);
    }

    /// <summary>The file's absolute path; empty for the placeholder.</summary>
    public string Path { get; }
    /// <summary>Level 0's size, after <see cref="C3dImages.MaxTexturePixels"/>.</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>The file's own pixels (what the Inspector's Pixels row says); 0 when it did not decode.</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    /// <summary>Every pixel's alpha is 255: drawn in the opaque pass when its object is opaque too.</summary>
    public bool Opaque { get; }
    /// <summary>The file is missing or did not decode: this is the checker placeholder (R-em3d101-4g).</summary>
    public bool Broken { get; }
    /// <summary>Level 0 is smaller than the file (the cap bit).</summary>
    public bool Downsampled => !Broken && (Width < SourceWidth || Height < SourceHeight);

    /// <summary>The mip levels, level 0 first down to 1 × 1 — made again if they were let go.</summary>
    public IReadOnlyList<Scene3DTextureLevel> Levels()
    {
        if (_levels is not null && _levels.TryGetTarget(out var kept)) return kept;
        var made = _make();
        _levels = new WeakReference<IReadOnlyList<Scene3DTextureLevel>>(made);
        return made;
    }

    /// <summary>The bytes every level together takes.</summary>
    public long Bytes => Levels().Sum(l => (long)l.Rgba.Length);
}

/// <summary>brief-em3d-101 — the one place a 3D image is decoded: per path, kept until <see cref="Refresh"/>.</summary>
public static class Scene3DTextures
{
    private static readonly ConcurrentDictionary<string, Lazy<Scene3DTexture>> _byPath = new(StringComparer.Ordinal);
    private static long _decodes;

    /// <summary>How many textures have been made (decoded and reduced) in this process — a gate's counter.</summary>
    public static long Decodes => Interlocked.Read(ref _decodes);

    /// <summary>The checker a missing or undecodable file is drawn with (the 3D form of <c>BitmapCache.DrawBrokenPlaceholder</c>):
    /// one object, so every broken image shares one upload.</summary>
    public static Scene3DTexture Placeholder { get; } = MakePlaceholder();

    /// <summary>The texture for the image at <paramref name="path"/> (absolute), or <see cref="Placeholder"/> when it cannot be read.
    /// A path that was broken is read again once its file has changed — appeared, been replaced — so a file copied in after the
    /// fact draws without a Refresh Image, as the Inspector's Pixels row and <c>check</c> (which read the file afresh) already say.</summary>
    public static Scene3DTexture Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return Placeholder;
        var t = _byPath.GetOrAdd(path, p => new Lazy<Scene3DTexture>(() => Make(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (ReferenceEquals(t, Placeholder) && _brokenStamp.TryGetValue(path, out var was) && was != Stamp(path))
        {
            Refresh(path);
            t = _byPath.GetOrAdd(path, p => new Lazy<Scene3DTexture>(() => Make(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        return t;
    }

    /// <summary>The Messages note for a texture the cap reduced (<see cref="C3dImages.MaxTexturePixels"/>), or null.</summary>
    public static string? DownsampleNote(Scene3DTexture t)
        => t.Downsampled
            ? $"'{System.IO.Path.GetFileName(t.Path)}' is {t.SourceWidth:N0} × {t.SourceHeight:N0} pixels; the 3D view draws it at " +
              $"{t.Width:N0} × {t.Height:N0}, the largest every GPU it runs on is sure to take."
            : null;

    /// <summary>A broken path's file as it was when it would not read: its write time, or null when it was not there.</summary>
    private static readonly ConcurrentDictionary<string, DateTime?> _brokenStamp = new(StringComparer.Ordinal);

    private static DateTime? Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Forgets <paramref name="path"/> here and in <see cref="BitmapCache"/>: the next <see cref="Get"/> reads the file
    /// again and is a NEW texture, which a backend uploads (Refresh Image, Resolve Path…).</summary>
    public static void Refresh(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        BitmapCache.Invalidate(path);
        _byPath.TryRemove(path, out _);
    }

    private static Scene3DTexture Make(string path)
    {
        Interlocked.Increment(ref _decodes);
        var stamp = Stamp(path);
        var bmp = BitmapCache.Load(path);
        // Every broken image is the one placeholder object, so they share one upload.
        if (bmp is null || bmp.Width <= 0 || bmp.Height <= 0) { _brokenStamp[path] = stamp; return Placeholder; }
        _brokenStamp.TryRemove(path, out _);
        int sw = bmp.Width, sh = bmp.Height;
        var levels = Reduce(path, bmp, out bool opaque);
        int w = levels[0].Width, h = levels[0].Height;
        return new Scene3DTexture(path, w, h, sw, sh, opaque, false, levels, () => Reduce(path, BitmapCache.Load(path), out _));
    }

    /// <summary>The image's mip chain: level 0 at most <see cref="C3dImages.MaxTexturePixels"/> on its long edge, each next level
    /// half the last (at least 1), down to 1 × 1, each reduced from the one before by a high-quality filter.</summary>
    internal static IReadOnlyList<Scene3DTextureLevel> Reduce(string path, SKBitmap? source, out bool opaque)
    {
        opaque = true;
        if (source is null) return Placeholder.Levels();
        int sw = source.Width, sh = source.Height;
        double scale = Math.Min(1.0, (double)C3dImages.MaxTexturePixels / Math.Max(sw, sh));
        int w = Math.Max(1, (int)Math.Round(sw * scale)), h = Math.Max(1, (int)Math.Round(sh * scale));
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        var levels = new List<Scene3DTextureLevel>();
        // Work premultiplied (filtering an unpremultiplied colour drags a transparent pixel's colour into its neighbours), and
        // read each level out unpremultiplied, which is what a vertex colour is.
        var work = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            if (w == sw && h == sh) source.CopyTo(work, SKColorType.Rgba8888);
            else if (!source.ScalePixels(work, sampling)) throw new InvalidOperationException($"SkiaSharp could not scale '{path}'.");
            if (work.ColorType != SKColorType.Rgba8888) { var c = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)); work.CopyTo(c, SKColorType.Rgba8888); work.Dispose(); work = c; }
            while (true)
            {
                var bytes = Unpremultiplied(work);
                if (levels.Count == 0) opaque = AllOpaque(bytes);
                levels.Add(new Scene3DTextureLevel(work.Width, work.Height, bytes));
                if (work.Width == 1 && work.Height == 1) break;
                var next = new SKBitmap(new SKImageInfo(Math.Max(1, work.Width / 2), Math.Max(1, work.Height / 2), SKColorType.Rgba8888, SKAlphaType.Premul));
                work.ScalePixels(next, sampling);
                work.Dispose();
                work = next;
            }
        }
        finally { work.Dispose(); }
        return levels;
    }

    private static byte[] Unpremultiplied(SKBitmap premul)
    {
        var info = new SKImageInfo(premul.Width, premul.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bytes = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { premul.PeekPixels().ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes); }
        finally { handle.Free(); }
        return bytes;
    }

    private static bool AllOpaque(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 255) return false;
        return true;
    }

    /// <summary>A 64 × 64 grey checker with a red cross corner to corner — opaque, so a broken image's sheet stays a solid,
    /// pickable thing that plainly says its file is missing.</summary>
    private static Scene3DTexture MakePlaceholder()
    {
        const int n = 64;
        IReadOnlyList<Scene3DTextureLevel> Make()
        {
            using var bmp = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(bmp))
            {
                for (int y = 0; y < n; y += 8)
                    for (int x = 0; x < n; x += 8)
                        using (var p = new SKPaint { Color = ((x + y) / 8 & 1) == 0 ? new SKColor(205, 205, 210) : new SKColor(150, 150, 158) })
                            c.DrawRect(x, y, 8, 8, p);
                using var red = new SKPaint { Color = new SKColor(210, 50, 50), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
                c.DrawLine(0, 0, n, n, red);
                c.DrawLine(n, 0, 0, n, red);
            }
            return Reduce("", bmp, out _);
        }
        var levels = Make();
        return new Scene3DTexture("", n, n, 0, 0, true, true, levels, Make);
    }
}

/// <summary>
/// brief-em3d-101 R-em3d101-4e — which textures one backend holds, and the rule for changing that: a texture a scene draws and the
/// backend does not hold is uploaded, one no scene draws is released, and everything else is left alone — however the scene was
/// rebuilt. <typeparamref name="T"/> is the backend's own handle. <see cref="Uploads"/> (and the process-wide
/// <see cref="UploadsTotal"/>) count every upload: what gate 6 asserts, never a time.
/// </summary>
public sealed class Scene3DTextureResidency<T>
{
    private readonly Dictionary<Scene3DTexture, T> _held = new(ReferenceEqualityComparer.Instance);
    private static long _uploadsTotal;

    /// <summary>Uploads this backend has made.</summary>
    public long Uploads { get; private set; }

    /// <summary>Uploads every backend in the process has made.</summary>
    public static long UploadsTotal => Interlocked.Read(ref _uploadsTotal);

    /// <summary>The handle for each of the last synced scene's images, by index (<see cref="Scene3DModel.Images"/>).</summary>
    public T[] Bound { get; private set; } = [];

    /// <summary>How many textures are held.</summary>
    public int Count => _held.Count;

    /// <summary>Makes the held set <paramref name="scene"/>'s: uploads what is new, releases what it no longer draws.</summary>
    public void Sync(Scene3DModel scene, Func<Scene3DTexture, T> upload, Action<T> release)
    {
        var want = new HashSet<Scene3DTexture>(scene.Images, ReferenceEqualityComparer.Instance);
        foreach (var gone in _held.Keys.Where(t => !want.Contains(t)).ToList())
        {
            release(_held[gone]);
            _held.Remove(gone);
        }
        var bound = new T[scene.Images.Length];
        for (int i = 0; i < scene.Images.Length; i++)
        {
            var t = scene.Images[i];
            if (!_held.TryGetValue(t, out var h))
            {
                h = upload(t);
                _held[t] = h;
                Uploads++;
                Interlocked.Increment(ref _uploadsTotal);
            }
            bound[i] = h;
        }
        Bound = bound;
    }

    /// <summary>Releases everything held.</summary>
    public void Clear(Action<T> release)
    {
        foreach (var h in _held.Values) release(h);
        _held.Clear();
        Bound = [];
    }
}
