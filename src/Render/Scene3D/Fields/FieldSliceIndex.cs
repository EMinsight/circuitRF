// A clip-plane plot's slider slices one mesh again and again, and FieldSlicer visited every sub-tetrahedron each time
// (118,599 order-2 cells, 948,792 sub-tetrahedra: 115-195 ms a slice in Release) to find the few thousand the plane
// crosses. This index holds each cell's extent along one axis, sorted, so an axis-aligned slice visits only the cells whose
// extent holds the plane. It is built once per mesh and axis and kept beside the mesh for as long as the mesh lives.
//
// The candidates are a SUPERSET, never a filter the slicer relies on for exactness: each is still cut by FieldSlicer's own
// test, in its own double arithmetic, so the triangles (and their order: candidates are visited in cell order) are exactly
// what a full pass produces.

using System.Runtime.CompilerServices;

namespace CircuitRF.Render.Scene3D.Fields;

internal sealed class FieldSliceIndex
{
    private static readonly ConditionalWeakTable<FieldMesh, FieldSliceIndex?[]> Built = new();

    private readonly float[] _max;         // per cell: the largest coordinate of its nodes along the axis
    private readonly float[] _sortedMin;   // the cells' smallest coordinates, ascending
    private readonly int[] _order;         // the cell at each place of _sortedMin
    private readonly float _widest;        // the widest cell's extent
    private readonly float _margin;        // a hair past float rounding, in the mesh's own unit

    private FieldSliceIndex(FieldMesh mesh, int axis)
    {
        int cells = mesh.CellCount, npc = mesh.NodesPerCell;
        var min = new float[cells];
        _max = new float[cells];
        float lo = float.MaxValue, hi = float.MinValue, widest = 0;
        for (int c = 0; c < cells; c++)
        {
            float a = float.MaxValue, b = float.MinValue;
            for (int k = 0; k < npc; k++)
            {
                float x = mesh.Points[3 * mesh.Cells[c * npc + k] + axis];
                if (x < a) a = x;
                if (x > b) b = x;
            }
            min[c] = a;
            _max[c] = b;
            widest = Math.Max(widest, b - a);
            lo = Math.Min(lo, a);
            hi = Math.Max(hi, b);
        }
        _order = new int[cells];
        for (int c = 0; c < cells; c++) _order[c] = c;
        Array.Sort(min, _order);
        _sortedMin = min;
        _widest = widest;
        _margin = cells == 0 ? 0 : 1e-5f * Math.Max(Math.Abs(lo), Math.Abs(hi)) + 1e-5f * (hi - lo);
    }

    /// <summary>The index of <paramref name="mesh"/> along <paramref name="axis"/> (0 x, 1 y, 2 z), built on first use.</summary>
    public static FieldSliceIndex For(FieldMesh mesh, int axis)
    {
        var slots = Built.GetValue(mesh, _ => new FieldSliceIndex?[3]);
        lock (slots) return slots[axis] ??= new FieldSliceIndex(mesh, axis);
    }

    /// <summary>The cells whose extent holds <paramref name="at"/> (the mesh's own unit), ascending.</summary>
    public int[] CellsAt(double at)
    {
        float lo = (float)(at - _widest) - _margin, hi = (float)at + _margin;
        int from = Bound(_sortedMin, lo, above: false), to = Bound(_sortedMin, hi, above: true);
        var hits = new List<int>();
        for (int i = from; i < to; i++)
        {
            int c = _order[i];
            if (_max[c] >= (float)at - _margin) hits.Add(c);
        }
        var cells = hits.ToArray();
        Array.Sort(cells);
        return cells;
    }

    /// <summary>The first place in <paramref name="sorted"/> holding a value not below <paramref name="x"/> (or, with
    /// <paramref name="above"/>, above it).</summary>
    private static int Bound(float[] sorted, float x, bool above)
    {
        int a = 0, b = sorted.Length;
        while (a < b)
        {
            int m = (a + b) >>> 1;
            if (sorted[m] < x || (above && sorted[m] == x)) a = m + 1; else b = m;
        }
        return a;
    }
}
