// brief-em3d-43 R-em3d43-6b — the Properties panel: the selection's fields in the document's display unit.
//
//   * an object: kind, material, role, placement, and each dimension;
//   * a face: its name, area and normal;
//   * a vertex: its coordinates.
//
// This brief makes an object's NAME, MATERIAL, ROLE and PLACEMENT editable; brief 47 adds vertex coordinates
// and face offsets. Each edit is a typed edit — validated, and ONE undo entry — committed on Enter or when
// the field loses focus, never per keystroke. Every length follows the display unit at once (gate 10): the
// panel is reloaded when the unit changes, and it holds no length of its own.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One read-only line of the panel; its value is selectable, so it can be copied.</summary>
public sealed record C3dPropertyRow(string Label, string Value);

public sealed partial class C3dPropertiesViewModel(C3dEditorViewModel editor) : ObservableObject
{
    /// <summary>The Role choices: the material's own, or an override.</summary>
    public const string RoleFromMaterial = "From material";

    public static IReadOnlyList<string> Roles { get; } = [RoleFromMaterial, .. Enum.GetNames<Em3dRole>()];

    private bool _loading;

    /// <summary>The document index of the object being edited, or −1.</summary>
    public int ObjectIndex { get; private set; } = -1;

    public ObservableCollection<C3dPropertyRow> Rows { get; } = [];
    public IReadOnlyList<string> Materials => editor.Materials;

    [ObservableProperty] private string _heading = "Nothing selected";
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string? _material;
    [ObservableProperty] private string _role = RoleFromMaterial;
    [ObservableProperty] private string _originX = "";
    [ObservableProperty] private string _originY = "";
    [ObservableProperty] private string _originZ = "";
    [ObservableProperty] private string _rotateText = "";
    [ObservableProperty] private bool _mirrorX;
    [ObservableProperty] private string _error = "";

    /// <summary>Shows the current selection.</summary>
    public void Reload()
    {
        _loading = true;
        try { Load(); }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Materials));
    }

    private void Load()
    {
        Rows.Clear();
        Error = "";
        ObjectIndex = -1;
        IsEditable = false;
        var viewer = editor.Viewer;
        var sel = viewer.Selection;
        if (sel.Count == 0) { Heading = "Nothing selected"; return; }
        if (sel.Count > 1) { Heading = $"{sel.Count} selected"; return; }
        var item = sel[0];
        if (viewer.Scene.Object(item.Object) is not { } o) { Heading = "Nothing selected"; return; }
        Heading = viewer.Name(item);

        if (viewer.SelectMode == Scene3DSelectMode.Face && item.Face >= 0)
        {
            var (area, normal) = Scene3DFaces.AreaAndNormal(viewer.Scene, o.Id, item.Face);
            Rows.Add(new C3dPropertyRow("Face", o.FaceName(item.Face)));
            Rows.Add(new C3dPropertyRow("Area", AreaText(area)));
            Rows.Add(new C3dPropertyRow("Normal", normal is { } n ? $"({Num(n.X)}, {Num(n.Y)}, {Num(n.Z)})" : "varies (a curved face)"));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Vertex)
        {
            var (x, y, z) = viewer.Scene.ToWorld(item.Point);
            Rows.Add(new C3dPropertyRow("x", viewer.FormatLength(x)));
            Rows.Add(new C3dPropertyRow("y", viewer.FormatLength(y)));
            Rows.Add(new C3dPropertyRow("z", viewer.FormatLength(z)));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            return;
        }

        if (editor.InstanceOf(o) is { } inst)
        {
            Rows.Add(new C3dPropertyRow("Instance", inst));
            if (editor.Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true)
            {
                Rows.Add(new C3dPropertyRow("From", p.DocumentPath));
                Rows.Add(new C3dPropertyRow("Its name there", p.ObjectName));
            }
            if (o.Material is { } m) Rows.Add(new C3dPropertyRow("Material", m));
            Rows.Add(new C3dPropertyRow("Editing", "Read-only here: an instance's contents belong to its own cell."));
            return;
        }

        int index = editor.DocumentIndex(o);
        if (index < 0) return;
        var obj = editor.Document.Objects[index];
        ObjectIndex = index;
        IsEditable = true;
        NameText = obj.Name;
        Material = obj.Material;
        Role = obj.Role?.ToString() ?? RoleFromMaterial;
        var pl = obj.Placement;
        OriginX = Len(pl.Origin.X); OriginY = Len(pl.Origin.Y); OriginZ = Len(pl.Origin.Z);
        RotateText = string.Join(", ", pl.Rotate.Select(r => $"{r.Axis.ToString().ToLowerInvariant()} {r.Deg.ToString("G", CultureInfo.InvariantCulture)}"));
        MirrorX = pl.MirrorX;
        Rows.Add(new C3dPropertyRow("Kind", C3dObject.KindOf(obj)));
        // brief-em3d-46 R-em3d46-4d — construction order is invisible unless it is shown: it decides overlap.
        Rows.Add(new C3dPropertyRow("Construction order",
            $"{index + 1} of {editor.Document.Objects.Count} — a later object wins where solids overlap (Modify ▸ Order)"));
        foreach (var row in Dimensions(obj)) Rows.Add(row);
    }

    // ── read-only dimensions, per kind ──────────────────────────────────────────────────────

    private string Len(long dbu)
        => LayoutUnits.Format(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);

    private string P3(C3dPoint3 p) => $"({editor.Length(p.X)}, {editor.Length(p.Y)}, {editor.Length(p.Z)})";

    private string P2(C3dPoint2 p) => $"({editor.Length(p.U)}, {editor.Length(p.V)})";

    private IEnumerable<C3dPropertyRow> Dimensions(C3dObject obj)
    {
        switch (obj)
        {
            case C3dBox b:
                yield return new("Corner", P3(b.Min));
                yield return new("Size", P3(b.Size));
                break;
            case C3dPrism p:
                yield return new("Plane", p.Plane.ToString());
                yield return new("Offset", editor.Length(p.Offset));
                yield return new("Height", editor.Length(p.Height));
                yield return new("Outline", $"{p.Outline.Count} points" + (p.Holes.Count > 0 ? $", {p.Holes.Count} hole(s)" : ""));
                if (p.Shear != default) yield return new("Shear", P2(p.Shear));
                break;
            case C3dCylinder c:
                yield return new("Axis", c.Axis.ToString());
                yield return new("Base", P3(c.Base));
                yield return new("Length", editor.Length(c.Length));
                yield return new("Radius", editor.Length(c.Radius));
                break;
            case C3dSheet s:
                yield return new("Plane", s.Plane.ToString());
                yield return new("Offset", editor.Length(s.Offset));
                if (s.Rect is { } r)
                {
                    yield return new("Corner", P2(r.Min));
                    yield return new("Size", P2(r.Size));
                }
                else yield return new("Outline", $"{s.Outline.Count} points" + (s.Holes.Count > 0 ? $", {s.Holes.Count} hole(s)" : ""));
                if (s.ThicknessUm is { } t) yield return new("Thickness", t.ToString("G6", CultureInfo.InvariantCulture) + " µm");
                break;
            case C3dPolyline l:
                yield return new("Plane", l.Plane.ToString());
                yield return new("Offset", editor.Length(l.Offset));
                yield return new("Points", l.VertexCount + (l.Closed ? ", closed" : "") + (l.Points3 is not null ? ", leaving its plane (stored in 3D)" : ""));
                yield return new("In the problem", "No: a polyline is construction geometry.");
                break;
            case C3dPolyhedron ph:
                yield return new("Vertices", ph.Vertices.Count.ToString(CultureInfo.InvariantCulture));
                yield return new("Faces", ph.Faces.Count.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string Num(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>An area in the display unit squared.</summary>
    private string AreaText(double m2)
    {
        var unit = editor.Document.DisplayUnit;
        // One display unit in metres, from the DBU conversion itself: 1 unit = FromDbu⁻¹ …
        long dbuPerUnit = LayoutUnits.ToDbu(1m, unit, editor.Document.DbuPerMicron);
        double metresPerUnit = dbuPerUnit * 1e-6 / editor.Document.DbuPerMicron;
        double v = m2 / (metresPerUnit * metresPerUnit);
        return v.ToString("G6", CultureInfo.InvariantCulture) + " " + LayoutUnits.Suffix(unit) + "²";
    }

    // ── commits: one typed edit, one undo entry, validated ──────────────────────────────────

    /// <summary>The Name field's Enter or lost focus.</summary>
    public void CommitName()
    {
        if (ObjectIndex < 0) return;
        Error = editor.Rename(ObjectIndex, NameText) ?? "";
        if (Error.Length > 0) NameText = editor.Document.Objects[ObjectIndex].Name;
    }

    partial void OnMaterialChanged(string? value)
    {
        if (_loading || ObjectIndex < 0 || value is null) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"Material of {editor.Document.Objects[i].Name}", [i], o => o.Material = value);
    }

    partial void OnRoleChanged(string value)
    {
        if (_loading || ObjectIndex < 0) return;
        Em3dRole? role = Enum.TryParse<Em3dRole>(value, out var r) ? r : null;
        int i = ObjectIndex;
        editor.ChangeObjects($"Role of {editor.Document.Objects[i].Name}", [i], o => o.Role = role);
    }

    partial void OnMirrorXChanged(bool value)
    {
        if (_loading || ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"Mirror {editor.Document.Objects[i].Name}", [i], o => o.Placement.MirrorX = value);
    }

    /// <summary>The origin fields' Enter or lost focus: each a length in the display unit (a suffix may
    /// name another unit), exact to the DBU.</summary>
    public void CommitOrigin()
    {
        if (ObjectIndex < 0) return;
        var doc = editor.Document;
        long x = 0, y = 0, z = 0;
        if (!LayoutUnits.TryParse(OriginX, doc.DisplayUnit, doc.DbuPerMicron, out x) ||
            !LayoutUnits.TryParse(OriginY, doc.DisplayUnit, doc.DbuPerMicron, out y) ||
            !LayoutUnits.TryParse(OriginZ, doc.DisplayUnit, doc.DbuPerMicron, out z))
        {
            Error = $"The origin is three lengths, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written.";
            return;
        }
        Error = "";
        int i = ObjectIndex;
        editor.ChangeObjects($"Move {doc.Objects[i].Name}", [i], o => o.Placement.Origin = new C3dPoint3(x, y, z));
    }

    /// <summary>The rotations field: <c>z 30, x 90</c> — each an axis and degrees, applied in order.</summary>
    public void CommitRotate()
    {
        if (ObjectIndex < 0) return;
        if (ParseRotations(RotateText) is not { } list)
        {
            Error = "Rotations are written as an axis and degrees, in order: \"z 30, x 90\".";
            return;
        }
        Error = "";
        int i = ObjectIndex;
        editor.ChangeObjects($"Rotate {editor.Document.Objects[i].Name}", [i], o => o.Placement.Rotate = list);
    }

    /// <summary>Parses <c>z 30, x 90</c>; null when it is not that shape. An empty text is no rotation.</summary>
    public static List<C3dRotation>? ParseRotations(string text)
    {
        var list = new List<C3dRotation>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length != 2 || !Enum.TryParse<C3dAxis>(bits[0], ignoreCase: true, out var axis) ||
                !double.TryParse(bits[1].TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) ||
                !double.IsFinite(deg))
                return null;
            list.Add(new C3dRotation { Axis = axis, Deg = deg });
        }
        return list;
    }
}
