using System.Diagnostics;
using System.Text.Json.Nodes;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-13 R-mc13-6: over the protocol, the <c>model</c> tool's <c>export</c> writes the bytes the CLI's
/// does, and <c>reference cmodel</c> returns the format's field table. MC-28 adds the Verilog-A output.
/// </summary>
public sealed class ModelMcpToolTests(ITestOutputHelper output)
{
    private const string Created = "2026-10-19T00:00:00Z";

    [Fact]
    public void TheModelToolsExport_IsTheClisBytes_AndReferenceServesTheCmodelSchema()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string viaTool = Path.Combine(dir, "tool.cmodel"), viaCli = Path.Combine(dir, "cli.cmodel");

        var calls = Serve(dir,
            new JsonObject
            {
                ["name"] = "model",
                ["arguments"] = new JsonObject { ["noun"] = "export", ["path"] = cmc, ["output"] = viaTool, ["created"] = Created },
            },
            new JsonObject { ["name"] = "reference", ["arguments"] = new JsonObject { ["topic"] = "cmodel" } });

        var doc = JsonNode.Parse(calls[0]["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(0, doc["exitCode"]!.GetValue<int>());
        Assert.Equal("export", doc["result"]!["model"]!["noun"]!.GetValue<string>());

        var cli = ModelCliFixture.Run(dir, "model", "export", cmc, "-o", viaCli, "--created", Created);
        Assert.Equal(0, cli.ExitCode);
        Assert.Equal(File.ReadAllBytes(viaCli), File.ReadAllBytes(viaTool));

        string page = JsonNode.Parse(calls[1]["content"]![0]!["text"]!.GetValue<string>())!["result"]!["reference"]!["topic"]!["text"]!.GetValue<string>();
        Assert.Contains("`Core.Constituents`", page, StringComparison.Ordinal);
        Assert.Contains("`Provenance`", page, StringComparison.Ordinal);
    }

    /// <summary>brief-modelcomposer-28 R-mc28-3: a <c>.va</c> output writes Verilog-A from a <c>.cmodel</c> — the CLI's bytes,
    /// with the module named — and a table model is refused naming every table constituent.</summary>
    [Fact]
    public void TheModelToolsExport_ToAVa_IsTheClisBytes_AndRefusesTables()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string cmodel = CmodelExport.Export(cmc, Path.Combine(dir, "fet.cmodel"), created: Created).Path;
        string mixed = Path.Combine(dir, "mixed.cmodel");
        File.Copy(CircuitRF.Core.Tests.Composed.ComposedTestModels.WriteTemp(CircuitRF.Engine.Tests.Composed.RuntimeTestModels.MixedModel(), "mixed.cmodel"), mixed);

        JsonObject Export(string path, string output) => new()
        {
            ["name"] = "model",
            ["arguments"] = new JsonObject { ["noun"] = "export", ["path"] = path, ["output"] = output, ["module"] = "amp" },
        };
        var calls = Serve(dir, Export(cmodel, Path.Combine(dir, "tool.va")), Export(mixed, Path.Combine(dir, "mixed.va")));

        var doc = JsonNode.Parse(calls[0]["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(0, doc["exitCode"]!.GetValue<int>());
        Assert.Equal("amp", doc["result"]!["model"]!["verilogA"]!["module"]!.GetValue<string>());
        var cli = ModelCliFixture.Run(dir, "model", "export", cmodel, "-o", Path.Combine(dir, "cli.va"), "--module", "amp");
        Assert.Equal(0, cli.ExitCode);
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "cli.va")), File.ReadAllBytes(Path.Combine(dir, "tool.va")));

        var refusal = JsonNode.Parse(calls[1]["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(1, refusal["exitCode"]!.GetValue<int>());
        string refused = string.Join("\n", refusal["diagnostics"]!.AsArray().Select(d => d!["message"]!.GetValue<string>()));
        foreach (string name in new[] { "Id", "Qg" })
            Assert.Contains($"Core.Constituents.{name}.Method: 'Table' cannot be written as Verilog-A", refused, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir, "mixed.va")));
    }

    /// <summary>One <c>serve</c> process over <paramref name="root"/>: initialize, then each tool call; their results.</summary>
    private List<JsonObject> Serve(string root, params JsonObject[] toolCalls)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in new[] { ModelCliFixture.CliDll(), "serve", "--root", root }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();

        JsonObject Ask(int id, string method, JsonObject parameters)
        {
            proc.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
            proc.StandardInput.Flush();
            while (proc.StandardOutput.ReadLine() is { } line)
                if (JsonNode.Parse(line) is JsonObject frame && frame["id"]?.GetValue<int>() == id) return frame["result"]!.AsObject();
            throw new InvalidOperationException("serve ended before answering");
        }

        Ask(1, "initialize", new JsonObject());
        var results = toolCalls.Select((call, k) => Ask(k + 2, "tools/call", call)).ToList();
        proc.StandardInput.Close();
        proc.WaitForExit(30_000);
        output.WriteLine(stderr.GetAwaiter().GetResult());
        return results;
    }
}
