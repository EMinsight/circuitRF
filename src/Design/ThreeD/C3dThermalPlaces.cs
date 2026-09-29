// brief-em3d-73 R-em3d73-4 — the document's thermal PLACES: where heat is put, where temperature is read, where the mesh
// is fine, and which contacts override the technology's interface resistance.
//
// PLACES IN THE DOCUMENT, VALUES IN THE SETUP (overview §1c). One model carries several thermal setups and EM setups beside
// them, so the geometry of a heat source lives here and its power in the setup that runs it — a power sweeps like any
// variable because it is an expression there. These are lists BESIDE Ports, not C3dObjects, because none of them is
// material: an EM lowering never sees one, the precedence rules never see one, and a sheet drawn as a heat source can never
// be mistaken for a PEC sheet (R-em3d73-4e).
//
// EVERY LIST IS LEFT OUT OF THE FILE WHEN EMPTY (C3dPersistence.OmitEmpty), so every .c3d written before this brief
// round-trips byte for byte. Every dimension is a DBU integer or an expression, as everywhere in the .c3d (C3dBindings).

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.ThreeD;

/// <summary>How a heat source's power is stated.</summary>
public enum C3dHeatDensity
{
    /// <summary>The whole power, W — the default.</summary>
    Total,
    /// <summary>W/m² over a sheet source's area.</summary>
    PerArea,
    /// <summary>W/m³ over a solid source's volume.</summary>
    PerVolume,
}

/// <summary>A heat source's sheet: a rectangle or a polygon on a drawing plane, spelled exactly as a <c>C3dSheet</c>'s.</summary>
public sealed class C3dHeatSheet : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    public C3dPlane Plane  { get; set; }
    public long     Offset { get; set; }
    public C3dRect? Rect   { get; set; }
    public List<C3dPoint2>       Outline { get; set; } = [];
    public List<List<C3dPoint2>> Holes   { get; set; } = [];
}

/// <summary>
/// R-em3d73-4a — a named place heat is put: a <see cref="Sheet"/> (which must lie inside or on ONE solid — a sheet
/// straddling two is refused, since the power split between them would be a guess) or a <see cref="Solid"/> (an object,
/// <c>U1/channel</c> allowed: a volumetric source over it). Exactly one of the two.
/// </summary>
public sealed class C3dHeatSource
{
    /// <summary>Unique across the document's objects, instances, ports and thermal places.</summary>
    public string Name { get; set; } = "";

    /// <summary>A sheet the heat is spread over.</summary>
    public C3dHeatSheet? Sheet { get; set; }

    /// <summary>An object whose whole volume the heat is spread through.</summary>
    public string? Solid { get; set; }

    /// <summary>The DEFAULT power, an expression in the document's variables (<c>Pdiss</c>, <c>0.5 W</c>); a thermal setup
    /// may override it. In W unless <see cref="Density"/> says otherwise.</summary>
    public string? Power { get; set; }

    /// <summary><c>Total</c> (W, the default and omitted), <c>PerArea</c> (W/m², a sheet) or <c>PerVolume</c> (W/m³, a
    /// solid).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dHeatDensity Density { get; set; }

    /// <summary>brief-em3d-93 D1 — whether the source heats a run. False keeps it drawn and editable and puts no power in any
    /// thermal run. Written only when false.</summary>
    [DefaultValue(true)]
    public bool Model { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>What a probe reports over its place.</summary>
public enum C3dProbeStat
{
    /// <summary>The hottest temperature.</summary>
    Max,
    /// <summary>The coolest temperature.</summary>
    Min,
    /// <summary>The area- (face, spot) or volume- (solid) weighted mean.</summary>
    Avg,
}

/// <summary>A spot probe: a disk on a face — the area an IR microscope averages (overview §1i).</summary>
public sealed class C3dProbeSpot : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>The face, <c>object/face</c>.</summary>
    public string    Face     { get; set; } = "";

    /// <summary>The disk's centre, on the face.</summary>
    public C3dPoint3 Center   { get; set; }

    /// <summary>The disk's diameter.</summary>
    public long      Diameter { get; set; }
}

/// <summary>A line probe: temperature along the segment between two points.</summary>
public sealed class C3dProbeLine : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    public C3dPoint3 From { get; set; }
    public C3dPoint3 To   { get; set; }
}

/// <summary>
/// R-em3d73-4b — a named place temperature is read: exactly one of <see cref="Point"/>, <see cref="Face"/>,
/// <see cref="Solid"/>, <see cref="Spot"/>, <see cref="Line"/> or <see cref="Wire"/>.
/// </summary>
public sealed class C3dProbe : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>Unique across the document's objects, instances, ports and thermal places.</summary>
    public string Name { get; set; } = "";

    /// <summary>A point, <c>[x, y, z]</c>: the temperature there.</summary>
    [Description("[x, y, z]")]
    public C3dPoint3? Point { get; set; }

    /// <summary>A face, <c>object/face</c> (§6.4's naming; a face an operation split is every piece) — or several, read as one
    /// place over their union (brief-em3d-86: a face a fold split in two). One face is written as a string, several as a list.</summary>
    [JsonConverter(typeof(C3dFaceListJsonConverter))]
    public List<string>? Face { get; set; }

    /// <summary>An object: over its volume.</summary>
    public string? Solid { get; set; }

    /// <summary>A disk on a face.</summary>
    public C3dProbeSpot? Spot { get; set; }

    /// <summary>A segment: T along it.</summary>
    public C3dProbeLine? Line { get; set; }

    /// <summary>A bond wire by name, or one element of an array (<c>w1[3]</c>): T along it and its maximum.</summary>
    public string? Wire { get; set; }

    /// <summary><c>Max</c>, <c>Min</c> or <c>Avg</c>, for a face, solid, spot or wire. A point and a line take none.</summary>
    public C3dProbeStat? Stat { get; set; }

    /// <summary>A limit, °C (D11): the result flags where it is crossed — a mould compound's glass transition, a wire's
    /// rating.</summary>
    public double? LimitC { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The kinds this probe states, by key — exactly one for a well-formed probe.</summary>
    public IReadOnlyList<string> Kinds()
    {
        var k = new List<string>(1);
        if (Point is not null) k.Add(nameof(Point));
        if (Face is not null) k.Add(nameof(Face));
        if (Solid is not null) k.Add(nameof(Solid));
        if (Spot is not null) k.Add(nameof(Spot));
        if (Line is not null) k.Add(nameof(Line));
        if (Wire is not null) k.Add(nameof(Wire));
        return k;
    }
}

/// <summary>
/// R-em3d73-4c — a box with a target element size: the fine mesh goes where the user puts it. Read by every 3D setup
/// that meshes with Gmsh — thermal and Palace (brief 74 lowers it); openEMS ignores it and says so.
/// </summary>
public sealed class C3dMeshRegion : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>Unique across the document's objects, instances, ports and thermal places.</summary>
    public string    Name { get; set; } = "";
    public C3dPoint3 Min  { get; set; }
    public C3dPoint3 Size { get; set; }

    /// <summary>The target element size inside the box, micrometres (or an expression).</summary>
    public double SizeUm { get; set; }

    /// <summary>How fast elements may grow leaving the box; omitted takes the setup's.</summary>
    public double? Grading { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// R-em3d73-4d — the thermal resistance of the contact between two named objects, overriding the technology's
/// material-pair value for that contact only (a die's attach, a voided region). The two must touch.
/// </summary>
public sealed class C3dContactResistance
{
    /// <summary>The two objects, by elaborated name (<c>U1/die</c> allowed). Order means nothing.</summary>
    public List<string> Between { get; set; } = [];

    /// <summary>m²·K/W, positive.</summary>
    public double ResistanceM2KW { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-76 R-em3d76-2a — a box over a via field whose board dielectric, copper planes and via barrels a thermal run
/// replaces, when <see cref="Enabled"/>, with ONE block of anisotropic effective conductivity (k_xy, k_xy, k_z) computed
/// from what it replaced (<see cref="Thermal.ThermalEffectiveBlocks"/> states the mixture). An approximation, so it is
/// never enabled by default: disabled, the geometry is solved as drawn, and the A/B comparison is one toggle.
/// </summary>
public sealed class C3dEffectiveBlock : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>Unique across the document's objects, instances, ports and thermal places.</summary>
    public string    Name { get; set; } = "";
    public C3dPoint3 Min  { get; set; }
    public C3dPoint3 Size { get; set; }

    /// <summary>Replace what is inside the box. Off (the default, and omitted): the geometry is solved as drawn.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Enabled { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-76 R-em3d76-4a — a mirror plane the user modelled HALF of the device across: normal to <see cref="Axis"/>,
/// at <see cref="At"/>. It lies on the model's own extent (the cut face), the faces on it are insulated (exact for a mirror),
/// and what is drawn is what is solved — a source carries the power in the modelled part. At most one per axis. Measures read
/// <c>SymmetryFactor</c> (2ⁿ) so a whole-device figure is explicit; the viewer draws the mirrored halves.
/// </summary>
public sealed class C3dSymmetryPlane : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    public C3dAxis Axis { get; set; }

    /// <summary>The plane's coordinate along <see cref="Axis"/>, DBU (or an expression).</summary>
    public long At { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-86 R-em3d86-2 — the image plane the document's DRAWN wires see when an array's RF current is shared among them
/// (wBond's image method, <c>ArrayShare</c>): horizontal in the view's frame, at <see cref="Z"/>, or at the height of a
/// horizontal conductor face named by <see cref="Face"/>. Spelled <c>{ "Z": … }</c>, or the face's <c>object/face</c> alone. It
/// is never inferred (em-3d.md §6.4): "the nearest conductor below the pads" would change a share silently when someone drew
/// a lid. Omitted, drawn wires are shared in free space. A .wBond's wires keep their own design's plane.
/// </summary>
public sealed class C3dWireGroundPlane : IC3dBindable
{
    [JsonIgnore] public Dictionary<string, C3dExpr?[]>? Exprs { get; set; }

    /// <summary>The plane's height, DBU (or an expression). Read when <see cref="Face"/> is null.</summary>
    public long Z { get; set; }

    /// <summary>A horizontal conductor face, <c>object/face</c>, whose height is the plane's. Written as the key's whole value.</summary>
    [JsonIgnore] public string? Face { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary><see cref="C3dWireGroundPlane"/> as a face's name (a JSON string) or as <c>{ "Z": … }</c>. Declared on the
/// document's PROPERTY, so the object form reads and writes through the type's own contract (whose Z takes an expression).</summary>
internal sealed class C3dWireGroundPlaneJsonConverter : JsonConverter<C3dWireGroundPlane>
{
    public override C3dWireGroundPlane? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? new C3dWireGroundPlane { Face = reader.GetString() }
            : JsonSerializer.Deserialize<C3dWireGroundPlane>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, C3dWireGroundPlane value, JsonSerializerOptions options)
    {
        if (value.Face is { } face) writer.WriteStringValue(face);
        else JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>A list of faces spelled as one string when it holds one, and as a JSON list when it holds several: a file with a
/// single face reads and writes exactly as it did before a list was possible.</summary>
internal sealed class C3dFaceListJsonConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString()!];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException(C3dDiagnostics.FaceListShape(reader.TokenType.ToString()).Render());
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException(C3dDiagnostics.FaceListShape("a list holding " + reader.TokenType).Render());
            list.Add(reader.GetString()!);
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) { writer.WriteStringValue(value[0]); return; }
        writer.WriteStartArray();
        foreach (string f in value) writer.WriteStringValue(f);
        writer.WriteEndArray();
    }
}
