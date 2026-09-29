// brief-em3d-76 R-em3d76-1a — which contacts of a thermal run carry a resistance, and where each value comes from. For every
// pair of meshed solids: the .c3d's ContactResistances override for THAT object pair, else the technology's
// ThermalInterfaces value for the two materials, else none (perfect contact). Whether the two actually touch is the mesh's
// to say — the solver finds the shared faces itself (src/Thermal/ThermalInterfaces) — so a pair here that shares no face is
// reported by the run as applying nowhere, never silently dropped.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Thermal;

/// <summary>One resistive contact: the two regions (indices into the lowering's regions), the solids' names, R″ (m²·K/W)
/// and where it came from.</summary>
public sealed record ThermalContact(int RegionA, int RegionB, string SolidA, string SolidB, double ResistanceM2KW, string Source);

public static class ThermalContacts
{
    /// <summary>
    /// The resistive contacts among <paramref name="regions"/> (each a solid's name and its material as elaborated):
    /// overrides first, then the technology's material pairs for every pair no override names. In region order.
    /// </summary>
    public static IReadOnlyList<ThermalContact> Resolve(IReadOnlyList<(string Solid, string Material)> regions,
                                                        IReadOnlyList<C3dContactResistance> overrides, Technology? tech,
                                                        List<string>? notes = null)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < regions.Count; i++) index.TryAdd(regions[i].Solid, i);
        var found = new SortedDictionary<(int, int), ThermalContact>();
        foreach (var c in overrides)
        {
            if (c.Between.Count != 2 || !index.TryGetValue(c.Between[0], out int a) || !index.TryGetValue(c.Between[1], out int b) || a == b)
            {
                // an override naming a solid this run does not mesh (a bond wire, one a block replaced, one a submodel cut away)
                // applies nowhere, and says so rather than vanishing
                if (c.Between.Count == 2)
                    notes?.Add($"The contact override between '{c.Between[0]}' and '{c.Between[1]}' applies nowhere in this run: " +
                               $"{string.Join(" and ", c.Between.Where(n => !index.ContainsKey(n)).Select(n => $"'{n}'").DefaultIfEmpty("the pair"))} " +
                               "is not a meshed solid here.");
                continue;
            }
            var key = a < b ? (a, b) : (b, a);
            found[key] = new ThermalContact(key.Item1, key.Item2, regions[key.Item1].Solid, regions[key.Item2].Solid, c.ResistanceM2KW,
                                            "the document's ContactResistances override");
        }
        if (tech is not null && tech.ResolvedThermalInterfaces.Count > 0)
        {
            var byMaterial = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < regions.Count; i++)
                (byMaterial.TryGetValue(ThermalMaterials.BaseName(regions[i].Material), out var l) ? l : byMaterial[ThermalMaterials.BaseName(regions[i].Material)] = []).Add(i);
            foreach (var p in tech.ResolvedThermalInterfaces)
            {
                if (!byMaterial.TryGetValue(p.MaterialA, out var sa) || !byMaterial.TryGetValue(p.MaterialB, out var sb)) continue;
                foreach (int a in sa)
                    foreach (int b in sb)
                    {
                        if (a == b) continue;
                        var key = a < b ? (a, b) : (b, a);
                        if (found.ContainsKey(key)) continue;       // an override, or this pair already met the other way round
                        found[key] = new ThermalContact(key.Item1, key.Item2, regions[key.Item1].Solid, regions[key.Item2].Solid,
                                                        p.ResistanceM2KW, $"the technology's pair '{p.MaterialA}' / '{p.MaterialB}'" +
                                                        (p.Source is { Length: > 0 } src ? $" ({src})" : ""));
                    }
            }
        }
        return [.. found.Values];
    }
}
