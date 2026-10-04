// brief-em3d-108 R-em3d108-2 — the Inspector's Appearance group, under Transparency, for exactly what Transparency is shown for (an
// object, an instance, a group whole, a multi-selection — Object mode). Each field shows the RESOLVED value: one the targets state
// normally, the rest greyed with their provenance. An edit writes that one field of each target's own appearance (one undo entry); a
// slider previews while dragged and writes on release; Clear override removes the targets' appearance; Like offers the materials in
// scope ("Gold" on a copper trace is the plating case). An instance's contents and a setup's view show the group read-only, with the
// sentence the Transparency row's readout uses. Editing works whether the realistic view is on or not; while it is off the group says
// where its effect is seen, one click from turning it on.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Appearance;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    /// <summary>The editor, for the group's host (a nested class does not see the primary constructor's parameter).</summary>
    private C3dEditorViewModel EditorOf => editor;

    /// <summary>The Appearance group, or null when the selection has none to show.</summary>
    [ObservableProperty] private AppearanceEditorViewModel? _appearance;

    public bool HasAppearance => Appearance is not null;

    partial void OnAppearanceChanged(AppearanceEditorViewModel? value) => OnPropertyChanged(nameof(HasAppearance));

    /// <summary>Where an operand's look comes from, shown in place of the Appearance group it does not have (an operand inside an
    /// operation has no look of its own: its material's, overridden by the top-level operation's, which styles its whole result —
    /// a kept Tool included). Empty for anything else.</summary>
    [ObservableProperty] private string _operandLookNote = "";

    public bool HasOperandLookNote => OperandLookNote.Length > 0;

    partial void OnOperandLookNoteChanged(string value) => OnPropertyChanged(nameof(HasOperandLookNote));

    /// <summary>The note for an operand of <paramref name="operation"/> made of <paramref name="material"/> (null: none stated).</summary>
    public static string OperandLookText(string operation, string? material)
        => material is { Length: > 0 }
            ? $"No appearance of its own inside an operation. Its look is its material's, {material} (Edit… above), unless '{operation}' sets one, which styles everything '{operation}' makes."
            : $"No appearance of its own inside an operation. Its look is '{operation}''s, which styles everything '{operation}' makes.";

    /// <summary>The group's note while the realistic view is off.</summary>
    public const string ShownInRealistic = "Shown in the realistic view";

    /// <summary>The sentence a read-only host's group carries: a setup's 3D view edits nothing.</summary>
    public const string ViewOnlyAppearance =
        "Read-only here: a setup's 3D view edits nothing. Its look is its material's, edited in the Materials editor.";

    /// <summary>The objects and instances the group writes: what <see cref="LoadAppearance"/> was given.</summary>
    private (IReadOnlyList<int> Objects, IReadOnlyList<int> Instances) _appearanceTargets = ([], []);

    /// <summary>Turns the realistic view on, from the group's note.</summary>
    [RelayCommand]
    private void ShowRealistic() => editor.Viewer.IsRealistic = true;

    private void ClearAppearanceGroup()
    {
        Appearance = null;
        OperandLookNote = "";
        _appearanceTargets = ([], []);
    }

    /// <summary>The group for <paramref name="objects"/> and <paramref name="instances"/> (polylines left out), named
    /// <paramref name="label"/> in the undo entry; <paramref name="part"/> is the scene object clicked, whose look an instance's row
    /// shows.</summary>
    private void LoadAppearance(IEnumerable<int> objects, IEnumerable<int> instances, string label, Scene3DObject? part = null)
    {
        var doc = editor.Document;
        var objs = objects.Where(i => i >= 0 && i < doc.Objects.Count && doc.Objects[i] is not C3dPolyline).Distinct().ToList();
        var insts = instances.Where(i => i >= 0 && i < doc.Instances.Count).Distinct().ToList();
        if (objs.Count + insts.Count == 0) return;
        _appearanceTargets = (objs, insts);
        Appearance = new AppearanceEditorViewModel(new InspectorAppearanceHost(this, objs, insts, label, part, null))
        {
            CanClearOverride = true, NoteCommand = ShowRealisticCommand,
        };
        Appearance.Note = editor.Viewer.IsRealistic ? "" : ShownInRealistic;
    }

    /// <summary>R-em3d108-2d — a part the document cannot restyle here (a setup's view): its look, read-only, with the reason.</summary>
    private void LoadAppearanceReadOnly(Scene3DObject part, string reason)
    {
        if (editor.Viewer.AppearanceOf(part) is null) return;
        _appearanceTargets = ([], []);
        Appearance = new AppearanceEditorViewModel(new InspectorAppearanceHost(this, [], [], "", part, reason)) { NoteCommand = ShowRealisticCommand };
        Appearance.Note = editor.Viewer.IsRealistic ? "" : ShownInRealistic;
    }

    /// <summary>After a reload: a drag whose targets are no longer shown here ends, so the view stops drawing a value nothing will write.</summary>
    private void EndStrayAppearancePreview()
    {
        if (!editor.IsPreviewingAppearance(_appearanceTargets.Objects, _appearanceTargets.Instances)) editor.EndAppearancePreview();
    }

    /// <summary>The realistic view was turned on or off: the group's note follows.</summary>
    internal void RealisticChanged()
    {
        if (Appearance is { } a) a.Note = editor.Viewer.IsRealistic ? "" : ShownInRealistic;
    }

    private sealed class InspectorAppearanceHost(C3dPropertiesViewModel owner, IReadOnlyList<int> objects, IReadOnlyList<int> instances,
                                                 string label, Scene3DObject? part, string? readOnly) : IAppearanceHost
    {
        private C3dEditorViewModel Editor => owner.EditorOf;

        public IReadOnlyList<TechAppearance?> Stated
            => readOnly is not null ? [null]
             : [.. objects.Select(i => Editor.Document.Objects[i].Appearance), .. instances.Select(i => Editor.Document.Instances[i].Appearance)];

        /// <summary>One look per target: a document object's first scene object; an instance's clicked part, else its first part.</summary>
        public IReadOnlyList<ResolvedAppearance?> Resolved
        {
            get
            {
                var viewer = Editor.Viewer;
                if (readOnly is not null) return [part is null ? null : viewer.AppearanceOf(part)];
                var looks = new List<ResolvedAppearance?>();
                foreach (int i in objects)
                    looks.Add(Editor.SceneObjectsFor(Editor.Document.Objects[i]).Select(viewer.AppearanceOf).FirstOrDefault(a => a is not null));
                foreach (int i in instances)
                {
                    string name = Editor.Document.Instances[i].Name;
                    var shown = part is not null && Editor.InstanceOf(part) is { } path && path.Split('/', '[')[0] == name ? part
                              : viewer.Scene.Objects.FirstOrDefault(o => Editor.InstanceOf(o) is { } p && p.Split('/', '[')[0] == name && viewer.AppearanceOf(o) is not null);
                    looks.Add(shown is null ? null : viewer.AppearanceOf(shown));
                }
                return looks;
            }
        }

        public IReadOnlyList<string> LikeChoices => Editor.AppearanceLikeChoices;
        public string? ReadOnlyReason => readOnly;
        public void Preview(string key, object? value) => Editor.PreviewAppearance(objects, instances, key, value);
        public void EndPreview() => Editor.EndAppearancePreview();

        public string? Commit(string key, object? value)
        {
            string? why = key.Length == 0
                ? Editor.ClearAppearance(objects, instances, $"Clear the appearance of {label}")
                : Editor.SetAppearance(objects, instances, key, value, $"{key} of {label}");
            owner.Error = why ?? "";
            return why;
        }
    }
}
