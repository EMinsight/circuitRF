// The scope's SELECTORS resolved against one run's artwork (brief-impedance-4): regions, picked copper
// and nets. Widths are a filter and are matched in Analyze by a number; these are geometry, so they are
// matched here, trace by trace, BEFORE cutting — a trace no selector chooses is never cut or solved.
//
// Four rules hold them together:
//  - A region matches when ANY part of a trace's centre line lies inside it (R-imp4-1b): a lasso that
//    clips the end of the RF trace must not drop it, and the whole trace is reviewed either way. It
//    matches only on its own layer, or on every layer when it names none (brief-impedance-6).
//  - A whole layer matches every trace on it — but only while another selector is set; with none,
//    every analysed layer is whole already and the scope never reaches here.
//  - A pick is a POINT, resolved at every run (R-imp4-2a). `Connected` reads the galvanic partition DRC
//    and railRF read (CopperPieces over DrcConnectivity), never a second walk; it is built only when a
//    connected pick exists, because on a large board it is the expensive part.
//  - A trace's net is the Net of the drawn shape under its middle, on its own layer, found through the
//    layout's spatial index (R-imp4-3b). Shapes that disagree, or carry none, give no net.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Em;

public static partial class TraceImpedanceAnalysis
{
    /// <summary>What the selectors found about each pick: whether there was copper under it, and how
    /// many traces it chose.</summary>
    private sealed class PickState
    {
        public bool OnCopper;
        public LayerKey? Key;
        public int Net = -1;
        public int Traces;
    }

    /// <summary>The selectors of one scope against one run's artwork.</summary>
    private sealed class Selection
    {
        private readonly TraceImpedanceScope? _scope;
        private readonly Prepared _prep;
        private readonly Technology _tech;
        private readonly CopperPieces? _pieces;
        private readonly NetLookup? _nets;
        private readonly HashSet<string> _netNames;
        public readonly Dictionary<TracePick, PickState> Picks = [];

        public Selection(TraceImpedanceScope? scope, Prepared prep, IReadOnlyList<LayoutShape> shapes, Technology tech)
        {
            _scope = scope is { HasSelectors: true } ? scope : null;
            _prep = prep;
            _tech = tech;
            _netNames = new HashSet<string>(_scope?.Nets ?? [], StringComparer.OrdinalIgnoreCase);

            // The nets: always read when the artwork carries any, so a report can count the traces no
            // net selector could choose (R-imp4-3b) — whether or not one is set.
            var bandLayers = prep.Ctx.BandOf.Keys.ToHashSet();
            var netted = shapes.Where(s => s.Net is { Length: > 0 } && bandLayers.Contains(s.Layer)).ToList();
            if (netted.Count > 0) _nets = new NetLookup(netted, tech, prep.Ctx.BandOf);

            if (_scope is null) return;
            if (_scope.Picks.Any(p => p.Extent == TracePickExtent.Connected))
                _pieces = CopperPieces.Build(shapes, tech);

            foreach (var pick in _scope.Picks)
            {
                if (Picks.ContainsKey(pick)) continue;
                var state = new PickState();
                Picks[pick] = state;
                if (tech.Layers.FirstOrDefault(l => string.Equals(l.Name, pick.LayerName, StringComparison.OrdinalIgnoreCase)) is not { } def
                    || !prep.Ctx.BandOf.TryGetValue(def.Key, out var band))
                    continue;
                state.Key = def.Key;
                if (pick.Extent == TracePickExtent.Connected)
                {
                    state.Net = _pieces!.PieceAt(pick.X, pick.Y, def.Key);
                    state.OnCopper = state.Net >= 0;
                }
                else
                    state.OnCopper = prep.Ctx.Copper.TryGetValue(band.Index, out var cu) && cu.Contains(pick.X, pick.Y);
            }
        }

        /// <summary>Whether the artwork carries any net on a copper layer.</summary>
        public bool HasNets => _nets is not null;

        /// <summary>Every net stated on a copper shape, sorted.</summary>
        public IReadOnlyList<string> NetNames => _nets?.Names ?? [];

        /// <summary>The net of a trace, or null — see <see cref="NetLookup.NetAt"/>.</summary>
        public string? NetOf(LayerWork lw, ChainWork chain)
        {
            if (_nets is null) return null;
            var (x, y) = ChainMiddle(chain);
            return _nets.NetAt(x, y, lw.Band.Index);
        }

        /// <summary>Which of <paramref name="lw"/>'s chains a selector chooses; null when the scope sets
        /// none, so every chain passes the selector clause.</summary>
        public bool[]? Select(LayerWork lw)
        {
            if (_scope is null) return null;
            var chosen = new bool[lw.Chains.Count];
            if (_scope.WholeLayers.Any(n => BandIndexOf(n) == lw.Band.Index))
            {
                // As with no scope on this layer: a SHORT chain stays a pad unless something points at it.
                for (int i = 0; i < chosen.Length; i++) chosen[i] = !lw.Chains[i].Short;
                return chosen;
            }
            var regions = _scope.Regions.Where(r => r.LayerName is null || BandIndexOf(r.LayerName) == lw.Band.Index).ToList();
            for (int i = 0; i < lw.Chains.Count; i++)
            {
                var chain = lw.Chains[i];
                if (regions.Any(r => InRegion(chain, r))) chosen[i] = true;
                if (_netNames.Count > 0 && NetOf(lw, chain) is { } net && _netNames.Contains(net)) chosen[i] = true;
            }

            foreach (var (pick, state) in Picks)
            {
                if (!state.OnCopper || state.Key is not { } key) continue;
                if (pick.Extent == TracePickExtent.Trace)
                {
                    if (_prep.Ctx.BandOf[key].Index != lw.Band.Index) continue;
                    if (NearestOnIsland(lw, pick.X, pick.Y) is { } k) { chosen[k] = true; state.Traces++; }
                    continue;
                }
                var layers = lw.Band.Layer.DrawingLayers;
                for (int i = 0; i < lw.Chains.Count; i++)
                {
                    var (x, y) = ChainMiddle(lw.Chains[i]);
                    long px = (long)Math.Round(x), py = (long)Math.Round(y);
                    if (layers.Any(dl => _pieces!.PieceAt(px, py, dl) == state.Net)) { chosen[i] = true; state.Traces++; }
                }
            }
            return chosen;
        }

        /// <summary>The conductor band a layer NAME is on — a drawing layer of the technology, as the
        /// panel and the CLI name them — or −1 when it names no copper layer, which then selects
        /// nothing.</summary>
        private int BandIndexOf(string layerName)
        {
            if (_bandByName.TryGetValue(layerName, out int index)) return index;
            index = _tech.Layers.FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase)) is { } def
                    && _prep.Ctx.BandOf.TryGetValue(def.Key, out var band) ? band.Index : -1;
            _bandByName[layerName] = index;
            return index;
        }

        private readonly Dictionary<string, int> _bandByName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Any part of the centre line — a piece, or the join between two — inside the region.</summary>
        private static bool InRegion(ChainWork chain, TraceScopeRegion region)
        {
            if (region.VertexCount < 3) return false;
            (double X, double Y)? last = null;
            foreach (var (p, reversed) in chain.Pieces)
            {
                var (ax, ay, bx, by) = reversed ? (p.Bx, p.By, p.Ax, p.Ay) : (p.Ax, p.Ay, p.Bx, p.By);
                if (last is { } l && region.Touches(l.X, l.Y, ax, ay)) return true;
                if (region.Touches(ax, ay, bx, by)) return true;
                last = (bx, by);
            }
            return false;
        }

        /// <summary>The chain on the copper island holding the point, nearest it by centre line — the
        /// trace a click on its copper means; null when the island carries no trace (a pour, a pad).</summary>
        private static int? NearestOnIsland(LayerWork lw, long x, long y)
        {
            if (lw.Copper is not { } cu || !cu.Contains(x, y)) return null;
            int e = cu.NearestEdge(x, y, Math.Max(4 * lw.MaxWidth, 1));
            if (e < 0) return null;
            int island = lw.IslandOfRing[cu.RingOf[e]];
            int? best = null;
            double bestD = double.MaxValue;
            for (int i = 0; i < lw.Chains.Count; i++)
            {
                var chain = lw.Chains[i];
                if (lw.IslandOfRing[chain.Pieces[0].Piece.Ring] != island) continue;
                foreach (var (p, _) in chain.Pieces)
                {
                    double d = SegmentDistance(x, y, p.Ax, p.Ay, p.Bx, p.By);
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
            return best;
        }
    }

    /// <summary>The point halfway along a chain's pieces — its "middle station" before it is cut.</summary>
    private static (double X, double Y) ChainMiddle(ChainWork chain)
    {
        double half = 0.5 * chain.Pieces.Sum(p => p.Piece.Length);
        foreach (var (p, reversed) in chain.Pieces)
        {
            if (half <= p.Length)
            {
                var (ax, ay, bx, by) = reversed ? (p.Bx, p.By, p.Ax, p.Ay) : (p.Ax, p.Ay, p.Bx, p.By);
                double t = p.Length > 0 ? half / p.Length : 0;
                return (ax + t * (bx - ax), ay + t * (by - ay));
            }
            half -= p.Length;
        }
        var last = chain.Pieces[^1].Piece;
        return (0.5 * (last.Ax + last.Bx), 0.5 * (last.Ay + last.By));
    }

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        return Math.Sqrt(Sq(px - ax - t * dx) + Sq(py - ay - t * dy));
    }

    /// <summary>
    /// The nets stated on copper shapes, found by POINT through the layout's spatial index — never a
    /// scan of every shape per trace.
    /// </summary>
    private sealed class NetLookup
    {
        private readonly List<LayoutShape> _shapes;
        private readonly Technology _tech;
        private readonly Dictionary<LayerKey, CrossSectionExtractor.Band> _bandOf;
        private readonly LayoutSpatialIndex _index = new();

        public NetLookup(List<LayoutShape> shapes, Technology tech, Dictionary<LayerKey, CrossSectionExtractor.Band> bandOf)
        {
            _shapes = shapes;
            _tech = tech;
            _bandOf = bandOf;
            Names = [.. shapes.Select(s => s.Net!).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];
        }

        public IReadOnlyList<string> Names { get; }

        /// <summary>The one net stated by the shapes on band <paramref name="bandIndex"/> holding the
        /// point; null where none does, or where two disagree.</summary>
        public string? NetAt(double x, double y, int bandIndex)
        {
            long px = (long)Math.Round(x), py = (long)Math.Round(y);
            string? net = null;
            foreach (int i in _index.QueryIntersecting(_shapes, new Bbox(px, py, px, py)))
            {
                var shape = _shapes[i];
                if (!_bandOf.TryGetValue(shape.Layer, out var band) || band.Index != bandIndex) continue;
                bool inside = false;
                DrcRegions.Expand(shape, _tech, _ => long.MaxValue, (_, _, paths) =>
                {
                    foreach (var path in paths)
                        if (Clipper.PointInPolygon(new Point64(px, py), path) != PointInPolygonResult.IsOutside) { inside = true; return; }
                });
                if (!inside) continue;
                if (net is null) net = shape.Net;
                else if (!string.Equals(net, shape.Net, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return net;
        }
    }
}
