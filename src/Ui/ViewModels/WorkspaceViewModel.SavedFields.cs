// brief-dfr32-3 R-dfr32-3-2 — Delete Saved Fields on a project-tree row: a .c3d's embedded setups, or a .cem's own run. The work
// is PalaceFieldFiles (src/Design); an open 3D view's own command is used for its setups, so its field plots are read again.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>The (setup, run folder) pairs a <c>.c3d</c> or <c>.cem</c> row's runs live in: each embedded setup's, or the
    /// <c>.cem</c>'s own. Empty for any other row, or a file that cannot be read.</summary>
    private List<(string Setup, string RunDir)> SavedFieldRuns(string path)
    {
        var runs = new List<(string, string)>();
        if (CurrentWorkspacePath is null) return runs;
        string root = EmResultsRoot();
        try
        {
            if (path.EndsWith(C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase))
            {
                var doc = OpenC3dEditorFor(path)?.Document ?? C3dPersistence.LoadFromFile(path);
                foreach (var read in C3dSetups.Read(doc))
                    if (read.Setup is { IsThermal: false } s && FieldPlotResolver.RunDirectories(C3dSetups.ForRun(s, path), root).Palace is { } dir)
                        runs.Add((s.Name, dir));
            }
            else if (EmSetupPersistence.LoadFromFile(path) is { IsThermal: false } cem && FieldPlotResolver.RunDirectories(cem, root).Palace is { } dir)
                runs.Add((EmRunService.ResolveResultKey(cem), dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or C3dReadException or System.Text.Json.JsonException
                                     or InvalidOperationException or ArgumentException) { }
        return runs;
    }

    public bool HasSavedFields(ProjectTreeNodeViewModel node)
        => SavedFieldRuns(node.AbsolutePath).Any(r => PalaceFieldFiles.Directories(r.RunDir).Count > 0);

    public void DeleteSavedFields(ProjectTreeNodeViewModel node)
    {
        string path = node.AbsolutePath;
        var runs = SavedFieldRuns(path).Where(r => PalaceFieldFiles.Directories(r.RunDir).Count > 0).ToList();
        if (runs.Count == 0) { Messages.Info($"'{Path.GetFileName(path)}' has no saved fields to delete.", path); return; }
        var editor = path.EndsWith(C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase) ? OpenC3dEditorFor(path) : null;
        foreach (var (setup, dir) in runs)
        {
            if (editor is not null) { editor.DeleteSavedFields(setup); continue; }       // it posts its own line, and re-reads its plots
            var result = PalaceFieldFiles.Delete(dir, setup, C3dEditorViewModel.DefaultMoveToTrash);
            if (result.Failed.Count > 0) Messages.Warning(result.Sentence, path); else Messages.Info(result.Sentence, path);
        }
        InvalidateTreeSolve(path);
    }
}
