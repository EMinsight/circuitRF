using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The MMIC passives' side of the substrate seam (brief-agent-authoring-overview.md AA-1): what
/// <see cref="MicrostripSubstrateInjection"/> is to MLIN. At extraction, a <c>MIMCAP</c>, <c>SPIRAL</c>,
/// <c>TFR</c> or <c>AIRBRIDGE</c> on a technology has its materials resolved from the stackup
/// (<see cref="MmicStackResolver"/>, the same resolution the artwork uses) and injected as plain SI
/// overrides, so none of them is typed twice.
///
/// <para><b>What the user states</b> is geometry only — W and L, the turns, the span. Everything a
/// process decides (the film's permittivity and thickness, the plates' metal, the sheet resistance,
/// the bridge height, the substrate under it all) comes from the technology. With no technology the
/// model takes its standalone defaults, which are the shipped GaAs process's; inside a technology that
/// cannot supply what a part needs, the run says which piece is missing and uses the same defaults.</para>
/// </summary>
public static class MmicPassiveInjection
{
    public static bool IsMmicKind(SymbolKind kind)
        => kind is SymbolKind.MimCap or SymbolKind.Spiral or SymbolKind.Tfr or SymbolKind.Airbridge;

    /// <summary>The parameters the technology supplies for <paramref name="kind"/> — the ones a line
    /// that states all of them has stated its own process with.</summary>
    public static IReadOnlyList<string> InjectedNames(SymbolKind kind) => kind switch
    {
        SymbolKind.MimCap    => ["Er", "Td", "TanD", "SigmaTop", "Ttop", "SigmaBot", "Tbot", "H", "ErSub"],
        SymbolKind.Spiral    => ["Sigma", "T", "H", "ErSub", "Hb", "Erb"],
        SymbolKind.Tfr       => ["Rs", "H", "ErSub"],
        SymbolKind.Airbridge => ["Sigma", "T", "Hb", "Erb"],
        _                    => [],
    };

    private static MmicPart PartOf(SymbolKind kind) => kind switch
    {
        SymbolKind.MimCap => MmicPart.MimCap,
        SymbolKind.Spiral => MmicPart.Spiral,
        SymbolKind.Tfr    => MmicPart.Tfr,
        _                 => MmicPart.Airbridge,
    };

    /// <summary>
    /// The overrides for one instance, and the sentence to post when the technology could not supply
    /// them. No technology at all is NOT a message — the standalone default is what such a line has
    /// always meant, exactly as for a microstrip.
    /// </summary>
    public static (IReadOnlyList<ParameterAssignment> Overrides, string? Warning) Build(Technology? technology, SymbolKind kind)
    {
        if (technology is null || !IsMmicKind(kind)) return ([], null);

        var stack = MmicStackResolver.Resolve(technology);
        if (MmicStackResolver.Why(stack, PartOf(kind), technology.Name) is { } why)
            return ([], $"{why}; the {ComponentTypeRegistry.EngineReference(kind)} uses its standalone defaults " +
                        "(the shipped GaAs process's).");

        var sub = stack.Substrate!;
        var o = new List<ParameterAssignment>();
        void Add(string name, double value) => o.Add(new ParameterAssignment(name, value.ToString("R", CultureInfo.InvariantCulture)));

        switch (kind)
        {
            case SymbolKind.MimCap:
                var d = stack.CapDielectric!;
                Add("Er",       d.Epsr);
                Add("Td",       MmicStackResolver.Metres(d.ThicknessDbu));
                Add("TanD",     d.TanD);
                Add("SigmaTop", stack.TopPlate!.SigmaSm);
                Add("Ttop",     MmicStackResolver.Metres(stack.TopPlate.ThicknessDbu));
                Add("SigmaBot", stack.BaseMetal!.SigmaSm);
                Add("Tbot",     MmicStackResolver.Metres(stack.BaseMetal.ThicknessDbu));
                Add("H",        sub.HeightMeters);
                Add("ErSub",    sub.RelativePermittivity);
                break;
            case SymbolKind.Spiral:
                Add("Sigma", stack.BaseMetal!.SigmaSm);
                Add("T",     MmicStackResolver.Metres(stack.BaseMetal.ThicknessDbu));
                Add("H",     sub.HeightMeters);
                Add("ErSub", sub.RelativePermittivity);
                Add("Hb",    stack.BridgeHeightMeters);
                Add("Erb",   stack.BridgeEpsR);
                break;
            case SymbolKind.Tfr:
                Add("Rs",    stack.ResistorLayer!.SheetResistanceOhmPerSq!.Value);
                Add("H",     sub.HeightMeters);
                Add("ErSub", sub.RelativePermittivity);
                break;
            case SymbolKind.Airbridge:
                Add("Sigma", stack.BridgeMetal!.SigmaSm);
                Add("T",     MmicStackResolver.Metres(stack.BridgeMetal.ThicknessDbu));
                Add("Hb",    stack.BridgeHeightMeters);
                Add("Erb",   stack.BridgeEpsR);
                break;
        }
        return (o, null);
    }
}
