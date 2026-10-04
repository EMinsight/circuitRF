// brief-em3d-111 — `convert x.c3d -o x.glb`: glTF export headlessly.
//
//    1  structure: every file below passes GlbFile.Validate
//    3  orientation and units: a 1 × 2 × 3 mm box (x, y, z) stands 3 mm tall in glTF's y after the root's turn, in metres
//   1f  the camera: the Look's Camera when the document saves one, and none otherwise
//    6  the field end to end: a Faces temperature plot as an unlit COLOR_0 mesh, the block it stands in for left out, each vertex's
//       colour the map at T there, and no edge wider than 1/16 of the range
//    7  deterministic: the same command twice gives the same bytes
//    8  refusal: a .glb source refuses with its sentence

using System.Numerics;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Export;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

// In process: the group the other in-process verb tests drive the verb in, whose RunHost and JsonRun this class shares.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class GltfConvertCliTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-glb-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 3. orientation and units, and 1f with no camera ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AOneByTwoByThreeMillimetreBox_IsThreeMillimetresTallInY_AndHasNoCameraWithoutTheLooks()
    {
        string c3d = Workspace(Box(1000, 2000, 3000));
        var glb = Convert(c3d, "box.glb");
        Assert.False(glb.Json.TryGetProperty("cameras", out _));

        // the root's rotation and translation as the file states them, applied in double (q v q*)
        var root = glb.Nodes[0];
        double[] q = [.. root.GetProperty("rotation").EnumerateArray().Select(e => e.GetDouble())];
        double[] shift = [.. root.GetProperty("translation").EnumerateArray().Select(e => e.GetDouble())];
        int node = glb.NodeNamed("box")!.Value;
        var prim = glb.Meshes[glb.Nodes[node].GetProperty("mesh").GetInt32()].GetProperty("primitives")[0];
        var world = glb.Vec3(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32()).Select(p => Turn(q, p, shift)).ToList();
        (double X, double Y, double Z) min = (world.Min(w => w.X), world.Min(w => w.Y), world.Min(w => w.Z)),
                                       max = (world.Max(w => w.X), world.Max(w => w.Y), world.Max(w => w.Z));
        Assert.Equal(1e-3, max.X - min.X, 1e-8);
        Assert.Equal(3e-3, max.Y - min.Y, 1e-8);         // z, stood up
        Assert.Equal(2e-3, max.Z - min.Z, 1e-8);         // y, now running toward the viewer's −z
        Assert.Equal(0, min.Y, 1e-8);                    // the box's floor at z = 0 is glTF's y = 0
    }

    /// <summary>v rotated by unit quaternion q (x, y, z, w), then moved by t: v + 2w(u × v) + 2u × (u × v) + t.</summary>
    private static (double X, double Y, double Z) Turn(double[] q, Vector3 p, double[] t)
    {
        double ux = q[0], uy = q[1], uz = q[2], w = q[3], vx = p.X, vy = p.Y, vz = p.Z;
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        double dx = uy * cz - uz * cy, dy = uz * cx - ux * cz, dz = ux * cy - uy * cx;
        return (vx + 2 * (w * cx + dx) + t[0], vy + 2 * (w * cy + dy) + t[1], vz + 2 * (w * cz + dz) + t[2]);
    }

    [Fact]
    public void Gate1f_TheLooksCamera_IsTheFilesCamera()
    {
        var look = new C3dLook
        {
            Camera = new C3dLookCamera { Direction = [1, -1, 1], Target = [500 * Um, 1000 * Um, 1500 * Um], Distance = 10_000 * Um, FovY = 30,
                                         Projection = C3dLookCamera.Perspective },
        };
        var glb = Convert(Workspace(Box(1000, 2000, 3000), d => d.Look = look), "cam.glb");
        var camera = glb.Json.GetProperty("cameras").EnumerateArray().Single();
        Assert.Equal("perspective", camera.GetProperty("type").GetString());
        Assert.Equal(30 * Math.PI / 180, camera.GetProperty("perspective").GetProperty("yfov").GetDouble(), 1e-6);
        Assert.NotNull(glb.NodeNamed("View"));
    }

    // ── 6. the field, end to end ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>RealisticRenderCliTests' fixture: a 20 µm gold block, T = 25 + x + 2z °C (µm), a Faces plot of its top mirrored across
    /// x = 0 — so the drawn values run 65 + |x| from 65 to 85 °C, and the colour at a vertex is Inferno at |x| / 20. A Faces plot paints
    /// a face and stands in for no object, so the block is written as the view draws it.</summary>
    [Fact]
    public void Gate6_AFacesPlot_IsAnUnlitVertexColouredMesh_InTheMapsColours()
    {
        string c3d = ThermalWorkspace();
        string glbPath = Out("field.glb");
        Assert.Equal(0, InProcess("convert", c3d, "-o", glbPath, "--gltf-field", "Top"));
        var glb = GlbFile.Validate(File.ReadAllBytes(glbPath));
        Assert.NotNull(glb.NodeNamed("block"));
        var field = glb.Meshes.Single(m => m.GetProperty("name").GetString() == "Top").GetProperty("primitives")[0];
        var material = glb.Materials[field.GetProperty("material").GetInt32()];
        Assert.True(material.GetProperty("extensions").TryGetProperty(GltfExport.Unlit, out _));
        Assert.Contains(GltfExport.Unlit, glb.Json.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()));

        var root = glb.Nodes[0].GetProperty("translation");
        double originX = root[0].GetDouble();                                  // the root's translation is the origin turned: x stays x
        var pos = glb.Vec3(field.GetProperty("attributes").GetProperty("POSITION").GetInt32());
        var col = glb.Vec3(field.GetProperty("attributes").GetProperty("COLOR_0").GetInt32());
        double T(int i) => Math.Abs(originX + pos[i].X) * 1e6 / 20;            // (T − 65) / (85 − 65)
        for (int i = 0; i < pos.Length; i++)
            Assert.True(Vector3.Distance(GltfExport.LinearColour(ColorMap3D.Inferno, T(i)), col[i]) < 0.015, $"vertex {i}");
        for (int t = 0; t < pos.Length; t += 3)
            for (int k = 0; k < 3; k++) Assert.True(Math.Abs(T(t + k) - T(t + (k + 1) % 3)) <= GltfExport.FieldSpan + 1e-4);
    }

    // ── 7. deterministic ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_TheSameCommandTwice_GivesTheSameBytes()
    {
        string c3d = Workspace(Box(1000, 2000, 3000));
        string a = Out("a.glb"), b = Out("b.glb");
        Assert.Equal(0, InProcess("convert", c3d, "-o", a, "--gltf-assembly"));
        Assert.Equal(0, InProcess("convert", c3d, "-o", b, "--gltf-assembly"));
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    // ── 8. refusal ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_AGlbSource_RefusesWithItsSentence()
    {
        string glb = Out("model.glb");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(glb, [0x67, 0x6C, 0x54, 0x46]);
        var (exit, _, err) = Process("convert", glb, "-o", Out("y.c3d"));
        Assert.Equal(1, exit);
        Assert.Contains("circuitRF exports glTF; it does not import it.", err, StringComparison.Ordinal);
        Assert.False(File.Exists(Out("y.c3d")));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(long xUm, long yUm, long zUm)
        => new() { Name = "box", Material = "Copper", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(xUm * Um, yUm * Um, zUm * Um) };

    private string Out(string name) => Path.Combine(_root, name);

    private GlbFile Convert(string c3d, string name)
    {
        string path = Out(name);
        Assert.Equal(0, InProcess("convert", c3d, "-o", path));
        return GlbFile.Validate(File.ReadAllBytes(path));
    }

    private string Workspace(C3dObject obj, Action<C3dDocument>? edit = null)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string c3d = Path.Combine(dir, "cell.c3d");
        var doc = new C3dDocument { Objects = [obj] };
        edit?.Invoke(doc);
        C3dPersistence.SaveToFile(c3d, doc);
        return c3d;
    }

    /// <summary>A 20 µm gold block with a thermal setup, a symmetry plane at x = 0, a Faces plot of its top, and the setup's finished
    /// run (T = 25 + x + 2z °C) — RealisticRenderCliTests' fixture.</summary>
    private string ThermalWorkspace()
    {
        string ws = Path.Combine(_root, "thermal");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string c3d = Path.Combine(dir, "cell.c3d");
        var setup = new EmSetup
        {
            Name = "Hot", Solver3D = Em3dSolver.None, Problem3D = Em3dProblemType.Thermal,
            Thermal = new CemThermal { Boundaries = [new CemThermalBoundary { Face = "block/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }] },
        };
        C3dPersistence.SaveToFile(c3d, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "block", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) }],
            SymmetryPlanes = [new C3dSymmetryPlane { Axis = C3dAxis.X, At = 0 }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
            FieldPlots =
            [
                new C3dFieldPlot
                {
                    Name = "Top", Setup = "Hot", Solution = new C3dFieldSolution { Point = 1 }, Quantity = C3dFieldPlot.TemperatureQuantity,
                    On = C3dFieldPlotOn.Faces, Faces = [new C3dFieldPlotFace { Face = "block/zmax" }],
                },
            ],
        });
        var doc = C3dPersistence.LoadFromFile(c3d);
        string run = ThermalRunService.RunDirectory(Path.Combine(ws, "results"), C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, c3d));
        var (mesh, _) = SyntheticThermal.Block(2, 10, 1, (_, _, _) => 0);
        var t = Enumerable.Range(0, mesh.NodeCount).Select(n => 25 + mesh.Nodes[3 * n] * 1e6 + 2 * mesh.Nodes[3 * n + 2] * 1e6).ToArray();
        ThermalFieldFiles.Write(run, mesh, [t], [1, 1]);
        File.WriteAllText(Path.Combine(run, GmshGeoWriter.GroupsFile),
            JsonSerializer.Serialize(new { Groups = new[] { new { Name = "block", Attribute = 1, Dimension = 3, Kind = "Conductor" } } }));
        C3dRunInputs.Take(doc, c3d, C3dElaborator.ElaborateOnce(doc, c3d, null).FilesRead).KeepIn(run);
        return c3d;
    }

    private static int InProcess(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    /// <summary>The verb's exit and its output — a process, since Console is one writer for every class running in parallel.</summary>
    private static (int Exit, string StdOut, string StdErr) Process(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = typeof(GltfConvertCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
