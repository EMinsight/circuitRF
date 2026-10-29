// ================================================================
//  EnvelopeModeRoundTripTests.cs — Envelope=single|lattice survives the
//  netlist and the schematic, and auto writes nothing (old files unchanged).
// ================================================================

using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class EnvelopeModeRoundTripTests
{
    private static EnvelopeAnalysis ReadEnv(string directive) =>
        new CnlReader().Read("R1 a 0 R=50\n" + directive + "\n", "tb", null).TestBench.Analyses.OfType<EnvelopeAnalysis>().Single();

    [Theory]
    [InlineData("single")]
    [InlineData("lattice")]
    public void ANonDefaultMode_RoundTripsThroughTheNetlistAndTheSchematic(string mode)
    {
        var env = ReadEnv($"analysis ENV1 type=env Stop=10e-6 Envelope={mode}");
        Assert.Equal(mode, env.Envelope);

        var tb = new TestBench("tb");
        tb.Analyses.Add(env);
        string cnl = CnlWriter.Write(tb);
        Assert.Contains($"Envelope={mode}", cnl);

        var back = AnalysisSerialization.FromDto(AnalysisSerialization.ToDto(env)) as EnvelopeAnalysis;
        Assert.Equal(mode, back!.Envelope);
    }

    [Fact]
    public void Auto_IsTheDefault_AndIsNotWritten()
    {
        var env = ReadEnv("analysis ENV1 type=env Stop=10e-6");
        Assert.Equal("auto", env.Envelope);
        var tb = new TestBench("tb");
        tb.Analyses.Add(env);
        Assert.DoesNotContain("Envelope=", CnlWriter.Write(tb));
        Assert.Null(AnalysisSerialization.ToDto(env).EnvMode);
    }

    [Fact]
    public void Zmeas_IsReadForTheRunsWarning_AndNeitherWriterEmitsIt()
    {
        // Brief ENV-MEAS-1 gate 9: the key drops out the next time a file is saved.
        var env = ReadEnv("analysis ENV1 type=env Stop=10e-6 Zmeas=25");
        Assert.Equal("25", env.ZmeasExpr);
        var tb = new TestBench("tb");
        tb.Analyses.Add(env);
        Assert.DoesNotContain("Zmeas", CnlWriter.Write(tb));
        Assert.Null(AnalysisSerialization.ToDto(env).EnvZmeas);
        var fromCsch = (EnvelopeAnalysis)AnalysisSerialization.FromDto(new CschAnalysis { Type = "env", Name = "ENV1", EnvZmeas = "25" })!;
        Assert.Equal("25", fromCsch.ZmeasExpr);
    }
}
