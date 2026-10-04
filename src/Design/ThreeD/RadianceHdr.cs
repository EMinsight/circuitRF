// brief-em3d-106 R-em3d106-4d — a Radiance .hdr (RGBE) reader: a user environment for the realistic view. Small and dependency-free,
// as the brief asks. In Design, not Render, so `check` (which cannot reference Render) reads a file with the same code the view
// lights the scene with — a file check accepts is a file the view can draw. Reads the uncompressed and the new-style run-length
// encoded forms (every writer in use emits one of them) with -Y +X orientation, the standard latitude–longitude layout. The old
// run-length form, XYZE and other orientations are refused with the reason, never guessed at.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.ThreeD;

/// <summary>A decoded image: linear radiance, three floats a pixel, rows top first.</summary>
public sealed record RadianceImage(int Width, int Height, float[] Rgb);

public static class RadianceHdr
{
    /// <summary>The largest side read: a 16k equirectangular image is 16384 × 8192 (1.5 GB of floats would be the next step).</summary>
    public const int MaxSide = 16384;

    /// <summary>The decoded file, or null with <paramref name="why"/>.</summary>
    public static RadianceImage? Read(string path, out string? why)
    {
        why = null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = e.Message; return null; }
        return Decode(bytes, out why);
    }

    /// <summary>Only the header: whether <paramref name="path"/> reads as a Radiance file this reader takes. What <c>check</c> asks.</summary>
    public static bool Probe(string path, out string? why)
    {
        why = null;
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[Math.Min(f.Length, 4096)];
            int n = f.Read(head, 0, head.Length);
            return Header(head.AsSpan(0, n), out _, out _, out _, out why);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = e.Message; return false; }
    }

    public static RadianceImage? Decode(byte[] bytes, out string? why)
    {
        if (!Header(bytes, out int w, out int h, out int at, out why)) return null;
        var rgbe = new byte[(long)w * h * 4];
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            if (!Scanline(bytes, ref at, row, w, out why)) { why = $"row {y}: {why}"; return null; }
            row.CopyTo(rgbe, (long)y * w * 4);
        }
        var rgb = new float[(long)w * h * 3];
        for (long p = 0; p < (long)w * h; p++)
        {
            byte e = rgbe[4 * p + 3];
            if (e == 0) continue;
            float f = MathF.ScaleB(1f, e - (128 + 8));
            rgb[3 * p] = (rgbe[4 * p] + 0.5f) * f;
            rgb[3 * p + 1] = (rgbe[4 * p + 1] + 0.5f) * f;
            rgb[3 * p + 2] = (rgbe[4 * p + 2] + 0.5f) * f;
        }
        return new RadianceImage(w, h, rgb);
    }

    private static bool Header(ReadOnlySpan<byte> b, out int w, out int h, out int at, out string? why)
    {
        w = h = at = 0;
        why = null;
        string Line(ReadOnlySpan<byte> s, ref int i)
        {
            int start = i;
            while (i < s.Length && s[i] != (byte)'\n') i++;
            // A header saved with CRLF line ends still reads (FORMAT= is compared exactly).
            string l = Encoding.ASCII.GetString(s[start..i]).TrimEnd('\r');
            if (i < s.Length) i++;
            return l;
        }
        int i = 0;
        string first = Line(b, ref i);
        if (!first.StartsWith("#?", StringComparison.Ordinal)) { why = "it is not a Radiance file (no #? signature)"; return false; }
        while (true)
        {
            if (i >= b.Length) { why = "its header ends before the image size"; return false; }
            string l = Line(b, ref i);
            if (l.Length == 0) break;
            if (l.StartsWith("FORMAT=", StringComparison.Ordinal) && l != "FORMAT=32-bit_rle_rgbe")
            {
                why = $"its {l} is not 32-bit_rle_rgbe";
                return false;
            }
        }
        string size = Line(b, ref i);
        var t = size.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != 4 || t[0] != "-Y" || t[2] != "+X"
            || !int.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out h)
            || !int.TryParse(t[3], NumberStyles.None, CultureInfo.InvariantCulture, out w))
        {
            why = $"its size line \"{size}\" is not the standard -Y height +X width";
            return false;
        }
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide) { why = $"it is {w} × {h}; a side must be 1 to {MaxSide}"; return false; }
        at = i;
        return true;
    }

    private static bool Scanline(byte[] b, ref int at, byte[] row, int w, out string? why)
    {
        why = null;
        if (at + 4 > b.Length) { why = "the file ends early"; return false; }
        // New-style run-length encoding: 2, 2, then the width (8 ≤ w < 32768), each channel encoded separately.
        if (w >= 8 && w < 32768 && b[at] == 2 && b[at + 1] == 2 && (b[at + 2] & 0x80) == 0)
        {
            int n = (b[at + 2] << 8) | b[at + 3];
            if (n != w) { why = $"a run-length row says {n} pixels, the image {w}"; return false; }
            at += 4;
            for (int c = 0; c < 4; c++)
            {
                int x = 0;
                while (x < w)
                {
                    if (at >= b.Length) { why = "the file ends early"; return false; }
                    int count = b[at++];
                    if (count > 128)
                    {
                        count -= 128;
                        if (x + count > w || at >= b.Length) { why = "a run overflows its row"; return false; }
                        byte v = b[at++];
                        for (int k = 0; k < count; k++) row[4 * x++ + c] = v;
                    }
                    else
                    {
                        if (count == 0 || x + count > w || at + count > b.Length) { why = "a literal overflows its row"; return false; }
                        for (int k = 0; k < count; k++) row[4 * x++ + c] = b[at++];
                    }
                }
            }
            return true;
        }
        if (b[at] == 1 && b[at + 1] == 1 && b[at + 2] == 1) { why = "it uses the old run-length form, which is not read"; return false; }
        if (at + 4L * w > b.Length) { why = "the file ends early"; return false; }
        Array.Copy(b, at, row, 0, 4 * w);
        at += 4 * w;
        return true;
    }

    /// <summary>Writes <paramref name="image"/> uncompressed — for tests and fixtures.</summary>
    public static byte[] Encode(RadianceImage image)
    {
        var head = Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {image.Height} +X {image.Width}\n");
        var px = new byte[(long)image.Width * image.Height * 4];
        for (long p = 0; p < (long)image.Width * image.Height; p++)
        {
            float r = image.Rgb[3 * p], g = image.Rgb[3 * p + 1], bl = image.Rgb[3 * p + 2];
            float m = MathF.Max(r, MathF.Max(g, bl));
            if (m < 1e-32f) continue;
            int e = (int)MathF.Floor(MathF.Log2(m)) + 1;
            float s = MathF.ScaleB(256f, -e);
            px[4 * p] = (byte)Math.Clamp((int)(r * s), 0, 255);
            px[4 * p + 1] = (byte)Math.Clamp((int)(g * s), 0, 255);
            px[4 * p + 2] = (byte)Math.Clamp((int)(bl * s), 0, 255);
            px[4 * p + 3] = (byte)(e + 128);
        }
        return [.. head, .. px];
    }
}
