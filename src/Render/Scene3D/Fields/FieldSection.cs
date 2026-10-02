// brief-em3d-84 R-em3d84-3 — a field on a clip plane, cut and ranged ONCE for both pictures: the 3D view's slice and
// `render --field`'s section. The cut is FieldSlicer's through FieldMeshTets at the scene's origin, on the plane as
// ClipPlane3D states it (in float, exactly as the view holds it), so the two pictures hold the same triangles; the range is
// FieldColorScale.Auto over that one slice, the GPU view's range for a ClipPlane plot — or, for a temperature (brief-em3d-88),
// FieldColorScale.MinMax, the view's range for one.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A clip-plane cut of one quantity: its triangles (scene-local metres), its range and its map, and where the scene's
/// origin is (world metres) so a picture can place it.</summary>
/// <param name="Axis">The plane's normal axis: 0 x, 1 y, 2 z.</param>
public sealed record FieldSectionCut(FieldQuantity Quantity, FieldSurface Slice, FieldColorScale Scale, ColorMap3D Map,
                                     (double X, double Y, double Z) Origin, int Axis)
{
    /// <summary>Where <paramref name="channels"/> fall in the range at <paramref name="phase"/> (radians), 0..1.</summary>
    public double Position(ReadOnlySpan<double> channels, double phase) => Scale.Position(Quantity.Evaluate(channels, phase));
}

public static class FieldSection
{
    /// <summary>The section of <paramref name="array"/> on <paramref name="mesh"/> by <paramref name="clip"/> — the view's slice
    /// and the headless one.</summary>
    public static FieldSurface Slice(FieldMesh mesh, FieldArray array, (double X, double Y, double Z) origin, ClipPlane3D clip,
                                     CancellationToken ct = default)
    {
        var e = clip.Equation;
        var tets = new FieldMeshTets(mesh, array, origin);
        // An axis plane visits only the cells whose extent holds it (FieldSliceIndex); a view-facing plane, every cell.
        int[]? only = null;
        if (clip.Axis != ClipAxis3D.View && mesh.Shape == FieldCellShape.Tetrahedron && mesh.ToMetres > 0)
        {
            int axis = (int)clip.Axis;
            double n = axis == 0 ? e.X : axis == 1 ? e.Y : e.Z;
            double o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var cells = FieldSliceIndex.For(mesh, axis).CellsAt((-e.W / n + o) / mesh.ToMetres);
            int sub = tets.Count / Math.Max(mesh.CellCount, 1);
            only = new int[cells.Length * sub];
            for (int i = 0; i < cells.Length; i++)
                for (int k = 0; k < sub; k++) only[i * sub + k] = cells[i] * sub + k;
        }
        return FieldSlicer.Slice(tets, new Vector3D(e.X, e.Y, e.Z), e.W, ct, only);
    }

    /// <summary>
    /// A ClipPlane plot's picture data: <paramref name="q"/> read from <paramref name="volume"/>, cut on <paramref name="clip"/>,
    /// ranged as the view ranges a ClipPlane plot (dB and the percentile the plot states; a temperature's true minimum and
    /// maximum), painted with the quantity's map.
    /// Null when the step holds no such volume array. brief-em3d-100 — the slice carries <paramref name="drive"/>, the plot's.
    /// </summary>
    public static FieldSectionCut? Cut(FieldQuantity q, FieldStep volume, (double X, double Y, double Z) origin, ClipPlane3D clip,
                                       bool db, double percentile, CancellationToken ct = default, FieldDriveReading? drive = null)
    {
        if (q.OnBoundary || volume.Load(q.Array.Name) is not { } array) return null;
        var slice = Slice(volume.Mesh, array, origin, clip, ct);
        FieldDrive.Apply([slice], drive?.Factor ?? 1);      // brief-em3d-100 — the plot's drive, as the view applies it
        // brief-em3d-88 — a temperature is ranged as the 3D view ranges one (brief-em3d-75 D9): its true minimum and maximum.
        var scale = q.IsTemperature ? FieldColorScale.MinMax(q, [slice]) : FieldSurfacePlot.EmScale(q, [slice], db, percentile, drive);
        return new FieldSectionCut(q, slice, scale, ColorMap3D.For(q), origin, (int)clip.Axis);
    }
}
