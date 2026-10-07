// OASIS LAYERNAME records as source layer names (brief-oasis-gdstk.md §9a, R-oas-4a). GDSII carries no
// layer names, so the shared import has always proposed a mapping from (layer, datatype) numbers alone;
// an OASIS file may name its layers, and a name is what survives the crossing between two technologies
// (LayoutLayerMapping's own premise). This turns the file's records into one name per layer key the
// import actually uses, which GdsiiLayerReconciliation.BuildSourceLayers then matches name-first —
// DxfLayerReconciliation's precedent — before it falls back to the numbers.

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

public static class GdstkLayerNames
{
    /// <summary>
    /// The name <paramref name="layerNames"/> gives each layer key used in <paramref name="structures"/>,
    /// for the keys a record covers. A label's key is (layer, text type), so it is looked up among the
    /// TEXT records, and every other shape's among the GEOMETRY records. When a key is used by both, the
    /// geometry name wins: a mapping row moves every shape on the key, and the geometry is what it is
    /// for. When several records cover one key, the first in the file wins.
    /// </summary>
    public static IReadOnlyDictionary<LayerKey, string> For(
        IReadOnlyList<InterchangeStructure> structures, IReadOnlyList<GdstkLayerName> layerNames)
    {
        var names = new Dictionary<LayerKey, string>();
        if (layerNames.Count == 0) return names;

        var geometry = new HashSet<LayerKey>();
        var text = new HashSet<LayerKey>();
        foreach (var shape in structures.SelectMany(s => s.Shapes))
            (shape is LabelShape ? text : geometry).Add(shape.Layer);

        foreach (var key in geometry)
            if (Find(layerNames, key, isText: false) is { } name) names[key] = name;
        foreach (var key in text)
            if (!names.ContainsKey(key) && Find(layerNames, key, isText: true) is { } name) names[key] = name;
        return names;
    }

    private static string? Find(IReadOnlyList<GdstkLayerName> layerNames, LayerKey key, bool isText) =>
        layerNames.FirstOrDefault(n => n.IsText == isText && n.Name.Length > 0
                                       && Covers(n.LayerIntervalType, n.LayerA, n.LayerB, key.Layer)
                                       && Covers(n.TypeIntervalType, n.TypeA, n.TypeB, key.Datatype))?.Name;

    /// <summary>OASIS's interval types (SEMI P39 §7.5, gdstk's <c>OasisInterval</c>): 0 every value,
    /// 1 at most A, 2 at least A, 3 exactly A, 4 from A to B. gdstk keeps the one bound of types 1–3 in A.</summary>
    private static bool Covers(int type, long a, long b, int value) => type switch
    {
        0 => true,
        1 => value >= 0 && value <= a,
        2 => value >= a,
        3 => value == a,
        4 => value >= a && value <= b,
        _ => false,
    };
}
