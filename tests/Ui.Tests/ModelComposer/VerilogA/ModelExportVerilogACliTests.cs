using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Engine.Tests.Composed;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-28 R-mc28-2: <c>model export -o x.va</c>, run as a process, writes the bytes an in-process
/// <see cref="CmodelVerilogA.Export"/> writes — from a <c>.cmc</c> (assembled as Export assembles it) and from a bare
/// <c>.cmodel</c>. <c>-o</c>'s extension picks the format, and every refusal says its sentence and writes nothing.
/// </summary>
public sealed class ModelExportVerilogACliTests
{
    private const string Created = "2026-10-21T00:00:00Z";

    [Fact]
    public void FromACmcAndFromACmodel_TheVerbWritesTheInProcessBytes_AndJsonReportsTheModule()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;

        // A .cmc is assembled at the time of the export, so its two writes differ in the provenance line alone: the
        // timestamp and the SHA-256 of a model.json that holds it.
        var cli = ModelCliFixture.Run(dir, "model", "export", cmc, "-o", Path.Combine(dir, "cli.va"));
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        CmodelVerilogA.Export(cmc, Path.Combine(dir, "inproc.va"));
        string[] a = File.ReadAllLines(Path.Combine(dir, "cli.va")), b = File.ReadAllLines(Path.Combine(dir, "inproc.va"));
        Assert.StartsWith("// Model created ", a[1]);
        Assert.Equal(a.Where((_, k) => k != 1), b.Where((_, k) => k != 1));
        Assert.Contains("module fet(g, d, s);", a);

        // A .cmodel needs no document, and is the same bytes.
        string cmodel = CmodelExport.Export(cmc, Path.Combine(dir, "fet.cmodel"), created: Created).Path;
        cli = ModelCliFixture.Run(dir, "model", "export", cmodel, "-o", Path.Combine(dir, "from-cmodel.vams"), "--module", "amp", "--json");
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        var r = CmodelVerilogA.Export(cmodel, Path.Combine(dir, "inproc.vams"), new VerilogAOptions { ModuleName = "amp" });
        Assert.Equal(File.ReadAllBytes(r.Path), File.ReadAllBytes(Path.Combine(dir, "from-cmodel.vams")));

        var doc = JsonNode.Parse(cli.StdOut)!;
        Assert.Equal("veriloga", doc["outputs"]![0]!["kind"]!.GetValue<string>());
        var va = doc["result"]!["model"]!["verilogA"]!;
        Assert.Equal(Path.Combine(dir, "from-cmodel.vams"), va["path"]!.GetValue<string>());
        Assert.Equal("amp", va["module"]!.GetValue<string>());
        Assert.Equal(r.Parameters, va["parameters"]!.GetValue<int>());
        Assert.Equal(r.InertSwitches, va["inertSwitches"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Contains("Dispersion", r.InertSwitches);
    }

    [Fact]
    public void EveryRefusal_SaysItsSentence_AndWritesNothing()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string cmodel = CmodelExport.Export(cmc, Path.Combine(dir, "fet.cmodel"), created: Created).Path;
        string table = Path.Combine(dir, "mixed.cmodel");
        File.Copy(ComposedTestModels.WriteTemp(RuntimeTestModels.MixedModel(), "mixed.cmodel"), table);

        void Refused(string[] whys, params string[] args)
        {
            var r = ModelCliFixture.Run(dir, ["model", "export", .. args]);
            Assert.True(r.ExitCode == 1, $"{string.Join(' ', args)}: exit {r.ExitCode}\n{r.StdOut}{r.StdErr}");
            foreach (string why in whys) Assert.Contains(why, r.StdOut + r.StdErr, StringComparison.Ordinal);
        }

        Refused(["-o 'fet.txt': the extension says the format, and it is one of .cmodel, .va or .vams (Verilog-A)."],
                cmc, "-o", Path.Combine(dir, "fet.txt"));
        Refused(["the model is a .cmodel already; there is nothing to export."], cmodel, "-o", Path.Combine(dir, "again.cmodel"));
        Refused(["--created is a .cmodel's; a .va records the model's own Created."], cmc, "-o", Path.Combine(dir, "c.va"), "--created", Created);
        Refused(["ModuleName: '2 fast' is not a legal Verilog-A module name"], cmodel, "-o", Path.Combine(dir, "m.va"), "--module", "2 fast");
        // Every table constituent, in one refusal.
        Refused([.. new[] { "Id", "Qg" }.Select(name =>
                    $"Core.Constituents.{name}.Method: 'Table' cannot be written as Verilog-A; refit {name} as an ANN or an equation to export it.")],
                table, "-o", Path.Combine(dir, "mixed.va"));

        Assert.Empty(Directory.GetFiles(dir, "*.va*").Concat(Directory.GetFiles(dir, "*.txt")).Concat(Directory.GetFiles(dir, "again*")));
    }
}
