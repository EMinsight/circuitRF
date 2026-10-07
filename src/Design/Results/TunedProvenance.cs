using RfCore.Data;

namespace CircuitRF.Design.Results;

/// <summary>
/// What a results file says about itself when it was produced from TUNED values rather than the
/// schematic's own (brief-tuneopt-3 R-to3-7): one metadata cube, <see cref="CubeName"/>, whose axis
/// labels list every tuned value as <c>key = value</c>. A file without it came from the schematic.
/// The <c>__</c> prefix keeps it out of every trace picker, as the run's other metadata cubes are.
/// </summary>
public static class TunedProvenance
{
    public const string CubeName = "__TunedValues";
    public const string AxisName = "tunable";

    /// <summary>A copy of <paramref name="data"/> carrying the provenance cube; the cubes themselves
    /// are shared, not copied.</summary>
    public static DataSet Stamp(DataSet data, IReadOnlyDictionary<string, string> values)
    {
        var stamped = new DataSet();
        foreach (var group in data.Groups)
            foreach (var (name, cube) in data.CubesIn(group))
                if (!(group == DataSet.DefaultGroup && name == CubeName))
                    stamped.AddToGroup(group, name, cube);

        var keys   = values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var labels = keys.Select(k => $"{k} = {values[k]}").ToArray();
        var index  = Enumerable.Range(0, keys.Length).Select(i => (double)i).ToArray();
        if (keys.Length > 0)
            stamped.Add(CubeName, new DataCube([new Axis(AxisName, index, "", labels)], index));
        return stamped;
    }

    /// <summary>The <c>key = value</c> lines a stamped DataSet carries; empty when it carries none.</summary>
    public static IReadOnlyList<string> Read(DataSet data) =>
        data.Contains(CubeName) && data[CubeName].Axes is [var axis] && axis.Labels is { } labels
            ? labels
            : [];
}
