using System;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Lvs;
using CircuitRF.Ui.ViewModels;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-wbond-wire-temperature G10 — the Inspector's Temp checkbox (FixedTemp) and the rows it greys — and the second half of
/// G7: a schematic saved before the solved mode existed loads, and netlists, with no FixedTemp written into it.
/// </summary>
public sealed class WBondWireTemperatureInspectorTests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) Place(Action<EditableComponent>? edit = null)
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        edit?.Invoke(comp);
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    private static ParameterRowViewModel Row(ParameterEditorViewModel editor, string name) => editor.Rows.Single(r => r.Name == name);

    private static string Value(SchematicViewModel vm, string name)
        => vm.EditModel.Components.Single(c => c.InstanceName == "W1").Parameters.Single(p => p.Name == name).Expression;

    [Fact]
    public void ANewPlacement_SolvesItsTemperature_BetweenTheDefaults()
    {
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        Assert.Equal("false", comp.Parameters.Single(p => p.Name == "FixedTemp").Expression);
        Assert.Equal("125", comp.Parameters.Single(p => p.Name == "TempStart").Expression);
        Assert.Equal("85", comp.Parameters.Single(p => p.Name == "TempEnd").Expression);
    }

    [Fact]
    public void TheCheckbox_GreysExactlyTheOtherMode_KeepsAVarThroughTwoToggles_AndUndoesInOneStep()
    {
        var (vm, _, editor) = Place(c => c.Parameters.Single(p => p.Name == "TempStart").Expression = "tdie");
        Assert.DoesNotContain(editor.Rows, r => r.Name == "FixedTemp");

        // solved: Temp greyed, the two ends live, TempStart/TempEnd directly under Temp
        var temp = Row(editor, "Temp");
        Assert.True(temp.HasLeadingCheck);
        Assert.False(temp.LeadingCheck);
        Assert.False(temp.ExpressionEnabled);
        Assert.True(Row(editor, "TempStart").ExpressionEnabled && Row(editor, "TempEnd").ExpressionEnabled);
        var names = editor.Rows.Select(r => r.Name).ToList();
        Assert.Equal(names.IndexOf("Temp") + 1, names.IndexOf("TempStart"));
        Assert.Equal(names.IndexOf("Temp") + 2, names.IndexOf("TempEnd"));
        Assert.DoesNotContain(editor.Rows, r => r.Name is not ("Temp" or "TempStart" or "TempEnd") && !r.ExpressionEnabled);

        // checked: the reverse — and the VAR in the now-greyed box is untouched
        Row(editor, "Temp").LeadingCheck = true;
        Assert.Equal("true", Value(vm, "FixedTemp"));
        Assert.True(Row(editor, "Temp").ExpressionEnabled);
        Assert.False(Row(editor, "TempStart").ExpressionEnabled || Row(editor, "TempEnd").ExpressionEnabled);
        Assert.Equal("tdie", Value(vm, "TempStart"));

        Row(editor, "Temp").LeadingCheck = false;
        Assert.Equal("false", Value(vm, "FixedTemp"));
        Assert.Equal("tdie", Value(vm, "TempStart"));

        vm.UndoRedo.Undo();
        Assert.Equal("true", Value(vm, "FixedTemp"));
        Assert.Equal("tdie", Value(vm, "TempStart"));
    }

    [Fact]
    public void AnOldInstance_StaysFixed_AndUncheckingAddsTheTwoEndsItLacks()
    {
        var (vm, _, editor) = Place(c => c.Parameters.RemoveAll(p => p.Name is "FixedTemp" or "TempStart" or "TempEnd"));
        Assert.True(Row(editor, "Temp").LeadingCheck);
        Assert.True(Row(editor, "Temp").ExpressionEnabled);

        Row(editor, "Temp").LeadingCheck = false;
        Assert.Equal("false", Value(vm, "FixedTemp"));
        Assert.Equal("125", Value(vm, "TempStart"));
        Assert.Equal("85", Value(vm, "TempEnd"));
    }

    /// <summary>G7 / R-wbt-2b — no load, netlist or elaboration path back-fills the registry's FixedTemp into a stored instance.</summary>
    [Fact]
    public void ASchematicSavedBeforeTheSolvedMode_LoadsAndNetlistsWithNoFixedTemp()
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        comp.Parameters.RemoveAll(p => p.Name is "FixedTemp" or "TempStart" or "TempEnd");
        model.Components.Add(comp);
        string path = Path.Combine(Path.GetTempPath(), $"wbt-{Guid.NewGuid():N}.csch");
        try
        {
            SchematicPersistence.SaveToFile(path, model, "tb");
            var (loaded, _, _) = SchematicPersistence.LoadFromFile(path);
            var w = loaded.Components.Single(c => c.Symbol == SymbolKind.WBond);
            Assert.DoesNotContain(w.Parameters, p => p.Name is "FixedTemp" or "TempStart" or "TempEnd");

            var extracted = NetExtractor.Extract(loaded, "tb");
            string cnl = CnlWriter.Write(extracted.TestBench, extracted.Library);
            Assert.DoesNotContain("FixedTemp", cnl);
            Assert.DoesNotContain("TempStart", cnl);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }
}

/// <summary>G6b — the CLI's <c>dc --json</c> carries WireTemp, because it packs through the one DC packer.</summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class WBondWireTemperatureCliTests
{
    [Fact]
    public void TheDcVerb_JsonCarriesWireTemp()
    {
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires = { new Wire { Points = { Point3.Mils(0, 0, 4), Point3.Mils(40, 0, 4) }, DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil) } },
        });
        string dir = Path.Combine(Path.GetTempPath(), $"wbt-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string wb = Path.Combine(dir, "w.wBond");
            WBondIo.WriteFile(wb, design);
            string cnl = Path.Combine(dir, "dc.cnl");
            File.WriteAllText(cnl, $"I_1Tone:I1 a 0 Idc=1 Freq=1e9 I=0\nwBond:WB1 a b 0 File=\"{wb}\" FixedTemp=false\nR:R1 b 0 R=1\nanalysis DC1 type=dc\n");

            var real = Console.Out;
            var buffer = new StringWriter();
            int exit;
            try
            {
                Console.SetOut(buffer);
                JsonRun.Reset();
                exit = CliEntry.Run(["dc", cnl, "--json"]);
            }
            finally { Console.SetOut(real); }

            Assert.Equal(0, exit);
            string json = buffer.ToString();
            Assert.Contains("\"WireTemp\"", json);
            Assert.Contains("WB1:G1", json);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
