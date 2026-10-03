// brief-em3d-101 R-em3d101-4i / -6 — images in a VECTOR picture of a 3D view: Copy as Vector, Export Drawing… and `render --iso`.
//
// ONE PATH, BELOW THE FIREWALL. Each visible image sheet is projected into the picture's plane (its corners, and its outline as the
// clip) once here, and drawn by Skia's DrawImage under the affine map of its face in that view, clipped to the face, in the FILL'S
// slot — before any line, so the outline drawn over a traced reference reads as it does in the 3D view. A sheet seen edge-on has no
// area in the picture and contributes nothing. Sections draw none (a plane cut edge-on has no area), and a field plot's context
// geometry draws none (owner decision D8).
//
// SVG carries the image as an <image> (an export, so the bytes are fine there) and PDF stays vector apart from the image itself.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>An image sheet in a picture's plane: the image's corners (<see cref="Origin"/> its bottom-left, <see cref="Right"/> its
/// bottom-right, <see cref="Up"/> its top-left) and the sheet's outline as even-odd rings, which clip it.</summary>
/// <para>brief-em3d-101 Phase B — <see cref="Faces"/>: a face image's clip is its face's TRIANGLES, filled by winding (their union),
/// where a sheet's is its outline and holes, filled even-odd.</para>
public sealed record Em3dSceneImage(string Object, string Path, Uv Origin, Uv Right, Uv Up, IReadOnlyList<IReadOnlyList<Uv>> Clip,
                                    bool Faces = false, byte Alpha = 255);

public static class Em3dSceneImages
{
    /// <summary>The image sheets of <paramref name="problem"/> that <paramref name="images"/> names, projected by
    /// <paramref name="project"/> — none for a sheet <paramref name="omit"/> names (hidden in the view) or one seen edge-on.</summary>
    public static IReadOnlyList<Em3dSceneImage> Of(Em3dProblem problem, IReadOnlyDictionary<string, C3dPlacedImage>? images,
                                                   Func<Point3, Uv> project, IReadOnlySet<string>? omit = null)
    {
        if (images is not { Count: > 0 }) return [];
        var list = new List<Em3dSceneImage>();
        foreach (var sh in problem.Sheets)
        {
            if (omit?.Contains(sh.Name) == true || !images.TryGetValue(sh.Name, out var img)) continue;
            Uv o = project(img.Origin), r = project(img.Right), u = project(img.Up);
            double area = Math.Abs((r.U - o.U) * (u.V - o.V) - (r.V - o.V) * (u.U - o.U));
            double size = Math.Max(Dist(o, r), Dist(o, u));
            if (!(area > 1e-6 * size * size)) continue;                         // edge-on (or degenerate): nothing to draw
            IReadOnlyList<Uv> Ring(IReadOnlyList<Point2> ring) => [.. ring.Select(q => project(sh.World(q)))];
            list.Add(new Em3dSceneImage(sh.Name, img.Path, o, r, u, [Ring(sh.Outline), .. sh.Holes.Select(Ring)]));
        }
        return list;
    }

    /// <summary>
    /// <paramref name="problem"/> with every image sheet of <paramref name="e"/> it lacks put back — a reference image is placed
    /// not modelled and often with no material, so a run's problem leaves it out, and the picture draws it as the 3D view does —
    /// and its box grown to take them in, so a reference beside the model is framed whole. The problem itself when it lacks none.
    /// </summary>
    public static Em3dProblem WithImageSheets(Em3dProblem problem, C3dElaboration e)
    {
        var have = problem.Sheets.Select(sh => sh.Name).ToHashSet(StringComparer.Ordinal);
        var extra = e.Sheets.Concat(e.UnassignedSheets).Where(sh => e.Images.ContainsKey(sh.Name) && have.Add(sh.Name)).ToList();
        if (extra.Count == 0) return problem;
        var b = problem.Boundary;
        double x0 = b.Min.X, y0 = b.Min.Y, z0 = b.Min.Z, x1 = b.Max.X, y1 = b.Max.Y, z1 = b.Max.Z;
        foreach (var sh in extra)
        {
            var w = sh.WorldBounds();
            (x0, y0, z0) = (Math.Min(x0, w.X0), Math.Min(y0, w.Y0), Math.Min(z0, w.Z0));
            (x1, y1, z1) = (Math.Max(x1, w.X1), Math.Max(y1, w.Y1), Math.Max(z1, w.Z1));
        }
        return problem with
        {
            Sheets = [.. problem.Sheets, .. extra],
            Boundary = b with { Min = new Point3(x0, y0, z0), Max = new Point3(x1, y1, z1) },
        };
    }

    /// <summary>brief-em3d-101 Phase B — the face images <paramref name="placed"/> projected by <paramref name="project"/>: none on an
    /// object <paramref name="omit"/> names (hidden in the view), none seen edge-on, and none on a face turned AWAY from the viewer —
    /// a face image is seen from outside its solid only (the 3D view's depth test hides the rest), and one drawn through the solid
    /// would read mirrored. Each is clipped to its face's triangles and drawn at its OWN transparency, never its object's.</summary>
    /// <param name="mirrored">The projection is a mirror image (right × up points AWAY from the viewer), as the isometric outline's
    /// <see cref="Em3dSectionScene.Project"/> is: a face toward the viewer then winds clockwise in the picture.</param>
    public static IReadOnlyList<Em3dSceneImage> OfFaces(IReadOnlyList<Scene3DPlacedFaceImage> placed, Func<Point3, Uv> project,
                                                        IReadOnlySet<string>? omit = null, bool mirrored = false)
    {
        var list = new List<Em3dSceneImage>();
        foreach (var f in placed)
        {
            if (omit?.Contains(f.Object) == true) continue;
            var img = f.Placed;
            Uv o = project(img.Origin), r = project(img.Right), u = project(img.Up);
            double area = Math.Abs((r.U - o.U) * (u.V - o.V) - (r.V - o.V) * (u.U - o.U));
            double size = Math.Max(Dist(o, r), Dist(o, u));
            if (!(area > 1e-6 * size * size)) continue;
            // The face's triangles wind counter-clockwise seen from outside (C3dImages.FaceFrame), so their signed area in the
            // picture is positive when the face looks toward the viewer — in perspective as well as orthographic.
            double facing = 0;
            foreach (var (ta, tb, tc) in f.Triangles)
            {
                Uv a = project(ta), b = project(tb), c = project(tc);
                facing += (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
            }
            if (!((mirrored ? -facing : facing) > 0)) continue;
            byte alpha = new Scene3DTransparency(f.Use.Record.Transparency, f.Use.Opacity).Alpha(255);
            list.Add(new Em3dSceneImage(f.Object, img.Path, o, r, u,
                [.. f.Triangles.Select(t => (IReadOnlyList<Uv>)[project(t.A), project(t.B), project(t.C)])], Faces: true, Alpha: alpha));
        }
        return list;
    }

    private static double Dist(Uv a, Uv b) => Math.Sqrt((a.U - b.U) * (a.U - b.U) + (a.V - b.V) * (a.V - b.V));

    /// <summary>Draws each of <paramref name="images"/> onto <paramref name="canvas"/>: <paramref name="map"/> takes a picture point to
    /// the page; an object's transparency (as the 3D view paints it) multiplies the image's own alpha. A file that cannot be read
    /// draws the 3D view's checker placeholder in its place.</summary>
    public static void Draw(SKCanvas canvas, IReadOnlyList<Em3dSceneImage> images, Func<Uv, SKPoint> map,
                            IReadOnlyDictionary<string, Scene3DTransparency>? transparency)
    {
        foreach (var im in images)
        {
            using var bitmap = Pixels(im.Path);
            if (bitmap is null) continue;
            using var image = SKImage.FromBitmap(bitmap);
            SKPoint o = map(im.Origin), r = map(im.Right), u = map(im.Up);
            float w = bitmap.Width, h = bitmap.Height;
            // pixel (x, y), y down from the image's top: o + (x / w)(r − o) + (1 − y / h)(u − o).
            var m = new SKMatrix
            {
                ScaleX = (r.X - o.X) / w, SkewX = -(u.X - o.X) / h, TransX = u.X,
                SkewY = (r.Y - o.Y) / w, ScaleY = -(u.Y - o.Y) / h, TransY = u.Y,
                Persp2 = 1,
            };
            using var clip = new SKPath { FillType = im.Faces ? SKPathFillType.Winding : SKPathFillType.EvenOdd };
            foreach (var ring in im.Clip)
            {
                if (ring.Count < 3) continue;
                var pts = ring.Select(map).ToArray();
                // a face's triangles are wound all one way in the face, which may be either way on the page: winding needs one
                if (im.Faces && (pts[1].X - pts[0].X) * (pts[2].Y - pts[0].Y) - (pts[1].Y - pts[0].Y) * (pts[2].X - pts[0].X) < 0) Array.Reverse(pts);
                clip.AddPoly(pts, close: true);
            }
            byte alpha = im.Faces ? im.Alpha : transparency?.TryGetValue(im.Object, out var see) == true ? see.Alpha(255) : (byte)255;
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(alpha) };
            canvas.Save();
            canvas.ClipPath(clip, antialias: true);
            canvas.Concat(in m);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            canvas.Restore();
        }
    }

    /// <summary>The file's pixels (the caller disposes them), or the placeholder's level 0 when it cannot be read. Decoded as the 3D
    /// view decodes them (<see cref="Scene3DTextures"/>) and not kept: a photo's full-size decode is not held for the session.</summary>
    private static SKBitmap? Pixels(string path)
    {
        if (Scene3DTextures.Decode(path).Bitmap is { } bmp) return bmp;
        var level = Scene3DTextures.Placeholder.Levels()[0];
        var info = new SKImageInfo(level.Width, level.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var b = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(level.Rgba, 0, b.GetPixels(), level.Rgba.Length);
        return b;
    }
}
