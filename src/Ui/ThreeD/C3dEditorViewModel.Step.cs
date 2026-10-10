// brief-em3d-68 — STEP parts in the 3D editor: the Import STEP commit, Reload from Source, and the copies a save removes.
//
// EVERY DECISION IS StepImport's (src/Design): which parts, their names, their materials, the copy, the fingerprints. This
// file runs its reads off the UI thread, pushes ONE undo entry per import or reload, and keeps the copied bytes so an
// entry never names a file that is not there.
//
// THE COPY OUTLIVES ITS UNDO UNTIL THE SAVE (R-em3d68-4d). OK writes the copy into the view's folder at once (the
// elaboration builds the part from it); Undo removes the objects and leaves the file; the next save deletes a copy that
// nothing names AND that this session wrote — never a file it did not create. An entry keeps the bytes of every file it
// names, so a redo after that save writes the copy back before it applies.

using System.Collections.ObjectModel;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

/// <summary>
/// brief-em3d-68 — an entry that names files in the 3D view's folder: before applying either way it writes back any of
/// them a save has since removed. The bytes are the entry's own, so undo and redo never depend on what is on disk.
/// </summary>
public sealed class C3dFilesEdit(IUiCommand inner, IReadOnlyList<(string Path, byte[] Bytes)> after,
                                 IReadOnlyList<(string Path, byte[] Bytes)> before) : IUiCommand, IC3dObjectTextEntry
{
    public string Description => inner.Description;

    public void RewriteObjects(Func<string, string> objectText, Func<string, string> documentText)
        => (inner as IC3dObjectTextEntry)?.RewriteObjects(objectText, documentText);

    public void Execute()
    {
        Restore(after);
        inner.Execute();
    }

    public void Undo()
    {
        Restore(before);
        inner.Undo();
    }

    private static void Restore(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        foreach (var (path, bytes) in files)
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
    }
}

public sealed partial class C3dEditorViewModel
{
    /// <summary>Copies an import or a reload wrote into a 3D view's folder in this session — the only files a save may delete.</summary>
    private readonly HashSet<string> _stepCopiesWritten = new(StringComparer.Ordinal);

    /// <summary>STEP imports committed.</summary>
    public int StepImports { get; private set; }

    /// <summary>What the last import or reload said it did, for the shell to post to the Messages panel.</summary>
    public IReadOnlyList<string> LastStepNotes { get; private set; } = [];

    /// <summary>brief-em3d-68 R-em3d68-5d — a reload found parts no object names; the shell offers them in the import table,
    /// unchecked. The source path and the new parts' occurrence paths.</summary>
    public Func<string, IReadOnlyList<string>, Task>? OfferStepParts { get; set; }

    /// <summary>Why Import STEP cannot run in this editor now, or null: the kernel first, then a design that has no folder yet.</summary>
    public string? ImportStepRefusal()
        => KernelMissing("Import STEP")
           ?? (IsScratch ? "Save this 3D design first: an imported STEP file is copied beside it." : null);

    /// <summary>Reads <paramref name="sourcePath"/> for the Import STEP table. Off the UI thread; <paramref name="control"/>
    /// cancels it by killing the worker, and nothing has changed.</summary>
    public StepImportPlan ReadStep(string sourcePath, RunControl? control)
        => StepImport.Read(sourcePath, Kernel, Elaboration?.Technology, Document, control);

    /// <summary>
    /// The dialog's OK (R-em3d68-4d): the copy written, one Step object per checked row appended, ONE undo entry, and the new
    /// objects selected, ready to Move — every member of the import's group, which is the group taken whole
    /// (<see cref="C3dGroups.Units"/>, R-em3d128-2d). Undo removes the objects, so the group with them. Null on success, else
    /// why nothing was imported.
    /// </summary>
    public string? AcceptStepImport(StepImportPlan plan)
    {
        if (ImportStepRefusal() is { } refusal) return refusal;
        var working = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        StepImportResult result;
        try
        {
            result = StepImport.Apply(plan, working, FilePath);
        }
        catch (StepImportException e) { return e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Could not copy the STEP file: {e.Message}"; }
        if (result.Created) _stepCopiesWritten.Add(result.CopiedPath);
        var before = C3dListsEdit.Of(Document);
        var after = C3dListsEdit.Of(working);
        string what = result.Objects.Count == 1 ? $"'{result.Objects[0].Name}'"
                    : C3dGroups.TopOf(result.Objects[0].Group) is { } group ? $"'{group}' ({result.Objects.Count} objects)"
                    : $"{result.Objects.Count} parts";
        string description = $"Import {what} from {Path.GetFileName(plan.SourcePath)}";
        Viewer.SetSelection([]);
        _selectAfterAdopt = [.. result.Objects.Select(o => o.Name)];
        Push(new C3dFilesEdit(new C3dListsEdit(description, before, after, ApplyLists), [(result.CopiedPath, result.Bytes)], []));
        StepImports++;
        LastStepNotes = result.Notes;
        StatusMessage = $"{description}." + (result.Notes.Count > 0 ? $" {result.Notes.Count} note{(result.Notes.Count == 1 ? "" : "s")} in Messages." : "");
        return null;
    }

    // ── Reload from Source (R-em3d68-5) ──────────────────────────────────────────────────────

    /// <summary>The Step object at <paramref name="index"/> — the object itself, or the first one inside it (a boolean's
    /// operand) — or null.</summary>
    public C3dStep? StepAt(int index)
        => index >= 0 && index < Document.Objects.Count
            ? C3dOperands.SelfAndDescendants(Document.Objects[index]).OfType<C3dStep>().FirstOrDefault()
            : null;

    /// <summary>Why Reload from Source is disabled for <paramref name="step"/>, or null.</summary>
    public string? ReloadRefusal(C3dStep? step)
        => KernelMissing("Reload from Source")
           ?? (step is null ? "Select an imported STEP part." : null)
           ?? (StepImport.ResolveSource(step!, FilePath) is not { } src ? $"'{step!.Name}' records no source to reload from."
               : !File.Exists(src) ? $"The source of '{step!.Name}' ({step.SourcePath}) is not there to reload from." : null);

    /// <summary>Reloads made (accepted or not) — a counter for the gates.</summary>
    public int StepReloads { get; private set; }

    /// <summary>
    /// Reload from Source for every object naming <paramref name="step"/>'s file: read off the UI thread, then ONE entry
    /// that moves the accepted objects to the revised copy and re-points their references. Unchanged bytes change nothing
    /// and say so (R-em3d68-5b); a part whose references no longer land keeps the old file and is named (R-em3d68-5c).
    /// </summary>
    public async Task ReloadFromSourceAsync(C3dStep step, RunControl? control = null)
    {
        if (ReloadRefusal(step) is { } why) { StatusMessage = why; return; }
        StepReloads++;
        string file = step.File;
        string before = C3dPersistence.Serialize(Document);
        var working = C3dPersistence.Deserialize(before);
        string path = FilePath;
        var kernel = Kernel;
        StepReloadPlan plan;
        try
        {
            plan = await Task.Run(() => StepImport.PlanReload(working, path, file, kernel, control));
        }
        catch (OperationCanceledException) { StatusMessage = "Reload cancelled; nothing changed."; return; }
        catch (Exception e) when (e is StepImportException or CircuitRF.Design.ThreeD.Occ.GeometryKernelException or IOException)
        {
            StatusMessage = e.Message;
            LastStepNotes = [e.Message];
            return;
        }
        if (plan.NoChange)
        {
            StatusMessage = $"'{file}' is unchanged at its source; nothing to reload.";
            LastStepNotes = [];
            return;
        }
        if (C3dPersistence.Serialize(Document) != before)
        {
            StatusMessage = "The document changed while its STEP source was being read: reload again.";
            return;
        }
        var notes = new List<string>(plan.Refusals);
        notes.AddRange(plan.Repoints.Select(r => r.ToString()));
        notes.AddRange(plan.SolidMoves.Select(m => m.ToString()));
        if (plan.Accepted.Count > 0)
        {
            string oldPath = Path.Combine(Path.GetDirectoryName(path)!, file);
            byte[] oldBytes = File.Exists(oldPath) ? await File.ReadAllBytesAsync(oldPath) : [];
            (string Path, bool Created) copy;
            try { copy = StepImport.ApplyReload(plan, working, path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                StatusMessage = $"Could not copy the revised STEP file: {e.Message}";
                return;
            }
            if (copy.Created) _stepCopiesWritten.Add(copy.Path);
            string after = C3dPersistence.Serialize(working);
            string description = $"Reload {file} from source";
            Push(new C3dFilesEdit(new C3dDocumentEdit(description, before, after, null, null, null, ApplyDocumentText),
                                  [(copy.Path, plan.Bytes)], oldBytes.Length > 0 ? [(oldPath, oldBytes)] : []));
            notes.Insert(0, $"Reloaded {plan.Accepted.Count} part{(plan.Accepted.Count == 1 ? "" : "s")} of '{file}' from {plan.SourcePath}" +
                            $" (now {Path.GetFileName(copy.Path)}).");
        }
        else notes.Insert(0, $"The reload of '{file}' was refused for every part; nothing changed.");
        LastStepNotes = notes;
        StatusMessage = notes[0] + (notes.Count > 1 ? $" {notes.Count - 1} more in Messages." : "");
        if (plan.NewParts.Count > 0 && OfferStepParts is { } offer)
            await offer(plan.SourcePath, [.. plan.NewParts.Select(p => p.Path)]);
    }

    // ── the save (R-em3d68-4d) ───────────────────────────────────────────────────────────────

    /// <summary>After a save: every copy this session wrote that no open frame's Step objects name is deleted. Returns the
    /// paths removed.</summary>
    internal IReadOnlyList<string> RemoveUnnamedStepCopies()
    {
        if (_stepCopiesWritten.Count == 0) return [];
        var named = new HashSet<string>(StringComparer.Ordinal);
        void Name(C3dDocument doc, string c3d)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(c3d))!;
            foreach (var s in doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dStep>())
                named.Add(Path.GetFullPath(Path.Combine(dir, s.File)));
        }
        Name(Document, FilePath);
        foreach (var f in _frames) Name(f.Document, f.FilePath);
        var removed = new List<string>();
        foreach (string p in _stepCopiesWritten.ToList())
        {
            if (named.Contains(Path.GetFullPath(p))) continue;
            try
            {
                if (File.Exists(p)) File.Delete(p);
                removed.Add(p);
                _stepCopiesWritten.Remove(p);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kept; tried again at the next save */ }
        }
        return removed;
    }

    // ── the menus ────────────────────────────────────────────────────────────────────────────

    /// <summary>The canvas menu's Reload from Source, for one selected object that is (or holds) a Step part.</summary>
    private IEnumerable<Viewer3DMenuItem> StepMenuItems()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object || Targets() is not [{ Instance: false } t] || IsOperandIndex(t.Index)) yield break;
        if (StepAt(t.Index) is not { } step) yield break;
        yield return ReloadItem(step);
    }

    private Viewer3DMenuItem ReloadItem(C3dStep step)
    {
        string? why = ReloadRefusal(step);
        return new Viewer3DMenuItem("Reload from Source", () => _ = ReloadFromSourceAsync(step), Enabled: why is null,
            Tip: why ?? $"Read {step.SourcePath} again and move every part of '{step.File}' to it, re-checking each face a port, boundary or fillet uses.");
    }
}
