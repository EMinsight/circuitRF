using System.Linq;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Ui.Layout;

// Cut Clearances into Planes (brief-dfr26-7 R-dfr26-7-2): a placed TESTPAD's declared cut-out, cut into the DRAWN
// shapes of each conductor its ClearLayers names. The plan is TestPadClearances' (src/Design); this applies it as ONE
// undo entry and posts its report.
public sealed partial class LayoutEditorViewModel
{
    /// <summary>The one selected instance, when it is a placed TESTPAD — what decides whether the context menu offers
    /// Cut Clearances into Planes at all.</summary>
    public bool SelectionIsOneTestPad
        => _selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 1
           && TestPadClearances.IsTestPad(Model, Model.Instances[_selectedInstanceIndices[0]]);

    /// <summary>Cuts the selected test pad's clearances into the planes. A refusal (nothing to cut) is posted as
    /// information and changes nothing.</summary>
    public void CutTestPadClearances()
    {
        if (!SelectionIsOneTestPad) return;
        var plan = TestPadClearances.For(Model, Model.Instances[_selectedInstanceIndices[0]], Technology);
        if (plan.Refusal is not null || plan.Indices.Count == 0)
        {
            _messageSink?.Info(plan.Report);
            return;
        }
        var removed = plan.Indices.OrderBy(i => i).Select(i => (i, Model.Shapes[i])).ToList();
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, plan.Shapes, "Cut Clearances into Planes"));
        _messageSink?.Success(plan.Report);
    }
}
