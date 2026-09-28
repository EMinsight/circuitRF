namespace CircuitRF.WBond;

/// <summary>One wire of a share computation: its centreline (metres, current flowing from the first point to the last), its
/// diameter and the array it belongs to.</summary>
public sealed record ShareCentreline(IReadOnlyList<(double X, double Y, double Z)> PointsM, double DiameterM, string Array);

/// <summary>
/// brief-em3d-78 R-em3d78-3 — how an array's current divides among its wires at RF (wbond.md §3.4): per unit current into
/// array k, wire i carries <c>(X·L_arr·e_k)ᵢ</c> — <see cref="ArrayReduction.CurrentShares"/> with only that array driven, which
/// for a single array is the X = L⁻¹A column scaled to sum to one. Inductive, so frequency-independent: one share serves every
/// harmonic. The wires of the OTHER arrays carry the circulating current an undriven array's shorted turn does, which sums to
/// zero over each of them.
///
/// <para><b>ONE PATH.</b> <see cref="For"/> is a design's own <see cref="WireMesh.Build"/> → <see cref="InductanceMatrix.Fill"/>
/// → <see cref="ArrayReduction.Reduce"/>, and <see cref="FromCentrelines"/> builds a design from resolved centrelines (points
/// rounded to the DBU, the nanometre) and calls it — so a set of drawn wires on a <c>.wBond</c>'s points gives that
/// <c>.wBond</c>'s share bit for bit, and the two cannot drift.</para>
/// </summary>
public sealed class ArrayShare
{
    private readonly double[][] _perArray;   // [array][wire]

    private ArrayShare(double[][] perArray, int[] arrayOfWire, string[] arrayNames)
    {
        _perArray = perArray;
        ArrayOfWire = arrayOfWire;
        ArrayNames = arrayNames;
    }

    /// <summary>The array each wire belongs to, in the design's wire order (arrays in order, members in order).</summary>
    public int[] ArrayOfWire { get; }

    public string[] ArrayNames { get; }

    public int WireCount => ArrayOfWire.Length;

    public int ArrayCount => ArrayNames.Length;

    /// <summary>Every wire's current per ampere into array <paramref name="array"/>, amperes per ampere.</summary>
    public IReadOnlyList<double> PerUnitCurrent(int array) => _perArray[array];

    /// <summary>The share of a design, through wBond's own reduction.</summary>
    public static ArrayShare For(WBondDesign design, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(design);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh, parallel), mesh);
        var per = new double[reduction.ArrayCount][];
        for (int k = 0; k < per.Length; k++)
        {
            var j = new double[reduction.ArrayCount];
            j[k] = 1.0;
            per[k] = reduction.CurrentShares(j);
        }
        return new ArrayShare(per, [.. mesh.ArrayOfWire], [.. mesh.ArrayNames]);
    }

    /// <summary>
    /// The share of wires given as resolved centrelines, arrays in order of first appearance and wires in the given order within
    /// each. <paramref name="groundPlane"/> reflects in z = 0 of the given frame, as a design's ground plane does; without it the
    /// wires are in free space. Consecutive points that round to one DBU are merged (a filament needs a length).
    /// </summary>
    /// <returns>The share, and the design-order index of each given wire.</returns>
    public static (ArrayShare Share, int[] DesignIndex) FromCentrelines(IReadOnlyList<ShareCentreline> wires, bool groundPlane, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(wires);
        var design = new WBondDesign { GroundPlane = new GroundPlane { Enabled = groundPlane }, IncludeCapacitance = false };
        var byName = new Dictionary<string, WireArray>(StringComparer.Ordinal);
        foreach (var w in wires)
        {
            if (!byName.TryGetValue(w.Array, out var array))
            {
                array = new WireArray { Name = w.Array };
                byName[w.Array] = array;
                design.Arrays.Add(array);
            }
            var pts = new List<Point3>();
            foreach (var (x, y, z) in w.PointsM)
            {
                var p = Point3.FromMetres(x, y, z);
                if (pts.Count == 0 || pts[^1] != p) pts.Add(p);
            }
            array.Wires.Add(new Wire { Points = pts, DiameterNm = WBondUnits.FromMetres(w.DiameterM) });
        }
        var index = new int[wires.Count];
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        int start = 0;
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in design.Arrays) { first[a.Name] = start; start += a.Wires.Count; }
        for (int i = 0; i < wires.Count; i++)
        {
            int k = next.GetValueOrDefault(wires[i].Array);
            index[i] = first[wires[i].Array] + k;
            next[wires[i].Array] = k + 1;
        }
        return (For(design, parallel), index);
    }
}
