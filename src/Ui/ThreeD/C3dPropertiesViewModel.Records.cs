// brief-em3d-90 R-em3d90-4 — a symmetry plane and a thermal boundary in the Properties Inspector.
//
// NEITHER PAGE HOLDS A WRITER OF ITS OWN. A plane's At goes through SetSymmetryPlaneAt (the place fields' binding, one undo
// entry, refused with the one rule's sentence — C3dThermal.SymmetryPlaneRefusal — when it leaves the model's extent); a
// boundary's fields go through SetThermalBoundary, the function right-click ▸ Thermal calls, which is already one entry.
//
// A boundary's KIND switch keeps the other kind's values in memory while its row stays selected: switching Fixed → Convection
// → Fixed brings the temperature back. The memory is the Inspector's, never written anywhere.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    // ── a symmetry plane ────────────────────────────────────────────────────────────────────

    /// <summary>A symmetry plane is shown: its At is the dimension line below (an expression allowed).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FieldsVisible))] private bool _isSymmetryPlane;

    private C3dAxis _planeAxis;

    private void LoadSymmetryPlane(string row)
    {
        if (!Enum.TryParse<C3dAxis>(row[C3dEditorViewModel.SymmetryRowPrefix.Length..], out var axis) ||
            editor.Document.SymmetryPlanes.FirstOrDefault(p => p.Axis == axis) is not { } plane)
        {
            Heading = "Nothing selected";
            return;
        }
        _planeAxis = axis;
        Heading = $"Symmetry plane {axis}";
        Rows.Add(new C3dPropertyRow("Axis", $"{axis} — to cut on another axis, delete this plane and declare one on a face normal to it"));
        Rows.Add(new C3dPropertyRow("Modelled", $"1/{1 << editor.Document.SymmetryPlanes.Count} of the device is modelled"));
        if (editor.IsViewOnly)
        {
            Rows.Add(new C3dPropertyRow("At", $"{editor.Length(plane.At)}"));
            return;
        }
        IsSymmetryPlane = true;
        foreach (var f in editor.DimensionFields(plane, C3dEditorViewModel.SymmetryItemName(axis), _ => "At", _ => ("At", "")))
            Fields.Add(f);
        foreach (var r in RowsOf(Fields)) FieldRows.Add(r);
    }

    // ── a thermal boundary ──────────────────────────────────────────────────────────────────

    /// <summary>The Kind picker's choices, in <see cref="ThermalBoundaryKind"/>'s order.</summary>
    public static IReadOnlyList<string> ThermalBoundaryKinds { get; } = ["Fixed temperature", "Convection"];

    [ObservableProperty] private bool _isThermalBoundary;
    /// <summary>False in a setup's view-only 3D view: shown, not edited.</summary>
    [ObservableProperty] private bool _boundaryEditable;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BoundaryIsFixed))] private string _boundaryKind = ThermalBoundaryKinds[0];
    [ObservableProperty] private string _boundaryT = "";
    [ObservableProperty] private string _boundaryH = "";
    [ObservableProperty] private string _boundaryAmbient = "";
    [ObservableProperty] private string _boundaryFace = "";

    public bool BoundaryIsFixed => BoundaryKind == ThermalBoundaryKinds[0];

    /// <summary>The values of the kind NOT stated, remembered while this boundary's row stays selected.</summary>
    private (string Face, string T, string H, string Ambient)? _boundaryMemory;

    private void LoadThermalBoundary(string row)
    {
        string face = row[C3dEditorViewModel.ThermalTintPrefix.Length..];
        if (editor.ThermalBoundaryOn(face) is not { } b) { Heading = "Nothing selected"; return; }
        IsThermalBoundary = true;
        BoundaryEditable = !editor.IsViewOnly;
        Heading = $"Thermal boundary on {face} · {editor.ActiveSetupName}";
        BoundaryFace = face;
        var memory = _boundaryMemory is { } m && m.Face == face ? m : (face, "25", "10", "25");
        bool fixedT = b.Kind == ThermalBoundaryKind.FixedT;
        BoundaryKind = ThermalBoundaryKinds[fixedT ? 0 : 1];
        BoundaryT = fixedT ? b.TempC ?? "" : memory.Item2;
        BoundaryH = fixedT ? memory.Item3 : b.H ?? "";
        BoundaryAmbient = fixedT ? memory.Item4 : b.AmbientC ?? "";
        _boundaryMemory = (face, BoundaryT, BoundaryH, BoundaryAmbient);
    }

    partial void OnBoundaryKindChanged(string value)
    {
        if (_loading || !IsThermalBoundary) return;
        CommitBoundary();
    }

    /// <summary>A boundary field's Enter or lost focus, or its kind switched: SetThermalBoundary with the kind's values — the
    /// menu's own function, one undo entry. A refusal is shown and the fields put back.</summary>
    public void CommitBoundary()
    {
        if (!IsThermalBoundary || !BoundaryEditable) return;
        string face = BoundaryFace;
        _boundaryMemory = (face, BoundaryT, BoundaryH, BoundaryAmbient);
        var now = editor.ThermalBoundaryOn(face);
        Error = (BoundaryIsFixed
                    ? now is { Kind: ThermalBoundaryKind.FixedT } && now.TempC == BoundaryT.Trim() ? null
                      : editor.SetThermalBoundary(face, ThermalBoundaryKind.FixedT, tempC: BoundaryT.Trim())
                    : now is { Kind: ThermalBoundaryKind.Convection } && now.H == BoundaryH.Trim() && now.AmbientC == BoundaryAmbient.Trim() ? null
                      : editor.SetThermalBoundary(face, ThermalBoundaryKind.Convection, h: BoundaryH.Trim(), ambientC: BoundaryAmbient.Trim())) ?? "";
        if (Error.Length > 0) Reload();
    }

    /// <summary>The Face line's Select face link: that face, selected in the view in Face mode.</summary>
    [RelayCommand]
    private void SelectBoundaryFace()
    {
        string face = BoundaryFace;
        Error = editor.SelectBoundaryFace(face) ?? "";
    }

    // ── an EM face boundary's tint ──────────────────────────────────────────────────────────

    private void LoadEmBoundary(Scene3DObject tint)
    {
        Heading = Scene3DBuilder.TintLabel(tint.Name);
        var b = editor.Document.FaceBoundaries.FirstOrDefault(f => Scene3DBuilder.FaceTintPrefix + f.Object + "/" + f.Face == tint.Name);
        if (b is null) return;
        Rows.Add(new C3dPropertyRow("Kind", b.Kind == Em3dFaceBoundaryKind.Conductive ? $"Conductive surface ({b.Material})" : "Perfect conductor"));
        Rows.Add(new C3dPropertyRow("Face", $"{b.Object}/{b.Face} — B selects the face under it; right-click the face ▸ Boundary changes it"));
    }

    /// <summary>What <see cref="Load"/> resets before showing anything.</summary>
    private void ClearRecords()
    {
        IsSymmetryPlane = false;
        if (!(IsThermalBoundary && editor.SelectedTreeItem is { Kind: C3dEditorViewModel.ThermalBoundaryKindName } r &&
              r.Name == C3dEditorViewModel.ThermalTintPrefix + BoundaryFace))
            _boundaryMemory = null;
        IsThermalBoundary = false;
    }

    /// <summary>The record page the selection shows, if it is one: a symmetry plane's or a thermal boundary's row (with nothing,
    /// or only its tint, selected in the view), or a tint picked in the view. False for anything else.</summary>
    private bool LoadRecord()
    {
        var viewer = editor.Viewer;
        var sel = viewer.Selection;
        var tint = sel.Count == 1 && viewer.Scene.Object(sel[0].Object) is { Tint: true } t ? t : null;
        if (sel.Count > 0 && tint is null) return false;
        switch (editor.SelectedTreeItem)
        {
            case { Kind: C3dEditorViewModel.SymmetryPlaneKind } plane when sel.Count == 0:
                LoadSymmetryPlane(plane.Name);
                return true;
            case { Kind: C3dEditorViewModel.ThermalBoundaryKindName } row
                when tint is null || tint.Name == Scene3DBuilder.FaceTintPrefix + row.Name:
                LoadThermalBoundary(row.Name);
                return true;
        }
        if (tint is null) return false;
        string rest = tint.Name[Scene3DBuilder.FaceTintPrefix.Length..];
        if (rest.StartsWith(C3dEditorViewModel.ThermalTintPrefix, StringComparison.Ordinal)) LoadThermalBoundary(rest);
        else LoadEmBoundary(tint);
        return true;
    }
}
