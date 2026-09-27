// brief-em3d-43 R-em3d43-6b — the Properties panel: the selection's fields in the document's display unit.
//
//   * an object: kind, material, role, placement, and each dimension;
//   * a face: its name, area and normal;
//   * a vertex: its coordinates.
//
// This brief makes an object's NAME, MATERIAL, ROLE and PLACEMENT editable; brief 47 adds a vertex's coordinates
// (Set Coordinates — the typed Vertex Move, one undo entry) and a face's perimeter, and the distance between two
// selected parallel faces (Face mode's Measure). Each edit is a typed edit — validated, and ONE undo entry — committed on Enter or when
// the field loses focus, never per keystroke. Every length follows the display unit at once (gate 10): the
// panel is reloaded when the unit changes, and it holds no length of its own.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One read-only line of the panel; its value is selectable, so it can be copied.</summary>
public sealed record C3dPropertyRow(string Label, string Value);

/// <summary>3D editor round 1 — one face of the air box in the Inspector: its boundary, editable, and its padding now.</summary>
public sealed partial class C3dAirBoxFaceRow(string face, Em3dBoundaryKind kind, string padding, Action<string, Em3dBoundaryKind> set)
    : ObservableObject
{
    public string Face { get; } = face;
    public string Padding { get; } = padding;
    [ObservableProperty] private Em3dBoundaryKind _kind = kind;

    partial void OnKindChanged(Em3dBoundaryKind value) => set(Face, value);
}

/// <summary>
/// brief-em3d-51 R-em3d51-4b — one named dimension of the selected object: its text (a number in the display unit, or an
/// expression) and its resolved value, editable as either. A resolution error shows here, red, with the engine's message;
/// the document keeps the text, and elaboration refuses with the same message.
/// </summary>
public sealed partial class C3dDimensionField : ObservableObject
{
    public required string Path { get; init; }
    public required string ValueText { get; init; }
    public string? Error { get; init; }
    public bool IsExpression { get; init; }
    [ObservableProperty] private string _text = "";
    internal string Loaded { get; set; } = "";
}

public sealed partial class C3dPropertiesViewModel(C3dEditorViewModel editor) : ObservableObject
{
    /// <summary>The Role choices: the material's own, or an override.</summary>
    public const string RoleFromMaterial = "From material";

    public static IReadOnlyList<string> Roles { get; } = [RoleFromMaterial, .. Enum.GetNames<Em3dRole>()];

    private bool _loading;

    /// <summary>The document index of the object being edited, or −1.</summary>
    public int ObjectIndex { get; private set; } = -1;

    public ObservableCollection<C3dPropertyRow> Rows { get; } = [];

    /// <summary>brief-em3d-51 — the selected object's named dimensions, editable as a number or an expression.</summary>
    public ObservableCollection<C3dDimensionField> Fields { get; } = [];
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

    /// <summary>brief-em3d-47 R-em3d47-3e — the selected vertex's world coordinates, editable (Set Coordinates).</summary>
    [ObservableProperty] private bool _isVertexEditable;
    [ObservableProperty] private string _vertexX = "";
    [ObservableProperty] private string _vertexY = "";
    [ObservableProperty] private string _vertexZ = "";

    /// <summary>Raised when the Name field should take the caret (Rename… from a menu); the Inspector's view handles it.</summary>
    public event Action? RenameRequested;

    internal void RequestRename() => RenameRequested?.Invoke();

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
        Fields.Clear();
        Error = "";
        ObjectIndex = -1;
        IsEditable = false;
        IsVertexEditable = false;
        IsAirBox = false;
        AirBoxFaces.Clear();
        var viewer = editor.Viewer;
        var sel = viewer.Selection;
        if (sel.Count == 0)
        {
            // 3D editor round 1 — the tree's node when the scene holds nothing selected: an object elaboration refused
            // (its fields are where the refusal is put right), or the air box.
            if (editor.TreeOnlyObjectIndex() is >= 0 and var only) { LoadObject(only, inScene: false); return; }
            if (editor.SelectedTreeItem is { IsAirBox: true }) { LoadAirBox(); return; }
            Heading = "Nothing selected";
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Object &&
            sel.All(i => viewer.Scene.Object(i.Object) is { } b && C3dEditorViewModel.BoxFaceOf(b) is not null))
        {
            LoadAirBox();
            return;
        }
        if (sel.Count == 2 && viewer.SelectMode == Scene3DSelectMode.Face && sel[0].Face >= 0 && sel[1].Face >= 0) { TwoFaces(sel[0], sel[1]); return; }
        if (sel.Count > 1) { Heading = $"{sel.Count} selected"; return; }
        var item = sel[0];
        if (viewer.Scene.Object(item.Object) is not { } o) { Heading = "Nothing selected"; return; }
        Heading = viewer.Name(item);

        if (viewer.SelectMode == Scene3DSelectMode.Face && item.Face >= 0)
        {
            var (area, normal) = Scene3DFaces.AreaAndNormal(viewer.Scene, o.Id, item.Face);
            Rows.Add(new C3dPropertyRow("Face", o.FaceName(item.Face)));
            Rows.Add(new C3dPropertyRow("Area", AreaText(area)));
            if (Perimeter(o.Id, item.Face) is { } per) Rows.Add(new C3dPropertyRow("Perimeter", viewer.FormatLength(per)));
            Rows.Add(new C3dPropertyRow("Normal", normal is { } n ? $"({Num(n.X)}, {Num(n.Y)}, {Num(n.Z)})" : "varies (a curved face)"));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Vertex)
        {
            // Set Coordinates: the vertex of one of this document's own objects, exactly where the document has it.
            if (editor.VertexSelection() is { Vertex: >= 0 } v)
            {
                IsVertexEditable = true;
                // Spelled losslessly, so a field left alone parses back to the same DBU and its lost focus moves nothing.
                string L(long dbu) => Tools.C3dDimension.Spell(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);
                VertexX = L(v.World.X); VertexY = L(v.World.Y); VertexZ = L(v.World.Z);
            }
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
        LoadObject(index, inScene: true);
    }

    /// <summary>A document object's editable fields — <paramref name="inScene"/> false when elaboration refused it, whose
    /// reason is then the first row.</summary>
    private void LoadObject(int index, bool inScene)
    {
        var obj = editor.Document.Objects[index];
        // A refused object is either not drawn at all, or — with no material — drawn as a wireframe the solver never
        // sees; either way the refusal Simulate will give is shown on the object it names.
        string? why = editor.Elaboration?.Refusals.FirstOrDefault(r => r.Contains($"'{obj.Name}'", StringComparison.Ordinal));
        if (!inScene)
        {
            Heading = obj.Name;
            Rows.Add(new C3dPropertyRow("Not drawn", why ?? "It is not in the 3D model."));
        }
        else if (why is not null) Rows.Add(new C3dPropertyRow("Not simulated", why));
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
        foreach (var f in editor.DimensionFields(obj)) Fields.Add(f);
    }

    // ── the air box (3D editor round 1) ─────────────────────────────────────────────────────

    /// <summary>The active setup's air box is what is shown: its padding per axis and each face's boundary.</summary>
    [ObservableProperty] private bool _isAirBox;
    [ObservableProperty] private string _padXPercent = "";
    [ObservableProperty] private string _padYPercent = "";
    [ObservableProperty] private string _padZPercent = "";

    /// <summary>One row per face: its boundary (editable) and the padding it has now.</summary>
    public ObservableCollection<C3dAirBoxFaceRow> AirBoxFaces { get; } = [];

    public static IReadOnlyList<Em3dBoundaryKind> BoundaryKinds { get; } = Enum.GetValues<Em3dBoundaryKind>();

    private void LoadAirBox()
    {
        IsAirBox = true;
        var setup = editor.ActiveSetup;
        Heading = "Air box";
        if (setup is null || editor.ShownAirBox is not { } box)
        {
            Rows.Add(new C3dPropertyRow("Setup", "No setup is active: the air box is a setup's."));
            IsAirBox = false;
            return;
        }
        Rows.Add(new C3dPropertyRow("Setup", editor.ActiveSetupLabel));
        var stated = setup.AirBox;
        string Pct(EmAirBoxFace? a, EmAirBoxFace? b)
            => a?.PaddingPercent is { } p && b?.PaddingPercent == p ? p.ToString("G6", CultureInfo.InvariantCulture) : "";
        PadXPercent = Pct(stated?.XMin, stated?.XMax);
        PadYPercent = Pct(stated?.YMin, stated?.YMax);
        PadZPercent = Pct(stated?.ZMin, stated?.ZMax);
        var f = box.Faces;
        foreach (var (face, kind, s) in new[]
                 {
                     ("xmin", f.XMin, stated?.XMin), ("xmax", f.XMax, stated?.XMax), ("ymin", f.YMin, stated?.YMin),
                     ("ymax", f.YMax, stated?.YMax), ("zmin", f.ZMin, stated?.ZMin), ("zmax", f.ZMax, stated?.ZMax),
                 })
        {
            string pad = editor.AirBoxPaddingText(face) + " " + LayoutUnits.Suffix(editor.Document.DisplayUnit)
                       + (s?.PaddingPercent is { } p ? $" ({p.ToString("G6", CultureInfo.InvariantCulture)} %)"
                          : s?.PaddingUm is not null ? " (stated)" : " (default)");
            AirBoxFaces.Add(new C3dAirBoxFaceRow(face, kind, pad, (fc, k) => Error = editor.SetAirBoxBoundary(fc, k) ?? ""));
        }
        Rows.Add(new C3dPropertyRow("Size", $"{editor.Viewer.FormatLength(box.Max.X - box.Min.X)} × " +
                                            $"{editor.Viewer.FormatLength(box.Max.Y - box.Min.Y)} × {editor.Viewer.FormatLength(box.Max.Z - box.Min.Z)}"));
        Rows.Add(new C3dPropertyRow("Editing", "The air box is the active setup's, not the geometry's: it cannot be deleted or duplicated."));
    }

    /// <summary>An axis's padding percentage — Enter or lost focus: both faces of the axis pad by that share of the
    /// content's extent along it; empty keeps the faces as they are. One undo entry.</summary>
    public void CommitAirBoxPercent(char axis)
    {
        if (!IsAirBox) return;
        string text = axis switch { 'x' => PadXPercent, 'y' => PadYPercent, _ => PadZPercent };
        if (string.IsNullOrWhiteSpace(text)) return;
        Error = editor.SetAirBoxPaddingPercent(axis, text) ?? "";
    }

    /// <summary>A dimension's Enter or lost focus: a number replaces the expression; an expression is bound (and kept even
    /// when it does not resolve — the field then says why). One undo entry.</summary>
    public void CommitField(C3dDimensionField field)
    {
        if (ObjectIndex < 0 || field.Text == field.Loaded) return;
        Error = editor.SetFieldText(ObjectIndex, field.Path, field.Text) ?? "";
    }

    /// <summary>A face's perimeter (metres): the scene's own feature edges around it, which are the solid's edges, not its
    /// triangles'. Null when the face has none (a sweep names no faces).</summary>
    private double? Perimeter(uint id, int face)
    {
        if (editor.Viewer.Scene.FeaturesOf(id) is not { Table: { } t } fr || face >= t.FaceCount) return null;
        double p = 0;
        for (int k = t.FaceEdgeStart[face]; k < t.FaceEdgeStart[face + 1]; k++)
        {
            int e = t.FaceEdges[k];
            var a = fr.Vertex(t.EdgeA[e]);
            var b = fr.Vertex(t.EdgeB[e]);
            p += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        return p > 0 ? p : null;
    }

    /// <summary>brief-em3d-47 R-em3d47-4 — Measure's second face: the distance between two parallel faces, or that they are not.</summary>
    private void TwoFaces(Scene3DItem a, Scene3DItem b)
    {
        var viewer = editor.Viewer;
        var scene = viewer.Scene;
        if (scene.Object(a.Object) is not { } oa || scene.Object(b.Object) is not { } ob) { Heading = "2 selected"; return; }
        Heading = "2 faces";
        Rows.Add(new C3dPropertyRow("First", viewer.Name(a)));
        Rows.Add(new C3dPropertyRow("Second", viewer.Name(b)));
        var (_, na) = Scene3DFaces.AreaAndNormal(scene, oa.Id, a.Face);
        var (_, nb) = Scene3DFaces.AreaAndNormal(scene, ob.Id, b.Face);
        if (na is not { } n1 || nb is not { } n2) { Rows.Add(new C3dPropertyRow("Distance", "A curved face has no single plane.")); return; }
        var c = System.Numerics.Vector3.Cross(n1, n2);
        if (c.Length() > 1e-5f) { Rows.Add(new C3dPropertyRow("Distance", "Not parallel.")); return; }
        if (Corner(oa.Id, a.Face) is not { } pa || Corner(ob.Id, b.Face) is not { } pb) return;
        double d = Math.Abs(n1.X * (pb.X - pa.X) + n1.Y * (pb.Y - pa.Y) + n1.Z * (pb.Z - pa.Z));
        Rows.Add(new C3dPropertyRow("Distance", viewer.FormatLength(d)));
        Rows.Add(new C3dPropertyRow("Facing", System.Numerics.Vector3.Dot(n1, n2) < 0 ? "each other" : "the same way"));
    }

    private Point3? Corner(uint id, int face)
        => editor.Viewer.Scene.FeaturesOf(id) is { Table: { } t } fr && face < t.FaceCount && t.FaceVertexStart[face] < t.FaceVertexStart[face + 1]
            ? fr.Vertex(t.FaceVertices[t.FaceVertexStart[face]]) : null;

    /// <summary>brief-em3d-47 R-em3d47-3e — Set Coordinates' Enter or lost focus: three lengths in the display unit (a suffix
    /// may name another), the vertex moved there as one undo entry — or the refusal, and the fields put back.</summary>
    public void CommitVertex()
    {
        if (!IsVertexEditable) return;
        var doc = editor.Document;
        if (!LayoutUnits.TryParse(VertexX, doc.DisplayUnit, doc.DbuPerMicron, out long x) ||
            !LayoutUnits.TryParse(VertexY, doc.DisplayUnit, doc.DbuPerMicron, out long y) ||
            !LayoutUnits.TryParse(VertexZ, doc.DisplayUnit, doc.DbuPerMicron, out long z))
        {
            Error = $"A vertex is three lengths, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written.";
            return;
        }
        if (editor.SetVertexCoordinates(new C3dPoint3(x, y, z)) is { } why) { Error = why; return; }
        Error = "";
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
