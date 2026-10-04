// brief-em3d-111 — glTF export: the ONE function File ▸ Export ▸ glTF… and `circuitrf convert x.c3d -o x.glb` both call. It writes
// WHAT THE VIEW DRAWS — the same triangles, the same face splits, brief 104's shading normals and brief 105's appearance slots, read
// from Scene3DModel — as binary glTF 2.0 (GlbWriter), so a path tracer can render what the realistic view approximates.
//
// WHY src/Render (overview §0): the normals (ShadingNormals) and the scene (Scene3DModel) live here, and src/Design cannot reference
// src/Render. It draws nothing; src/Cli already references this project.
//
// CONTENT (R-em3d111-1b). Every visible solid and sheet with an appearance. Hidden objects, the air box, ports, boundaries, face
// tints, reference images, wireframes (no material) and the dimmed parent around a pushed-in child are not written; neither is
// anything the view never draws as geometry (grid, overlays). A Model: false object IS written — it is drawn.
//
// FRAMES (R-em3d111-1e). Meshes are in the scene-local frame (metres, relative to the scene's origin) or, with Assembly, in their
// instance's own frame. A ROOT node carries the origin's translation and the Z-up → Y-up turn (−90° about x), so every viewer stands
// the model up. Positions stay small floats, as the view keeps them, and the root's translation carries the large number in double.
//
// THE FIELD (R-em3d111-2) is a separate unlit mesh with linear COLOR_0. A vertex colour is interpolated between corners while the
// view maps the VALUE per fragment, so each field triangle is subdivided — its channels interpolated as the GPU interpolates them,
// which makes a new vertex's colour exactly the fragment's — until no edge spans more than 1/16 of the range, at most four levels.
//
// DETERMINISTIC (R-em3d111-1g, gate 7): object order, first-use vertex order, lists rather than hash iteration, shortest round-trip
// numbers and no timestamp — the same scene is the same bytes.

using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;

namespace CircuitRF.Render.Scene3D.Export;

/// <summary>R-em3d111-2a — a drawn field plot to include: its triangles as the view packs them (three vertices each, scene-local),
/// the quantity, the range and the phase it is coloured at, and the objects it stands in for.</summary>
public sealed record GltfField(string Name, FieldVertex[] Vertices, FieldQuantity Quantity, FieldColorScale Scale, double PhaseRad,
                               IReadOnlyCollection<string> Covered);

/// <summary>R-em3d111-1f — a camera to write: the view's (GUI), or the Look's Camera (CLI), with the aspect it frames at.</summary>
public sealed record GltfCamera(Camera3D Camera, float Aspect);

/// <summary>The Export glTF dialog's options — and <c>convert</c>'s flags, one for one.</summary>
public sealed record GltfExportOptions
{
    /// <summary>Each placed instance a node sharing its cell's meshes; off writes everything flattened.</summary>
    public bool Assembly { get; init; }
    public GltfField? Field { get; init; }
    public GltfCamera? Camera { get; init; }
}

/// <summary>What is exported: the scene, which of its objects are shown (by ID − 1), the elaboration it was built from (instance
/// frames, provenance) and its document (groups), and the name the root node and the scene take.</summary>
public sealed record GltfExportSource(Scene3DModel Scene, IReadOnlyList<bool> Visible, C3dElaboration? Elaboration, C3dDocument? Document,
                                      string Name);

/// <summary>What <see cref="GltfExport.Build"/> made: the file's bytes and what the dialog and the CLI report of it.</summary>
public sealed class GltfExportResult
{
    public required byte[] Bytes { get; init; }
    public int Objects { get; init; }
    public long Triangles { get; init; }
    public int Materials { get; init; }
    public int Meshes { get; init; }
    public int InstanceNodes { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = [];
    /// <summary>The field's triangles after subdivision, and how many reached the level cap with an edge still too wide; −1 with no field.</summary>
    public long FieldTriangles { get; init; } = -1;
    public long FieldCapped { get; init; }
    public bool Camera { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>R-em3d111-3b — the dialog's summary line: objects, triangles, materials and the extensions that will be used.</summary>
    public string Summary
    {
        get
        {
            string s = $"{Objects} object{(Objects == 1 ? "" : "s")}, {Triangles:N0} triangle{(Triangles == 1 ? "" : "s")}, " +
                       $"{Materials} material{(Materials == 1 ? "" : "s")}";
            if (InstanceNodes > 0) s += $"; {InstanceNodes} instance node{(InstanceNodes == 1 ? "" : "s")}, {Meshes} mesh{(Meshes == 1 ? "" : "es")}";
            s += Extensions.Count == 0 ? "; no extensions" : "; " + string.Join(", ", Extensions);
            if (FieldTriangles >= 0) s += $"; field colours are per vertex; subdivided to {FieldTriangles:N0} triangles";
            return s + ".";
        }
    }
}

public static class GltfExport
{
    /// <summary>R-em3d111-2c — the widest an edge of a field triangle may span, as a fraction of the colour range, and the most
    /// times a triangle is split to get there.</summary>
    public const double FieldSpan = 1.0 / 16;
    public const int FieldLevels = 4;

    public const string Transmission = "KHR_materials_transmission", Ior = "KHR_materials_ior", Clearcoat = "KHR_materials_clearcoat",
                        Volume = "KHR_materials_volume", Unlit = "KHR_materials_unlit";

    /// <summary>R-em3d111-1g — <c>asset.generator</c>: circuitRF and the version every assembly carries from the repo-root VERSION
    /// file (STEP's originating system — one spelling).</summary>
    public static string Generator => StepExport.OriginatingSystem;

    /// <summary>The extension a glTF target takes (D1: binary only).</summary>
    public static bool IsGltfExtension(string path) => string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// R-em3d111-3c — what a document shows with no session state: every object its kind starts visible, less each the document hides
    /// (an object's own <c>Hidden</c>, found through its provenance: an operation's result, a wire's elements and balls). What the
    /// editor's pane follows on open (C3dEditorViewModel.ApplyHiddenFlags), so `convert` exports what opening the file shows.
    /// </summary>
    public static bool[] DocumentVisibility(Scene3DModel scene, C3dElaboration? elaboration, C3dDocument? document)
    {
        var hidden = new HashSet<string>(document?.Objects.Where(o => o.Hidden).Select(o => o.Name) ?? [], StringComparer.Ordinal);
        var v = new bool[scene.Objects.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var o = scene.Objects[i];
            v[i] = o.InitiallyVisible;
            if (hidden.Count == 0) continue;
            string owner = o.Name;
            if (elaboration?.Provenance.TryGetValue(o.Name, out var p) == true)
            {
                if (p.InstancePath.Length > 0) continue;
                owner = p.TopObject ?? p.ObjectName;
            }
            if (hidden.Contains(owner)) v[i] = false;
        }
        return v;
    }

    /// <summary>R-em3d111-1b — whether object <paramref name="o"/> is written: shown, a material's (not chrome, a tint, a wireframe or
    /// the dimmed parent), with an appearance, and not stood in for by the field.</summary>
    public static bool Exported(Scene3DModel scene, Scene3DObject o, IReadOnlyList<bool> visible, IReadOnlyCollection<string>? covered)
    {
        int k = (int)o.Id - 1;
        if (k < 0 || k >= visible.Count || !visible[k]) return false;
        if (Scene3DFramePlan.ChromeOfObject(scene, o) is not null || o.Tint || o.Wireframe || o.Context) return false;
        if (o.AppearanceSlot < 0 || o.AppearanceSlot >= scene.Appearances.Length) return false;
        return covered is null || !covered.Contains(o.Name);
    }

    /// <summary>Writes <paramref name="result"/> to <paramref name="path"/> through a temporary file renamed into place, so a failed
    /// write leaves whatever was there.</summary>
    public static string Write(GltfExportResult result, string path)
    {
        string output = Path.GetFullPath(path);
        if (Path.GetDirectoryName(output) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        string temp = output + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(temp, result.Bytes);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { /* best effort */ }
        }
        return output;
    }

    // ── Build ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The file for <paramref name="source"/> under <paramref name="options"/>, and what it holds.</summary>
    public static GltfExportResult Build(GltfExportSource source, GltfExportOptions options, CancellationToken ct = default)
        => new Builder(source, options, ct).Run();

    private sealed class Prim
    {
        public int Material;
        public readonly List<Vector3> Positions = [], Normals = [];
        public readonly List<uint> Indices = [];
        public readonly Dictionary<uint, uint> Local = [];       // scene vertex → this primitive's; lookups only, never iterated
    }

    private readonly record struct MaterialKey(int Slot, string Name, byte Alpha, double Thickness, bool DoubleSided);

    private sealed class Builder(GltfExportSource source, GltfExportOptions options, CancellationToken ct)
    {
        private readonly Scene3DModel _scene = source.Scene;
        private readonly GlbWriter _glb = new();
        private readonly JsonArray _nodes = [], _meshes = [], _materials = [];
        private readonly List<MaterialKey> _materialKeys = [];
        private readonly SortedSet<string> _extensions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _groupNodes = new(StringComparer.Ordinal), _instanceNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(int Mesh, List<Prim> Prims)>> _sharedMeshes = new(StringComparer.Ordinal);
        private readonly List<string> _notes = [];
        private int _root;
        private bool _volumeNote;

        public GltfExportResult Run()
        {
            _root = Node(new JsonObject
            {
                ["name"] = source.Name,
                ["rotation"] = new JsonArray(-Math.Sqrt(0.5), 0, 0, Math.Sqrt(0.5)),     // −90° about x: Z-up → Y-up
                ["translation"] = new JsonArray(_scene.Origin.X, _scene.Origin.Z, -_scene.Origin.Y),   // the origin, turned
            }, parent: -1);

            var frames = new Dictionary<string, C3dInstanceFrame>(StringComparer.Ordinal);
            if (options.Assembly && source.Elaboration is { } e)
                foreach (var f in e.Instances) frames.TryAdd(f.Path, f);

            var batches = new Dictionary<uint, List<Scene3DBatch>>();
            foreach (var b in _scene.Batches)
            {
                if (!batches.TryGetValue(b.ObjectId, out var list)) batches[b.ObjectId] = list = [];
                list.Add(b);
            }

            int objects = 0;
            long triangles = 0;
            var covered = options.Field?.Covered;
            foreach (var o in _scene.Objects)
            {
                ct.ThrowIfCancellationRequested();
                if (!Exported(_scene, o, source.Visible, covered) || !batches.TryGetValue(o.Id, out var mine)) continue;
                C3dProvenance? prov = null;
                source.Elaboration?.Provenance.TryGetValue(o.Name, out prov);
                C3dInstanceFrame? frame = prov is { InstancePath.Length: > 0 } && frames.TryGetValue(prov.InstancePath, out var fr) ? fr : null;

                var prims = Primitives(o, mine, frame);
                if (prims.Count == 0) continue;
                long tris = prims.Sum(p => (long)p.Indices.Count / 3);
                objects++;
                triangles += tris;

                int mesh;
                if (frame is not null)
                {
                    // Assembly: two elements whose contents are the same in their own frames share one mesh — the same part of the same
                    // cell, the same triangles and materials, and positions equal to within float noise at the scene's size (a scene's
                    // vertices are single precision, so STEP's 0.1 nm in DBU is finer than they hold).
                    string key = frame.DocumentPath + "\u001f" + prov!.ObjectName + "\u001f" + Structure(prims);
                    if (!_sharedMeshes.TryGetValue(key, out var candidates)) _sharedMeshes[key] = candidates = [];
                    int found = candidates.FindIndex(c => Same(c.Prims, prims));
                    if (found >= 0) mesh = candidates[found].Mesh;
                    else candidates.Add((mesh = Mesh(prov.ObjectName, prims), prims));
                    Node(new JsonObject { ["name"] = prov.ObjectName, ["mesh"] = mesh }, InstanceNode(frame.Path, frames));
                }
                else
                {
                    mesh = Mesh(o.Name, prims);
                    Node(new JsonObject { ["name"] = o.Name, ["mesh"] = mesh }, GroupNode(GroupOf(o, prov)));
                }
            }

            long fieldTriangles = -1, capped = 0;
            if (options.Field is { } field) (fieldTriangles, capped) = Field(field);
            if (options.Camera is { } camera) Camera(camera);

            if (_volumeNote)
                _notes.Add("KHR_materials_volume's thickness is each object's smallest bounding-box extent: an approximation of the distance light travels through it.");
            if (fieldTriangles >= 0)
                _notes.Add($"Field colours are per vertex; subdivided to {fieldTriangles:N0} triangles." +
                           (capped > 0 ? $" {capped:N0} reached the {FieldLevels}-level cap with an edge still spanning more than 1/16 of the range." : ""));

            var root = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = Generator },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["name"] = source.Name, ["nodes"] = new JsonArray(_root) }),
                ["nodes"] = _nodes,
            };
            if (_meshes.Count > 0) root["meshes"] = _meshes;
            if (_materials.Count > 0) root["materials"] = _materials;
            if (_cameras.Count > 0) root["cameras"] = _cameras;
            if (_extensions.Count > 0) root["extensionsUsed"] = new JsonArray([.. _extensions.Select(x => (JsonNode)x)]);
            byte[] bytes = _glb.Finish(root);
            return new GltfExportResult
            {
                Bytes = bytes, Objects = objects, Triangles = triangles, Materials = _materialKeys.Count, Meshes = _meshes.Count,
                InstanceNodes = _instanceNodes.Count, Extensions = [.. _extensions], FieldTriangles = fieldTriangles, FieldCapped = capped,
                Camera = options.Camera is not null, Notes = _notes,
            };
        }

        // ── nodes ─────────────────────────────────────────────────────────────────────────────────────────────────────

        private int Node(JsonObject node, int parent)
        {
            _nodes.Add(node);
            int index = _nodes.Count - 1;
            if (parent >= 0)
            {
                var p = (JsonObject)_nodes[parent]!;
                if (p["children"] is not JsonArray children) p["children"] = children = [];
                children.Add(index);
            }
            return index;
        }

        /// <summary>The node of group path <paramref name="path"/> (<c>pa/match</c>), made with its parents on first use; the root for none.</summary>
        private int GroupNode(string? path)
        {
            if (string.IsNullOrEmpty(path)) return _root;
            if (_groupNodes.TryGetValue(path, out int n)) return n;
            int slash = path.LastIndexOf('/');
            int parent = GroupNode(slash > 0 ? path[..slash] : null);
            return _groupNodes[path] = Node(new JsonObject { ["name"] = slash >= 0 ? path[(slash + 1)..] : path }, parent);
        }

        /// <summary>The group an object is drawn in: the document object's own (an operation's for its result), or — for a part of an
        /// instance — the top-level instance's.</summary>
        private string? GroupOf(Scene3DObject o, C3dProvenance? prov)
        {
            var doc = source.Document;
            if (doc is null) return null;
            if (prov is { InstancePath.Length: > 0 }) return InstanceGroup(prov.InstancePath);
            string name = prov is null ? o.Name : prov.TopObject ?? prov.ObjectName;
            if (_documentGroups is null)
            {
                _documentGroups = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var d in doc.Objects) _documentGroups.TryAdd(d.Name, d.Group);
            }
            return _documentGroups.GetValueOrDefault(name);
        }

        private Dictionary<string, string?>? _documentGroups;

        private string? InstanceGroup(string path)
        {
            string top = path.Split('/')[0];
            int bracket = top.IndexOf('[');
            if (bracket > 0) top = top[..bracket];
            return source.Document?.Instances.FirstOrDefault(i => i.Name == top)?.Group;
        }

        /// <summary>R-em3d111-1e — an instance element's node: under its parent element's (or its group, at the top), carrying the
        /// placement relative to that parent — at the top, relative to the scene-local origin.</summary>
        private int InstanceNode(string path, Dictionary<string, C3dInstanceFrame> frames)
        {
            if (_instanceNodes.TryGetValue(path, out int n)) return n;
            var frame = frames[path];
            string? parentPath = null;
            for (int at = path.LastIndexOf('/'); at > 0; at = path.LastIndexOf('/', at - 1))
                if (frames.ContainsKey(path[..at])) { parentPath = path[..at]; break; }
            C3dTransform local;
            int parent;
            if (parentPath is not null)
            {
                parent = InstanceNode(parentPath, frames);
                local = frame.World.Then(frames[parentPath].World.Inverse());
            }
            else
            {
                parent = GroupNode(InstanceGroup(path));
                var o = _scene.Origin;
                local = frame.World with { Tx = frame.World.Tx - o.X, Ty = frame.World.Ty - o.Y, Tz = frame.World.Tz - o.Z };
            }
            string name = parentPath is null ? path : path[(parentPath.Length + 1)..];
            var node = new JsonObject { ["name"] = name };
            if (local != C3dTransform.Identity)
                node["matrix"] = new JsonArray(local.M00, local.M10, local.M20, 0, local.M01, local.M11, local.M21, 0,
                                               local.M02, local.M12, local.M22, 0, local.Tx, local.Ty, local.Tz, 1);   // column-major
            return _instanceNodes[path] = Node(node, parent);
        }

        // ── meshes ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Object <paramref name="o"/>'s triangles, one primitive per appearance slot it uses (first use first), each with the
        /// vertices it uses in first-use order: positions in the scene-local frame, or <paramref name="frame"/>'s own.</summary>
        private List<Prim> Primitives(Scene3DObject o, List<Scene3DBatch> mine, C3dInstanceFrame? frame)
        {
            var prims = new List<Prim>();
            var bySlot = new Dictionary<int, Prim>();
            bool shaded = _scene.ShadeVertices.Length == _scene.Vertices.Length;
            C3dTransform? inverse = frame?.World.Inverse();
            foreach (var b in mine)
            {
                for (int t = 0; t < b.IndexCount / 3; t++)
                {
                    int at = b.FirstIndex + 3 * t;
                    uint first = _scene.Indices[at];
                    int slot = shaded ? (int)(_scene.ShadeVertices[first].Slot & Scene3DShadeVertex.SlotMask) : o.AppearanceSlot;
                    if (slot >= _scene.Appearances.Length) slot = o.AppearanceSlot;
                    if (!bySlot.TryGetValue(slot, out var prim))
                    {
                        bySlot[slot] = prim = new Prim { Material = Material(o, slot) };
                        prims.Add(prim);
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        uint v = _scene.Indices[at + k];
                        if (!prim.Local.TryGetValue(v, out uint local))
                        {
                            prim.Local[v] = local = (uint)prim.Positions.Count;
                            var sv = _scene.Vertices[v];
                            var p = new Vector3(sv.X, sv.Y, sv.Z) + b.Offset;
                            var nrm = shaded ? new Vector3(_scene.ShadeVertices[v].Nx, _scene.ShadeVertices[v].Ny, _scene.ShadeVertices[v].Nz) : Vector3.Zero;
                            if (inverse is { } inv)
                            {
                                var (wx, wy, wz) = _scene.ToWorld(p);
                                var (lx, ly, lz) = Apply(inv, wx, wy, wz);
                                p = new Vector3((float)lx, (float)ly, (float)lz);
                                nrm = Rotate(inv, nrm);
                            }
                            prim.Positions.Add(p);
                            prim.Normals.Add(nrm);
                        }
                        prim.Indices.Add(local);
                    }
                }
            }
            if (!shaded) foreach (var prim in prims) Renormal(prim);
            return prims;
        }

        /// <summary>A scene built with no shade stream (not the builder's): the normals ShadingNormals makes, its duplicates appended.</summary>
        private static void Renormal(Prim prim)
        {
            var r = ShadingNormals.Compute(prim.Positions, prim.Indices.ToArray());
            foreach (int d in r.Duplicates) prim.Positions.Add(prim.Positions[d]);
            prim.Normals.Clear();
            prim.Normals.AddRange(r.Normals);
            prim.Indices.Clear();
            prim.Indices.AddRange(r.Indices);
        }

        private static (double X, double Y, double Z) Apply(C3dTransform m, double x, double y, double z) => (
            m.M00 * x + m.M01 * y + m.M02 * z + m.Tx, m.M10 * x + m.M11 * y + m.M12 * z + m.Ty, m.M20 * x + m.M21 * y + m.M22 * z + m.Tz);

        private static Vector3 Rotate(C3dTransform m, Vector3 n) => new(
            (float)(m.M00 * n.X + m.M01 * n.Y + m.M02 * n.Z), (float)(m.M10 * n.X + m.M11 * n.Y + m.M12 * n.Z),
            (float)(m.M20 * n.X + m.M21 * n.Y + m.M22 * n.Z));

        /// <summary>The part of two meshes compared exactly: each primitive's material, its counts and its triangles.</summary>
        private static string Structure(List<Prim> prims)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var b = new byte[8];
            void Add(long v) { System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b, v); hash.AppendData(b); }
            foreach (var p in prims)
            {
                Add(p.Material); Add(p.Positions.Count); Add(p.Indices.Count);
                foreach (uint i in p.Indices) Add(i);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>Whether two meshes of one structure are the same shape: every position within a millionth of the scene's size, every
        /// normal within 1e-3.</summary>
        private bool Same(List<Prim> a, List<Prim> b)
        {
            float tol = MathF.Max(1e-12f, 1e-6f * (_scene.BoundsMax - _scene.BoundsMin).Length());
            for (int k = 0; k < a.Count; k++)
                for (int i = 0; i < a[k].Positions.Count; i++)
                {
                    var d = Vector3.Abs(a[k].Positions[i] - b[k].Positions[i]);
                    if (d.X > tol || d.Y > tol || d.Z > tol || Vector3.Distance(a[k].Normals[i], b[k].Normals[i]) > 1e-3f) return false;
                }
            return true;
        }

        private int Mesh(string name, List<Prim> prims)
        {
            var list = new JsonArray();
            foreach (var p in prims)
            {
                int position = _glb.Vec3(p.Positions, bounds: true);
                int normal = _glb.Vec3(p.Normals);
                int indices = _glb.Indices(p.Indices, p.Positions.Count);
                list.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normal },
                    ["indices"] = indices, ["material"] = p.Material, ["mode"] = 4,
                });
            }
            _meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = list });
            return _meshes.Count - 1;
        }

        // ── materials ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>R-em3d111-1d — the material object <paramref name="o"/> draws slot <paramref name="slot"/> with: the slot's values,
        /// its material's name (<c>+override</c> where the document's statement made it look different), the alpha its stated
        /// transparency draws it at, its volume's thickness, and a sheet's two sides.</summary>
        private int Material(Scene3DObject o, int slot)
        {
            var a = _scene.Appearances[slot];
            string name = (o.Material is { Length: > 0 } m ? m : $"appearance {slot}") + (Overridden(o, slot) ? "+override" : "");
            byte alpha = o.Transparency is not null ? (byte)(o.Rgba >> 24) : (byte)255;
            double thickness = a.Transmission > 0 ? Math.Max(0, (double)MathF.Min(o.Max.X - o.Min.X, MathF.Min(o.Max.Y - o.Min.Y, o.Max.Z - o.Min.Z))) : 0;
            var key = new MaterialKey(slot, name, alpha, thickness, o.Kind == Scene3DKind.Sheet);
            int found = _materialKeys.IndexOf(key);
            if (found >= 0) return found;
            _materialKeys.Add(key);

            var pbr = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(a.BaseColor.R, a.BaseColor.G, a.BaseColor.B, alpha / 255.0),
                ["metallicFactor"] = a.Metallic,
                ["roughnessFactor"] = a.Roughness,
            };
            var mat = new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = pbr };
            if (alpha < 255) mat["alphaMode"] = "BLEND";
            if (key.DoubleSided) mat["doubleSided"] = true;
            var ext = new JsonObject();
            if (a.Transmission != AppearanceResolver.DefaultTransmission) ext[Transmission] = new JsonObject { ["transmissionFactor"] = a.Transmission };
            if (a.Ior != AppearanceResolver.DefaultIor) ext[Ior] = new JsonObject { ["ior"] = a.Ior };
            if (a.Clearcoat != AppearanceResolver.DefaultClearcoat || a.ClearcoatRoughness != AppearanceResolver.DefaultClearcoatRoughness)
            {
                var c = new JsonObject();
                if (a.Clearcoat != AppearanceResolver.DefaultClearcoat) c["clearcoatFactor"] = a.Clearcoat;
                if (a.ClearcoatRoughness != AppearanceResolver.DefaultClearcoatRoughness) c["clearcoatRoughnessFactor"] = a.ClearcoatRoughness;
                ext[Clearcoat] = c;
            }
            bool attenuationColour = a.AttenuationColor != AppearanceColour.White, attenuationDistance = double.IsFinite(a.AttenuationDistance);
            if (thickness > 0 || attenuationColour || attenuationDistance)
            {
                var v = new JsonObject();
                if (thickness > 0) { v["thicknessFactor"] = thickness; _volumeNote = true; }
                if (attenuationDistance) v["attenuationDistance"] = a.AttenuationDistance;
                if (attenuationColour) v["attenuationColor"] = new JsonArray(a.AttenuationColor.R, a.AttenuationColor.G, a.AttenuationColor.B);
                ext[Volume] = v;
            }
            if (ext.Count > 0)
            {
                mat["extensions"] = ext;
                foreach (var kv in ext) _extensions.Add(kv.Key);
            }
            _materials.Add(mat);
            return _materials.Count - 1;
        }

        /// <summary>Whether the document's statement (the object's own, an instance's) made <paramref name="o"/> look different from its
        /// material alone: the resolver asked again without it.</summary>
        private bool Overridden(Scene3DObject o, int slot)
        {
            int k = (int)(o.Element >= 0 ? o.Prototype : o.Id) - 1;
            if (k < 0 || k >= _scene.AppearanceRequests.Length || _scene.AppearanceRequests[k] is not { Override: not null } request) return false;
            return AppearanceResolver.Resolve(request with { Override = null }).Values != _scene.Appearances[slot];
        }

        // ── the field (R-em3d111-2) ───────────────────────────────────────────────────────────────────────────────────

        private (long Triangles, long Capped) Field(GltfField field)
        {
            var q = field.Quantity;
            var map = ColorMap3D.For(q);
            int channels = q.Array.Components * (q.Array.IsComplex ? 2 : 1);
            var positions = new List<Vector3>();
            var colours = new List<Vector3>();
            long capped = 0;
            var v = field.Vertices;
            for (int t = 0; t + 2 < v.Length; t += 3)
            {
                if ((t & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                capped += Split(Corner(v[t]), Corner(v[t + 1]), Corner(v[t + 2]), 0);
            }

            int position = _glb.Vec3(positions, bounds: true);
            int colour = _glb.Vec3(colours);
            _materials.Add(new JsonObject
            {
                ["name"] = field.Name,
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(1.0, 1.0, 1.0, 1.0), ["metallicFactor"] = 0.0, ["roughnessFactor"] = 1.0 },
                ["extensions"] = new JsonObject { [Unlit] = new JsonObject() },
            });
            _extensions.Add(Unlit);
            int material = _materials.Count - 1;
            _meshes.Add(new JsonObject
            {
                ["name"] = field.Name,
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = position, ["COLOR_0"] = colour }, ["material"] = material, ["mode"] = 4,
                }),
            });
            Node(new JsonObject { ["name"] = field.Name, ["mesh"] = _meshes.Count - 1 }, _root);
            return (positions.Count / 3, capped);

            // a corner: its position and its channels, as FieldView.Pack laid them out (real parts, then imaginary)
            double[] Corner(in FieldVertex f)
            {
                var c = new double[3 + channels];
                c[0] = f.X; c[1] = f.Y; c[2] = f.Z;
                int comps = q.Array.Components;
                c[3] = f.R0;
                if (comps == 3) { c[4] = f.R1; c[5] = f.R2; }
                if (q.Array.IsComplex)
                {
                    c[3 + comps] = f.I0;
                    if (comps == 3) { c[3 + comps + 1] = f.I1; c[3 + comps + 2] = f.I2; }
                }
                return c;
            }

            double At(double[] c) => field.Scale.Position(q.Evaluate(c.AsSpan(3), field.PhaseRad));

            double[] Mid(double[] a, double[] b)
            {
                var m = new double[a.Length];
                for (int i = 0; i < m.Length; i++) m[i] = 0.5 * (a[i] + b[i]);
                return m;
            }

            long Split(double[] a, double[] b, double[] c, int level)
            {
                double ta = At(a), tb = At(b), tc = At(c);
                double span = Math.Max(Math.Abs(ta - tb), Math.Max(Math.Abs(tb - tc), Math.Abs(tc - ta)));
                if (span > FieldSpan && level < FieldLevels)
                {
                    var ab = Mid(a, b); var bc = Mid(b, c); var ca = Mid(c, a);
                    return Split(a, ab, ca, level + 1) + Split(ab, b, bc, level + 1) + Split(ca, bc, c, level + 1) + Split(ab, bc, ca, level + 1);
                }
                Emit(a, ta); Emit(b, tb); Emit(c, tc);
                return span > FieldSpan ? 1 : 0;
            }

            void Emit(double[] c, double t)
            {
                positions.Add(new Vector3((float)c[0], (float)c[1], (float)c[2]));
                colours.Add(LinearColour(map, t));
            }
        }

        // ── the camera (R-em3d111-1f) ─────────────────────────────────────────────────────────────────────────────────

        private readonly JsonArray _cameras = [];

        private void Camera(GltfCamera input)
        {
            var cam = input.Camera;
            float aspect = input.Aspect > 0 ? input.Aspect : 1;
            var (near, far) = cam.DepthRange();
            float fov = cam.FovY > 0 && cam.FovY < MathF.PI ? cam.FovY : Camera3D.DefaultFovY;
            JsonObject camera;
            if (cam.Projection == Projection3D.Orthographic)
            {
                float h = MathF.Max(1e-12f, cam.Distance * MathF.Tan(fov * 0.5f));
                float zn = MathF.Max(0, near);
                camera = new JsonObject
                {
                    ["name"] = "View", ["type"] = "orthographic",
                    ["orthographic"] = new JsonObject { ["xmag"] = h * aspect, ["ymag"] = h, ["znear"] = zn, ["zfar"] = MathF.Max(far, zn + 1e-9f) },
                };
            }
            else
            {
                float zn = near > 0 ? near : MathF.Max(1e-9f, far * 1e-4f);
                camera = new JsonObject
                {
                    ["name"] = "View", ["type"] = "perspective",
                    ["perspective"] = new JsonObject { ["aspectRatio"] = aspect, ["yfov"] = fov, ["znear"] = zn, ["zfar"] = MathF.Max(far, zn * 2) },
                };
            }
            _cameras.Add(camera);
            var q = CameraRotation(cam);
            var eye = cam.Eye;
            Node(new JsonObject
            {
                ["name"] = "View", ["camera"] = _cameras.Count - 1,
                ["rotation"] = new JsonArray(q.X, q.Y, q.Z, q.W), ["translation"] = new JsonArray(eye.X, eye.Y, eye.Z),
            }, _root);
        }
    }

    /// <summary>The rotation taking a glTF camera's own axes (x right, y up, looking down −z) onto <paramref name="camera"/>'s, in the
    /// scene-local frame its node sits in.</summary>
    public static Quaternion CameraRotation(Camera3D camera)
    {
        Vector3 r = Vector3.Normalize(camera.Right), u = Vector3.Normalize(camera.Up), b = Vector3.Normalize(camera.Back);
        // System.Numerics is row-vector: row i is where axis i goes.
        var m = new Matrix4x4(r.X, r.Y, r.Z, 0, u.X, u.Y, u.Z, 0, b.X, b.Y, b.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>R-em3d111-2b — the colour map's colour at <paramref name="t"/> (sRGB, as the view shows it), decoded to the linear
    /// light glTF vertex colours are in.</summary>
    public static Vector3 LinearColour(ColorMap3D map, double t)
    {
        var (r, g, b) = map.Sample((float)t);
        var c = AppearanceColour.FromSrgb(r, g, b);
        return new Vector3((float)c.R, (float)c.G, (float)c.B);
    }
}
