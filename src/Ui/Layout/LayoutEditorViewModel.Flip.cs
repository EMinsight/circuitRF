using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Flip — moving the selection to the other face of the board (brief-bsp-2; series overview D1/D2).
///
/// <para><b>A flip is Mirror's horizontal reflection plus a change of SURFACE</b>, and runs through the
/// same rigid-body path (<see cref="Reflect"/>), so a part flipped at 30 deg lands exactly where Mirror
/// would put it. On top of the reflection: every selected placement's <see cref="LayoutInstance.Side"/>
/// toggles — its cell is not touched, the side-aware resolution draws its top-surface artwork on the
/// bottom roles — and every selected loose shape on a surface role moves to that role's layer on the
/// other face, so a part and the tracks under it go together. Inner copper, drill and outline stay put,
/// and a via keeps its layers: a through via is symmetric.</para>
///
/// <para><b>Refused, and nothing moves</b>: a technology with no bottom copper; a selected loose shape on
/// a role the technology has on one side only (it would be lost — a placed part's missing role is
/// instead simply not drawn on the bottom, and reported, because its cell still holds it); and a
/// selected blind or buried via, whose span is not mapped to the mirrored conductor pair.</para>
/// </summary>
public partial class LayoutEditorViewModel
{
    /// <summary>Mirror's availability, narrowed by Flip's own refusals so the menu item and the toolbar
    /// button say why before the click.</summary>
    public LayoutCommandAvailability FlipAvailability
    {
        get
        {
            var basic = MirrorAvailability;
            if (!basic.CanExecute) return new LayoutCommandAvailability(false, "Select a part, geometry or a wire to flip.");
            return FlipRefusal() is { } reason ? new LayoutCommandAvailability(false, reason) : basic;
        }
    }

    /// <summary>The context menu's wording: "Flip to Bottom" / "Flip to Top" for one part on its own, "Flip"
    /// for anything else.</summary>
    public string FlipHeader =>
        _selectedInstanceIndices.Count == 1 && RotatableSelectedIndices.Count == 0 && SelectedWireIndices.Count == 0
        && _selectedInstanceIndices[0] < Model.Instances.Count
            ? Model.Instances[_selectedInstanceIndices[0]].Side == PlacementSide.Top ? "Flip to Bottom" : "Flip to Top"
            : "Flip";

    /// <summary>
    /// Flips the selection to the other face (<c>B</c>): one undo step, pivot the selection's combined
    /// centre. Returns false, says why and moves nothing when it is refused.
    /// </summary>
    public bool FlipSelection()
    {
        if (!MirrorAvailability.CanExecute) return false;
        if (FlipRefusal() is { } reason) { ReportError("Flip: " + reason); return false; }

        var instances = _selectedInstanceIndices.ToList();
        Reflect(horizontal: true, "Flip", SurfaceRoles.For(Technology!));
        ReportFlipOmissions(instances);
        return true;
    }

    /// <summary>
    /// The Properties panel's Side (R-bsp2-5): every selected placement not already on <paramref name="side"/>
    /// is flipped about its OWN origin — the part stays where it is and turns over in place. The same
    /// refusal as <see cref="FlipSelection"/>; one undo step.
    /// </summary>
    public bool SetSelectedInstancesSide(PlacementSide side)
    {
        if (TechnologyRefusal() is { } reason) { ReportError("Flip: " + reason); return false; }

        var edits = new List<(int Index, LayoutInstance Before, LayoutInstance After)>();
        foreach (int i in _selectedInstanceIndices)
        {
            if (i < 0 || i >= Model.Instances.Count) continue;
            var before = Model.Instances[i];
            if (before.Side == side) continue;
            // A reflection about the vertical axis through the part's own origin — Mirror's bookkeeping
            // (Rotate.cs): the rotation negates and the mirror flag toggles; the position is the pivot.
            var after = LayoutGeometry.Clone(before);
            after.RotationDegrees = -before.RotationDegrees;
            after.MirrorX = !before.MirrorX;
            after.Side = side;
            edits.Add((i, before, after));
        }
        if (edits.Count == 0) return false;

        Execute(new Commands.Layout.ReplaceInstancesCommand(
            Model, edits, side == PlacementSide.Bottom ? "Flip to Bottom" : "Flip to Top"));
        ReportFlipOmissions(edits.Select(e => e.Index).ToList());
        RebuildOverlay();
        return true;
    }

    /// <summary>Why nothing can be flipped on this layout's technology at all, or null.</summary>
    private string? TechnologyRefusal()
    {
        if (Technology is not { } tech)
            return "this layout has no technology, so there is no bottom surface to flip to.";
        return SurfaceRoles.For(tech).HasBottomCopper
            ? null
            : $"'{tech.Name}' has no bottom copper, so there is no bottom surface to put a part on.";
    }

    /// <summary>Why the current selection cannot be flipped, or null.</summary>
    private string? FlipRefusal()
    {
        if (TechnologyRefusal() is { } reason) return reason;
        var tech = Technology!;
        var roles = SurfaceRoles.For(tech);

        foreach (int i in RotatableSelectedIndices)
        {
            var shape = Model.Shapes[i];
            if (shape is ViaShape via)
            {
                if (ViaSpanResolver.Resolve(via.Layer, tech) is { } span && !ViaSpanResolver.IsThrough(span, tech))
                    return $"a blind or buried via ({span.Top.Name} to {span.Bottom.Name}) is selected, and Flip does not " +
                           "move a via to the mirrored pair of conductors. Deselect it to flip the rest.";
                continue;
            }

            if (roles.OmittedRole(shape.Layer) is not { } role) continue;
            string word = SurfaceRoles.Describe(role);
            string missing = roles.Top[role] == shape.Layer ? "bottom" : "top";
            string layer = tech.Layers.FirstOrDefault(l => l.Key == shape.Layer)?.Name ?? shape.Layer.ToString();
            return $"a {KindWord(shape)} on '{layer}' is selected, and '{tech.Name}' has no {missing} {word} layer to " +
                   "move it to, so it would be lost. Deselect it to flip the rest.";
        }
        return null;
    }

    /// <summary>
    /// R-bsp2-3: a placement now on the bottom whose cell draws on a role the technology has on the top
    /// only is not refused — that artwork is simply not drawn while it is on the bottom — and is said once
    /// per placement and role. Its designator counts as silkscreen. Flipping back restores it, because the
    /// cell never changed.
    /// </summary>
    private void ReportFlipOmissions(IReadOnlyList<int> instanceIndices)
    {
        if (Technology is not { } tech) return;
        var roles = SurfaceRoles.For(tech);
        foreach (int i in instanceIndices)
        {
            if (i < 0 || i >= Model.Instances.Count) continue;
            var inst = Model.Instances[i];
            if (inst.Side != PlacementSide.Bottom) continue;

            var omitted = new List<SurfaceRole>(
                CellLayoutResolver.Resolve(inst, InstanceBaseDir, tech).OmittedRoles);
            if (inst.DesignatorShown && inst.DisplayRefDes is { Length: > 0 }
                && roles.Top.Silkscreen is not null && roles.Bottom.Silkscreen is null
                && !omitted.Contains(SurfaceRole.Silkscreen))
                omitted.Add(SurfaceRole.Silkscreen);

            string who = inst.DisplayRefDes is { Length: > 0 } rd ? rd : $"The part '{inst.CellRef}'";
            foreach (var role in omitted)
            {
                string word = SurfaceRoles.Describe(role);
                _messageSink?.Info($"{who}: no bottom {word} in this technology, so its {word} is not drawn while it is on the bottom.");
            }
        }
    }

    private static string KindWord(LayoutShape shape)
        => shape.GetType().Name.Replace("Shape", "", StringComparison.Ordinal).ToLowerInvariant();
}
