// The artwork as the region checks see it: every shape flattened once, grouped by layer, and each
// layer's contributions unioned into one region. Factored out of DrcEngine.Run so the layout review
// (brief-review-2 R-rev2-1) builds its regions with the same code rather than a copy of the loop.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Drc;

internal static class DrcLayerGeometry
{
    /// <summary>
    /// <paramref name="checkable"/> flattened through <see cref="DrcRegions.Expand"/>: per layer the
    /// net-attributed pieces (minimum SPACING reads these) and the unioned region (everything else
    /// reads these). A shape that cannot be flattened is skipped and said in
    /// <paramref name="diagnostics"/> — one unflattenable shape must not lose the whole run's findings.
    /// </summary>
    internal static (Dictionary<LayerKey, List<(string? Net, Paths64 Paths)>> ByLayer,
                     Dictionary<LayerKey, Paths64> Regions)
        Build(IEnumerable<LayoutShape> checkable, Technology? tech, Func<LayerKey, long> capFor, List<string> diagnostics)
    {
        // Flattening is the expensive half and every rule on a layer wants the same result, so it
        // happens once per shape here rather than once per rule.
        var byLayer = new Dictionary<LayerKey, List<(string? Net, Paths64 Paths)>>();

        foreach (var shape in checkable)
        {
            try
            {
                DrcRegions.Expand(shape, tech, capFor, (layer, net, paths) =>
                {
                    if (!byLayer.TryGetValue(layer, out var list)) byLayer[layer] = list = [];
                    list.Add((net, paths));
                });
            }
            catch (Exception ex)
            {
                diagnostics.Add($"A {shape.GetType().Name} on layer {shape.Layer.Layer}/{shape.Layer.Datatype} " +
                                $"could not be flattened and was skipped: {ex.Message}");
            }
        }

        var regions = new Dictionary<LayerKey, Paths64>();
        foreach (var (layer, entries) in byLayer)
        {
            var all = new Paths64();
            foreach (var (_, paths) in entries) all.AddRange(paths);
            regions[layer] = DrcRegions.Union(all);
        }
        return (byLayer, regions);
    }
}
