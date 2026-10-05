using CircuitRF.Diagnostics;

namespace CircuitRF.WBond.Thermal;

/// <summary>
/// brief-wbond-wire-temperature — the wire-temperature solve's user-facing sentences, as coded diagnostics (the pattern
/// <c>src/Design/Layout/Em/EmDiagnostics.cs</c> establishes): an id to group and filter by, typed arguments and the English
/// template. The ids are the durable part; reword a template freely.
/// </summary>
public static class WireThermalDiagnostics
{
    /// <summary>R-wbt-1b — a solved-temperature instance whose wire metal states no thermal conductivity.</summary>
    public static Diagnostic NoThermalConductivity(string instance, string metal) => Diagnostic.Create(
        "wbond.thermal.no-thermal-k",
        DiagnosticSeverity.Error,
        "wBond '{instance}' solves its wire temperature, and its wire material '{metal}' states no thermal conductivity. " +
        "State ThermalK for '{metal}' in the Materials editor, or check Temp to fix the temperature.",
        ("instance", instance), ("metal", metal));
}
