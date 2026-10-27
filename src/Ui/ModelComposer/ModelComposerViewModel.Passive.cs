// The passive Data and Topology steps (brief-modelcomposer-14 R-mc14-9; MC-12): the block or the family with its
// geometry values, the analysis lines, and the top three topology proposals with their scores — or the user's own,
// drawn or pasted into the network panel. Analysing and proposing write nothing; choosing a topology is an edit.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    public ObservableCollection<string> AnalysisLines { get; } = [];
    public ObservableCollection<ProposalRow> Proposals { get; } = [];

    /// <summary>Why Propose offered nothing — a block with more ports than any built-in topology; empty otherwise.</summary>
    [ObservableProperty] private string _proposalNote = "";

    /// <summary>The blocks of the passive data, with their geometry values — the family list.</summary>
    public ObservableCollection<string> PassiveBlocks { get; } = [];

    /// <summary>The topology the step fits: a library id, or a <c>.csch</c> beside the document.</summary>
    public string TopologyName => IsPassive ? PassiveStep.Settings(Document).Topology ?? "" : "";

    private void RefreshPassive()
    {
        OnPropertyChanged(nameof(TopologyName));
        if (!IsPassive) return;
        PassiveBlocks.Clear();
        foreach (var set in Document.DataSets.Where(s => s.Kind == DataSetKind.PassiveSParams))
            if (Loaded.Data.TryGetValue(set.Name, out var d))
                foreach (var p in d.Points)
                    PassiveBlocks.Add(double.IsFinite(p.Geometry)
                        ? $"{p.PointId} · {p.Geometry.ToString("G4", CultureInfo.InvariantCulture)}"
                        : p.PointId);
        NetworkDrawing = TopologyDrawing(TopologyName);
        NetworkRefusal = "";
    }

    /// <summary>A topology drawn as the schematic canvas draws it: a library one from its resource, the user's from its file.</summary>
    private SchematicModel? TopologyDrawing(string topology)
    {
        if (topology.Length == 0) return null;
        try
        {
            if (topology.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
                return DocumentFolder is { } dir && DataImport.ResolveSource(dir, topology) is var p && File.Exists(p)
                    ? SchematicPersistence.LoadFromFile(p).model.BuildRenderModel().Model : null;
            return SchematicPersistence.Deserialize(PassiveTopologies.Text(topology)).model.BuildRenderModel().Model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Ranks the library's topologies against the block (MC-12 R-mc12-4) and lists the three offered.</summary>
    public async Task<bool> ProposeAsync()
    {
        if (IsRunning && !_isRefresh) { Warn($"{TaskStage} is running; cancel it or wait for it to finish."); return false; }
        if (!await EnsureWritten()) return false;
        string path = DocumentPath!;
        return await StartTask("Proposing topologies", refresh: false, writesFile: false, rc =>
        {
            var notes = new List<string>();
            var ranked = ModelCommands.Propose(path, notes, rc);
            return (ranked, notes);
        }, r =>
        {
            var (ranked, notes) = ((IReadOnlyList<PassiveProposal>, List<string>))r!;
            Proposals.Clear();
            foreach (var p in ranked.Where(p => p.Offered).Take(3))
                Proposals.Add(new ProposalRow(p.Rank, p.Fit.Topology, p.Title,
                                              $"|ΔS| {p.Fit.RmsDeltaS.ToString("G3", CultureInfo.InvariantCulture)}", TopologyDrawing(p.Fit.Topology)));
            ProposalNote = Proposals.Count == 0 && notes.Count > 0 ? notes[0] : "";
            if (ProposalNote.Length > 0) Info("No topology offered.");     // the step says why; the status need not repeat it
            else if (notes.Count > 0) Warn(notes[0]);
            else Info($"{Proposals.Count} topologies offered.");
        });
    }

    /// <summary>The block's analysis lines (MC-12 R-mc12-2).</summary>
    public async Task<bool> AnalyseAsync()
    {
        if (IsRunning && !_isRefresh) { Warn($"{TaskStage} is running; cancel it or wait for it to finish."); return false; }
        if (!await EnsureWritten()) return false;
        string path = DocumentPath!;
        return await StartTask("Analysing", refresh: false, writesFile: false, rc => ModelCommands.Analyse(path, rc), r =>
        {
            AnalysisLines.Clear();
            foreach (string l in (IReadOnlyList<string>)r!) AnalysisLines.Add(l);
            Info(AnalysisLines.Count > 0 ? AnalysisLines[0] : "Analysed.");
        });
    }

    /// <summary>Makes a proposal the step's topology — one undo step.</summary>
    [RelayCommand]
    private void ChooseTopology(ProposalRow? row)
    {
        if (row is null || !IsPassive) return;
        EditPart($"Topology: {row.Title}", PassiveStep.Settings, PassiveStep.WithSettings,
                 PassiveStep.Settings(Document) with { Topology = row.Topology });
    }

    private bool InstallTopology(string json)
    {
        try
        {
            var next = ModelCommands.InstallTopology(Document, DocumentPath!, "topology", json);
            var settings = PassiveStep.Settings(next);
            return EditPart($"Topology pasted: {settings.Topology}", PassiveStep.Settings, PassiveStep.WithSettings, settings);
        }
        catch (ModelingException ex)
        {
            Error(ex.Message);
            return false;
        }
    }

    private async Task<bool> CopyTopology()
    {
        string t = TopologyName;
        if (t.Length == 0) { Warn("There is no topology to copy."); return false; }
        SchematicEditModel model;
        try
        {
            model = t.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
                ? SchematicPersistence.LoadFromFile(DataImport.ResolveSource(DocumentFolder!, t)).model
                : SchematicPersistence.Deserialize(PassiveTopologies.Text(t)).model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error(ex.Message);
            return false;
        }
        if (NetworkCopySink is not null) await NetworkCopySink(model);
        Info($"Copied {t}.");
        return true;
    }
}
