// brief-em3d-41 R-em3d41-2/-3 — the .c3d document: a cell's 3D view.
//
// ONE TYPE FAMILY IS BOTH THE WORKING MODEL AND THE FILE. `.clay`'s shapes are the precedent: an
// object here is exactly what the file says and nothing the file does not, so a second DTO tree and
// a mapping between the two would be a copy that could only disagree. The generated reference page
// (`circuitrf reference 3d`) walks these types, which is why every computed member is [JsonIgnore]
// and every point type says, in a [Description], how it is SPELLED rather than what its fields are.
//
// EVERY COORDINATE IS AN INTEGER DBU (owner decision D4, overview §1d). Two solids drawn to touch
// share a face EXACTLY, so Gmsh's fragment makes one surface and the FDTD grid one line — doubles
// would make touching a matter of tolerance. A rotation that is not a multiple of 90° would break
// that if it were baked into coordinates, so it never is: it lives in the object's Placement, and
// the geometry stays integer in the object's own frame.
//
// DIMENSIONS ARE SIZES, NOT SECOND CORNERS (R-em3d41-2-dims). Brief 51 binds a typed width to a
// FIELD; with two corners "the width is w" could only be written as an expression nobody typed.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

// ── Points ────────────────────────────────────────────────────────────────────────────────────

/// <summary>A point in the document's 3D frame, integer DBU. Spelled <c>[x, y, z]</c>.</summary>
[JsonConverter(typeof(C3dPoint3JsonConverter))]
[Description("[x, y, z]")]
public readonly record struct C3dPoint3(long X, long Y, long Z)
{
    public static C3dPoint3 operator +(C3dPoint3 a, C3dPoint3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static C3dPoint3 operator -(C3dPoint3 a, C3dPoint3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}

/// <summary>A point on a drawing plane, integer DBU. Spelled <c>[u, v]</c>; which world axes u and v
/// are is the plane's (<see cref="C3dPlane"/>).</summary>
[JsonConverter(typeof(C3dPoint2JsonConverter))]
[Description("[u, v]")]
public readonly record struct C3dPoint2(long U, long V);

/// <summary>
/// A drawing plane, named by the two world axes it spans. u is the first, v the second, and the
/// plane's normal — the direction a prism's <c>Height</c> and a plane's <c>Offset</c> are measured
/// along — is the third: XY → (x, y) along +z, YZ → (y, z) along +x, XZ → (x, z) along +y.
/// </summary>
public enum C3dPlane { XY, YZ, XZ }

/// <summary>A world axis.</summary>
public enum C3dAxis { X, Y, Z }

/// <summary>Which view of the placed cell an instance shows (brief 48 swaps it).</summary>
public enum C3dInstanceView { ThreeD, Layout }

// ── Placement ─────────────────────────────────────────────────────────────────────────────────

/// <summary>One rotation of a placement: about a world axis through the object's origin, right-handed,
/// in degrees.</summary>
public sealed class C3dRotation
{
    public C3dAxis Axis { get; set; } = C3dAxis.Z;
    public double  Deg  { get; set; }
}

/// <summary>
/// Where an object or an instance sits (R-em3d41-2a): its own frame is mirrored, then rotated by each
/// entry of <see cref="Rotate"/> in list order, then translated to <see cref="Origin"/> — layout's
/// mirror-then-rotate order, extended by a list. <see cref="MirrorX"/> negates the object's own x, as
/// a layout instance's does. The whole record is omitted from the file when it states nothing.
/// </summary>
public sealed partial class C3dPlacement
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dPoint3 Origin { get; set; }

    public List<C3dRotation> Rotate { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MirrorX { get; set; }

    /// <summary>True when the placement states nothing, which is when the file leaves it out. A stated
    /// 0° rotation is not nothing: it was written, so it is kept.</summary>
    [JsonIgnore]
    public bool IsDefault => Origin == default && Rotate.Count == 0 && !MirrorX;

    /// <summary>The placement as one rigid transform — see <see cref="C3dTransform"/>.</summary>
    public C3dTransform ToTransform()
    {
        var t = MirrorX ? C3dTransform.MirrorX : C3dTransform.Identity;
        foreach (var r in Rotate) t = t.Then(C3dTransform.Rotation(r.Axis, r.Deg));
        return t.Then(C3dTransform.Translation(Origin));
    }
}

// ── Objects ───────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One object of the document: a primitive, a polyhedron, or a construction polyline. The list order
/// is construction order (R-em3d41-2b): where two solids overlap, the LATER one wins the volume, which
/// is <c>Em3dSolid.Order</c> — the one overlap rule both backends honour. Nothing sorts the list.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(C3dBox),        "Box")]
[JsonDerivedType(typeof(C3dPrism),      "Prism")]
[JsonDerivedType(typeof(C3dCylinder),   "Cylinder")]
[JsonDerivedType(typeof(C3dSheet),      "Sheet")]
[JsonDerivedType(typeof(C3dPolyline),   "Polyline")]
[JsonDerivedType(typeof(C3dPolyhedron), "Polyhedron")]
public abstract class C3dObject
{
    /// <summary>Unique in the document, validated as a cell name is; <c>airbox</c> is reserved (the air
    /// box's faces are <c>airbox/xmin</c> …).</summary>
    [JsonPropertyOrder(-10)]
    public string Name { get; set; } = "";

    /// <summary>A material of the document's technology. A polyline has none.</summary>
    [JsonPropertyOrder(-9)]
    public string? Material { get; set; }

    /// <summary>Overrides the role the material implies. Omitted: the material decides (brief 42).</summary>
    [JsonPropertyOrder(-8)]
    public Em3dRole? Role { get; set; }

    [JsonPropertyOrder(10)]
    public C3dPlacement Placement { get; set; } = new();

    /// <summary>Document state, like a layer's visibility in a <c>.ctech</c>. The camera is not.</summary>
    [JsonPropertyOrder(11)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    /// <summary>Keys this build does not read. Kept, and written back, so a document from a later build
    /// does not lose them by being opened here; <c>check</c> names each one.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The object's faces by NAME (R-em3d41-2c) — never by index, so a boundary attached to a
    /// face survives an edit (em-3d.md §6.4). Empty for a sheet and a polyline, which are not
    /// solids.</summary>
    public abstract IReadOnlyList<string> FaceNames();

    /// <summary>The <c>$type</c> this object is written as.</summary>
    public static string KindOf(C3dObject o) => KindOf(o.GetType());

    /// <summary>The <c>$type</c> spelling of a concrete object type, read off the attributes the reader
    /// itself uses.</summary>
    public static string KindOf(Type t)
    {
        foreach (var d in Kinds) if (d.Type == t) return d.Name;
        return t.Name;
    }

    /// <summary>Every <c>$type</c> this build reads.</summary>
    public static IReadOnlyList<(string Name, Type Type)> Kinds { get; } =
        [.. typeof(C3dObject).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => ((string)a.TypeDiscriminator!, a.DerivedType))];
}

/// <summary>An axis-aligned box in its own frame: a corner and a size (never two corners).</summary>
public sealed class C3dBox : C3dObject
{
    public C3dPoint3 Min  { get; set; }
    public C3dPoint3 Size { get; set; }

    public static readonly string[] FaceNameList = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    public override IReadOnlyList<string> FaceNames() => FaceNameList;
}

/// <summary>
/// A polygon on a drawing plane, pulled along the plane's normal by <see cref="Height"/> — negative is
/// allowed, and says which way it was pulled. <see cref="Shear"/> moves the top against the bottom,
/// which is an oblique prism: still a prism, never a polyhedron.
/// </summary>
public sealed class C3dPrism : C3dObject
{
    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public List<C3dPoint2>       Outline { get; set; } = [];
    public List<List<C3dPoint2>> Holes   { get; set; } = [];
    public long     Height { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dPoint2 Shear { get; set; }

    /// <summary><c>bottom</c>, <c>top</c>, <c>side&lt;k&gt;</c> for the outline edge from vertex k to
    /// k+1, and <c>hole&lt;h&gt;.side&lt;k&gt;</c> for each hole's.</summary>
    public override IReadOnlyList<string> FaceNames()
    {
        var names = new List<string>(2 + Outline.Count) { "bottom", "top" };
        for (int k = 0; k < Outline.Count; k++) names.Add($"side{k}");
        for (int h = 0; h < Holes.Count; h++)
            for (int k = 0; k < Holes[h].Count; k++) names.Add($"hole{h}.side{k}");
        return names;
    }
}

/// <summary>A right circular cylinder along a world axis from <see cref="Base"/>. Any other axis is a
/// placement's rotation.</summary>
public sealed class C3dCylinder : C3dObject
{
    public C3dPoint3 Base   { get; set; }
    public C3dAxis   Axis   { get; set; } = C3dAxis.Z;
    public long      Length { get; set; }
    public long      Radius { get; set; }

    public static readonly string[] FaceNameList = ["bottom", "top", "side"];

    public override IReadOnlyList<string> FaceNames() => FaceNameList;
}

/// <summary>A rectangle on a drawing plane: a corner and a size.</summary>
public sealed class C3dRect
{
    public C3dPoint2 Min  { get; set; }
    public C3dPoint2 Size { get; set; }
}

/// <summary>A conductor thin enough to be a surface, on a drawing plane: a <see cref="Rect"/>, or an
/// <see cref="Outline"/> with <see cref="Holes"/> — one or the other.</summary>
public sealed class C3dSheet : C3dObject
{
    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public C3dRect? Rect   { get; set; }
    public List<C3dPoint2>       Outline { get; set; } = [];
    public List<List<C3dPoint2>> Holes   { get; set; } = [];

    /// <summary>The metal's real thickness, micrometres — what the solver's surface impedance uses.</summary>
    public double? ThicknessUm { get; set; }

    public override IReadOnlyList<string> FaceNames() => [];
}

/// <summary>Construction geometry (R-em3d41-2e): drawn and snapped to, and the input to Extrude, but
/// NEVER in the solved problem. It has no material.</summary>
/// <para>brief-em3d-45 R-em3d45-3d — a polyline's vertices snap to features off its plane, so one whose points
/// leave the plane is stored in 3D form: <see cref="Points3"/> instead of <see cref="Points"/>, with
/// <see cref="Plane"/>/<see cref="Offset"/> still the plane it was drawn on. <c>Points3</c> is written only then,
/// so a planar polyline's spelling is unchanged.</para>
public sealed class C3dPolyline : C3dObject
{
    public C3dPlane        Plane  { get; set; }
    public long            Offset { get; set; }
    public List<C3dPoint2> Points { get; set; } = [];

    /// <summary>The vertices in 3D, integer DBU, when they do not all lie on the plane; null (and absent from the
    /// file) otherwise. When present it is the polyline and <see cref="Points"/> is empty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<C3dPoint3>? Points3 { get; set; }

    /// <summary>How many vertices the polyline has, in whichever form it is stored.</summary>
    [JsonIgnore]
    public int VertexCount => Points3?.Count ?? Points.Count;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; set; }

    public override IReadOnlyList<string> FaceNames() => [];
}

/// <summary>One planar face of a polyhedron: an outer loop and hole loops, as indices into the
/// polyhedron's vertices, and a NAME that stays with it through every edit.</summary>
public sealed class C3dFace
{
    public string          Name  { get; set; } = "";
    public List<int>       Outer { get; set; } = [];
    public List<List<int>> Holes { get; set; } = [];
}

/// <summary>A closed solid with planar faces — what an edit a primitive cannot express turns an object
/// into (overview §1g).</summary>
public sealed class C3dPolyhedron : C3dObject
{
    public List<C3dPoint3> Vertices { get; set; } = [];
    public List<C3dFace>   Faces    { get; set; } = [];

    public override IReadOnlyList<string> FaceNames() => [.. Faces.Select(f => f.Name)];
}

// ── Instances ─────────────────────────────────────────────────────────────────────────────────

/// <summary>An array of an instance: <see cref="Counts"/> copies along x, y and z, <see cref="Pitch"/>
/// apart. What it elaborates to is brief 42's.</summary>
public sealed class C3dArray
{
    /// <summary>[nx, ny, nz], each at least 1.</summary>
    public List<int>  Counts { get; set; } = [1, 1, 1];
    public C3dPoint3  Pitch  { get; set; }
}

/// <summary>
/// A placed cell (R-em3d41-4). <see cref="CellRef"/> is spelled exactly as a <c>.clay</c> instance's —
/// relative to this document's folder, or <c>ws://</c> — so every walker that follows a cell reference
/// follows this one the same way. What it MEANS is brief 42's.
/// </summary>
public sealed class C3dInstance
{
    public string Name    { get; set; } = "";
    public string CellRef { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dInstanceView View { get; set; }

    public C3dPlacement Placement { get; set; } = new();
    public C3dArray?    Array     { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

// ── Ports and face boundaries (brief-em3d-49) ─────────────────────────────────────────────────

/// <summary>A wave port's stated voltage path, on the port's own plane: from the negative conductor to the positive one.</summary>
public sealed class C3dVoltagePath
{
    public C3dPoint2 From { get; set; }
    public C3dPoint2 To   { get; set; }
}

/// <summary>
/// brief-em3d-49 R-em3d49-2a — a port, drawn in the parent (overview §1k: a placed cell's ports are never used). A
/// <see cref="Rect"/> on a drawing plane, axis-aligned. A lumped port is a sheet between two conductors; which two, and
/// which way round, is INFERRED from what the rectangle's edges touch unless <see cref="Positive"/> and
/// <see cref="Negative"/> say (both, or neither). A wave port's rectangle is a region of an air-box face of the setup
/// being run. Every setup uses every port.
/// </summary>
public sealed class C3dPort
{
    public int    Number { get; set; }
    public string Name   { get; set; } = "";
    public Em3dPortKind Kind { get; set; }
    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public C3dRect  Rect   { get; set; } = new();

    /// <summary>The reference impedance, Ω — a real number or a complex one (<c>50</c>, <c>25+j10</c>), as a .cem's.</summary>
    public string Z0 { get; set; } = "50";

    /// <summary>The positive conductor, by elaborated name (<c>U1/pad3</c> allowed) or an air-box face
    /// (<c>airbox/zmin</c>); null is inferred.</summary>
    public string? Positive { get; set; }

    /// <summary>The negative conductor; null is inferred. Stating one without the other is refused.</summary>
    public string? Negative { get; set; }

    /// <summary>Swaps the inferred (or stated) conductors: a wrong polarity is a 180° error in every transmission term.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flip { get; set; }

    /// <summary>A wave port's voltage path; null is inferred as brief 23 infers it.</summary>
    public C3dVoltagePath? VoltagePath { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-49 R-em3d49-4a — a boundary on a NAMED face of a dielectric or air object (overview §1e): it follows the
/// face through every edit, and a fold hands it to every piece. <see cref="Material"/> is a Conductive face's metal.
/// </summary>
public sealed class C3dFaceBoundary
{
    public string Object { get; set; } = "";
    public string Face   { get; set; } = "";
    public Em3dFaceBoundaryKind Kind { get; set; }
    public string? Material { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

// ── The document ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A cell's 3D view — the mutable working model and the file (R-em3d41-3a). Framework-free.
/// <see cref="C3dPersistence"/> reads and writes it.
/// </summary>
public sealed class C3dDocument
{
    public int FormatVersion { get; set; } = C3dPersistence.CurrentFormatVersion;

    /// <summary>What one DBU is worth, exactly as a <c>.clay</c>'s: at 1000 one DBU is a nanometre.</summary>
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>A document preference, not geometry: changing it moves nothing (layout-view §1.3).</summary>
    public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;

    /// <summary>The drawing grid. It never re-snaps geometry.</summary>
    public long SnapDbu { get; set; }

    /// <summary>Resolved as a <c>.clay</c>'s is — relative to this file, or null for the workspace's
    /// default. The materials come from here.</summary>
    public string? TechRef { get; set; }

    /// <summary>Always written, even empty: with <see cref="FormatVersion"/> it is how a <c>.c3d</c> is
    /// told from another program's file of the same extension (overview §1c).</summary>
    [JsonRequired]
    public List<C3dObject>   Objects   { get; set; } = [];

    public List<C3dInstance> Instances { get; set; } = [];

    /// <summary>
    /// Brief 51's VARs. <b>Read and written from brief 41</b>, uninterpreted, so a document written
    /// after brief 51 keeps its VARs when a build from before it saves it (R-em3d41-2d).
    /// </summary>
    public List<JsonElement> Variables      { get; set; } = [];

    /// <summary>brief-em3d-49 — the document's ports. They belong to the document, not to a setup: every setup uses all
    /// of them.</summary>
    public List<C3dPort> Ports { get; set; } = [];

    /// <summary>brief-em3d-49 — boundaries on named faces of dielectric and air objects.</summary>
    public List<C3dFaceBoundary> FaceBoundaries { get; set; } = [];

    /// <summary>Brief 42's embedded EM setups (the <c>.cem</c> schema minus <c>LayoutRef</c>), each read by the
    /// <c>.cem</c> reader (<see cref="C3dSetups.Read"/>).</summary>
    public List<JsonElement> Setups         { get; set; } = [];

    /// <summary>Keys this build does not read, kept and written back — see <see cref="C3dObject.Unread"/>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}
