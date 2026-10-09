using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Design ▸ Create Schematic from Artwork… (brief-artsch-8-gui-command.md R-as8-1, R-as8-4, R-as8-5) — the shell around
/// the dialog: opening it on the focused layout, writing what it creates into the workspace's surfaces (the schematic
/// opened focused, the report in Messages), and the cross-probe back to the artwork from either.
///
/// <para><b>It never runs except from the user's invocation</b> (the L5 rule): no save, open or activation hook
/// reaches it. And it never writes the layout — what it writes is the schematic, through
/// <see cref="ArtworkRecognition.Run"/>, the function <c>circuitrf recognize --into</c> calls (overview D2, D5).</para>
/// </summary>
public partial class WorkspaceViewModel
{
    private CreateSchematicFromArtworkDialog? _artworkDialog;

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private async Task CreateSchematicFromArtwork(Window? owner)
    {
        if (ResolveActiveDocumentForCommands() is not LayoutDocument doc) return;

        // The same refusal Update Schematic from Layout gives a scratch layout: no file, no cell to write beside.
        if (doc.FilePath is not { Length: > 0 } layoutPath)
        {
            Messages.Error("Create Schematic from Artwork: save the layout first — a scratch layout has no cell to write beside.");
            return;
        }
        layoutPath = Path.GetFullPath(layoutPath);

        if (_artworkDialog is { } open) { open.Activate(); return; }

        var layoutVm = doc.ActiveViewModel;
        if (layoutVm.IsDirty)
            Messages.Warning("Create Schematic from Artwork reads the saved layout; save it to include the edits made since.");

        var selection = layoutVm.SelectionOutline();
        RecognitionInput input;
        try { input = await Task.Run(() => ArtworkRecognitionRunner.Instance.Load(layoutPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Messages.Error($"Create Schematic from Artwork: the layout could not be read: {ex.Message}");
            return;
        }

        var vm = new CreateSchematicFromArtworkViewModel(input, selection, post: a => Dispatcher.UIThread.Post(a));
        vm.PartHighlighted = (rings, focus) =>
        {
            if (rings is null) layoutVm.ClearArtworkProbe();
            else layoutVm.ShowArtworkProbe(rings, focus, zoom: false);
        };
        vm.PickGroundRequested = onPicked => layoutVm.ArmArtworkPointPick(
            "Create Schematic from Artwork: click the copper that is ground. Escape cancels.", onPicked);

        var dialog = new CreateSchematicFromArtworkDialog(vm);
        vm.Created += run =>
        {
            ReportArtworkSchematic(run, layoutPath);
            dialog.Close();
        };
        dialog.Closed += (_, _) => _artworkDialog = null;
        _artworkDialog = dialog;
        if (ResolveOwner(owner) is { } window) dialog.Show(window);
        else dialog.Show();
    }

    /// <summary>
    /// R-as8-4: the schematic opens focused, and the report goes to Messages — one line per class, expandable to its
    /// anchors, each of which a double-click takes to the artwork (R-as8-5). The Tuning panel follows the focused
    /// schematic, so its unknown-value variables are listed there with nothing more done here.
    /// </summary>
    private void ReportArtworkSchematic(RecognitionRun run, string layoutPath)
    {
        _factory.ProjectTreeTool?.Refresh();
        foreach (var finding in run.Report.Findings)
        {
            var level = finding.Class == RecognitionFindingClass.SchematicWritten ? MessageLevel.Success : MessageLevel.Info;
            var items = finding.Anchors
                .Select(a => new MessageItem(
                    CreateSchematicFromArtworkViewModel.AnchorText(run.Result.Parts, a),
                    () => ProbeArtworkAt(layoutPath, a)))
                .ToList();
            string? file = finding.Class == RecognitionFindingClass.SchematicWritten ? run.SchematicPath : null;
            if (items.Count > 0) Messages.PostItems(level, $"Create Schematic from Artwork: {finding.Sentence}", items, file);
            else Messages.Post(level, $"Create Schematic from Artwork: {finding.Sentence}", file);
        }
        if (run.SchematicPath is { } schematic) OpenOrActivateSchematic(Path.GetFullPath(schematic));
    }

    /// <summary>R-as8-5: a report anchor — select the copper there and zoom to it, with the probe drawn over it.</summary>
    private void ProbeArtworkAt(string layoutPath, RecognitionAnchor anchor)
    {
        OpenOrActivateLayout(layoutPath);
        var layoutVm = GetOrCreateLayoutSession(layoutPath);
        var rings = ArtworkCrossProbe.FindingRings(anchor);
        if (!layoutVm.SelectArtworkAt(anchor.X, anchor.Y, anchor.Layer))
            layoutVm.RequestZoomToRegion(ArtworkCrossProbe.Extent(rings));
        layoutVm.ShowArtworkProbe(rings, CircuitRF.Design.Layout.Bbox.Empty, zoom: false);
    }

    /// <summary>
    /// R-as8-5: a <c>FromArtwork</c> component's Show in Artwork — the source layout opened from the provenance block
    /// and the anchor drawn and framed: a line element's centre line, a part's pads. A probe that cannot point says why.
    /// </summary>
    public void ShowInArtwork(SchematicDocument doc, EditableComponent comp)
    {
        var target = ArtworkCrossProbe.Resolve(doc.FilePath, doc.ViewModel.EditModel, comp);
        if (!target.Ok) { Messages.Warning($"Show in Artwork: {target.Refusal}"); return; }
        OpenOrActivateLayout(target.ClayPath!);
        GetOrCreateLayoutSession(target.ClayPath!).ShowArtworkProbe(target.Rings, target.Focus, zoom: true);
    }
}
