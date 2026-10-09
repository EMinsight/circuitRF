// brief-artsch-7-cli-and-mcp.md §4 — `circuitrf recognize` is a command line onto ArtworkRecognition and nothing
// else: run as a PROCESS it writes the bytes the in-process call writes; with no output asked for it writes nothing;
// the parts table round-trips through a file; its refusals are the brief's; its result simulates headlessly; and
// src/Cli names nothing of the recognition but its entry point, options, result and PartsTableCsv.
//
// Every call is a process (~1 s each): the claims are about the command line a caller actually runs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class RecognizeCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as7-cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Board(string name) => EmitBoards.Saved(Path.Combine(_root, name)).ClayPath!;

    [Fact]
    public void TheVerbAsAProcess_WritesTheInProcessRunsBytes()
    {
        string clayA = Board("a"), clayB = Board("b");
        string cnlA = Path.Combine(_root, "a", "out", "board.cnl");
        var (exit, _, stderr) = Cli("recognize", clayA, "--into", "new:Board_model", "-o", cnlA);
        Assert.True(exit == 0, stderr);

        var run = ArtworkRecognition.Run(RecognitionInput.FromFile(clayB), RecognitionTarget.NewCell("Board_model"));
        Assert.True(run.Ok, run.Refusal);

        static string NoTime(string csch) => Regex.Replace(csch, "\"CreatedUtc\"\\s*:\\s*\"[^\"]*\"", "\"CreatedUtc\": \"\"", RegexOptions.IgnoreCase);
        string cschA = File.ReadAllText(Path.Combine(_root, "a", "Board_model", "schematic", "Board_model.csch"));
        Assert.Equal(NoTime(File.ReadAllText(run.SchematicPath!)), NoTime(cschA));
        Assert.NotEqual(NoTime(cschA), cschA);   // the exclusion is the provenance time, and it was there to exclude

        Assert.Equal(run.Circuit!.CnlText(Path.Combine(_root, "b", "out")), File.ReadAllText(cnlA));
    }

    [Fact]
    public void TheReadOnlyDefault_WritesNothing()
    {
        string clay = Board("ro");
        var before = Snapshot(Path.Combine(_root, "ro"));
        var (exit, stdout, stderr) = Cli("recognize", clay);
        Assert.True(exit == 0, stderr);
        Assert.Contains(string.Join(",", PartsTableCsv.Columns), stdout);
        Assert.Equal(before, Snapshot(Path.Combine(_root, "ro")));
    }

    [Fact]
    public void AnEditedPartsTable_GivesItsValue_AndNoVariable()
    {
        string clay = Board("parts");
        string csv = Path.Combine(_root, "parts.csv"), cnl = Path.Combine(_root, "edited.cnl");
        Assert.Equal(0, Cli("recognize", clay, "--parts-out", csv).Exit);

        var lines = File.ReadAllLines(csv).ToList();
        var header = lines[0].Split(',');
        int value = Array.IndexOf(header, "Value"), variable = Array.IndexOf(header, "Variable");
        int at = lines.FindIndex(1, l => l.Split(',')[variable].Length > 0);
        var cells = lines[at].Split(',');
        string name = cells[variable];
        cells[value] = name.EndsWith("_L", StringComparison.Ordinal) ? "22nH" : name.EndsWith("_R", StringComparison.Ordinal) ? "47" : "22pF";
        lines[at] = string.Join(",", cells);
        File.WriteAllLines(csv, lines);

        var (exit, _, stderr) = Cli("recognize", clay, "--parts", csv, "-o", cnl);
        Assert.True(exit == 0, stderr);
        string text = File.ReadAllText(cnl);
        output.WriteLine(text);
        Assert.DoesNotContain(name, text);
        Assert.Matches(@$"\b{Regex.Escape(cells[0])}\b[^\n]*\b22\b", text);
    }

    [Fact]
    public void TheRefusals_AreTheBriefs()
    {
        string clay = Board("refuse");

        var bare = Cli("recognize", clay, "--region", "0,0,10,5");
        Assert.Equal(1, bare.Exit);
        Assert.Contains("is a bare number", bare.StdErr);

        string cell = Path.GetDirectoryName(Path.GetDirectoryName(clay))!;
        CellCreate.WriteSchematicView(cell, "Board", "Board", new CircuitRF.Design.Schematic.SchematicEditModel());
        var artwork = Cli("recognize", clay, "--into", "artwork");
        Assert.Equal(1, artwork.Exit);
        Assert.Contains("--into new:<name>", artwork.StdErr);

        Assert.Equal(0, Cli("recognize", clay, "--into", "new:x").Exit);
        var again = Cli("recognize", clay, "--into", "new:x");
        Assert.Equal(1, again.Exit);
        Assert.Contains("--replace", again.StdErr);
        var replaced = Cli("recognize", clay, "--into", "new:x", "--replace");
        Assert.True(replaced.Exit == 0, replaced.StdErr);
    }

    [Fact]
    public void TheResult_SimulatesWithNoDisplay()
    {
        string clay = Board("e2e");
        Assert.Equal(0, Cli("recognize", clay, "--into", "new:m").Exit);
        string csch = Path.Combine(_root, "e2e", "m", "schematic", "m.csch");
        var (exit, stdout, stderr) = Cli("sparam", csch, "--json");
        Assert.True(exit == 0, stderr);
        var shape = JsonNode.Parse(stdout)!["result"]!["shape"];
        Assert.NotNull(shape);
        output.WriteLine(shape!.ToJsonString());

        // R-as7-8: check finds nothing to say about it, and explain resolves where it came from.
        var check = JsonNode.Parse(Cli("check", csch, "--json").StdOut)!;
        Assert.Empty(check["diagnostics"]!.AsArray().Where(d => d!["severity"]!.GetValue<string>() is "warning" or "error"));
        var explain = JsonNode.Parse(Cli("explain", csch, "--ref", "../../Board/layout/Board.clay", "--json").StdOut)!;
        Assert.Equal(Path.GetFullPath(clay), explain["result"]!["explain"]!["reference"]!["resolvedPath"]!.GetValue<string>());
        Assert.Contains(explain["result"]!["explain"]!["walks"]!.AsArray(), w => w!["step"]!.GetValue<string>() == "artwork source");
    }

    /// <summary>R-as7-1: src/Cli names no recognition type but the entry point, its options, its result and
    /// PartsTableCsv — every other public type of the namespace is the recognition's own business.</summary>
    [Fact]
    public void TheVerb_OwnsNoRecognition()
    {
        string repo = RepoRoot();
        string[] allowed =
        [
            "ArtworkRecognition", "RecognitionInput", "RecognitionOptions", "RecognitionRunOptions", "RecognitionEmitOptions",
            "RecognitionScope", "RecognitionTarget", "RecognitionTargetKind", "ViaPolicy", "CoplanarReading",
            "RecognitionResult", "RecognitionRun", "RecognitionCircuit", "RecognitionReport", "RecognitionFinding",
            "RecognitionFindingClass", "RecognitionAnchor", "PartsTable", "PartRow", "PartsTableCsv", "PartsCsvReading",
        ];
        var declared = Directory.GetFiles(Path.Combine(repo, "src", "Design", "Layout", "Recognition"), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f),
                @"^(?:public|internal)\s+(?:(?:static|sealed|readonly|abstract|partial)\s+)*(?:record\s+struct|record|class|enum|struct|interface)\s+(\w+)",
                RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.Contains("BoardGraph", declared);   // not vacuous
        var forbidden = declared.Except(allowed).ToList();

        var cli = Directory.GetFiles(Path.Combine(repo, "src", "Cli"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var hits = cli.SelectMany(f =>
        {
            string code = Regex.Replace(Regex.Replace(Regex.Replace(File.ReadAllText(f), @"/\*.*?\*/", "", RegexOptions.Singleline),
                                                      @"//[^\n]*", ""), "\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"");
            return forbidden.Where(t => Regex.IsMatch(code, $@"\b{t}\b")).Select(t => $"{Path.GetFileName(f)}: {t}");
        }).ToList();
        Assert.Empty(hits);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, DateTime> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

    private (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var result = (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        output.WriteLine($"$ recognize-test: {string.Join(' ', args)} -> {result.ExitCode}\n{result.Item3}");
        return result;
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RecognizeCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
