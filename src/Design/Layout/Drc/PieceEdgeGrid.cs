// brief-railrf-36 R-rail36-2 — "does this via barrel touch this piece" answered LOCALLY.
//
// ── WHY ────────────────────────────────────────────────────────────────────────────────────────
//
// DrcConnectivity used to clip every inflated barrel against the WHOLE of every candidate piece
// whose bounding box met it. A plane's bounding box is the board, so on the six-layer field board
// every barrel was clipped against every plane's full path set: 15,831 clips handing Clipper
// 631 million vertices, 224 s for one extraction. The question is local — a barrel is a fraction of
// a millimetre across — and this index is what lets it be answered locally.
//
// ── WHAT IT ANSWERS, AND WHY THE ANSWER IS THE SAME ONE ────────────────────────────────────────
//
// For a rectangle R (the grown barrel's bounding box) it splits the piece's rings into the ones
// with an edge meeting R — "crossing" — and the rest. Every other ring has no edge in R, so its
// winding number is CONSTANT over R, and one exact ray cast from R's centre gives it. The piece
// restricted to R is then: the crossing rings, unmodified, plus that constant as a rectangle
// slightly larger than R. Intersected with a barrel that lies inside R, that is the same point set
// as the barrel against the whole piece, built from the same original edges — no vertex is moved
// and nothing is rounded, which is why RectClip (which rounds where it cuts a diagonal edge) is not
// used: a diagonal edge exactly at the dilation boundary must decide as it did.
//
// Uniform grid, CSR arrays, rebuilt per extraction and never maintained — WirePairSweep's and
// PieceIndex's reasoning, and PlanarEdgeIndex's (WB-D) shape, in DBU rather than nanometres.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Drc;

/// <summary>A uniform grid over one piece's edges, for rectangle queries and exact ray casts.</summary>
internal sealed class PieceEdgeGrid
{
    /// <summary>A pathological aspect ratio must cost a slower query, never unbounded memory.</summary>
    private const int MaxCellsPerAxis = 1024;

    private readonly long[] _ax, _ay, _bx, _by;
    private readonly int[] _ringOf;
    private readonly int _rings;
    private readonly long _minX, _minY, _maxX, _maxY, _cell;
    private readonly int _nx, _ny;
    private readonly int[] _cellStart;      // CSR: edges of cell k are _cellEdges[_cellStart[k].._cellStart[k+1])
    private readonly int[] _cellEdges;

    // Per-query scratch. One grid belongs to one extraction, which is single-threaded.
    private readonly int[] _seen;
    private readonly bool[] _crossing;
    private int _stamp;

    private PieceEdgeGrid(Paths64 rings)
    {
        int edges = 0;
        foreach (var ring in rings) edges += ring.Count;

        _ax = new long[edges]; _ay = new long[edges]; _bx = new long[edges]; _by = new long[edges];
        _ringOf = new int[edges];
        _rings = rings.Count;
        _seen = new int[edges];
        _crossing = new bool[rings.Count];

        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        int e = 0;
        for (int r = 0; r < rings.Count; r++)
        {
            var ring = rings[r];
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Point64 a = ring[i], b = ring[i + 1 == n ? 0 : i + 1];     // implicitly closed
                _ax[e] = a.X; _ay[e] = a.Y; _bx[e] = b.X; _by[e] = b.Y; _ringOf[e] = r;
                minX = Math.Min(minX, a.X); maxX = Math.Max(maxX, a.X);
                minY = Math.Min(minY, a.Y); maxY = Math.Max(maxY, a.Y);
                e++;
            }
        }

        if (edges == 0) { minX = minY = maxX = maxY = 0; }
        _minX = minX; _minY = minY; _maxX = maxX; _maxY = maxY;

        // About one edge per cell, bounded per axis.
        long w = maxX - minX + 1, h = maxY - minY + 1;
        double ideal = Math.Sqrt((double)w * h / Math.Max(1, edges));
        long cell = Math.Max(1, (long)Math.Ceiling(ideal));
        cell = Math.Max(cell, Math.Max(w, h) / MaxCellsPerAxis + 1);
        _cell = cell;
        _nx = (int)((w + cell - 1) / cell);
        _ny = (int)((h + cell - 1) / cell);

        var counts = new int[_nx * _ny + 1];
        for (int k = 0; k < edges; k++)
        {
            var (x0, y0, x1, y1) = CellsOf(k);
            for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
                counts[cy * _nx + cx + 1]++;
        }
        for (int k = 1; k < counts.Length; k++) counts[k] += counts[k - 1];
        _cellStart = counts;

        _cellEdges = new int[counts[^1]];
        var fill = new int[_nx * _ny];
        for (int k = 0; k < edges; k++)
        {
            var (x0, y0, x1, y1) = CellsOf(k);
            for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                int c = cy * _nx + cx;
                _cellEdges[_cellStart[c] + fill[c]++] = k;
            }
        }
    }

    /// <summary>Indexes a piece's rings.</summary>
    public static PieceEdgeGrid Build(Paths64 rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        return new PieceEdgeGrid(rings);
    }

    /// <summary>
    /// The piece as seen inside <paramref name="r"/>: the indices of the rings with an edge meeting
    /// the CLOSED rectangle, and the summed winding number of every other ring over it.
    /// </summary>
    /// <remarks>The winding is taken at <paramref name="r"/>'s centre by Sunday's rule, the same
    /// rule and sign <c>Regions.Contains</c> uses. No edge of a non-crossing ring meets
    /// <paramref name="r"/>, so the centre is on none of them and no cross product is zero.</remarks>
    public void Local(Bbox r, List<int> crossingRings, out int windingOfRest)
    {
        crossingRings.Clear();
        windingOfRest = 0;
        if (r.IsEmpty || _ax.Length == 0) return;

        Array.Clear(_crossing);

        // ── Which rings have an edge in R ───────────────────────────────────────────────────────
        if (r.MaxX >= _minX && r.MinX <= _maxX && r.MaxY >= _minY && r.MinY <= _maxY)
        {
            int stamp = NextStamp();
            int cx0 = ColOf(r.MinX), cx1 = ColOf(r.MaxX), cy0 = RowOf(r.MinY), cy1 = RowOf(r.MaxY);
            for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int c = cy * _nx + cx;
                for (int j = _cellStart[c]; j < _cellStart[c + 1]; j++)
                {
                    int k = _cellEdges[j];
                    if (_seen[k] == stamp) continue;
                    _seen[k] = stamp;
                    if (_crossing[_ringOf[k]]) continue;
                    if (SegmentMeetsRect(_ax[k], _ay[k], _bx[k], _by[k], r))
                    {
                        _crossing[_ringOf[k]] = true;
                        crossingRings.Add(_ringOf[k]);
                    }
                }
            }
        }

        // ── The rest's winding at R's centre: a ray to +x through the centre's grid row ─────────
        long px = r.MinX + (r.MaxX - r.MinX) / 2, py = r.MinY + (r.MaxY - r.MinY) / 2;
        if (px < _minX || px > _maxX || py < _minY || py > _maxY) return;   // outside every ring

        {
            int stamp = NextStamp();
            int row = RowOf(py);
            for (int cx = ColOf(px); cx < _nx; cx++)
            {
                int c = row * _nx + cx;
                for (int j = _cellStart[c]; j < _cellStart[c + 1]; j++)
                {
                    int k = _cellEdges[j];
                    if (_seen[k] == stamp) continue;
                    _seen[k] = stamp;
                    if (_crossing[_ringOf[k]]) continue;

                    long ax = _ax[k], ay = _ay[k], bx = _bx[k], by = _by[k];
                    Int128 cross = (Int128)(bx - ax) * (py - ay) - (Int128)(px - ax) * (by - ay);
                    if (ay <= py) { if (by > py && cross > 0) windingOfRest++; }
                    else if (by <= py && cross < 0) windingOfRest--;
                }
            }
        }
    }

    /// <summary>How many rings this grid indexes.</summary>
    public int Rings => _rings;

    private int NextStamp()
    {
        if (++_stamp == int.MaxValue) { Array.Clear(_seen); _stamp = 1; }
        return _stamp;
    }

    private (int X0, int Y0, int X1, int Y1) CellsOf(int k) =>
        (ColOf(Math.Min(_ax[k], _bx[k])), RowOf(Math.Min(_ay[k], _by[k])),
         ColOf(Math.Max(_ax[k], _bx[k])), RowOf(Math.Max(_ay[k], _by[k])));

    private int ColOf(long x) => (int)Math.Clamp((x - _minX) / _cell, 0, _nx - 1);
    private int RowOf(long y) => (int)Math.Clamp((y - _minY) / _cell, 0, _ny - 1);

    /// <summary>
    /// Whether segment a–b meets the CLOSED rectangle — exact: the bounding boxes overlap and the
    /// segment's line does not leave all four corners strictly on one side (separating axes).
    /// </summary>
    internal static bool SegmentMeetsRect(long ax, long ay, long bx, long by, Bbox r)
    {
        if (Math.Max(ax, bx) < r.MinX || Math.Min(ax, bx) > r.MaxX ||
            Math.Max(ay, by) < r.MinY || Math.Min(ay, by) > r.MaxY) return false;

        if (r.Contains(ax, ay) || r.Contains(bx, by)) return true;

        int Side(long x, long y)
        {
            Int128 c = (Int128)(bx - ax) * (y - ay) - (Int128)(x - ax) * (by - ay);
            return c > 0 ? 1 : c < 0 ? -1 : 0;
        }

        int s0 = Side(r.MinX, r.MinY), s1 = Side(r.MaxX, r.MinY),
            s2 = Side(r.MaxX, r.MaxY), s3 = Side(r.MinX, r.MaxY);
        return !((s0 > 0 && s1 > 0 && s2 > 0 && s3 > 0) || (s0 < 0 && s1 < 0 && s2 < 0 && s3 < 0));
    }
}
