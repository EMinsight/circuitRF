using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Draw Ground Pour (designer feedback round 10): the ground plane this layout's microstrip lines return through,
/// drawn as copper. The geometry is <see cref="GroundPourPlanner"/>'s, computed from where the lines are NOW; this file
/// is the undo and the Messages. Reached from Design ▸ Draw Ground Pour and from the button on the message Update
/// Layout from Schematic posts when it places lines over an empty ground layer.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>The pours <see cref="DrawGroundPour"/> would draw now — empty when there are none to draw.</summary>
    internal IReadOnlyList<GroundPour> PlanGroundPours() => GroundPourPlanner.Plan(Model, Technology, InstanceBaseDir);

    /// <summary>
    /// Draws one pour per ground conductor a placed microstrip returns through and that carries no artwork yet, as
    /// ONE undo entry. Says what it drew, the clearance it cut round each via, and what it could not see; says why
    /// when there is nothing to draw.
    /// </summary>
    public void DrawGroundPour()
    {
        if (Technology is null)
        {
            ReportError("Draw Ground Pour: this layout has no technology, so there is no stackup to name a ground plane.");
            return;
        }
        var pours = PlanGroundPours();
        if (pours.Count == 0)
        {
            _messageSink?.Info("Draw Ground Pour: nothing to draw. It draws the plane placed microstrip lines " +
                               "(MLIN, MBEND, MTEE, MCROSS, MTAPER, MKLOPF) return through, and only on a ground " +
                               "layer that has no artwork yet — a plane already drawn is left as it is.");
            return;
        }

        IUiCommand? chain = null;
        foreach (var shape in pours.SelectMany(p => p.Shapes))
        {
            var add = new AddShapeCommand(Model, shape);
            chain = chain is null ? add : new CompositeCommand(chain, add);
        }
        if (chain is not null) Execute(chain);

        string unit = LayoutUnits.Suffix(DisplayUnit);
        string clearance = LayoutUnits.Format((long)System.Math.Round(GroundPourPlanner.ViaClearanceMicrons * Model.DbuPerMicron),
                                              DisplayUnit, Model.DbuPerMicron);
        foreach (var p in pours)
        {
            string margin = LayoutUnits.Format(p.MarginDbu, DisplayUnit, Model.DbuPerMicron);
            string vias = p.ViaClearances == 0
                ? "No via passes through it."
                : $"{p.ViaClearances} via(s) pass through it, each with a clearance of {clearance} {unit} beyond its " +
                  "pad — a default, since the technology states no antipad; change it by editing the pour.";
            _messageSink?.Success(
                $"Draw Ground Pour: drew the plane on '{p.Ground.Name}' under {p.Lines} microstrip line(s), reaching " +
                $"{margin} {unit} ({GroundPourPlanner.MarginHeights:0} substrate heights) beyond them. {vias} " +
                (p.ViasJoined == 0 ? "" :
                    $"{p.ViasJoined} via(s) end on '{p.Ground.Name}' and are now joined to the plane; a signal via " +
                    "among them needs a clearance cut by hand. ") +
                "A via inside a placed cell is not seen.");
        }
    }
}
