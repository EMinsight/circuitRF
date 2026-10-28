// Hands CircuitRF.Design the values WBondPlacement could not bring across the UI firewall.
//
// WBondPlacement moved to CircuitRF.Design with the schematic document model
// (brief-automation-2-schematic-below-the-firewall.md), so `circuitrf` can place a wBond into a
// schematic with no Avalonia in the process. Every decision it makes is a document decision and went
// with it — except what a NEW wire is made with (its feet's z, diameter, metal and point count), which
// is a per-installation preference (Settings ▸ Wirebonds) and therefore app state. That half stays here,
// and installs itself.
//
// A MODULE INITIALIZER, for the same reason RenderTypefaceInstaller is one: it runs before any type in
// this assembly is touched, so there is no startup ordering to get wrong and no second entry point
// (the standalone harmonicaRF and wBond binaries are this same assembly with a different Main) to
// remember. Unset, WBondPlacement falls back to the shipped wire (4 mil z, 1 mil gold, 7 points), which
// is what a headless process gets and what it always did before the preferences existed.

using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.WBond;

internal static class UiWBondDefaultsInstaller
{
    [ModuleInitializer]
    internal static void Install() => WBondPlacement.NewWireSettings = static () => WBondDefaults.NewWire;
}
