using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Interchange.Gdstk;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Em3d;

namespace CircuitRF.Ui.Tests.Interchange;

/// <summary>
/// brief-oasis-gdstk.md §9 (R-oas-4): OASIS import and export through gdstk. One test per claim, each under
/// §8b's equality (<see cref="InterchangeEquality"/>), each skipping with a reason when this build has no
/// worker. Reads and writes go through <see cref="StreamInterchange"/>, the calls File ▸ Import / Export
/// make.
/// </summary>
public sealed class GdstkOasisTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gdstk-oasis-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Cases => GdstkCorpus.Names();

    /// <summary>1 — a layout written as OASIS imports as the layout it was, over the §8a corpus, in what
    /// OASIS can hold (<see cref="WhatOasisHolds"/>: G0's conditions 1 and 2, reported by test 7).</summary>
    [GdstkTheory, MemberData(nameof(Cases))]
    public void Layout_WrittenAsOasis_ImportsAsTheLayout(string name)
    {
        var c = GdstkCorpus.Named(name);
        string file = Path.Combine(_dir, $"{name}.oas");
        StreamInterchange.Write(StreamRoute.OasisGdstk, file, GdstkCorpus.Plan(c));

        var read = GdstkImport.Read(file, GdstkFormat.Oasis);

        InterchangeEquality.AssertEqual(WhatOasisHolds(c.Structures), c.DbuPerMicron, read.Structures, read.SourceDbuPerMicron);
    }

    /// <summary>2 — GDSII (circuitRF's writer) → <c>convert</c> → OASIS → <c>convert</c> → GDSII (circuitRF's
    /// writer) gives the first file's content. The verb runs as a process, as a user runs it.</summary>
    [GdstkTheory]
    [InlineData("hierarchy"), InlineData("aref-3x2"), InlineData("sref-transform"), InlineData("dbu-0p25nm")]
    public void Gdsii_ThroughOasis_BackToGdsii_KeepsTheContent(string name)
    {
        var c = GdstkCorpus.Named(name);
        string first = Path.Combine(_dir, "first.gds");
        StreamInterchange.Write(StreamRoute.Gdsii, first, GdstkCorpus.Plan(c));

        string oas = Convert(first, "oas");
        string last = Convert(oas, "gds");

        var (a, aDbu) = ReadNative(first);
        var (b, bDbu) = ReadNative(last);
        InterchangeEquality.AssertEqual(a, aDbu, b, bDbu);
    }

    public static TheoryData<string> Q5Fixtures => new()
    {
        "q5-rectangles", "q5-polygon7", "q5-trapezoids", "q5-paths", "q5-labels", "q5-placements",
        "q5-ref-repetitions", "q5-shape-repetition", "q5-hierarchy", "q5-hierarchy-deflate0",
        "q5-hierarchy-deflate9", "q5-hierarchy-checksum32", "q5-hierarchy-stdprops",
    };

    /// <summary>3 — each committed gdstk-written OASIS fixture imports to its stated content: the
    /// <c>reads_back_as</c> its <c>.json</c> records, in circuitRF's model (<see cref="Stated"/>).</summary>
    [GdstkTheory, MemberData(nameof(Q5Fixtures))]
    public void CommittedOasisFixture_ImportsToItsStatedContent(string fixture)
    {
        string dir = FixtureDir("oasis-gdstk");
        var (expected, grid) = Stated(Path.Combine(dir, $"{fixture}.json"));

        var read = GdstkImport.Read(Path.Combine(dir, $"{fixture}.oas"), GdstkFormat.Oasis);

        InterchangeEquality.AssertEqual(expected, grid, read.Structures, read.SourceDbuPerMicron);
    }

    /// <summary>4 — a shape repetition that would expand past the OASIS limit (R-oas-4c) is refused, naming
    /// the count and the limit, and nothing is created. The file is 1001 × 1000 copies of one rectangle,
    /// written by gdstk at test time: a few hundred bytes, one past the limit by a thousand.</summary>
    [GdstkFact]
    public void Repetition_OverTheLimit_IsRefused_AndCreatesNothing()
    {
        string file = Path.Combine(_dir, "big-rep.oas");
        using (var w = GdstkWorker.Start())
        {
            var session = new GdstkSession(w);
            int h = session.BeginWrite(GdstkFormat.Oasis, 1e-6, 1e-9, OasisWriteOptions.Default.ToJson());
            var rep = new GdstkRepetition("rectangular", 1001, 1000, V1X: 20, V2Y: 20);
            session.AddCell(h, new GdstkCell("TOP", [new GdstkPolygon(1, 0, [0, 0, 10, 0, 10, 10, 0, 10], rep)], [], [], [], GdstkCellNotes.None), file);
            session.FinishWrite(h, file);
        }
        string into = Directory.CreateDirectory(Path.Combine(_dir, "into")).FullName;

        var ex = Assert.Throws<GdstkException>(() => StreamInterchange.Import(StreamRoute.OasisGdstk, file, into, null, 1000, true));

        Assert.Equal("import.expansion-limit", ex.Code);
        Assert.Contains("1001000", ex.Message);
        Assert.Contains(GdstkMapping.MaxExpandedOasis.ToString(), ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    /// <summary>5 — a truncated OASIS file is refused by the worker's guard, and nothing is created.</summary>
    [GdstkFact]
    public void TruncatedOasis_IsRefused_AndCreatesNothing()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir("oasis-gdstk"), "q5-hierarchy.oas"));
        string file = Path.Combine(_dir, "truncated.oas");
        File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);
        string into = Directory.CreateDirectory(Path.Combine(_dir, "into")).FullName;

        var ex = Assert.Throws<GdstkException>(() => StreamInterchange.Import(StreamRoute.OasisGdstk, file, into, null, 1000, true));

        Assert.Equal("read.truncated", ex.Code);
        Assert.Contains(file, ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(into));
    }

    /// <summary>6 — OASIS LAYERNAME names land on the technology layers of the same name, before numbers:
    /// the file names layer 10 "M1", and the technology's M1 is layer 3 while its layer 10 is called
    /// something else.</summary>
    [GdstkFact]
    public void LayerNames_LandOnTheTechnologyLayerOfThatName()
    {
        var tech = new Technology
        {
            Name = "T",
            Layers =
            {
                new LayerDef { Key = new LayerKey(3, 0), Name = "M1" },
                new LayerDef { Key = new LayerKey(10, 0), Name = "Poly" },
            },
        };
        string file = Path.Combine(FixtureDir("oasis-hand"), "hand-layernames.oas");

        var result = StreamInterchange.Import(StreamRoute.OasisGdstk, file, _dir, tech, 1000, true);

        var view = LayoutPersistence.LoadFromFile(Path.Combine(
            CellFolder.SubFolderPath(Assert.Single(result.CreatedCellDirs), ViewType.Layout), "ln.clay"));
        Assert.Equal(new LayerKey(3, 0), Assert.Single(view.Shapes).Layer);
    }

    /// <summary>7 — G0's write-side conditions 1 and 2 are messages stating their counts, and they are the
    /// counts the export dialog showed (<see cref="OasisLosses.Of(GdsiiExport.ExportPlan)"/>).</summary>
    [GdstkFact]
    public void OasisWrite_ReportsWhatOasisCannotHold()
    {
        var L = new LayerKey(1, 0);
        var c = new GdstkCorpus.Case("losses", 1000,
        [
            new InterchangeStructure("TOP",
            [
                new PathShape { Layer = L, Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Round },
                new PathShape { Layer = L, Xy = [0, 500, 1000, 500], Width = 3, End = PathEndStyle.Flush },
                new LabelShape { Layer = L, X = 0, Y = 0, Text = "A", Height = 1000, RotationDegrees = 90 },
                new LabelShape { Layer = L, X = 0, Y = 0, Text = "B", Height = 2000 },
            ], []),
        ]);
        var plan = GdstkCorpus.Plan(c);

        var summary = StreamInterchange.Write(StreamRoute.OasisGdstk, Path.Combine(_dir, "losses.oas"), plan);

        var losses = OasisLosses.Of(plan);
        Assert.Equal(new OasisLosses(1, 1, 2), losses);
        Assert.All(losses.Messages(), m => Assert.Contains(m, summary.Diagnostics));
    }

    /// <summary>8 — G0's import-side conditions 3 and 4 are messages stating their counts: a CIRCLE's
    /// off-grid vertices are rounded and counted, and XNAME/XELEMENT/XGEOMETRY and properties are
    /// counted in ONE line rather than one line per record.</summary>
    [GdstkFact]
    public void OasisRead_ReportsWhatItRoundedAndDidNotImport()
    {
        string dir = FixtureDir("oasis-hand");

        var circle = GdstkImport.Read(Path.Combine(dir, "hand-circle.oas"), GdstkFormat.Oasis);
        var xrecords = GdstkImport.Read(Path.Combine(dir, "hand-xrecords.oas"), GdstkFormat.Oasis);

        Assert.Contains(circle.Diagnostics, d => d.Contains("off the file's grid and rounded") && d.Contains("CIRCLE"));
        Assert.DoesNotContain(xrecords.Diagnostics, d => d.Contains("Record type"));
        var line = Assert.Single(xrecords.Diagnostics, d => d.Contains("not imported"));
        Assert.Contains("XNAME", line);
        Assert.Contains("XELEMENT", line);
        Assert.Contains("XGEOMETRY", line);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The corpus case as OASIS holds it (G0's Q5): a round path end is written flush, an odd
    /// width one unit wider, a label upright at the default height, and an extended end of width/2 — the
    /// same shape as a half-width end — comes back as half-width.</summary>
    private static List<InterchangeStructure> WhatOasisHolds(IReadOnlyList<InterchangeStructure> structures) =>
        structures.Select(s => new InterchangeStructure(s.Name, s.Shapes.Select(shape => shape switch
        {
            PathShape p => new PathShape
            {
                Layer = p.Layer, Xy = p.Xy, Width = p.Width + (p.Width % 2),
                End = p.End switch { PathEndStyle.Round => PathEndStyle.Flush, PathEndStyle.Extended => PathEndStyle.Square, var e => e },
            },
            LabelShape l => new LabelShape { Layer = l.Layer, X = l.X, Y = l.Y, Text = l.Text, Height = 1000 },
            var other => other,
        }).ToList(), s.Instances)).ToList();

    /// <summary>
    /// A fixture's <c>reads_back_as</c> (the corpus README's canonical form) in circuitRF's model, by §6c's
    /// mapping table: a polygon is a polygon; a path's end is its PATHTYPE's; a label's height is 1000 ×
    /// its magnification and its mirror is dropped; a reference with a rectangular repetition, or a
    /// regular one along the axes, is an array, and any other repetition — and any repetition on a
    /// shape — is one element per copy, the copies being gdstk's own (an explicit list does not include
    /// the origin, which is the first copy).
    /// </summary>
    private static (List<InterchangeStructure> Structures, double Grid) Stated(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var back = doc.RootElement.GetProperty("reads_back_as");
        double grid = Math.Round(1e-6 / back.GetProperty("precision_m").GetDouble());

        var result = new List<InterchangeStructure>();
        foreach (var cell in back.GetProperty("cells").EnumerateObject())
        {
            var shapes = new List<LayoutShape>();
            var instances = new List<LayoutInstance>();
            foreach (var p in cell.Value.GetProperty("polygons").EnumerateArray())
            {
                long[] xy = Longs(p.GetProperty("xy"));
                foreach (var (dx, dy) in Copies(p))
                    shapes.Add(new PolygonShape { Layer = Layer(p, "datatype"), Xy = Shift(xy, dx, dy) });
            }
            foreach (var p in cell.Value.GetProperty("paths").EnumerateArray())
            {
                long[] xy = Longs(p.GetProperty("xy"));
                var end = p.GetProperty("end").GetString() switch
                {
                    "round" => PathEndStyle.Round, "halfwidth" => PathEndStyle.Square, "extended" => PathEndStyle.Extended, _ => PathEndStyle.Flush,
                };
                foreach (var (dx, dy) in Copies(p))
                    shapes.Add(new PathShape { Layer = Layer(p, "datatype"), Xy = Shift(xy, dx, dy), Width = p.GetProperty("width").GetInt64(), End = end });
            }
            foreach (var l in cell.Value.GetProperty("labels").EnumerateArray())
                foreach (var (dx, dy) in Copies(l))
                    shapes.Add(new LabelShape
                    {
                        Layer = Layer(l, "texttype"), Text = l.GetProperty("text").GetString()!,
                        X = l.GetProperty("x").GetInt64() + dx, Y = l.GetProperty("y").GetInt64() + dy,
                        Height = (long)Math.Round(1000 * l.GetProperty("magnification").GetDouble()),
                        RotationDegrees = l.GetProperty("rotation").GetDouble(),
                    });
            foreach (var r in cell.Value.GetProperty("refs").EnumerateArray())
                instances.AddRange(Placements(r));
            result.Add(new InterchangeStructure(cell.Name, shapes, instances));
        }
        return (result, grid);
    }

    private static IEnumerable<LayoutInstance> Placements(JsonElement r)
    {
        var (mirrorX, deg) = GdsiiTransformCodec.FromGdsii(r.GetProperty("mirror").GetBoolean(), Math.Round(r.GetProperty("rotation").GetDouble(), 9));
        long x = r.GetProperty("x").GetInt64(), y = r.GetProperty("y").GetInt64();
        LayoutInstance At(long px, long py, int rows = 1, int cols = 1, long pitchX = 0, long pitchY = 0) => new()
        {
            CellRef = r.GetProperty("cell").GetString()!, X = px, Y = py, RotationDegrees = deg, MirrorX = mirrorX,
            Mag = r.GetProperty("magnification").GetDouble(), Rows = rows, Cols = cols, PitchX = pitchX, PitchY = pitchY,
        };

        if (r.GetProperty("rep") is { ValueKind: JsonValueKind.Object } rep)
        {
            string kind = rep.GetProperty("kind").GetString()!;
            if (kind == "rectangular")
            {
                int cols = rep.GetProperty("columns").GetInt32(), rows = rep.GetProperty("rows").GetInt32();
                long[] s = Longs(rep.GetProperty("spacing"));
                return [At(x, y, rows, cols, cols == 1 ? 0 : s[0], rows == 1 ? 0 : s[1])];
            }
            if (kind == "regular")
            {
                int cols = rep.GetProperty("columns").GetInt32(), rows = rep.GetProperty("rows").GetInt32();
                long[] v1 = Longs(rep.GetProperty("v1")), v2 = Longs(rep.GetProperty("v2"));
                if (v1[1] == 0 && v2[0] == 0) return [At(x, y, rows, cols, v1[0], v2[1])];
                if (v1[0] == 0 && v2[1] == 0) return [At(x, y, cols, rows, v2[0], v1[1])];
            }
        }
        return Copies(r).Select(o => At(x + o.X, y + o.Y)).ToList();
    }

    /// <summary>Every copy's displacement, as gdstk's <c>Repetition::get_offsets</c> lists them.</summary>
    private static List<(long X, long Y)> Copies(JsonElement element)
    {
        if (element.GetProperty("rep") is not { ValueKind: JsonValueKind.Object } rep) return [(0, 0)];
        var copies = new List<(long, long)>();
        switch (rep.GetProperty("kind").GetString())
        {
            case "rectangular":
            {
                long[] s = Longs(rep.GetProperty("spacing"));
                for (int i = 0; i < rep.GetProperty("columns").GetInt32(); i++)
                    for (int j = 0; j < rep.GetProperty("rows").GetInt32(); j++) copies.Add((i * s[0], j * s[1]));
                break;
            }
            case "regular":
            {
                long[] v1 = Longs(rep.GetProperty("v1")), v2 = Longs(rep.GetProperty("v2"));
                for (int i = 0; i < rep.GetProperty("columns").GetInt32(); i++)
                    for (int j = 0; j < rep.GetProperty("rows").GetInt32(); j++) copies.Add((i * v1[0] + j * v2[0], i * v1[1] + j * v2[1]));
                break;
            }
            case "explicit":
            {
                long[] o = Longs(rep.GetProperty("offsets"));
                copies.Add((0, 0));
                for (int k = 0; k < o.Length; k += 2) copies.Add((o[k], o[k + 1]));
                break;
            }
            case var axis:
                copies.Add((0, 0));
                foreach (long v in Longs(rep.GetProperty("coords"))) copies.Add(axis == "explicit_x" ? (v, 0) : (0, v));
                break;
        }
        return copies;
    }

    private static LayerKey Layer(JsonElement e, string type) => new(e.GetProperty("layer").GetInt32(), e.GetProperty(type).GetInt32());

    private static long[] Longs(JsonElement array) => array.EnumerateArray().Select(v => (long)Math.Round(v.GetDouble())).ToArray();

    private static long[] Shift(long[] xy, long dx, long dy) =>
        xy.Select((v, k) => v + (k % 2 == 0 ? dx : dy)).ToArray();

    /// <summary><c>circuitrf convert file -o …</c>, formats from the extensions; the written path.</summary>
    private string Convert(string file, string extension)
    {
        string output = Path.Combine(_dir, $"{Path.GetFileNameWithoutExtension(file)}-to.{extension}");
        var (code, _, stderr) = CliProcess.Run(_dir, [], "convert", file, "-o", output);
        Assert.True(code == 0, $"convert {Path.GetFileName(file)} -> .{extension} failed:\n{stderr}");
        return output;
    }

    private static (List<InterchangeStructure> Structures, double DbuPerMicron) ReadNative(string file)
    {
        using var f = File.OpenRead(file);
        var reader = GdsiiReader.Open(f);
        var structures = reader.ReadStructures().ToList();
        return (structures, reader.Units.SourceDbuPerMicron);
    }

    private static string FixtureDir(string sub)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "testdata", "interchange", "gdstk", sub);
    }
}
