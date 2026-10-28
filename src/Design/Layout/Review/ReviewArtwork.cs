// What a review report's maps draw (brief-review-7 R-rev7-2): a layer's copper and the board outline, read
// off the artwork the run reviewed. Held by the result, so the PDF is drawn from the one result every other
// surface reads, and built only when a map asks: a run that is never exported never pays for it. Where the
// run already unioned the copper (every check reading regions or the partition does), the map reuses that
// union rather than making a second; otherwise the one layer asked for is unioned by the same code.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Review;

public sealed class ReviewArtwork
{
    private readonly IReadOnlyList<LayoutShape> _shapes;
    private readonly Technology _tech;
    private readonly ReviewLayers _layers;
    private readonly IReadOnlyDictionary<LayerKey, Paths64>? _regions;
    private readonly LayoutUnit _unit;
    private readonly int _dbuPerMicron;
    private ReviewBoard? _board;
    private readonly Dictionary<LayerKey, IReadOnlyList<long[]>> _copper = new();
    private readonly Lock _gate = new();

    private ReviewArtwork(ReviewContext ctx)
    {
        _shapes = ctx.Source.Shapes;
        _tech = ctx.Tech;
        _layers = ctx.Layers;
        _unit = ctx.Unit;
        _dbuPerMicron = ctx.DbuPerMicron;
        if (ctx.RegionsBuilt)
        {
            // Copper only: the other layers' unions are the run's business, and a panel holds this result.
            var copper = ctx.Layers.CopperDrawingLayers.ToHashSet();
            _regions = ctx.Regions.Where(kv => copper.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        _board = ctx.BoardBuilt;
    }

    internal static ReviewArtwork From(ReviewContext ctx) => new(ctx);

    /// <summary>The technology's name for a drawing layer.</summary>
    public string NameOf(LayerKey key) => _layers.NameOf(key);

    /// <summary>The layer's colour in its technology, or null when the technology does not list it.</summary>
    public Theming.Rgba? ColorOf(LayerKey key) => _tech.Layers.FirstOrDefault(l => l.Key == key)?.Color;

    /// <summary>The first copper layer of the stackup — what a map of a finding with no layer (an edge-stitching
    /// gap) draws under the outline — or null.</summary>
    public LayerKey? TopCopper => _layers.CopperDrawingLayers.Cast<LayerKey?>().FirstOrDefault();

    /// <summary>The drawing layers of the conductor next below the one <paramref name="upper"/> is drawn on, or
    /// empty — the lower layer of a finding between two adjacent conductors (<c>stacked-traces</c>).</summary>
    public IReadOnlyList<LayerKey> LowerNeighbour(LayerKey upper) =>
        _layers.AdjacentCopper.FirstOrDefault(p => p.Upper.DrawingLayers.Contains(upper)).Lower?.DrawingLayers ?? [];

    /// <summary>The copper drawn on <paramref name="key"/>, unioned, as flat DBU rings (outer and hole rings; fill
    /// even-odd).</summary>
    public IReadOnlyList<long[]> Copper(LayerKey key)
    {
        lock (_gate)
        {
            if (_copper.TryGetValue(key, out var rings)) return rings;
            Paths64? region = null;
            if (_regions is not null) _regions.TryGetValue(key, out region);
            else
            {
                var shapes = _shapes.Where(s => DrcRegions.IsCheckable(s)
                                             && (s.Layer == key || s is ViaShape { LandingLayer: { } l } && l == key));
                DrcLayerGeometry.Build(shapes, _tech, _ => long.MaxValue, []).Regions.TryGetValue(key, out region);
            }
            return _copper[key] = region is null ? [] : DrcRegions.ToRings(region);
        }
    }

    /// <summary>The board as the edge checks read it, as flat DBU rings; empty when the technology names no outline
    /// or nothing is drawn on it.</summary>
    public IReadOnlyList<long[]> Board
    {
        get
        {
            lock (_gate)
            {
                _board ??= ReviewBoard.Build(_shapes, _tech, _layers, _unit, _dbuPerMicron);
                return DrcRegions.ToRings(_board.Region);
            }
        }
    }
}
