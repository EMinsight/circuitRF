// brief-em3d-68 — STEP parts in the 3D editor: the Import STEP commit, Reload from Source, Split into Solids (brief-em3d-129),
// Replace with Prism / Box and Convert to Polyhedron (brief-em3d-130), and the copies a save removes.
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

    // ── Split into Solids (brief-em3d-129) ───────────────────────────────────────────────────

    /// <summary>Splits made — a counter for the gates.</summary>
    public int StepSplits { get; private set; }

    /// <summary>What a split or a refused one has to say, for the shell to post to the Messages panel: the lines, and whether
    /// they are refusals.</summary>
    public event Action<IReadOnlyList<string>, bool>? StepReported;

    /// <summary>
    /// R-em3d129-3a — why Split into Solids is disabled for the object at <paramref name="index"/>, or null: the kernel, then a
    /// top-level Step object with no Solid that the elaboration built as more than one solid.
    /// </summary>
    public string? SplitRefusal(int index)
    {
        if (KernelMissing("Split into Solids") is { } kernel) return kernel;
        if (IsOperandIndex(index) || index < 0 || index >= Document.Objects.Count) return "Select one imported STEP object.";
        var obj = Document.Objects[index];
        if (obj is not C3dStep step)
            return StepAt(index) is not null
                ? $"The STEP part in '{obj.Name}' is an operand of it; split a STEP object before it is combined."
                : $"'{obj.Name}' is not an imported STEP object.";
        if (step.Solid is { } k) return $"'{step.Name}' is already one solid (solid {k}) of its part.";
        var built = Elaboration?.KernelBuilds.FirstOrDefault(b => b.Name == step.Name);
        if (built is null || built.Refusal is not null) return $"'{step.Name}' is not built, so its solids are not counted.";
        if (built.Solids < 2) return $"'{step.Name}' is one solid; there is nothing to split.";
        return null;
    }

    /// <summary>Split into Solids for the view's selection: exactly one object, then <see cref="SplitRefusal"/>.</summary>
    public string? SplitSelectionRefusal()
        => Targets() switch
        {
            [{ Instance: false } t] => SplitRefusal(t.Index),
            [] => KernelMissing("Split into Solids") ?? "Select one imported STEP object.",
            _ => KernelMissing("Split into Solids") ?? "Select exactly one object: Split into Solids acts on one STEP object.",
        };

    /// <summary>
    /// R-em3d129-3b — Split into Solids: the plan read off the UI thread, every refusal to Messages at once, a confirmation
    /// naming any solid that is not closed and would be left out, then ONE undo entry, and the new group selected whole.
    /// </summary>
    public async Task SplitIntoSolidsAsync(int index, RunControl? control = null)
    {
        if (SplitRefusal(index) is { } why) { StatusMessage = why; return; }
        string name = Document.Objects[index].Name;
        string before = C3dPersistence.Serialize(Document);
        var working = C3dPersistence.Deserialize(before);
        string path = FilePath;
        var kernel = Kernel;
        StepSplitPlan plan;
        try
        {
            plan = await Task.Run(() => StepSplit.Plan(working, path, name, kernel, control));
        }
        catch (OperationCanceledException) { StatusMessage = "Split cancelled; nothing changed."; return; }
        catch (Exception e) when (e is StepImportException or CircuitRF.Design.ThreeD.Occ.GeometryKernelException or IOException)
        {
            Report([e.Message], true);
            return;
        }
        if (plan.Refused)
        {
            Report([$"'{name}' was not split: {plan.Refusals.Count} reason{(plan.Refusals.Count == 1 ? "" : "s")}.", .. plan.Refusals], true);
            return;
        }
        bool drop = false;
        if (plan.Dropped.Count > 0)
        {
            string question = $"'{name}' has {plan.Pieces.Count + plan.Dropped.Count} solids, and {string.Join("; ", plan.Dropped)} " +
                              $"{(plan.Dropped.Count == 1 ? "is" : "are")} not a closed solid, so splitting leaves {(plan.Dropped.Count == 1 ? "it" : "them")} out. " +
                              $"Split '{name}' into the other {plan.Pieces.Count}?";
            if (Confirm is not { } ask || !await ask(question)) { StatusMessage = "Split cancelled; nothing changed."; return; }
            drop = true;
        }
        if (C3dPersistence.Serialize(Document) != before)
        {
            StatusMessage = $"The document changed while '{name}' was being read: split it again.";
            return;
        }
        StepSplit.Apply(plan, working, drop);
        string description = $"Split {name} into {plan.Pieces.Count} solids";
        Viewer.SetSelection([]);
        _selectAfterAdopt = [.. plan.Pieces.Select(p => p.Name)];
        Push(new C3dDocumentEdit(description, before, C3dPersistence.Serialize(working), null, null, null, ApplyDocumentText));
        StepSplits++;
        var notes = new List<string> { $"{description}, gathered in group '{C3dGroups.NameOf(plan.Group)}'." };
        if (plan.Dropped.Count > 0) notes.Add($"Left out, not closed: {string.Join("; ", plan.Dropped)}.");
        notes.AddRange(plan.Moves.Select(m => m.ToString()));
        Report(notes, false);
    }

    private void Report(IReadOnlyList<string> lines, bool refused)
    {
        LastStepNotes = lines;
        StatusMessage = lines[0] + (lines.Count > 1 ? $" {lines.Count - 1} more in Messages." : "");
        StepReported?.Invoke(lines, refused);
    }

    private Viewer3DMenuItem SplitItem(string? why, Action run)
        => new("Split into Solids", run, Enabled: why is null,
               Tip: why ?? "One object per solid of this STEP part, gathered in a group named after it; each keeps the placement and material, " +
                           "and every face reference moves to the piece whose face it is.");

    // ── Replace with Prism / Box, Convert to Polyhedron (brief-em3d-130) ─────────────────────

    /// <summary>Replacements made — a counter for the gates.</summary>
    public int StepConverts { get; private set; }

    private Task<StepConvertAnalysis>? _convertAnalysis;
    private (long Generation, string Name) _convertKey;

    /// <summary>
    /// Why <paramref name="kind"/> is disabled for the object at <paramref name="index"/>, or null. The cheap conditions first —
    /// the kernel, a top-level Step object, built as ONE solid — then the analysis of its faces, which is read off the UI thread
    /// once per elaboration: until it is in, the item says so and is disabled, and the menus are re-asked when it lands.
    /// </summary>
    public string? ConvertRefusal(StepConvertKind kind, int index)
    {
        if (ConvertBaseRefusal(kind, index) is { } why) return why;
        var analysis = AnalyseForConvert(index);
        if (!analysis.IsCompleted) return $"Reading the faces of '{Document.Objects[index].Name}'…";
        var plan = analysis.Result[kind];
        return plan.Refused ? string.Join(" ", plan.Refusals) : null;
    }

    /// <summary><see cref="ConvertRefusal"/> for the view's selection: exactly one object.</summary>
    public string? ConvertSelectionRefusal(StepConvertKind kind)
        => Targets() switch
        {
            [{ Instance: false } t] => ConvertRefusal(kind, t.Index),
            [] => KernelMissing(StepConvert.CommandOf(kind)) ?? "Select one imported STEP object.",
            _ => KernelMissing(StepConvert.CommandOf(kind)) ?? $"Select exactly one object: {StepConvert.CommandOf(kind)} acts on one STEP object.",
        };

    private string? ConvertBaseRefusal(StepConvertKind kind, int index)
    {
        if (KernelMissing(StepConvert.CommandOf(kind)) is { } kernel) return kernel;
        if (IsOperandIndex(index) || index < 0 || index >= Document.Objects.Count) return "Select one imported STEP object.";
        var obj = Document.Objects[index];
        if (obj is not C3dStep step)
            return StepAt(index) is not null
                ? $"The STEP part in '{obj.Name}' is an operand of it; replace a STEP object before it is combined."
                : $"'{obj.Name}' is not an imported STEP object.";
        var built = Elaboration?.KernelBuilds.FirstOrDefault(b => b.Name == step.Name);
        if (built is null || built.Refusal is not null) return $"'{step.Name}' is not built, so its faces are not known.";
        if (built.Solids > 1) return $"'{step.Name}' is {built.Solids} solids; Split into Solids first, then replace each piece.";
        return null;
    }

    /// <summary>The three plans for the object at <paramref name="index"/>, read off the UI thread from a copy of the document —
    /// once per elaboration and object; the menus are re-asked when it completes.</summary>
    public Task<StepConvertAnalysis> AnalyseForConvert(int index)
    {
        var key = (AdoptedGeneration, Document.Objects[index].Name);
        if (_convertAnalysis is { } running && _convertKey == key) return running;
        var working = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        string path = FilePath;
        var kernel = Kernel;
        _convertKey = key;
        var task = Task.Run(() =>
        {
            try { return StepConvert.Analyse(working, path, key.Name, kernel); }
            catch (Exception e) when (e is StepImportException or CircuitRF.Design.ThreeD.Occ.GeometryKernelException or IOException)
            {
                return StepConvert.Refused(key.Name, index, e.Message);
            }
        });
        _convertAnalysis = task;
        task.ContinueWith(_ => _post(RaiseMenuStateChanged), TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// brief-em3d-130 — Replace with Prism, Replace with Box… or Convert to Polyhedron: a fresh plan read off the UI thread (the
    /// document it is applied to is the one it was read from), every refusal to Messages at once, a confirmation stating what
    /// changes for any result that is not the piece exactly (a box's size and volume; a curved face's facets), then ONE undo
    /// entry, and the object selected.
    /// </summary>
    public async Task ReplaceStepAsync(StepConvertKind kind, int index, RunControl? control = null)
    {
        if (ConvertBaseRefusal(kind, index) is { } why) { StatusMessage = why; return; }
        string name = Document.Objects[index].Name;
        string before = C3dPersistence.Serialize(Document);
        var working = C3dPersistence.Deserialize(before);
        string path = FilePath;
        var kernel = Kernel;
        StepConvertPlan plan;
        try
        {
            plan = await Task.Run(() => StepConvert.Analyse(working, path, name, kernel, control)[kind]);
        }
        catch (OperationCanceledException) { StatusMessage = $"{StepConvert.CommandOf(kind)} cancelled; nothing changed."; return; }
        catch (Exception e) when (e is StepImportException or CircuitRF.Design.ThreeD.Occ.GeometryKernelException or IOException)
        {
            Report([e.Message], true);
            return;
        }
        if (plan.Refused)
        {
            Report([$"'{name}' was not changed: {plan.Refusals.Count} reason{(plan.Refusals.Count == 1 ? "" : "s")}.", .. plan.Refusals], true);
            return;
        }
        if (!plan.Exact && (Confirm is not { } ask || !await ask(plan.Summary + (kind == StepConvertKind.Polyhedron ? " Convert it?" : " Replace it?"))))
        {
            StatusMessage = $"{StepConvert.CommandOf(kind)} cancelled; nothing changed.";
            return;
        }
        if (C3dPersistence.Serialize(Document) != before)
        {
            StatusMessage = $"The document changed while '{name}' was being read: try {StepConvert.CommandOf(kind)} again.";
            return;
        }
        StepConvert.Apply(plan, working);
        string description = kind switch
        {
            StepConvertKind.Prism => $"Replace {name} with a prism",
            StepConvertKind.Box => $"Replace {name} with its bounding box",
            _ => $"Convert {name} to a polyhedron",
        };
        Viewer.SetSelection([]);
        _selectAfterAdopt = [name];
        Push(new C3dDocumentEdit(description, before, C3dPersistence.Serialize(working), null, null, null, ApplyDocumentText));
        StepConverts++;
        Report([plan.Summary, .. plan.Moves.Select(m => m.ToString())], false);
    }

    /// <summary>The three items, each enabled only where it applies and saying why not otherwise.</summary>
    private IEnumerable<Viewer3DMenuItem> ConvertItems(int index)
    {
        foreach (var kind in new[] { StepConvertKind.Prism, StepConvertKind.Box, StepConvertKind.Polyhedron })
        {
            string? why = ConvertRefusal(kind, index);
            var k = kind;
            yield return new Viewer3DMenuItem(kind == StepConvertKind.Box ? "Replace with Box…" : StepConvert.CommandOf(kind),
                () => _ = ReplaceStepAsync(k, index), Enabled: why is null, Tip: why ?? ConvertTip(kind));
        }
    }

    /// <summary>What each does, for an enabled item.</summary>
    public static string ConvertTip(StepConvertKind kind) => kind switch
    {
        StepConvertKind.Prism => "This piece as a prism along x, y or z: its outline and length become ordinary parameters. A curved face " +
                                 "is cut into flat facets, stated before anything changes. Every face reference moves to the face that coincides.",
        StepConvertKind.Box => "This piece as its bounding box, whose size is then a parameter. Where the box is not the piece, the size " +
                               "and the volume change are stated first; a face reference with no coinciding box face is refused.",
        _ => "This piece as a polyhedron, so its faces and vertices can be edited: its flat faces exactly, each curved face cut into " +
             "flat facets, stated before anything changes. Every reference on a flat face stays on it.",
    };

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

    /// <summary>The canvas menu's Reload from Source and Split into Solids, for one selected object that is (or holds) a Step part.</summary>
    private IEnumerable<Viewer3DMenuItem> StepMenuItems()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object || Targets() is not [{ Instance: false } t] || IsOperandIndex(t.Index)) yield break;
        if (StepAt(t.Index) is not { } step) yield break;
        yield return ReloadItem(step);
        int index = t.Index;
        yield return SplitItem(SplitRefusal(index), () => _ = SplitIntoSolidsAsync(index));
        foreach (var item in ConvertItems(index)) yield return item;     // brief-em3d-130
    }

    private Viewer3DMenuItem ReloadItem(C3dStep step)
    {
        string? why = ReloadRefusal(step);
        return new Viewer3DMenuItem("Reload from Source", () => _ = ReloadFromSourceAsync(step), Enabled: why is null,
            Tip: why ?? $"Read {step.SourcePath} again and move every part of '{step.File}' to it, re-checking each face a port, boundary or fillet uses.");
    }
}
