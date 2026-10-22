using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Engine.Tests.Composed;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-26 R-mc26-1, R-mc26-2: the writer's text-level gates, which need nothing installed — the same model
/// writes the same bytes, and every refusal says its sentence, all of them in one message.
/// </summary>
public sealed class CmodelVerilogAWriterTests
{
    private static readonly VerilogAOptions Named = new() { SourceName = "gan.cmodel" };

    [Fact]
    public void TheSameModel_WritesTheSameBytes_TwiceAndAcrossAReadOfTheCmodel()
    {
        foreach (var model in (ComposedModel[])[ComposedTestModels.Model(), VaTestModels.AnnModel(AnnActivation.Tanh), VaTestModels.Drift()])
        {
            string a = CmodelVerilogA.Write(model, Named), b = CmodelVerilogA.Write(model, Named);
            Assert.Equal(a, b);

            // Write → read → write of the .cmodel, then the export of the file itself, whose header hashes model.json as
            // the file holds it.
            string path = ComposedTestModels.WriteTemp(model, "gan.cmodel");
            Assert.Equal(a, CmodelVerilogA.Write(CmodelFile.Read(path), Named));
            string va = Path.Combine(Path.GetDirectoryName(path)!, "gan.va");
            CmodelVerilogA.Export(path, va);
            Assert.Equal(a, File.ReadAllText(va, Encoding.UTF8));

            Assert.DoesNotContain('\r', a);
            Assert.True(a.All(c => c is '\n' or (>= ' ' and <= '~')), "the module is printable ASCII");
            Assert.Contains("`include \"disciplines.vams\"\n`include \"constants.vams\"\n", a);
            Assert.Equal(2, a.Split("`include").Length - 1);
            Assert.Contains("module gan(g, d, s);", a);
        }
    }

    [Fact]
    public void TheHeader_ListsEveryParameter_WithItsUnitDefaultAndMeaning()
    {
        string va = CmodelVerilogA.Write(ComposedTestModels.Model(), Named);
        string header = va[..va.IndexOf("`include", StringComparison.Ordinal)];
        Assert.Contains("SHA-256 ", header);
        Assert.Contains("Model created 2026-10-19T00:00:00Z by circuitRF test", header);
        foreach (string name in (string[])["N", "Temp", "Cpgi", "rg", "M_feedback", "Id_beta", "Ig_Isg", "Qd_Cy"])
        {
            Assert.Contains($"//   {name} ", header);
            Assert.Contains($"parameter {(name == "N" ? "integer" : "real")} {name} = ", va);
        }
        Assert.Contains("scaled x N0/N (perFingerInverse)", header);
        Assert.Contains("parameter real rg = 5.2 from (0:inf);", va);
        Assert.Contains("parameter integer N = 4 from [1:inf);", va);
        Assert.Contains("parameter real Temp = -273.15 from [-273.15:inf);", va);
        // Doubles in shortest round-trip form with a lower-case exponent.
        Assert.Contains("parameter real Cpdgi = 1e-15;", va);
        Assert.DoesNotContain(" m;", va);
    }

    [Theory]
    [InlineData("gan_4x125", "gan_4x125")]
    [InlineData("gan 4x125-v2", "gan_4x125_v2")]
    [InlineData("4x125", "m_4x125")]
    [InlineData("real", "m_real")]
    public void TheModuleName_IsTheFileNameMadeLegal(string stem, string module)
        => Assert.Contains($"module {module}(g, d, s);",
                           CmodelVerilogA.Write(ComposedTestModels.Model(), new VerilogAOptions { SourceName = stem + ".cmodel" }));

    // ── The refusals (R-mc26-2) ───────────────────────────────────────────────

    [Fact]
    public void EveryTable_IsRefusedByName_InOneMessage()
    {
        var m = RuntimeTestModels.MixedModel();
        m = m with
        {
            Core = m.Core with { IdRF = Constituent.Table("Id", VaTestModels.Inputs, RuntimeTestModels.Pentagon), Dispersion = new ModelDispersion(1e-6) },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double>(m.Extrapolation!.Gx) { ["IdRF"] = 2.0 }),
        };
        var ex = Assert.Throws<VerilogAExportException>(() => CmodelVerilogA.Write(m, Named));
        foreach (string name in (string[])["Id", "Qg", "IdRF"])
            Assert.Contains($"Core.Constituents.{name}.Method: 'Table' cannot be written as Verilog-A; refit {name} as an ANN or an equation to export it.",
                            ex.Message);
        Assert.Equal("Core.Constituents.Id.Method", ex.Field);
        Assert.DoesNotContain("Core.Constituents.Qd.Method", ex.Message);
    }

    [Fact]
    public void ANetworkElementWithNoTranslation_IsRefusedByNameAndType()
    {
        var m = WithNetwork(ComposedTestModels.GanVariables + ComposedTestModels.GanElements + "SRL:SR1 n1 n2 R=1 L=1e-12\n");
        var ex = Assert.Throws<VerilogAExportException>(() => CmodelVerilogA.Write(m, Named));
        Assert.Equal("Network.Netlist: element 'SR1' (SRL) cannot be written as Verilog-A; the export writes R, C, L, Mutual and Short.",
                     ex.Message);
    }

    /// <summary>brief-modelcomposer-27: the shipped example — traps, self-heating, ANNs — exports, and every parameter the module
    /// declares is in the header, the switches in <see cref="ComposedSwitches.Names"/>' order with its defaults. A switch
    /// added to circuitRF without one here fails this.</summary>
    [Fact]
    public void EveryModuleParameter_IsInTheHeader_AndTheSwitchesAreComposedSwitchesNamesInOrder()
    {
        string va = CmodelVerilogA.Write(CmodelFile.Read(FixturePaths.Require("examples/Model Composer/synthetic-gan/gan_4x125.cmodel")), Named);
        string header = va[..va.IndexOf("`include", StringComparison.Ordinal)];
        var declared = va.Split('\n').Where(l => l.StartsWith("  parameter ", StringComparison.Ordinal))
                         .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]).ToList();
        foreach (string name in declared) Assert.Contains($"//   {name} ", header);
        foreach (string name in (string[])["Rth", "Cth", "VgsQ", "VdsQ", "TjQ", "TauCapt2", "TauEmit2", "Xref", "Vc", "Tref"])
            Assert.Contains(name, declared);

        var switches = declared.Where(ComposedSwitches.IsSwitch).ToList();
        Assert.Equal(ComposedSwitches.Names, switches);
        foreach (string s in ComposedSwitches.Names)
            Assert.Contains($"parameter integer {s} = {(ComposedSwitches.DefaultOf(s) ? 1 : 0)} from [0:1];", va);
        Assert.Contains("For this model GateLag and Dispersion do nothing.", header);
        Assert.Contains("module gan(g, d, s, t);", va);
        Assert.Contains("parameter real VgsQ = -1e+300;", va);
    }

    /// <summary>A model with no self-heating still declares the instance's Rth and Cth, refusing a value by range, and every
    /// switch.</summary>
    [Fact]
    public void AModelWithoutTheParts_DeclaresEverySwitch_AndRefusesRthByRange()
    {
        string va = CmodelVerilogA.Write(ComposedTestModels.Model(), Named);
        Assert.Contains("parameter real Rth = 0.0 from [0:0];", va);
        Assert.Contains("parameter real Cth = 0.0 from [0:0];", va);
        foreach (string s in ComposedSwitches.Names) Assert.Contains($"parameter integer {s} = ", va);
        Assert.Contains("For this model GateLag, DrainLag, SelfHeating and Dispersion do nothing.", va);
    }

    [Fact]
    public void ATrapModelWithNoWriter_IsRefusedByName()
    {
        string name = "NoWriter" + Guid.NewGuid().ToString("N")[..8];
        TrapModels.Register(new UnwrittenTraps(name));
        var m = ComposedTestModels.Model() with
        {
            Traps = new ModelTraps { Model = name, States = ["phi1"] },
            Core = ComposedTestModels.Model().Core with
            {
                Qd = Constituent.Equation(ComposedTestModels.QdEq + "+0*phi1", ["vgs", "vds", "phi1"], ComposedTestModels.QdParams),
            },
        };
        var ex = Assert.Throws<VerilogAExportException>(() => CmodelVerilogA.Write(m, Named));
        Assert.Equal($"Traps.Model: the trap model '{name}' has no Verilog-A writer, so a model with traps cannot be exported.", ex.Message);
    }

    /// <summary>A trap model a developer registered without giving it a Verilog-A writer.</summary>
    private sealed class UnwrittenTraps(string name) : TrapModelDefinition
    {
        public override string Name => name;
        public override IReadOnlyList<string> States { get; } = ["phi1"];
        public override IReadOnlyList<TrapParameter> Parameters { get; } = [];
        public override TrapDynamics Build(ModelTraps traps, string label) => new Relax(traps.States);

        private sealed class Relax(IReadOnlyList<string> states) : RateTrapDynamics(states)
        {
            public override double Rate(int state, double phi, double vgs, double vds, double t, Span<double> grad)
            {
                grad[0] = -1e3; grad[1] = 1e3; grad[2] = 0; grad[3] = 0;
                return (vgs - phi) * 1e3;
            }

            public override double DcValue(int state, double vgs, double vds, double t) => vgs;
        }
    }

    [Fact]
    public void NamesThatCollideOrAreKeywords_AreRefusedNamingBoth()
    {
        string vars = "parameters rg=5.2 RG=1 begin=6.6 n=2\n";
        string elements = "R:R1 P1 P4 R=rg\nR:R2 P2 P5 R=RG\nR:R3 P3 P6 R=begin\nR:R4 P1 P2 R=n*1000\n";
        var ex = Assert.Throws<VerilogAExportException>(() => CmodelVerilogA.Write(WithNetwork(vars + elements), Named));
        Assert.Contains("Network.Netlist: the parameters 'rg' and 'RG' differ only in case, which many simulators read as one name; rename one to export.", ex.Message);
        Assert.Contains("Network.Netlist: the network variable 'begin' is a Verilog-A keyword or built-in name; rename it to export.", ex.Message);
        Assert.Contains("Network.Netlist: the parameters 'N' and 'n' differ only in case, which many simulators read as one name; rename one to export.", ex.Message);
    }

    [Fact]
    public void AnIllegalModuleName_IsRefused()
    {
        var ex = Assert.Throws<VerilogAExportException>(() =>
            CmodelVerilogA.Write(ComposedTestModels.Model(), Named with { ModuleName = "2 fast" }));
        Assert.Equal("ModuleName: '2 fast' is not a legal Verilog-A module name; use letters, digits and underscores, beginning with a letter.",
                     ex.Message);
        Assert.Contains("module fast_one(g, d, s);",
                        CmodelVerilogA.Write(ComposedTestModels.Model(), Named with { ModuleName = "fast_one" }));
    }

    [Fact]
    public void ARefusedExport_WritesNoFile()
    {
        string path = ComposedTestModels.WriteTemp(RuntimeTestModels.MixedModel(), "mixed.cmodel");
        string va = Path.Combine(Path.GetDirectoryName(path)!, "mixed.va");
        Assert.Throws<VerilogAExportException>(() => CmodelVerilogA.Export(path, va));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "mixed.va*"));
    }

    private static ComposedModel WithNetwork(string body) => ComposedTestModels.Model() with
    {
        Network = new ExtrinsicNetwork("define gan (P1 P2 P3 P4 P5 P6)\n" + body + "end\n", []),
    };
}
