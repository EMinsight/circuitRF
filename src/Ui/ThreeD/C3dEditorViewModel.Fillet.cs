// brief-em3d-67 — fillets and chamfers in the 3D editor: Fillet… and Chamfer… on the selected edges of one solid, the panel
// (brief 66's popover, reused), its asynchronous preview, the commit, and the feature rows' edits.
//
// EVERY RULE IS C3dFillets' (src/Design): what may be rounded and why not, the object that is made (it WRAPS its target and
// takes its name and place), how it is removed, and the sentences a refusal is said in. This file asks the kernel, shows its
// answers, and pushes ONE undo entry.
//
// THE PANEL'S EDGE LIST IS THE TRUTH, AND THE VIEW'S SELECTION SHOWS IT (R-em3d67-5b). The panel is not modal: while it is
// open a click, a Shift-click or a double-click (a chain) in Edge mode changes the view's selection, and the list follows it;
// an edge of another object is refused on the status line and the selection is put back. A scene the preview regenerates
// renumbers its edges, so the list — names — is written back into the selection after every adoption, never read from it.
//
// THE PREVIEW (R-em3d67-5c) is brief 66's: asynchronous, the newest request superseding the older, OK disabled until a valid
// reply. What it draws differs in one respect, because the list is edited while the preview shows: the RESULT is drawn
// under the object's own name (so the commit's elaboration finds its tree in the cache and asks the kernel nothing), and
// the TARGET beside it as "<name>:target" — translucent but still pickable, so its edges, whose names the list holds, can be
// clicked through the preview.

using System.Collections.ObjectModel;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One edge of the Fillet panel's list, with its remove ×.</summary>
public sealed partial class C3dFilletEdgeRow(C3dEditorViewModel owner, string name) : ObservableObject
{
    public string Name { get; } = name;

    [RelayCommand]
    private void Remove() => owner.RemoveFilletEdge(Name);
}

public sealed partial class C3dEditorViewModel
{
    /// <summary>What the preview draws a fillet's target under, beside its result.</summary>
    public const string FilletTargetSuffix = ":target";

    // ── what may be rounded (R-em3d67-5a) ──────────────────────────────────────────────────────

    /// <summary>
    /// Why Fillet… or Chamfer… is disabled for the selection now, or null when it is enabled: the kernel first, then an edge
    /// selection, one object, and a document's own solid.
    /// </summary>
    public string? EdgeOpRefusal(C3dEdgeOp op)
    {
        var edges = Viewer.SelectMode == Scene3DSelectMode.Edge ? Viewer.Selection.Where(i => i.IsEdge).ToList() : [];
        // brief-em3d-102 R-em3d102-3d — a selected sphere: say it has no edges, kernel or not, rather than ask for some.
        if (edges.Count == 0 && Viewer.SelectedObjects() is [var only] && DocumentIndex(only) is >= 0 and var si && Document.Objects[si] is C3dSphere)
            return C3dFillets.SphereRefused(op);
        if (KernelMissing(op.ToString()) is { } k) return k;
        if (edges.Count == 0) return C3dFillets.SelectEdges(op);
        var ids = edges.Select(i => i.Object).Distinct().ToList();
        if (ids.Count > 1) return C3dFillets.OneObject(op);
        if (Viewer.Scene.Object(ids[0]) is not { } o) return C3dFillets.SelectEdges(op);
        if (InstanceOf(o) is not null) return "Part of an instance: it belongs to its own cell. Push into the cell to edit it.";
        if (OperandIndexOf(o) >= 0) return $"Round an operand's edges outside its boolean: leave it (Esc) and {op.ToString().ToLowerInvariant()} the result.";
        int i = DocumentIndex(o);
        if (i < 0) return "Select edges of this document's own objects.";
        var obj = Document.Objects[i];
        // 3D editor round 5 — a solid already rounded, its features enabled or not, takes another (C3dFillets.SoleEnabled).
        return C3dFillets.TargetRefusal(obj);
    }

    // ── the panel (R-em3d67-5b) ────────────────────────────────────────────────────────────────

    public ObservableCollection<C3dFilletEdgeRow> FilletEdgeRows { get; } = [];

    [ObservableProperty] private bool _filletOpen;
    [ObservableProperty] private C3dEdgeOp _filletOp;
    [ObservableProperty] private string _filletSizeText = "";
    [ObservableProperty] private string _filletSize2Text = "";
    [ObservableProperty] private bool _chamferTwoDistances;
    [ObservableProperty] private bool _chamferFlip;
    [ObservableProperty] private string _filletSizePreview = "";
    [ObservableProperty] private string? _filletError;
    [ObservableProperty] private string? _filletNote;
    [ObservableProperty] private bool _filletBusy;
    /// <summary>brief 51 — a name the Radius uses and nothing defines: offered as a VAR, with its value.</summary>
    [ObservableProperty] private string? _filletUnknown;
    [ObservableProperty] private string _filletDefineValue = "";

    public string FilletTitle => _filletEditPath is null ? FilletOp.ToString() : $"Edit {FilletOp}";
    public bool IsChamferPanel => FilletOp == C3dEdgeOp.Chamfer;
    public string FilletSizeLabel => FilletOp == C3dEdgeOp.Fillet ? "Radius" : ChamferTwoDistances ? "Distance 1" : "Distance";
    public string FilletEdgesText => $"{FilletEdgeRows.Count} edge{(FilletEdgeRows.Count == 1 ? "" : "s")}";
    public string FilletUnit => LayoutUnits.Suffix(Document.DisplayUnit);
    public bool HasFilletUnknown => FilletUnknown is not null;

    /// <summary>OK: a reply is drawn, nothing is outstanding and nothing refused.</summary>
    public bool CanAcceptFillet => FilletOpen && !FilletBusy && FilletError is null && _filletShown is not null;

    /// <summary>Preview requests sent, replies drawn, replies discarded (superseded), and commits — the gates' counters.</summary>
    public int FilletPreviewsRequested { get; private set; }
    public int FilletPreviewsDrawn { get; private set; }
    public int FilletRepliesDiscarded { get; private set; }
    public int FilletCommits { get; private set; }

    private string _filletTarget = "";
    private string? _filletEditPath;
    private long _filletSeq;
    private (C3dDocument Doc, int At, C3dOperation Feature, C3dShapePreview Reply)? _filletShown;
    private bool _filletLoading, _filletSyncing;
    private long _filletSceneGen = -1;
    private IReadOnlyList<string>? _filletReselect;

    partial void OnFilletSizeTextChanged(string value) => RequestFilletPreview();
    partial void OnFilletSize2TextChanged(string value) => RequestFilletPreview();
    partial void OnChamferTwoDistancesChanged(bool value) { OnPropertyChanged(nameof(FilletSizeLabel)); RequestFilletPreview(); }
    partial void OnChamferFlipChanged(bool value) => RequestFilletPreview();
    partial void OnFilletOpenChanged(bool value) => NotifyFillet();
    partial void OnFilletBusyChanged(bool value) => NotifyFillet();
    partial void OnFilletErrorChanged(string? value) => NotifyFillet();
    partial void OnFilletUnknownChanged(string? value) => OnPropertyChanged(nameof(HasFilletUnknown));

    partial void OnFilletOpChanged(C3dEdgeOp value)
    {
        OnPropertyChanged(nameof(FilletTitle));
        OnPropertyChanged(nameof(IsChamferPanel));
        OnPropertyChanged(nameof(FilletSizeLabel));
    }

    private void NotifyFillet()
    {
        OnPropertyChanged(nameof(CanAcceptFillet));
        AcceptFilletCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Fillet… or Chamfer… on the selected edges: the panel, its list from the selection, a size to start from, and the first
    /// preview request. A refusal is said on the status line and nothing opens.
    /// </summary>
    public void OpenFillet(C3dEdgeOp op)
    {
        if (EdgeOpRefusal(op) is { } why) { StatusMessage = why; return; }
        var picked = Viewer.SelectedEdgeNames();
        double shortest = Viewer.Selection.Where(i => i.IsEdge).Select(i => Viewer.EdgeOf(i)?.Edge.Length ?? 0).Where(l => l > 0).DefaultIfEmpty(0).Min();
        OpenPanel(op, picked[0].Object, null, [.. picked.Select(p => p.Edge)], DefaultSize(shortest), "", false, false);
        // 3D editor round 5 — one feature of a solid enabled at a time: the one on it now is kept, switched off.
        if (Document.Objects.FirstOrDefault(o => o.Name == _filletTarget) is { } t && C3dFillets.Chain(t).FirstOrDefault(c => c.Feature.Enabled).Feature is { } on)
            StatusMessage = $"'{t.Name}' keeps its {(on is C3dChamfer ? "chamfer" : "fillet")}, switched off while this one is enabled: one at a time.";
    }

    /// <summary>R-em3d67-6b — Edit… on a feature row: the panel on that feature's edges and sizes; OK rewrites it in place.</summary>
    public string? EditFeature(int top, string path)
    {
        if (KernelMissing("Edit") is { } k) return StatusMessage = k;
        if (top < 0 || top >= Document.Objects.Count || C3dFillets.At(Document.Objects[top], path) is not C3dOperation feature
            || !C3dFillets.IsFeature(feature))
            return StatusMessage = "Select one fillet or chamfer to edit.";
        string L(long dbu) => Tools.C3dDimension.Spell(dbu, Document.DisplayUnit, Document.DbuPerMicron);
        string Text(string field, long value)
            => C3dBindings.GetExpr(feature, field, 0) is { } e ? e.Expr + (e.Unit is { Length: > 0 } u && u != C3dUnits.Stored(Document.DisplayUnit) ? " " + u : "") : L(value);
        if (feature is C3dFillet f)
            OpenPanel(C3dEdgeOp.Fillet, Document.Objects[top].Name, path, f.Edges, Text(nameof(C3dFillet.Radius), f.Radius), "", false, false);
        else if (feature is C3dChamfer c)
            OpenPanel(C3dEdgeOp.Chamfer, Document.Objects[top].Name, path, c.Edges, Text(nameof(C3dChamfer.Distance), c.Distance),
                      c.Distance2 > 0 ? Text(nameof(C3dChamfer.Distance2), c.Distance2) : "", c.Distance2 > 0, false);
        return null;
    }

    private void OpenPanel(C3dEdgeOp op, string target, string? editPath, IReadOnlyList<string> edges, string size, string size2, bool two, bool flip)
    {
        SetTool(null);
        CloseArray();
        CancelBoolean();
        CancelFillet();
        _filletTarget = target;
        _filletEditPath = editPath;
        _filletLoading = true;
        try
        {
            FilletOp = op;
            FilletSizeText = size;
            FilletSize2Text = size2;
            ChamferTwoDistances = two;
            ChamferFlip = flip;
            FilletUnknown = null;
            FilletEdgeRows.Clear();
            foreach (string e in edges.Distinct(StringComparer.Ordinal)) FilletEdgeRows.Add(new C3dFilletEdgeRow(this, e));
        }
        finally { _filletLoading = false; }
        OnPropertyChanged(nameof(FilletTitle));
        OnPropertyChanged(nameof(FilletEdgesText));
        OnPropertyChanged(nameof(FilletUnit));
        StatusMessage = "";
        if (Viewer.SelectMode != Scene3DSelectMode.Edge) Viewer.SelectMode = Scene3DSelectMode.Edge;
        _filletSceneGen = Viewer.Scene.Generation;
        FilletOpen = true;
        RequestFilletPreview();
        Viewer.Regenerate();
    }

    /// <summary>A starting size: a tenth of the shortest selected edge, in whole display units where that is not zero.</summary>
    private string DefaultSize(double shortestM)
    {
        long dbu = (long)Math.Round(shortestM * 0.1 * 1e6 * Document.DbuPerMicron);
        long unit = Math.Max(1, (long)Math.Round(MetresPerDisplayUnit * 1e6 * Document.DbuPerMicron));
        if (dbu >= unit) dbu = dbu / unit * unit;
        return Tools.C3dDimension.Spell(Math.Max(1, dbu), Document.DisplayUnit, Document.DbuPerMicron);
    }

    /// <summary>The × on a row: that edge leaves the list (a list is never emptied: the last edge stays).</summary>
    internal void RemoveFilletEdge(string name)
    {
        if (FilletEdgeRows.Count <= 1) { StatusMessage = "A fillet rounds at least one edge: Cancel to leave it."; return; }
        if (FilletEdgeRows.FirstOrDefault(r => r.Name == name) is { } row) FilletEdgeRows.Remove(row);
        OnPropertyChanged(nameof(FilletEdgesText));
        SyncFilletSelection();
        RequestFilletPreview();
    }

    /// <summary>What a size field says: a number (DBU), or an expression resolved in the document's scope, or the names it
    /// uses that nothing defines, or why it is neither.</summary>
    private (long Dbu, C3dExpr? Expr, IReadOnlyList<string> Unknown, string? Error) ParseSize(string text, string label)
    {
        text = text.Trim();
        if (text.Length == 0) return (0, null, [], $"Type a {label.ToLowerInvariant()}.");
        if (LayoutUnits.TryParse(text, Document.DisplayUnit, Document.DbuPerMicron, out long dbu))
            return dbu > 0 ? (dbu, null, [], null) : (0, null, [], $"A {label.ToLowerInvariant()} is positive.");
        var (expr, unit) = SplitUnit(text, Document.DisplayUnit);
        if (!Parses(expr)) return (0, null, [], $"'{expr}' is neither a number nor an expression the engine can read.");
        var res = Resolution;
        var unknown = C3dExpressionText.Names(expr).Where(n => !res.IsDefined(n)).ToList();
        if (unknown.Count > 0) return (0, null, unknown, null);
        try
        {
            string stored = C3dUnits.Stored(unit);
            var v = res.Evaluate(expr, stored);
            if (v.Kind != CircuitRF.Core.Expressions.ValueKind.Real) return (0, null, [], $"'{expr}' is not a length.");
            long d = (long)Math.Round(v.AsReal() * 1e6 * Document.DbuPerMicron, MidpointRounding.AwayFromZero);
            return d > 0 ? (d, new C3dExpr(expr, stored), [], null) : (0, null, [], $"A {label.ToLowerInvariant()} is positive: '{expr}' is {v.AsReal():G4} m.");
        }
        catch (CircuitRF.Core.Expressions.ExpressionException ex) { return (0, null, [], ex.Message); }
    }

    /// <summary>The document OK would write: the new feature wrapping its target at the target's place, or the edited feature
    /// in place. A copy; the document itself is untouched. Null with the reason when the panel does not say one yet.</summary>
    private (C3dDocument Doc, int At, C3dOperation Feature)? FilletCandidate(out string? why, out IReadOnlyList<string> unknown)
    {
        why = null;
        unknown = [];
        int top = Document.Objects.FindIndex(o => o.Name == _filletTarget);
        if (top < 0) { why = $"'{_filletTarget}' is gone."; return null; }
        bool chamfer = FilletOp == C3dEdgeOp.Chamfer;
        var s1 = ParseSize(FilletSizeText, FilletSizeLabel);
        var s2 = chamfer && ChamferTwoDistances ? ParseSize(FilletSize2Text, "Distance 2") : (0, null, [], null);
        unknown = [.. s1.Unknown.Concat(s2.Unknown).Distinct()];
        if (unknown.Count > 0) { why = "unknown: " + string.Join(", ", unknown); return null; }
        if ((why = s1.Error ?? s2.Error) is not null) return null;
        var edges = FilletEdgeRows.Select(r => r.Name).ToList();
        if (edges.Count == 0) { why = C3dFillets.SelectEdges(FilletOp); return null; }
        // Flip: the first distance on the edge's SECOND face — which the worker's Distance2 is.
        var (d1, e1, d2, e2) = chamfer && ChamferTwoDistances && ChamferFlip ? (s2.Dbu, s2.Expr, s1.Dbu, s1.Expr) : (s1.Dbu, s1.Expr, s2.Dbu, s2.Expr);
        var doc = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        C3dOperation feature;
        if (_filletEditPath is { } path)
        {
            var root = doc.Objects[top];
            if (C3dFillets.At(root, path) is not C3dOperation was || !C3dFillets.IsFeature(was)) { why = "That feature is gone."; return null; }
            feature = (C3dOperation)C3dBooleans.Copy(was);
            C3dFillets.SetEdges(feature, edges);
            doc.Objects[top] = C3dFillets.With(root, path, feature);
        }
        else
        {
            // 3D editor round 5 — the new feature is the one enabled; those already on the solid stay, switched off.
            var target = C3dFillets.SoleEnabled(doc.Objects[top], null);
            feature = chamfer ? C3dFillets.MakeChamfer(target, edges, d1, d2) : C3dFillets.MakeFillet(target, edges, d1);
            doc.Objects[top] = feature;
        }
        void Size(string field, long dbu, C3dExpr? expr)
        {
            if (C3dBindings.Find(feature, field) is not { } f) return;
            C3dBindings.SetNumber(f.Owner, f.Spec, f.Component, dbu);
            C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, expr);
        }
        if (chamfer)
        {
            Size(nameof(C3dChamfer.Distance), d1, e1);
            Size(nameof(C3dChamfer.Distance2), ChamferTwoDistances ? d2 : 0, ChamferTwoDistances ? e2 : null);
        }
        else Size(nameof(C3dFillet.Radius), d1, e1);
        return (doc, top, feature);
    }

    /// <summary>R-em3d67-5c — one preview request for the panel as it is now.</summary>
    private void RequestFilletPreview()
    {
        if (!FilletOpen || _filletLoading) return;
        FilletError = null;
        FilletNote = null;
        long seq = ++_filletSeq;
        if (FilletCandidate(out string? why, out var unknown) is not var (doc, at, feature))
        {
            bool had = _filletShown is not null;
            _filletShown = null;
            FilletUnknown = unknown.Count > 0 ? unknown[0] : null;
            FilletSizePreview = unknown.Count > 0 ? why ?? "" : "";
            FilletError = unknown.Count > 0 ? null : why;
            FilletBusy = false;
            if (had) Viewer.Regenerate();
            NotifyFillet();
            return;
        }
        FilletUnknown = null;
        FilletSizePreview = feature switch
        {
            C3dFillet { Radius: var r } when C3dBindings.GetExpr(feature, nameof(C3dFillet.Radius), 0) is not null => "= " + Spell(r),
            C3dChamfer { Distance: var d } when C3dBindings.GetExpr(feature, nameof(C3dChamfer.Distance), 0) is not null => "= " + Spell(d),
            _ => "",
        };
        if (feature is C3dChamfer { Distance2: > 0 } && Viewer.Scene.Object(TargetSceneId()) is { } so && FilletEdgeRows.Count > 0 &&
            Viewer.Scene.FeaturesOf(so.Id).Table?.Named is { } named && named.IndexOf(FilletEdgeRows[0].Name) is >= 0 and var ei)
        {
            var e = named.Edges[ei];
            var (first, second) = ChamferFlip ? (e.FaceName1, e.FaceName0) : (e.FaceName0, e.FaceName1);
            FilletNote = $"On '{e.Name}': distance 1 along '{first}', distance 2 along '{second}'.";
        }
        FilletPreviewsRequested++;
        GeometryKernelTree tree;
        try { tree = C3dElaborator.KernelTreeOf(doc, at, FilePath, Cell); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            FilletBusy = false;
            FilletError = e.Message;
            return;
        }
        FilletBusy = true;
        NotifyFillet();
        C3dElaborator.PreviewShape(Kernel, tree, _filletTarget)
                     .ContinueWith(t => _post(() => FilletReplied(seq, t, doc, at, feature)), TaskScheduler.Default);
    }

    private string Spell(long dbu) => LayoutUnits.Format(dbu, Document.DisplayUnit, Document.DbuPerMicron) + " " + LayoutUnits.Suffix(Document.DisplayUnit);

    /// <summary>A reply: drawn when it is the latest, discarded (and counted) when superseded. A refusal names the edge.</summary>
    private void FilletReplied(long seq, Task<C3dShapePreview?> t, C3dDocument doc, int at, C3dOperation feature)
    {
        if (!FilletOpen || seq != _filletSeq) { FilletRepliesDiscarded++; return; }
        bool wasShown = _filletShown is not null;
        _filletShown = null;
        var failure = t.Exception?.InnerException;
        string? error = null;
        bool drawn = false;
        if (failure is OperationCanceledException || (failure is null && t.Result is null)) FilletRepliesDiscarded++;
        else if (failure is GeometryKernelException ge)
            error = C3dFillets.Worded(ge, doc.Objects[at], _filletTarget, Document.DisplayUnit, Document.DbuPerMicron) ?? ge.Message;
        else if (failure is not null) error = failure.Message;
        else if (t.Result is { } r)
        {
            _filletShown = (doc, at, feature, r);
            drawn = true;
        }
        if (wasShown || _filletShown is not null) Viewer.Regenerate();
        FilletError = error;
        if (drawn) FilletPreviewsDrawn++;
        FilletBusy = false;
        NotifyFillet();
    }

    /// <summary>R-em3d67-5e — OK: one document change, one undo entry. The reply holds every answer the elaboration will ask
    /// for, so the commit asks the kernel nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptFillet))]
    public void AcceptFillet()
    {
        if (!CanAcceptFillet || _filletShown is not { } shown) return;
        var before = C3dListsEdit.Of(Document);
        var after = C3dListsEdit.Of(shown.Doc);
        int n = C3dFillets.EdgesOf(shown.Feature).Count;
        string description = _filletEditPath is null
            ? C3dFillets.Description(FilletOp, n, _filletTarget)
            : $"Edit the {FilletOp.ToString().ToLowerInvariant()} of {_filletTarget} ({n} edge{(n == 1 ? "" : "s")})";
        _filletShown = null;
        CloseFilletPanel();
        Viewer.SetSelection([]);
        Push(new C3dListsEdit(description, before, after, ApplyLists));
        FilletCommits++;
        StatusMessage = description + ".";
    }

    /// <summary>Cancel and Esc: nothing changed; the preview's scene goes, and the edges stay selected.</summary>
    [RelayCommand]
    public void CancelFillet()
    {
        if (!FilletOpen) return;
        _filletShown = null;
        _filletReselect = _filletEditPath is null ? [.. FilletEdgeRows.Select(r => r.Name)] : null;
        CloseFilletPanel();
        Kernel.CancelPreview();
        Viewer.Regenerate();
    }

    private void CloseFilletPanel()
    {
        ++_filletSeq;
        FilletOpen = false;
        FilletBusy = false;
        FilletError = FilletNote = null;
        FilletUnknown = null;
        FilletSizePreview = "";
        FilletEdgeRows.Clear();
        _filletEditPath = null;
        OnPropertyChanged(nameof(FilletEdgesText));
    }

    /// <summary>brief 51 — Define: the unknown name the size uses becomes a VAR of this 3D view at the value typed beside it,
    /// one undo entry of its own; the preview then resolves.</summary>
    [RelayCommand]
    public void DefineFilletName()
    {
        if (FilletUnknown is not { } name) return;
        string value = FilletDefineValue.Trim();
        if (C3dResolver.ValidateName(name) is { } bad) { FilletError = bad; return; }
        if (value.Length == 0 || !Parses(value)) { FilletError = $"Type the value of '{name}'."; return; }
        var (expr, unit) = SplitUnit(value, Document.DisplayUnit);
        string? refusal = EditNames($"Define {name}", (doc, _) =>
        {
            doc.Variables.Add(new C3dVariable { Name = name, Expression = expr, Unit = C3dUnits.Stored(unit) });
            return null;
        });
        if (refusal is not null) { FilletError = refusal; return; }
        FilletDefineValue = "";
        RequestFilletPreview();
    }

    // ── the view while the panel is open ───────────────────────────────────────────────────────

    /// <summary>The scene object the panel's edges are picked on: the target beside the preview, or the object itself.</summary>
    private uint TargetSceneId()
        => (SceneObject(_filletTarget + FilletTargetSuffix) ?? SceneObject(_filletTarget))?.Id ?? 0;

    /// <summary>The scene's inputs while the panel is open: the result under the object's name (drawn as it is, never
    /// picked) and the target beside it, translucent and pickable. Null when the panel is closed.</summary>
    private (string Text, Dictionary<string, Scene3DGhost> Ghosts)? FilletScene()
    {
        if (!FilletOpen) return null;
        int top = Document.Objects.FindIndex(o => o.Name == _filletTarget);
        if (top < 0) return null;
        var doc = _filletShown is { } shown ? C3dPersistence.Deserialize(C3dPersistence.Serialize(shown.Doc)) : C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        var root = Document.Objects[top];
        // The target the edges belong to: the object itself, or — editing a feature — what that feature wraps.
        C3dObject target;
        if (_filletEditPath is { } path && C3dFillets.At(root, path + "Target.") is { } inner)
        {
            target = C3dBooleans.Copy(inner);
            foreach (var (p, f) in C3dFillets.Chain(root).Where(c => c.Path.Length <= path.Length).Reverse())
                if (!f.Placement.IsDefault) target.Placement = target.Placement.Then(f.Placement.ToTransform(), out _);
        }
        else target = C3dFillets.SoleEnabled(root, null);   // round 5: what a new feature wraps — the solid, its features off
        target.Name = _filletTarget + FilletTargetSuffix;
        target.Hidden = false;
        doc.Objects.Add(target);
        var ghosts = new Dictionary<string, Scene3DGhost>(StringComparer.Ordinal)
        {
            [_filletTarget] = Scene3DGhost.Inert,
            [target.Name] = Scene3DGhost.Pickable,
        };
        return (C3dPersistence.Serialize(doc), ghosts);
    }

    /// <summary>The view's selection changed while the panel is open: a click's change becomes the list; an edge of another
    /// object is refused and the selection put back. A regenerated scene's remap is not a click, and is ignored.</summary>
    private void FilletSelectionChanged()
    {
        if (!FilletOpen || _filletSyncing || Viewer.SelectMode != Scene3DSelectMode.Edge || Viewer.Scene.Generation != _filletSceneGen) return;
        var picked = Viewer.SelectedEdgeNames();
        if (picked.Count == 0) { SyncFilletSelection(); return; }
        string Base(string n) => n.EndsWith(FilletTargetSuffix, StringComparison.Ordinal) ? n[..^FilletTargetSuffix.Length] : n;
        if (picked.Any(p => Base(p.Object) != _filletTarget))
        {
            StatusMessage = C3dFillets.OneObject(FilletOp);
            SyncFilletSelection();
            return;
        }
        var names = picked.Select(p => p.Edge).Distinct(StringComparer.Ordinal).ToList();
        if (names.SequenceEqual(FilletEdgeRows.Select(r => r.Name))) return;
        FilletEdgeRows.Clear();
        foreach (string n in names) FilletEdgeRows.Add(new C3dFilletEdgeRow(this, n));
        OnPropertyChanged(nameof(FilletEdgesText));
        StatusMessage = "";
        RequestFilletPreview();
    }

    /// <summary>The list, written into the view's selection on the object its edges are picked on.</summary>
    private void SyncFilletSelection()
    {
        if (!FilletOpen || Viewer.SelectMode != Scene3DSelectMode.Edge) return;
        uint id = TargetSceneId();
        if (Viewer.Scene.Object(id) is not { } o) return;
        _filletSyncing = true;
        try { Viewer.SetSelection([.. FilletEdgeRows.Select(r => Viewer.EdgeItem(o.Name, r.Name)).OfType<Scene3DItem>()]); }
        finally { _filletSyncing = false; }
    }

    /// <summary>A scene was adopted: the panel's edges are selected on it; after Cancel, on the object again.</summary>
    private void FilletSceneAdopted(long generation)
    {
        _filletSceneGen = generation;
        if (FilletOpen) { SyncFilletSelection(); return; }
        if (_filletReselect is not { } names) return;
        _filletReselect = null;
        if (SceneObject(_filletTarget) is not { } o || Viewer.SelectMode != Scene3DSelectMode.Edge) return;
        Viewer.SetSelection([.. names.Select(n => Viewer.EdgeItem(o.Name, n)).OfType<Scene3DItem>()]);
    }

    /// <summary>The panel's Esc and Enter.</summary>
    private bool FilletKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        if (!FilletOpen) return false;
        if (key == Avalonia.Input.Key.Escape) { CancelFillet(); return true; }
        if (key == Avalonia.Input.Key.Enter && modifiers == Avalonia.Input.KeyModifiers.None) { AcceptFillet(); return true; }
        return false;
    }

    // ── the menus (R-em3d67-5a) ────────────────────────────────────────────────────────────────

    /// <summary>IViewer3DEditHost — Edge mode's Fillet… and Chamfer…, each disabled with its sentence, never hidden.</summary>
    public IEnumerable<Viewer3DMenuItem> EdgeMenuItems()
    {
        foreach (var op in (C3dEdgeOp[])[C3dEdgeOp.Fillet, C3dEdgeOp.Chamfer])
        {
            var o = op;
            string? why = EdgeOpRefusal(op);
            yield return new Viewer3DMenuItem(op + "…", () => OpenFillet(o), Enabled: why is null,
                Tip: why ?? (op == C3dEdgeOp.Fillet ? "Round the selected edges with a radius; previewed before OK."
                                                   : "Bevel the selected edges, at one distance or two; previewed before OK."));
        }
    }

    /// <summary>IViewer3DEditHost — a sentence on the status line.</summary>
    public void Say(string text) => StatusMessage = text;

    /// <summary>3D ▸ Modify ▸ Edge's items, by name.</summary>
    private bool RunEdgeModify(string which)
    {
        switch (which)
        {
            case "Fillet": OpenFillet(C3dEdgeOp.Fillet); return true;
            case "Chamfer": OpenFillet(C3dEdgeOp.Chamfer); return true;
            case "TangentChain":
                if (Viewer.SelectMode == Scene3DSelectMode.Edge && Viewer.Selection.LastOrDefault(i => i.IsEdge) is { IsEdge: true } last)
                    Viewer.SelectTangentChain(last, add: false);
                else StatusMessage = "Select an edge (Edge mode, E) to take its tangent chain.";
                return true;
        }
        return false;
    }

    // ── the feature rows' edits (R-em3d67-6b) ──────────────────────────────────────────────────

    /// <summary>Enabled on a feature at <paramref name="path"/> under top-level object <paramref name="top"/>: one undo entry,
    /// re-evaluated through the client; a failure is not rolled back — the row carries the refusal. 3D editor round 5 —
    /// enabling one switches every other feature of the solid off in the same entry: one at a time.</summary>
    public void SetFeatureEnabled(int top, string path, bool enabled)
    {
        if (top < 0 || top >= Document.Objects.Count || C3dFillets.At(Document.Objects[top], path) is not C3dOperation f) return;
        var root = Document.Objects[top];
        C3dObject after;
        if (enabled) after = C3dFillets.SoleEnabled(root, path);
        else
        {
            var copy = (C3dOperation)C3dBooleans.Copy(f);
            copy.Enabled = false;
            after = C3dFillets.With(root, path, copy);
        }
        string before = C3dPersistence.SerializeObject(root), written = C3dPersistence.SerializeObject(after);
        if (written == before) return;
        Push(new C3dEdit($"{(enabled ? "Enable" : "Disable")} the {(f is C3dChamfer ? "chamfer" : "fillet")} of {root.Name}",
                         [new C3dEditSlot(false, top, before, written)], ApplySlots));
    }

    /// <summary>One undo entry changing the feature at <paramref name="path"/> under top-level object <paramref name="top"/>.</summary>
    public void ChangeFeature(int top, string path, string description, Action<C3dObject> mutate)
    {
        if (top < 0 || top >= Document.Objects.Count || C3dFillets.At(Document.Objects[top], path) is not { } f) return;
        var root = Document.Objects[top];
        var copy = C3dBooleans.Copy(f);
        mutate(copy);
        string before = C3dPersistence.SerializeObject(root), after = C3dPersistence.SerializeObject(C3dFillets.With(root, path, copy));
        if (after != before) Push(new C3dEdit(description, [new C3dEditSlot(false, top, before, after)], ApplySlots));
    }

    /// <summary>Show: the feature's edges selected in the view (switching to Edge mode) — on what it rounds, whose edges they
    /// name; the ones the view cannot find (the feature is enabled, so they are rounded away) are said.</summary>
    public void ShowFeatureEdges(int top, string path)
    {
        if (top < 0 || top >= Document.Objects.Count || C3dFillets.At(Document.Objects[top], path) is not { } f) return;
        var names = C3dFillets.EdgesOf(f);
        if (Viewer.SelectMode != Scene3DSelectMode.Edge) Viewer.SelectMode = Scene3DSelectMode.Edge;
        var found = names.Select(n => Viewer.EdgeItem(Document.Objects[top].Name, n)).OfType<Scene3DItem>().ToList();
        Viewer.SetSelection(found);
        StatusMessage = found.Count == names.Count ? $"{names.Count} edge{(names.Count == 1 ? "" : "s")} of '{Document.Objects[top].Name}'."
            : $"{found.Count} of {names.Count} shown: the rest are rounded away while the {(f is C3dChamfer ? "chamfer" : "fillet")} is enabled — Edit… shows them all.";
    }

    /// <summary>Remove: the feature unwrapped — what it wraps back in its place with its name. One undo entry.</summary>
    public void RemoveFeature(int top, string path)
    {
        if (top < 0 || top >= Document.Objects.Count || C3dFillets.At(Document.Objects[top], path) is not C3dOperation f || !C3dFillets.IsFeature(f)) return;
        var root = Document.Objects[top];
        var unwrapped = C3dFillets.Unwrap(f);
        if (path.Length > 0) unwrapped.Name = "";
        var after = C3dFillets.With(root, path, unwrapped);
        Push(new C3dEdit($"Remove the {(f is C3dChamfer ? "chamfer" : "fillet")} of {root.Name}",
                         [new C3dEditSlot(false, top, C3dPersistence.SerializeObject(root), C3dPersistence.SerializeObject(after))], ApplySlots));
    }

    // ── a disabled chain's target is edited directly (R-em3d67-6e) ─────────────────────────────

    /// <summary>What a face or vertex edit acts on at <paramref name="index"/>: a chain of disabled features' managed target,
    /// in its world form under the chain's name; otherwise <see cref="ObjectAt"/>'s answer.</summary>
    private C3dObject? EditObjectAt(int index)
        => !IsOperandIndex(index) && index >= 0 && index < Document.Objects.Count && C3dFillets.DisabledCore(Document.Objects[index]) is { } core
            ? core : ObjectAt(index);

    /// <summary>An edited object at <paramref name="index"/> as the document holds it: back inside its disabled chain when it
    /// is one's target.</summary>
    private C3dObject Rewrapped(int index, C3dObject edited)
        => !IsOperandIndex(index) && index >= 0 && index < Document.Objects.Count && C3dFillets.DisabledCore(Document.Objects[index]) is not null
            ? C3dFillets.WithCore(Document.Objects[index], edited) : edited;
}
