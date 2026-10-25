using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The test pad's side of the substrate seam (brief-dfr26-7 R-dfr26-7-4): what <see cref="ViaSubstrateInjection"/>
/// is to a via. At extraction a <c>TESTPAD</c> on a technology has the dielectric its pad faces, its via's length
/// and the planes the via passes resolved from the stackup (<see cref="SubstrateResolver.ResolveTestPad"/>) and
/// injected as plain SI overrides.
///
/// <para><b>What the user states</b> stays theirs: <c>D</c>, <c>Drill</c> and <c>ClearD</c> reach the engine as
/// typed, expressions and units included, so a swept diameter sweeps. The layer rows, <c>Feed</c>,
/// <c>FeedW</c>, <c>Centre</c> and <c>PortNum</c> are resolution or artwork inputs and never reach the
/// <c>.cnl</c>; an empty row never does either (<c>Key=</c> is the empty-value trap).</para>
/// </summary>
public static class TestPadInjection
{
    public const string PadLayerParam = "PadLayer";
    public const string ViaFromParam = "ViaFrom";
    public const string ClearLayersParam = "ClearLayers";

    /// <summary>The rows the engine reads as typed — every other TESTPAD row is consumed here.</summary>
    public static readonly IReadOnlySet<string> EngineParams = new HashSet<string>(StringComparer.Ordinal) { "D", "Drill", "ClearD" };

    /// <summary>The overrides to append, the sentences to post, and the resolved stack for a readout.</summary>
    public sealed record Injection(IReadOnlyList<ParameterAssignment> Overrides, IReadOnlyList<string> Messages, ResolvedTestPad? Stack);

    public static Injection Build(Technology? technology, IEnumerable<EditableParameter> parameters)
    {
        var list = parameters as IReadOnlyList<EditableParameter> ?? parameters.ToList();
        string? Text(string name) => list.FirstOrDefault(p => p.Name == name)?.Expression is { } e && e.Trim().Length > 0
            ? e.Trim() : null;

        bool viaFed = (Text("Feed") ?? TestPadPCell.ViaFeed).Equals(TestPadPCell.ViaFeed, StringComparison.OrdinalIgnoreCase);
        var clear = TestPadPCell.ParseClearLayers(Text(ClearLayersParam));
        var (stack, failure, warnings) = SubstrateResolver.ResolveTestPad(technology, Text(PadLayerParam), Text(ViaFromParam), viaFed, clear);
        var messages = new List<string>(warnings);
        var overrides = new List<ParameterAssignment>();
        void Add(string name, double value) => overrides.Add(new ParameterAssignment(name, value.ToString("R", CultureInfo.InvariantCulture)));

        if (stack is null)
        {
            messages.Add($"{failure?.Reason ?? "no stackup resolved"} — the test pad uses its standalone defaults " +
                         $"({ComponentModelFactory.DefaultSubstrateHMeters * 1e3:G4} mm of εr {ComponentModelFactory.DefaultSubstrateEpsR:G3} " +
                         "to one plane, and no via)");
            return new Injection(overrides, messages, null);
        }

        // The planes the pad faces, at most one a side. A pad facing none has no capacitance, which is worth saying.
        if (stack.Faces.Count == 0)
        {
            Add("Hpad", 0);
            messages.Add($"the pad on '{stack.Pad.Name}' faces no conductor on either side outside ClearLayers, so it is " +
                         "modelled with no capacitance");
        }
        for (int i = 0; i < stack.Faces.Count && i < 2; i++)
        {
            string suffix = i == 0 ? "" : "2";
            Add($"Hpad{suffix}", stack.Faces[i].HeightMeters);
            Add($"Erpad{suffix}", stack.Faces[i].EpsR);
        }

        if (stack.Via is { } via)
        {
            Add("Hvia", via.LengthMeters);
            Add("ErVia", via.MaxRelativePermittivity);
            if (Text("Drill") is null)
            {
                if (via.DrillMeters is { } dr) Add("Drill", dr);
                else messages.Add($"A default is used where the technology states none: Drill = {ComponentModelFactory.DefaultViaDrillMeters * 1e3:0.###} mm. " +
                                  "Set it on the component to use your own.");
            }
            double antipad = (via.PadMeters ?? ComponentModelFactory.DefaultViaPadMeters) + ComponentModelFactory.DefaultViaAntipadRingMeters;
            if (stack.Planes.Count > 0)
            {
                Add("Planes", stack.Planes.Count);
                for (int i = 0; i < stack.Planes.Count; i++)
                {
                    var p = stack.Planes[i];
                    Add($"Zp{i + 1}", p.PositionMeters);
                    Add($"Tp{i + 1}", p.LengthMeters);
                    Add($"Erp{i + 1}", p.EpsR);
                    if (p.Cleared) Add($"Clp{i + 1}", 1);
                    else Add($"Bp{i + 1}", antipad);
                }
            }
        }
        return new Injection(overrides, messages, stack);
    }
}
