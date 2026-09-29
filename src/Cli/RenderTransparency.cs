using CircuitRF.Design.ThreeD;

namespace CircuitRF.Cli;

/// <summary>
/// brief-em3d-92 D5 — <c>render x.c3d --transparency lid=80,pa/match=40</c>: how see-through an object, an instance or a group's
/// every member is drawn, for ONE picture. A report's figure often wants one lid see-through without the design being edited.
///
/// <para>Argument parsing and refusals only, on <see cref="Render"/>'s terms. It is applied to the document
/// <see cref="Em3dSetupSource"/> has just read from disk — that call's own copy, which nothing else holds — so the file is
/// never written and no cache is narrowed for a later call (the <c>TechnologyLayerSelection</c> clone lesson). The range is
/// <see cref="C3dTransparency.Max"/>'s.</para>
/// </summary>
internal sealed class RenderTransparency(string path, IReadOnlyList<string> entries)
{
    /// <summary>The refusal an unknown name earned while <see cref="Apply"/> ran, or null.</summary>
    public int? Refusal { get; private set; }

    /// <summary>Each <c>name=percent</c> entry, or the refusal for the first that is malformed or out of range.</summary>
    public static int? Parse(IReadOnlyList<string> entries, out List<(string Name, int Percent)> parsed)
    {
        parsed = [];
        foreach (string entry in entries)
        {
            int eq = entry.LastIndexOf('=');
            string name = eq > 0 ? entry[..eq].Trim() : "";
            string value = eq > 0 ? entry[(eq + 1)..].Trim() : "";
            if (name.Length == 0 || !long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                                                   System.Globalization.CultureInfo.InvariantCulture, out long percent))
                return JsonRun.Fail(CliDiagnostics.RenderTransparencyMalformed(entry));
            if (percent is < 0 or > C3dTransparency.Max) return JsonRun.Fail(CliDiagnostics.RenderTransparencyRange(name, percent));
            parsed.Add((name, (int)percent));
        }
        return null;
    }

    /// <summary>Writes each entry onto <paramref name="doc"/>: an object's own, an instance's, or every member of a group at
    /// every depth (a polyline is a line and takes none). An unknown name sets <see cref="Refusal"/>, listing the names there are.</summary>
    public void Apply(C3dDocument doc)
    {
        if (entries.Count == 0 || Parse(entries, out var parsed) is not null) return;
        foreach (var (name, percent) in parsed)
        {
            if (doc.Objects.FirstOrDefault(o => o.Name == name) is { } obj and not C3dPolyline) { obj.Transparency = percent; continue; }
            if (doc.Instances.FirstOrDefault(i => i.Name == name) is { } inst) { inst.Transparency = percent; continue; }
            if (C3dGroups.All(doc).Any(g => g.Path == name))
            {
                foreach (var m in C3dGroups.MembersOf(doc, name))
                {
                    if (m.Instance) doc.Instances[m.Index].Transparency = percent;
                    else if (doc.Objects[m.Index] is not C3dPolyline) doc.Objects[m.Index].Transparency = percent;
                }
                continue;
            }
            var names = doc.Objects.Where(o => o is not C3dPolyline).Select(o => o.Name)
                           .Concat(doc.Instances.Select(i => i.Name)).Concat(C3dGroups.All(doc).Select(g => g.Path)).ToList();
            Refusal = JsonRun.Fail(CliDiagnostics.RenderTransparencyUnknown(name, path, names.Count == 0 ? "nothing" : string.Join(", ", names)));
            return;
        }
    }
}
