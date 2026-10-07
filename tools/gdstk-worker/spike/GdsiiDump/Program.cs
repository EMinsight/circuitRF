// GdsiiDump -- the G0 spike's bridge to circuitRF's own GDSII stack (brief-oasis-gdstk.md §3 Q4).
//
//   GdsiiDump read  <file.gds>               GdsiiReader -> the spike's canonical JSON on stdout
//   GdsiiDump write <in.json> <out.gds>      the canonical JSON -> InterchangeStructure -> GdsiiWriter
//
// The canonical form holds GDSII's own conventions (reflect about X, then rotate), so an instance goes
// through GdsiiTransformCodec in each direction exactly as GdsiiReader and GdsiiWriter do. Whatever our
// model cannot hold is LISTED on the way in ("lost"), never silently dropped -- that list is part of
// Q4's answer. Scratch harness code.

using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;

if (args.Length >= 2 && args[0] == "read") return Read(args[1]);
if (args.Length >= 3 && args[0] == "write") return Write(args[1], args[2]);
Console.Error.WriteLine("usage: GdsiiDump read <file.gds> | write <in.json> <out.gds>");
return 2;

static int Read(string path)
{
    using var fs = File.OpenRead(path);
    var reader = GdsiiReader.Open(fs);
    var cells = new JsonObject();
    foreach (var s in reader.ReadStructures())
    {
        var polys = new JsonArray();
        var paths = new JsonArray();
        var labels = new JsonArray();
        var refs = new JsonArray();
        foreach (var shape in s.Shapes)
        {
            switch (shape)
            {
                case PolygonShape p:
                    polys.Add(new JsonObject { ["layer"] = p.Layer.Layer, ["datatype"] = p.Layer.Datatype, ["xy"] = Arr(p.Xy) });
                    break;
                case RectShape r:
                    polys.Add(new JsonObject { ["layer"] = r.Layer.Layer, ["datatype"] = r.Layer.Datatype,
                        ["xy"] = Arr([r.X1, r.Y1, r.X2, r.Y1, r.X2, r.Y2, r.X1, r.Y2]) });
                    break;
                case PathShape p:
                    paths.Add(new JsonObject
                    {
                        ["layer"] = p.Layer.Layer, ["datatype"] = p.Layer.Datatype, ["width"] = p.Width,
                        ["end"] = p.End switch { PathEndStyle.Round => "round", PathEndStyle.Square => "halfwidth",
                                                 PathEndStyle.Extended => "extended", _ => "flush" },
                        ["ext"] = p.End == PathEndStyle.Extended ? Arr([p.Width / 2, p.Width / 2]) : null,
                        ["xy"] = Arr(p.Xy),
                    });
                    break;
                case LabelShape l:
                    labels.Add(new JsonObject
                    {
                        ["layer"] = l.Layer.Layer, ["texttype"] = l.IsPort ? 1 : 0, ["text"] = l.Text, ["x"] = l.X, ["y"] = l.Y,
                        ["rotation"] = l.RotationDegrees, ["magnification"] = 1.0, ["mirror"] = false, ["height"] = l.Height,
                    });
                    break;
                default:
                    Console.Error.WriteLine($"{s.Name}: a {shape.GetType().Name} the dump has no form for");
                    break;
            }
        }
        foreach (var i in s.Instances)
        {
            var (reflect, angle) = GdsiiTransformCodec.ToGdsii(i.MirrorX, i.RotationDegrees);
            var o = new JsonObject
            {
                ["cell"] = i.CellRef, ["x"] = i.X, ["y"] = i.Y, ["rotation"] = angle, ["magnification"] = i.Mag, ["mirror"] = reflect,
            };
            if (i.Rows > 1 || i.Cols > 1)
                o["rep"] = new JsonObject { ["kind"] = "rectangular", ["columns"] = i.Cols, ["rows"] = i.Rows, ["spacing"] = Arr([i.PitchX, i.PitchY]) };
            refs.Add(o);
        }
        cells[s.Name] = new JsonObject { ["polygons"] = polys, ["paths"] = paths, ["labels"] = labels, ["refs"] = refs };
    }
    var root = new JsonObject
    {
        ["unit_m"] = reader.Units.UserUnitMeters, ["precision_m"] = reader.Units.DbUnitMeters, ["cells"] = cells,
        ["diagnostics"] = new JsonArray(reader.Diagnostics.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
    };
    Console.Out.Write(root.ToJsonString());
    return 0;
}

static int Write(string inJson, string outGds)
{
    var root = JsonNode.Parse(File.ReadAllText(inJson))!;
    double precision = root["precision_m"]?.GetValue<double>() ?? 1e-6 / root["grid_per_um"]!.GetValue<double>();
    var lost = new List<string>();
    var structures = new List<InterchangeStructure>();
    foreach (var (name, cellNode) in root["cells"]!.AsObject())
    {
        var cell = cellNode!;
        var shapes = new List<LayoutShape>();
        var instances = new List<LayoutInstance>();
        foreach (var p in Items(cell, "polygons"))
        {
            if (p["rep"] is not null) lost.Add($"{name}: a polygon's repetition (GDSII has none)");
            shapes.Add(new PolygonShape { Layer = Key(p), Xy = Longs(p["xy"]!) });
        }
        foreach (var p in Items(cell, "paths"))
        {
            string end = p["end"]!.GetValue<string>();
            long width = p["width"]!.GetValue<long>();
            var style = end switch { "round" => PathEndStyle.Round, "halfwidth" => PathEndStyle.Square, "extended" => PathEndStyle.Extended, _ => PathEndStyle.Flush };
            if (end == "extended" && p["ext"] is JsonArray ext && (ext[0]!.GetValue<long>() != width / 2 || ext[1]!.GetValue<long>() != width / 2))
                lost.Add($"{name}: a path's extensions {ext.ToJsonString()} (our model extends by width/2 only)");
            shapes.Add(new PathShape { Layer = Key(p), Xy = Longs(p["xy"]!), Width = width, End = style });
        }
        foreach (var l in Items(cell, "labels"))
        {
            int texttype = l["texttype"]?.GetValue<int>() ?? 0;
            if (texttype is not (0 or 1)) lost.Add($"{name}: label texttype {texttype} (our model holds port or not)");
            if (l["mirror"]?.GetValue<bool>() == true) lost.Add($"{name}: a label's mirror");
            if (Math.Abs((l["magnification"]?.GetValue<double>() ?? 1) - 1) > 1e-12) lost.Add($"{name}: a label's magnification");
            var label = new LabelShape
            {
                Layer = new LayerKey(l["layer"]!.GetValue<int>(), 0), X = l["x"]!.GetValue<long>(), Y = l["y"]!.GetValue<long>(),
                Text = l["text"]!.GetValue<string>(), Height = GdsiiReader.DefaultTextHeightDbu, IsPort = texttype == 1,
            };
            label.RotationDegrees = l["rotation"]?.GetValue<double>() ?? 0;
            shapes.Add(label);
        }
        foreach (var r in Items(cell, "refs"))
        {
            var (mirrorX, rot) = GdsiiTransformCodec.FromGdsii(r["mirror"]?.GetValue<bool>() ?? false, r["rotation"]?.GetValue<double>() ?? 0);
            var inst = new LayoutInstance
            {
                CellRef = r["cell"]!.GetValue<string>(), X = r["x"]!.GetValue<long>(), Y = r["y"]!.GetValue<long>(),
                MirrorX = mirrorX, Mag = r["magnification"]?.GetValue<double>() ?? 1,
            };
            inst.RotationDegrees = rot;
            if (r["rep"] is JsonObject rep)
            {
                if (rep["kind"]!.GetValue<string>() == "rectangular")
                {
                    inst.Cols = rep["columns"]!.GetValue<int>();
                    inst.Rows = rep["rows"]!.GetValue<int>();
                    inst.PitchX = rep["spacing"]![0]!.GetValue<long>();
                    inst.PitchY = rep["spacing"]![1]!.GetValue<long>();
                }
                else lost.Add($"{name}: a {rep["kind"]} repetition on a reference");
            }
            instances.Add(inst);
        }
        structures.Add(new InterchangeStructure(name, shapes, instances));
    }
    using (var fs = File.Create(outGds))
        GdsiiWriter.Write(fs, structures, new GdsiiUnits(1e-6, precision), null);
    Console.Out.Write(new JsonObject { ["lost"] = new JsonArray(lost.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) }.ToJsonString());
    return 0;
}

static IEnumerable<JsonNode> Items(JsonNode cell, string key) =>
    cell[key] is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];

static LayerKey Key(JsonNode n) => new(n["layer"]!.GetValue<int>(), n["datatype"]?.GetValue<int>() ?? 0);

static long[] Longs(JsonNode n) => n.AsArray().Select(v => v!.GetValue<long>()).ToArray();

static JsonArray Arr(IEnumerable<long> v) => new(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
