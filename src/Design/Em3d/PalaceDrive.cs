// brief-em3d-100 R-em3d100-0 — the drive a Palace driven field is at, settled from Palace's own documentation (the pinned
// changeset, 0dc74cd, the one every committed fixture was written by):
//
//   docs/src/reference.md:200-202   "We normalize port fields so the total power flow is |P^inc| = 1 W … Palace uses peak
//                                    phasors for this convention, so P^inc is twice the time-averaged power for a propagating
//                                    mode", with P^inc = V^inc·[I^inc]* = ∫ n·(E^inc × H^inc*) dS — no ½.
//   docs/src/guide/postprocessing.md:64-65   "the values output are the complex-valued peak voltages and currents, computed
//                                    from the field phasors."
//   palace/models/lumpedportoperator.cpp:136-141   GetExcitationPower: "the power integrated over the port is 1:
//                                    ∫ (E_inc x H_inc) ⋅ n dS = 1".
//
// So the field Palace writes is a PEAK phasor, and its "1 W" is |V_inc|²/R on peak values: the time-averaged incident power,
// ½·|V_inc|²/R in circuitRF's peak convention (½·Re(V·I*), as HB, the planar kernel and the far-field stage use it), is 0.5 W.
// The committed fixture testdata/em3d/f0/B-via/palace agrees: V_inc = √50 V and I_inc = 1/√50 A at R = 50 Ω, a product of 1.
//
// Every drive in circuitRF reads THIS constant and never repeats the arithmetic. src/Design/RESOLVED.md §brief-em3d-100.

namespace CircuitRF.Design.Em3d;

/// <summary>brief-em3d-100 — the incident power of the field Palace writes, in circuitRF's convention.</summary>
public static class PalaceDrive
{
    /// <summary>The time-averaged incident power, W, of the field Palace writes (peak phasors, ½·Re(V·I*)): 0.5 W.</summary>
    public const double IncidentPowerW = 0.5;

    /// <summary>The peak incident voltage at a port of reference resistance <paramref name="r"/> Ω carrying
    /// <see cref="IncidentPowerW"/>: √(2·R·P), which is √R — Palace's own <c>V_inc</c>.</summary>
    public static double IncidentPeakVolts(double r) => Math.Sqrt(2 * r * IncidentPowerW);
}
