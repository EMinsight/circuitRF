// brief-wbond-wire-temperature G3 — the schematic wBond's one-dimensional wire temperature (src/WBond/Thermal) against this project's
// conductive balance on the smallest problem it already builds with a wire between two fixed-temperature contacts and no side
// coupling: the W1 bench (two small pad boxes, no Gmsh). And the constants the 1D solve restates, held equal to their originals.

using System.Reflection;
using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Nonlinear;
using CircuitRF.WBond;
using CircuitRF.WBond.Thermal;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class WireTemperatureCrossCheckTests(ITestOutputHelper output)
{
    private const double D = 2.54e-5, L = 1e-3, Ta = 125, Tb = 85, Idc = 1.2, F = 2e9, Peak = 0.8;

    [Fact]
    public void G3_OneGoldWire_MatchesConductiveBalance()
    {
        var gold = WireMaterials.Gold;
        var k = ThermalConductivity.Varying(gold.ThermalKAt(20).K, gold.ThermalKAt);
        var sigma = ElectricalConductivity.Varying(gold.Sigma20, gold.SigmaWithSlopeAt, gold.SigmaVsTemp![^1].TempC);
        var p = ElectrothermalGateTests.Bench(D, L, Ta, Tb, Idc, k, sigma);
        var w = p.Wires[0];
        var rf = new ThermalWire
        {
            Name = w.Name, Points = w.Points, S = w.S, Area = w.Area, Diameter = w.Diameter, K = w.K, Sigma = w.Sigma, Contacts = w.Contacts,
            OnPad = w.OnPad, Harmonics = [new WireHarmonic("h1", F, Peak)],
            AcResistance = (f, s) => InternalImpedance.ResistanceWithSigmaSlope(f, D / 2, s),
        };
        var three = ConductiveBalance.Solve(new ElectrothermalProblem { Thermal = p.Thermal, Wires = [rf], Sigma = p.Sigma, Currents = p.Currents },
                                            new ThermalSolveOptions { Solver = ThermalSolverKind.Direct });
        Assert.True(three.Converged, three.Failure);
        var chain = three.WireTemperature[0];
        double threeMax = Enumerable.Range(2, chain.Length - 4).Max(j => chain[j]);   // heel to heel: the span

        var one = WireConductiveBalance.Solve([new ThermalWireSpec(L, D, gold)], Ta, Tb, new WireArrayDrive(Idc, [F], [[Peak]]));
        Assert.True(one.Converged, one.Failure);
        output.WriteLine($"1D {one.MaxC:F4} °C, conductive balance {threeMax:F4} °C (heels {chain[2]:F3} / {chain[^3]:F3} °C)");
        Assert.True(one.MaxC > Ta + 50, $"a hot wire: {one.MaxC:F1} °C");
        Assert.Equal(threeMax, one.MaxC, 0.1);
    }

    /// <summary>R-wbt-3d — the constants the 1D solve restates (src/WBond cannot reference this project) are its originals.</summary>
    [Fact]
    public void TheRestatedConstants_AreConductiveBalances()
    {
        Assert.Equal(ConductiveBalance.Absurd, WireConductiveBalance.Absurd);
        Assert.Equal(ConductiveBalance.StallStep, WireConductiveBalance.StallStep);
        Assert.Equal(Continuation.Bracket, WireConductiveBalance.Bracket);
        var defaults = new ThermalSolveOptions();
        Assert.Equal(WireConductiveBalance.NewtonTolerance, defaults.NewtonTolerance);
        Assert.Equal(WireConductiveBalance.NewtonMaxIterations, defaults.NewtonMaxIterations);
        var floor = typeof(ThermalSolver).GetField("SpanFloorK", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();
        Assert.Equal(WireConductiveBalance.SpanFloorK, (double)floor!);
    }
}
