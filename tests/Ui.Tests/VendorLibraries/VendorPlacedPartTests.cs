using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Engine;
using CircuitRF.Ui.Archive;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-3 §5: the placed part — the picker that parses nothing, value stepping, the
/// footprint following the part only when it came from it, one-block extraction, the frequency-range
/// warning, the archive row and <c>check</c>. Every library is generated
/// (<see cref="VendorLibraryFixture"/>); every gate is a counter or a structure, never a clock.
/// </summary>
public sealed class VendorPlacedPartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl3-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _lib;
    private readonly string _ws;
    private readonly string _schDir;

    public VendorPlacedPartTests()
    {
        _lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(_lib);
        // A board workspace, so a part's case size has a footprint to give.
        _ws = WorkspaceCreate.Create(_dir, "ws", "pcb-2layer_FR-4_70mil_1oz", vendorLibraries: [("Parts", _lib)]).WorkspaceDir;
        _schDir = Path.Combine(_ws, "cells", "top", "schematic");
        Directory.CreateDirectory(_schDir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private const string FileA = "capacitors/series-a.lib";

    private static EditableComponent VendorPart(string name, double x = 0, string glyph = "C", string? footprint = null)
    {
        var comp = new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.SpiceModel, X = x, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        comp.Parameters.First(p => p.Name == "File").Expression = VendorLibraryRefs.RefFor("Parts", FileA);
        comp.Parameters.First(p => p.Name == "Name").Expression = name;
        comp.Parameters.Add(new EditableParameter { Name = "Glyph", Expression = glyph });
        if (footprint is not null)
            comp.Parameters.Add(new EditableParameter { Name = ArtworkParameters.FootprintName, Expression = footprint });
        return comp;
    }

    private static string Param(EditableComponent c, string name)
        => c.Parameters.FirstOrDefault(p => p.Name == name)?.Expression ?? "";

    private (SchematicEditModel Model, EditableComponent Comp, SchematicViewModel Vm, ParameterEditorViewModel Editor)
        OpenEditor(EditableComponent comp)
    {
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        model.Components.Add(comp);
        var wire = new EditableWire();
        wire.Points.AddRange([(-200, 0), (-100, 0)]);
        model.Wires.Add(wire);
        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (model, comp, vm, editor);
    }

    private void BuildIndex() => VendorLibraryIndex.StartBuild(_lib).GetAwaiter().GetResult();

    // 1 — selecting a placed part with the panel open parses nothing and reads no file.
    [Fact]
    public void SelectingAPlacedPart_ParsesNothing()
    {
        BuildIndex();
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        var comp = VendorPart("TSTC0402A0001");
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);

        int peeks = SpiceModelPeek.ReadsOnThisThread, reads = SpiceNetlistReader.ReadsOnThisThread;
        long files = VendorLibraryIndex.FileReads(_lib);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        editor.VendorPartQuery = "10p";

        Assert.Equal(peeks, SpiceModelPeek.ReadsOnThisThread);
        Assert.Equal(reads, SpiceNetlistReader.ReadsOnThisThread);
        Assert.Equal(files, VendorLibraryIndex.FileReads(_lib));

        Assert.True(editor.ShowVendorPartPanel);
        Assert.False(editor.IsFileSpiceModel);
        Assert.Equal("1.5 pF", editor.VendorPartValue);
        Assert.Equal("100 MHz – 30 GHz", editor.VendorPartRange);
        Assert.Equal(["TSTC0201A0018"], editor.VendorPartMatches.Select(m => m.Part));
    }

    // 3 — Value ▲/▼ walks one file's same-description parts in value order and keeps the instance.
    [Fact]
    public void ValueStepping_WalksTheSameKindInValueOrder_AndKeepsTheInstance()
    {
        BuildIndex();
        var (model, comp, _, editor) = OpenEditor(VendorPart("TSTC0402A0001"));
        var wires = model.Wires.ToList();

        Assert.False(editor.StepVendorValueDownCommand.CanExecute(null));   // the lowest 0402 C0G part
        editor.StepVendorValueUpCommand.Execute(null);
        Assert.Equal("TSTC0402A0004", Param(comp, "Name"));                  // 1.5 pF → 3 pF, same size
        editor.StepVendorValueUpCommand.Execute(null);
        Assert.Equal("TSTC0402A0007", Param(comp, "Name"));
        editor.StepVendorValueDownCommand.Execute(null);
        Assert.Equal("TSTC0402A0004", Param(comp, "Name"));

        Assert.Same(comp, model.Components.Single());
        Assert.Equal((0.0, 0.0), (comp.X, comp.Y));
        Assert.Equal(wires, model.Wires);
    }

    // The value row's inline edit opens on the same-kind list with the placed part highlighted, and ↓
    // then Enter takes the next value as one undoable part change.
    [Fact]
    public void TheValueRowsInlineEdit_StepsThroughTheSameKind()
    {
        BuildIndex();
        var (model, comp, vm, _) = OpenEditor(VendorPart("TSTC0402A0001"));
        Assert.True(comp.HasVendorValueRow);

        vm.BeginInlineEditForHit(new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentVendorValue, comp.Id, 2), 0, 0);
        Assert.True(vm.IsVendorInlineOpen);
        Assert.Equal("TSTC0402A0001", vm.SelectedVendorInlineMatch!.Part);
        Assert.All(vm.VendorInlineMatches, m => Assert.StartsWith("TSTC0402A", m.Part));   // same kind only

        vm.MoveVendorInlineSelection(+1);
        vm.CommitInlineEdit();
        Assert.Equal("TSTC0402A0004", Param(comp, "Name"));
        Assert.False(vm.IsVendorInlineOpen);

        vm.UndoRedo.Undo();
        Assert.Equal("TSTC0402A0001", Param(comp, "Name"));

        // Seeded with the value, the box still searches by part number: typing one replaces the seed.
        vm.BeginInlineEditForHit(new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentVendorValue, comp.Id, 2), 0, 0);
        vm.InlineEditValue = "TSTC0603A00";
        Assert.All(vm.VendorInlineMatches, m => Assert.StartsWith("TSTC0603A00", m.Part));
        vm.InlineEditValue = "TSTC0603A0005";
        vm.CommitInlineEdit();
        Assert.Equal("TSTC0603A0005", Param(comp, "Name"));
        _ = model;
    }

    // 4 — the footprint follows the part only when it came from the part; undo restores both.
    [Fact]
    public void TheFootprint_FollowsThePart_OnlyWhenItCameFromThePart()
    {
        BuildIndex();
        var (_, comp, vm, editor) = OpenEditor(VendorPart("TSTC0402A0001", footprint: "smt:0402@N"));
        editor.VendorPartQuery = "TSTC0603A0002";
        editor.ChooseVendorPart(editor.VendorPartMatches.Single());
        Assert.Equal("TSTC0603A0002", Param(comp, "Name"));
        Assert.Equal("smt:0603@N", Param(comp, ArtworkParameters.FootprintName));

        vm.UndoRedo.Undo();
        Assert.Equal("TSTC0402A0001", Param(comp, "Name"));
        Assert.Equal("smt:0402@N", Param(comp, ArtworkParameters.FootprintName));

        var (_, chosenByHand, _, editor2) = OpenEditor(VendorPart("TSTC0402A0001", footprint: "smt:0805@L"));
        editor2.VendorPartQuery = "TSTC0603A0002";
        editor2.ChooseVendorPart(editor2.VendorPartMatches.Single());
        Assert.Equal("TSTC0603A0002", Param(chosenByHand, "Name"));
        Assert.Equal("smt:0805@L", Param(chosenByHand, ArtworkParameters.FootprintName));
    }

    // 2 — a run reads one block per part, through the one reader, and matches the file-based path.
    [Fact]
    public void Extraction_ReadsOneBlockPerPart_AndMatchesTheFileBasedSpiceModel()
    {
        double[] freqs = [1e8, 1e9, 1e10];
        string[] parts = ["TSTC0402A0001", "TSTC0603A0002"];

        int reads = SpiceNetlistReader.ReadsOnThisThread;
        long files = VendorLibraryIndex.FileReads(_lib);
        // No glyph, so each part draws as its own two-pin box — read from its block too — and the
        // terminations land on pins the same way on both sides.
        var vendor = Run(parts.Select((p, i) => VendorPart(p, x: i * 2000, glyph: "")).ToList(), freqs);

        // One header scan of the ONE file the parts name (no index existed: nothing else of the
        // library is touched) and one block read per part, shared by the symbol and the run; the
        // reader ran once per part.
        Assert.Equal(3, VendorLibraryIndex.FileReads(_lib) - files);
        Assert.Equal(2, SpiceNetlistReader.ReadsOnThisThread - reads);
        Assert.Null(VendorLibraryIndex.Latest(_lib));
        var entries = VendorLibraryIndex.ScanFile(_lib, FileA)!;
        Assert.All(parts, p => Assert.True(entries.Single(e => e.Part == p).Length < 200));

        var plain = Run(parts.Select((p, i) =>
        {
            var c = VendorPart(p, x: i * 2000, glyph: "");
            c.Parameters.First(q => q.Name == "File").Expression = Path.Combine(_lib, "capacitors", "series-a.lib");
            return c;
        }).ToList(), freqs);

        var a = vendor.Cubes["S"].ComplexValues;
        var b = plain.Cubes["S"].ComplexValues;
        Assert.Equal(b.Length, a.Length);
        Assert.Contains(a, v => v.Magnitude > 1e-6 && v.Magnitude < 1 - 1e-6);
        for (int i = 0; i < a.Length; i++) Assert.True((a[i] - b[i]).Magnitude <= 1e-12, $"S[{i}]: {a[i]} vs {b[i]}");
    }

    private DataSet Run(List<EditableComponent> comps, double[] freqs)
    {
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        int port = 0;
        foreach (var comp in comps)
        {
            comp.InstanceName = $"X{comps.IndexOf(comp) + 1}";
            model.Components.Add(comp);
            var res = CellSymbolResolver.Resolve(comp.ExternalSymbolRef!, _schDir);
            foreach (var pin in res.Symbol!.Pins.OrderBy(p => p.PortIndex))
            {
                double px = pin.LocalX + comp.X, py = pin.LocalY + comp.Y;
                port++;
                var term = new EditableComponent { InstanceName = $"P{port}", Symbol = SymbolKind.Term, X = px, Y = py + 200 };
                term.Parameters.Add(new EditableParameter { Name = "Num", Expression = port.ToString() });
                term.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
                model.Components.Add(term);
                model.Components.Add(new EditableComponent { InstanceName = $"G{port}", Symbol = SymbolKind.Ground, X = px, Y = py + 400 });
            }
        }
        var r = NetExtractor.Extract(model, "tb");
        Assert.Empty(r.Conflicts);
        return SParameterEngine.Run(new Elaborator(r.Library).Elaborate(r.TestBench), freqs);
    }

    // 5 — a sweep from 1 MHz on a part characterised from 100 MHz warns once; DC does not warn.
    [Fact]
    public void LeavingThePartsRange_WarnsOncePerPartNumber_AndDcDoesNot()
    {
        var part = VendorLibraryIndex.ScanFile(_lib, FileA)!.First(e => e.Part == "TSTC0402A0001");

        var sweep = new TestBench("tb");
        sweep.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1", "1000", 11, startUnit: "MHz", stopUnit: "MHz")));
        var w = VendorPartRange.Warnings(sweep, [part, part]);
        Assert.Equal(["TSTC0402A0001 is characterised from 100 MHz to 30 GHz; this run reaches 1 MHz."], w);

        var dc = new TestBench("tb");
        dc.Analyses.Add(new DcAnalysis("DC1"));
        Assert.Empty(VendorPartRange.Warnings(dc, [part]));
    }

    // A size code in a query is the stated case code, never a substring of the other system's twin: the
    // header writes "0603M(0201)", and "0603" must not find that 0201 part. A value's unit names the
    // quantity, and a description is matched without its unit brackets.
    [Fact]
    public void ACaseCodeToken_MatchesTheStatedCodeAndNotItsTwin()
    {
        var entries = VendorLibraryIndex.ScanFile(_lib, FileA)!;
        var q = VendorLibrarySearch.Parse("0603 2pF C0G");
        Assert.Equal(["TSTC0603A0002"], entries.Where(e => VendorLibrarySearch.Match(e, q)).Select(e => e.Part));
        Assert.DoesNotContain(entries, e => VendorLibrarySearch.Match(e, "0603 1pF"));               // 1 pF is a 0201
        Assert.Equal(["TSTC0201A0000"], entries.Where(e => VendorLibrarySearch.Match(e, "0603M 1pF")).Select(e => e.Part));
        Assert.DoesNotContain(entries, e => VendorLibrarySearch.Match(e, "1pH"));   // a unit names the quantity
        Assert.Contains(entries, e => VendorLibrarySearch.Match(e, "50V 1p"));      // "50[V]" without its brackets
    }

    // An edition that ships a schematic symbol per part beside its models is still a vendor library; a
    // file named after no part is still kit structure.
    [Fact]
    public void PerPartCompanionFiles_DoNotMakeALibraryAKit()
    {
        string sym = Path.Combine(_lib, "symbols");
        Directory.CreateDirectory(sym);
        File.WriteAllText(Path.Combine(sym, "TSTC0402A0001.sym"), "symbol");
        File.WriteAllText(Path.Combine(sym, "TSTL0603B0000.sym"), "symbol");
        Assert.Equal(VendorLibraryRefs.FolderKind.VendorLibrary, VendorLibraryRefs.Classify(_lib).Kind);

        File.WriteAllText(Path.Combine(sym, "catalog.sym"), "not a part");
        Assert.Equal(VendorLibraryRefs.FolderKind.Kit, VendorLibraryRefs.Classify(_lib).Kind);
    }

    // 6 — the archive offers the library as one unticked row and no file of it as an external file.
    [Fact]
    public void Archive_OffersTheLibraryUnticked_AndNoneOfItsFilesAsExternalFiles()
    {
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        model.Components.Add(VendorPart("TSTC0402A0001"));
        SchematicPersistence.SaveToFile(Path.Combine(_schDir, "top.csch"), model, "top");

        var plan = WorkspaceArchiveScanner.Scan(_ws);

        var row = plan.Kits.Single(o => o.SourcePath == Path.GetFullPath(_lib));
        Assert.False(row.Selected);
        Assert.Equal("Parts (vendor library)", row.DisplayName);
        Assert.Equal(WorkspaceArchiveScanner.VendorLibraryRowSentence, row.Note);
        Assert.DoesNotContain(plan.ExternalFiles, o => o.SourcePath.StartsWith(_lib, StringComparison.Ordinal));
    }

    // 7 — check: a missing library and a vanished part are errors; an unreadable file is a warning.
    [Fact]
    public void Check_ReportsAMissingLibraryAndAVanishedPartAsErrors_AndAnUnreadableFileAsAWarning()
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile
        {
            VendorLibraries =
            [
                new CwsVendorLibraryRef { Name = "Parts", Path = _lib },
                new CwsVendorLibraryRef { Name = "Gone",  Path = Path.Combine(_dir, "moved-away") },
            ],
        });
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        model.Components.Add(VendorPart("TSTC9999Z0000"));
        SchematicPersistence.SaveToFile(Path.Combine(_schDir, "top.csch"), model, "top");

        var run = RunCli("check", _ws, "--json");
        using var doc = JsonDocument.Parse(run.StdOut);
        var found = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => (Id: d.GetProperty("id").GetString(), Severity: d.GetProperty("severity").GetString()))
            .ToList();

        Assert.Contains(("check.workspace.vendor-library.missing", "error"), found);
        Assert.Contains(("check.workspace.vendor-library.part-missing", "error"), found);
        Assert.Contains(("check.workspace.vendor-library.unreadable", "warning"), found);
        Assert.Equal(1, run.ExitCode);
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(VendorPlacedPartTests).Assembly)
            .First(a => a.Key == "CliDir").Value!, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Path.Combine(_dir, "state");
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
