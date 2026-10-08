// The "these two halves of one cell are on different technologies" sentence — framework-free, so it
// can be tested without a WorkspaceViewModel (the ScaleFieldLinker / InstanceCellChoices precedent).
// The COMMAND still owns when to say it; this owns what it says and when there is nothing to say.

using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Says so when the artwork about to be generated and the electrical model already being simulated
/// are on two different technologies.
///
/// <para><b>This divergence is real, live, and was silent.</b> The generator is handed the LAYOUT's
/// technology — its own <c>TechRef</c> first and the workspace default only as a fallback — while a
/// microstrip component's substrate comes from the SCHEMATIC's: its own <c>TechRef</c> when it has one
/// (brief-artsch-6 R-as6-1), else the workspace default. So when a layout carries its own <c>TechRef</c>
/// and its schematic does not name the same one, the line is drawn on one substrate and computed on
/// another: the widths look right, the artwork looks right, and only a simulation shows it.</para>
///
/// <para><b>A stated FOOTPRINT is the second trigger</b> (brief-footprint-3 R-fp3-2b), and the
/// microstrip check did not cover it: a land pattern resolves its copper, mask and silkscreen BY
/// ROLE against the technology it is generated on, so a schematic of thirteen capacitors laid into a
/// layout on another technology places its lands on that technology's keys — silently, with no
/// microstrip anywhere in the design to make the first trigger fire.</para>
///
/// <para><b>For a stackup-bound line the STACKUPS are compared as well as the layer tables</b>
/// (<see cref="StackupComparison"/>): the layer-table comparison is the placement gate's, which judges what a
/// layout view means and so ignores the stackup, and two technologies that agree there can still put the line on
/// a different substrate.</para>
///
/// <para><b>Reported, not refused.</b> A layout with its own <c>TechRef</c> is a deliberate act
/// (<c>TechnologyResolver</c>'s own header: "a .clay only stores a TechRef when it deliberately
/// deviates"), so the answer is to name both technologies and what differs — not to block a gesture
/// the user meant.</para>
/// </summary>
public static class TechnologyDivergenceReport
{
    /// <summary>The warning to post, or null when there is nothing to say — no trigger in the
    /// schematic, or both halves resolve the same technology. <paramref name="schematicTechPath"/> is the
    /// schematic's RESOLVED technology (<see cref="SchematicTechnology.PathOf"/>): its own <c>TechRef</c>
    /// first, the workspace default only without one (brief-artsch-6 R-as6-1) — so a schematic that names
    /// its layout's technology no longer warns.</summary>
    public static string? Describe(
        SchematicEditModel schematic, string? schematicTechPath, string? layoutTechPath)
    {
        bool hasMicrostrip = schematic.Components.Any(c => PlanarLineSubstrateInjection.IsStackupLineKind(c.Symbol));
        bool hasFootprint  = schematic.Components.Any(c => c.Footprint is { Length: > 0 });
        if (!hasMicrostrip && !hasFootprint) return null;

        // Same file — the ordinary case, and nothing to say.
        if (schematicTechPath is not null && layoutTechPath is not null
            && string.Equals(Path.GetFullPath(schematicTechPath), Path.GetFullPath(layoutTechPath),
                             System.StringComparison.OrdinalIgnoreCase))
            return null;

        // Two copies of one table are the same technology (R47a's own rule), so comparing the TABLES
        // rather than the paths is what keeps this from firing on a workspace that simply holds the
        // process twice.
        string? difference = ExternalWorkspaceGate.CompareTechnologies(schematicTechPath, layoutTechPath);

        // A line computes on the STACKUP, which the layer-table comparison deliberately does not look at — two
        // technologies with one layer table can still hold 1.6 mm of FR-4 and 0.5 mm of εr 3.5. A footprint
        // resolves only its layers, so the stackup is asked about only where a stackup-bound line is.
        if (difference is null && hasMicrostrip && schematicTechPath is not null && layoutTechPath is not null
            && TechnologyResolver.LoadForPath(schematicTechPath) is { } schematicTech
            && TechnologyResolver.LoadForPath(layoutTechPath) is { } layoutTech)
            difference = StackupComparison.Difference(
                schematicTech, layoutTech,
                $"'{Path.GetFileNameWithoutExtension(schematicTechPath)}'", $"'{Path.GetFileNameWithoutExtension(layoutTechPath)}'");

        if (difference is null && schematicTechPath is not null && layoutTechPath is not null) return null;

        string schematicName = schematicTechPath is null
            ? "no technology (the workspace has no default)"
            : $"'{Path.GetFileNameWithoutExtension(schematicTechPath)}'";
        string layoutName = layoutTechPath is null
            ? "no technology"
            : $"'{Path.GetFileNameWithoutExtension(layoutTechPath)}'";

        string subject = hasMicrostrip && hasFootprint
            ? "This cell's microstrip components and its stated footprints"
            : hasMicrostrip ? "This cell's microstrip components"
            : "This cell's stated footprints";

        // A footprint resolves its LAYERS against the technology; a microstrip resolves its
        // SUBSTRATE. Two different consequences of one divergence, so the sentence says which.
        string consequence = hasMicrostrip
            ? "The artwork and the electrical model do not agree."
            : "A land pattern resolves its copper, mask and silkscreen by role against the technology "
            + "it is generated on, so the lands would be drawn on the other one's layers.";

        return $"{subject} are resolved against {schematicName} while this layout is drawn with "
             + $"{layoutName}. {consequence}"
             + (difference is null ? "" : $" {difference}")
             + " Point the schematic at the layout's technology (its TechRef), set the workspace default to "
             + "it, or point the layout back at the default with Change Technology….";
    }
}
