// brief-em3d-92 R-em3d92-3 — the Inspector's Transparency row: a slider (0 to C3dTransparency.Max) that previews while dragged
// and commits on release (one undo entry per drag, as brief 86's sweep slider does), a box that takes a plain number (D3: not
// an expression), and Default, which clears the value back to the kind's. With the value null the row shows the kind's
// default greyed, "65 (default)", so the user sees where they start from. One row serves an object, an instance, a group
// selected whole (every member at every depth) and a multi-selection; members that differ read "mixed".

using System.Globalization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    /// <summary>The slider's and the box's largest value — <see cref="C3dTransparency.Max"/>, never a literal.</summary>
    public static double TransparencyMax => C3dTransparency.Max;

    /// <summary>The placeholder when the members' values differ.</summary>
    public const string TransparencyMixed = "mixed";

    [ObservableProperty] private bool _hasTransparency;
    [ObservableProperty] private double _transparencySlider;
    [ObservableProperty] private string _transparencyText = "";
    [ObservableProperty] private string _transparencyPlaceholder = "";

    /// <summary>The objects and instances the row writes, and what its undo entry is called.</summary>
    private (IReadOnlyList<int> Objects, IReadOnlyList<int> Instances, string Label) _transparencyTargets = ([], [], "");

    /// <summary>A drag's value not yet written; written on release.</summary>
    private int? _transparencyDragged;

    /// <summary>The drag's value across a reload. Each preview's scene reloads this panel when it is adopted — mid-drag — so the
    /// row must come back showing the value being dragged, not the document's, or the thumb would jump back under the pointer
    /// and the release would write nothing.</summary>
    private int? _draggedAcrossReload;

    private void ClearTransparency()
    {
        HasTransparency = false;
        _transparencyTargets = ([], [], "");
        _draggedAcrossReload = _transparencyDragged;
        _transparencyDragged = null;
        TransparencyText = "";
        TransparencyPlaceholder = "";
        TransparencySlider = 0;
    }

    /// <summary>Shows the row for <paramref name="objects"/> and <paramref name="instances"/> (polylines are left out: a line has
    /// none), named <paramref name="label"/> in the undo entry.</summary>
    private void LoadTransparency(IEnumerable<int> objects, IEnumerable<int> instances, string label)
    {
        var doc = editor.Document;
        var objs = objects.Where(i => i >= 0 && i < doc.Objects.Count && doc.Objects[i] is not C3dPolyline).Distinct().ToList();
        var insts = instances.Where(i => i >= 0 && i < doc.Instances.Count).Distinct().ToList();
        if (objs.Count + insts.Count == 0) return;
        HasTransparency = true;
        _transparencyTargets = (objs, insts, label);
        if (_draggedAcrossReload is { } dragged && editor.IsPreviewingTransparency(objs, insts))
        {
            _transparencyDragged = dragged;
            TransparencyText = dragged.ToString(CultureInfo.InvariantCulture);
            TransparencyPlaceholder = "";
            TransparencySlider = dragged;
            return;
        }
        var values = objs.Select(i => doc.Objects[i].Transparency).Concat(insts.Select(i => doc.Instances[i].Transparency)).Distinct().ToList();
        if (values.Count > 1)
        {
            TransparencyText = "";
            TransparencyPlaceholder = TransparencyMixed;
            TransparencySlider = 0;
            return;
        }
        if (values[0] is { } v)
        {
            TransparencyText = v.ToString(CultureInfo.InvariantCulture);
            TransparencyPlaceholder = "";
            TransparencySlider = v;
            return;
        }
        // Null everywhere: the kind's default, greyed — one number when every member's kind agrees on it.
        var defaults = objs.Select(i => DefaultPercentOf(doc.Objects[i])).Concat(insts.Select(_ => (int?)0)).Distinct().ToList();
        int? shown = defaults is [{ } d] ? d : null;
        TransparencyText = "";
        TransparencyPlaceholder = shown is { } s ? $"{s} (default)" : "(default)";
        TransparencySlider = shown ?? 0;
    }

    /// <summary>The kind's default transparency for a document object, from the role it elaborated with; null when it did not
    /// elaborate (it is then drawn as a wireframe, whatever it states).</summary>
    private int? DefaultPercentOf(C3dObject obj)
    {
        if (obj is C3dWire) return Scene3DTransparency.DefaultPercent(Em3dRole.Conductor);
        var e = editor.Elaboration;
        if (e?.Solids.FirstOrDefault(s => s.Name == obj.Name) is { } solid) return Scene3DTransparency.DefaultPercent(solid.Role);
        if (e?.Sheets.Any(s => s.Name == obj.Name) == true) return Scene3DTransparency.DefaultPercent(Em3dRole.Conductor);
        return null;
    }

    /// <summary>After a reload: a drag whose targets are no longer shown here (the selection changed under it) ends, so the
    /// view stops drawing a value nothing will write.</summary>
    private void EndStrayTransparencyPreview()
    {
        _draggedAcrossReload = null;
        if (_transparencyDragged is null) editor.EndTransparencyPreview();
    }

    partial void OnTransparencySliderChanged(double value)
    {
        if (_loading || !HasTransparency) return;
        int v = (int)Math.Clamp(Math.Round(value), 0, C3dTransparency.Max);
        _transparencyDragged = v;
        _loading = true;
        try { TransparencyText = v.ToString(CultureInfo.InvariantCulture); }
        finally { _loading = false; }
        editor.PreviewTransparency(_transparencyTargets.Objects, _transparencyTargets.Instances, v);
    }

    /// <summary>The slider was released (or its keys let go): the dragged value, written as one undo entry.</summary>
    public void CommitTransparencySlider()
    {
        if (_transparencyDragged is not { } v) return;
        _transparencyDragged = null;
        Error = editor.SetTransparency(_transparencyTargets.Objects, _transparencyTargets.Instances, v,
                                       $"Transparency of {_transparencyTargets.Label}") ?? "";
    }

    /// <summary>The box, on Enter or when it loses focus: a whole number in range, written as one undo entry. Left empty, nothing
    /// is written (Default clears a value).</summary>
    public void CommitTransparencyText()
    {
        if (_loading || !HasTransparency) return;
        string text = TransparencyText.Trim().TrimEnd('%').Trim();
        if (text.Length == 0) return;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
        {
            Error = $"A transparency is a whole number from {C3dTransparency.Range}.";
            return;
        }
        Error = editor.SetTransparency(_transparencyTargets.Objects, _transparencyTargets.Instances, v,
                                       $"Transparency of {_transparencyTargets.Label}") ?? "";
    }

    /// <summary>Default: the value cleared, so each member is drawn at its kind's transparency again.</summary>
    [RelayCommand]
    private void ResetTransparency()
    {
        if (!HasTransparency) return;
        _transparencyDragged = null;
        Error = editor.SetTransparency(_transparencyTargets.Objects, _transparencyTargets.Instances, null,
                                       $"Default transparency of {_transparencyTargets.Label}") ?? "";
    }
}
