// Every layer the review needs, found by ROLE and never by number (brief-review-2 R-rev2-2). A `.ctech`
// rule names layers by number, and an imported board's numbers differ from every shipped
// technology's; roles are what lets one review run unchanged on a shipped, an imported and a
// hand-built technology. Each role is found by the code that already names it — the stackup, the
// surface roles a footprint draws on, the via entries' spans, the outline layers the 3D view and the
// crop read, the ground flag the partition reads — so the review has no second opinion about any of
// them. A role the technology lacks is null or empty, and every check needing it is not run.

using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Design.Layout.Review;

/// <summary>One conductor of the stackup, with its place top to bottom among the conductors.</summary>
public sealed record ReviewCopperLayer(int Index, StackupLayer Row, IReadOnlyList<LayerKey> DrawingLayers)
{
    public string Name => Row.Name;
}

/// <summary>A drill layer — a stackup via entry's drawing layer — and the conductors it spans, null
/// where the entry names no resolvable span.</summary>
public sealed record ReviewDrillLayer(LayerKey Layer, StackupLayer Entry, StackupLayer? Top, StackupLayer? Bottom);

public sealed class ReviewLayers
{
    /// <summary>Conductors bound to a drawing layer, TOP to BOTTOM.</summary>
    public IReadOnlyList<ReviewCopperLayer> Copper { get; }

    /// <summary>Consecutive conductors — two with no conductor between them.</summary>
    public IReadOnlyList<(ReviewCopperLayer Upper, ReviewCopperLayer Lower)> AdjacentCopper { get; }

    public LayerKey? TopCopper    { get; }
    public LayerKey? BottomCopper { get; }
    public LayerKey? TopMask      { get; }
    public LayerKey? BottomMask   { get; }
    public LayerKey? TopPaste     { get; }
    public LayerKey? BottomPaste  { get; }

    public IReadOnlyList<ReviewDrillLayer> Drills { get; }

    /// <summary>The board-outline layers; empty when the technology names none.</summary>
    public IReadOnlySet<LayerKey> Outline { get; }

    /// <summary>The conductor flagged as the ground reference — the one the connectivity partition
    /// reads (<c>GroundReach.ReferenceName</c>) — or null when none is flagged.</summary>
    public StackupLayer? GroundReference { get; }

    private readonly Technology _tech;
    private readonly Dictionary<LayerKey, int> _order = new();

    private ReviewLayers(Technology tech)
    {
        _tech = tech;
        var copper = new List<ReviewCopperLayer>();
        foreach (var row in tech.Stackup.Layers)
            if (row.Kind == StackupKind.Conductor && row.DrawingLayers.Count > 0)
                copper.Add(new ReviewCopperLayer(copper.Count, row, [.. row.DrawingLayers]));
        Copper = copper;
        AdjacentCopper = [.. copper.Zip(copper.Skip(1))];

        foreach (var c in copper)
            foreach (var key in c.DrawingLayers)
                _order.TryAdd(key, _order.Count);

        var roles = SurfaceRoles.For(tech);
        (TopCopper, BottomCopper)   = (roles.Top.Copper, roles.Bottom.Copper);
        (TopMask, BottomMask)       = (roles.Top.Soldermask, roles.Bottom.Soldermask);
        (TopPaste, BottomPaste)     = (roles.Top.Paste, roles.Bottom.Paste);

        var drills = new List<ReviewDrillLayer>();
        foreach (var entry in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via))
        {
            var span = ViaSpanResolver.Resolve(entry, tech);
            foreach (var key in entry.DrawingLayers)
                drills.Add(new ReviewDrillLayer(key, entry, span?.Top, span?.Bottom));
        }
        Drills = drills;

        Outline = Em3dGenerator.BoardOutlineLayers(tech);
        GroundReference = tech.Stackup.Layers.FirstOrDefault(
            l => l.Kind == StackupKind.Conductor && l.IsGroundReference && l.Name.Length > 0);
    }

    public static ReviewLayers For(Technology tech)
    {
        ArgumentNullException.ThrowIfNull(tech);
        return new ReviewLayers(tech);
    }

    /// <summary>Every copper drawing layer, in stackup order.</summary>
    public IEnumerable<LayerKey> CopperDrawingLayers => _order.OrderBy(kv => kv.Value).Select(kv => kv.Key);

    /// <summary>A layer's place in stackup order: copper top to bottom first, every other layer after,
    /// by number.</summary>
    public (int, int, int) OrderOf(LayerKey? key) => key switch
    {
        null => (int.MaxValue, int.MaxValue, int.MaxValue),
        { } k when _order.TryGetValue(k, out int i) => (0, i, 0),
        { } k => (1, k.Layer, k.Datatype),
    };

    /// <summary>The technology's name for a drawing layer, or its number.</summary>
    public string NameOf(LayerKey key) =>
        _tech.Layers.FirstOrDefault(l => l.Key == key)?.Name is { Length: > 0 } name ? name : $"{key.Layer}/{key.Datatype}";
}
