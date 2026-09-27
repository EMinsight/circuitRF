using System.Diagnostics;
using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>
/// brief-em3d-63 against the REAL worker (gates 2, 5, 6, 7 and R-em3d63-5b). These need a built worker, which a fresh
/// clone does not have, so each skips naming what to build (<see cref="KernelFactAttribute"/>) — the ordinary state of CI.
/// </summary>
public sealed class GeometryKernelWorkerTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "crf-kernel-worker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch (IOException) { }
    }

    private static GeometryKernelTree Box(string name) =>
        GeometryKernelTree.From(new C3dBox { Name = name, Size = new C3dPoint3(100_000, 200_000, 50_000) }, 1000);

    /// <summary>Gate 2: a real worker answers the handshake with the recipe's protocol and OCCT, and says so without the
    /// protocol too (<c>--version</c>, what the About box of brief 62 and CliSmoke read).</summary>
    [KernelFact]
    public void TheWorker_AnswersWithTheRecipesProtocolAndOcct()
    {
        using var kernel = KernelForTests.New();
        var cap = kernel.Probe();
        Assert.True(cap.Available, cap.Reason);
        Assert.Equal(GeometryKernelRecipe.OcctVersion, cap.OcctVersion);
        Assert.Equal(AppVersion.Display, cap.WorkerVersion);

        using var p = Process.Start(new ProcessStartInfo(cap.WorkerPath!, "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        string stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(0, p.ExitCode);
        Assert.Contains($"occt {GeometryKernelRecipe.OcctVersion}\n", stdout, StringComparison.Ordinal);
    }

    /// <summary>brief 62's skeleton, over the framed protocol: an unknown request is refused and the worker keeps running;
    /// the self-test is the whole kernel in one request; shutdown exits 0.</summary>
    [KernelFact]
    public void AnUnknownRequest_IsRefused_AndTheWorkerCarriesOn()
    {
        using var w = new GeometryKernelProcessWorker(KernelForTests.Capability.WorkerPath!);
        var unknown = w.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "no-such-operation" }));
        Assert.False(unknown.Ok);
        Assert.Equal("request.unknown", unknown.Text("code"));
        Assert.Contains("no-such-operation", unknown.Text("detail"), StringComparison.Ordinal);

        var selftest = w.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "selftest" }));
        Assert.True(selftest.Ok, selftest.Json.ToJsonString());
        Assert.True(selftest.Json["step_roundtrip"]!.GetValue<bool>());

        Assert.True(w.Exchange(new GeometryKernelMessage(new JsonObject { ["op"] = "shutdown" })).Ok);
        for (int i = 0; i < 100 && w.ExitCode is null; i++) Thread.Sleep(20);
        Assert.Equal(0, w.ExitCode);
    }

    public static TheoryData<string> Primitives => ["box", "cylinder", "prism", "polyhedron"];

    private static C3dObject Primitive(string kind) => kind switch
    {
        // Mirrored and turned, so the names are proven to follow the object's OWN frame through a placement.
        "box" => new C3dBox
        {
            Name = "b", Size = new C3dPoint3(100_000, 200_000, 50_000),
            Placement = new C3dPlacement { MirrorX = true, Rotate = [new C3dRotation { Axis = C3dAxis.Y, Deg = 30 }] },
        },
        "cylinder" => new C3dCylinder { Name = "c", Axis = C3dAxis.X, Length = -80_000, Radius = 20_000 },
        "prism" => new C3dPrism
        {
            Name = "p", Plane = C3dPlane.YZ, Height = 30_000, Shear = new C3dPoint2(5_000, 0),
            Outline = [new(0, 0), new(100_000, 0), new(100_000, 100_000), new(0, 100_000)],
            Holes = [[new(20_000, 20_000), new(40_000, 20_000), new(30_000, 40_000)]],
        },
        _ => new C3dPolyhedron
        {
            Name = "w",
            Vertices = [new(0, 0, 0), new(10_000, 0, 0), new(10_000, 10_000, 0), new(0, 10_000, 0), new(0, 0, 10_000), new(0, 10_000, 10_000)],
            Faces =
            [
                new() { Name = "base", Outer = [0, 3, 2, 1] }, new() { Name = "back", Outer = [0, 4, 5, 3] },
                new() { Name = "slope", Outer = [1, 2, 5, 4] }, new() { Name = "left", Outer = [0, 1, 4] },
                new() { Name = "right", Outer = [3, 5, 2] },
            ],
        },
    };

    /// <summary>R-em3d63-5b: every primitive's names go in and come back — each face named, exactly the document's names,
    /// and the tessellation's face indices point into that table.</summary>
    [KernelTheory]
    [MemberData(nameof(Primitives))]
    public void EachPrimitive_ComesBackWithTheDocumentsFaceNames(string kind)
    {
        using var kernel = KernelForTests.New();
        var obj = Primitive(kind);
        var tree = GeometryKernelTree.From(obj, 1000);

        var build = kernel.Build(tree);
        Assert.True(build.Valid);
        var faces = kernel.Faces(tree);
        Assert.Equal(obj.FaceNames().Order(StringComparer.Ordinal), faces.Select(f => f.Name).Order(StringComparer.Ordinal));

        var mesh = kernel.Tessellate(tree, 1);
        Assert.All(mesh.TriangleFace, f => Assert.InRange((int)f, 0, faces.Count - 1));
        Assert.Equal(faces.Count, mesh.TriangleFace.Distinct().Count());

        var edges = kernel.Edges(tree, 1);
        Assert.All(edges, e => Assert.Equal(e.Name.Split('|')[..2], new[] { e.FaceA, e.FaceB }));
        if (kind == "cylinder")
        {
            Assert.Equal(20, faces.Single(f => f.Name == "side").MinRadius, 9);
            Assert.Equal(["bottom|side", "side|top"], edges.Select(e => e.Name));   // the seam is not a feature edge
        }
    }

    /// <summary>Gate 5: the same tree twice sends one build; after the memory cache is cleared the disk serves it.</summary>
    [KernelFact]
    public void TheSameTreeTwice_SendsOneBuild_AndTheDiskServesItAfterAMemoryClear()
    {
        using var kernel = KernelForTests.New(_cache);
        var first = kernel.Build(Box("lid"));
        long sent = kernel.RequestsSent;
        kernel.Build(Box("lid"));
        Assert.Equal(sent, kernel.RequestsSent);

        kernel.ClearMemoryCache();
        var fromDisk = kernel.Build(Box("lid"));
        Assert.Equal(1L, kernel.DiskHits);
        Assert.Equal(sent, kernel.RequestsSent);
        Assert.Equal(first.BrepHash, fromDisk.BrepHash);
    }

    private static GeometryKernelTree TestNode(string name, string node) =>
        GeometryKernelTree.FromJson(name, "{\"tree\":1,\"root\":{\"kind\":" + node + ",\"name\":\"" + name + "\"}}");

    /// <summary>Gate 6: a worker that dies mid-build refuses that build with R-em3d63-7a's sentence, the next request runs
    /// on a restarted worker, and the same tree is refused again from the failed cache with no call.</summary>
    [KernelFact]
    public void AWorkerThatDiesMidBuild_CostsThatBuildOnly()
    {
        using var kernel = KernelForTests.New(testOps: true);
        var crash = TestNode("doomed", "\"crash\"");

        var e = Assert.Throws<GeometryKernelException>(() => kernel.Build(crash));
        Assert.Equal("The geometry kernel stopped while building 'doomed' (exit code 70). Nothing was changed; it restarts for the next operation.", e.Message);

        Assert.True(kernel.Build(Box("lid")).Valid);
        Assert.Equal(1L, kernel.WorkerRestarts);

        long sent = kernel.RequestsSent;
        Assert.Throws<GeometryKernelException>(() => kernel.Build(crash));
        Assert.Equal(sent, kernel.RequestsSent);
    }

    /// <summary>Gate 7: cancelling a long build kills the worker — the only interruption that always works — and throws
    /// <see cref="OperationCanceledException"/> well inside the deadline; the next request restarts the worker.</summary>
    [KernelFact]
    public void CancellingALongBuild_KillsTheWorker_AndTheNextRequestRestartsIt()
    {
        using var kernel = KernelForTests.New(testOps: true);
        kernel.Probe();
        var slow = TestNode("slow", """
            "sleep","seconds":60,"then":{"kind":"box","name":"slow","faces":["xmin","xmax","ymin","ymax","zmin","zmax"],"min":[0,0,0],"size":[1,1,1]}
            """.Trim());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => kernel.Build(slow, control: new RunControl { Token = cts.Token }));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "cancel waited for the build");

        Assert.True(kernel.Build(Box("lid")).Valid);
        Assert.Equal(1L, kernel.WorkerRestarts);
    }

    /// <summary>Convert this: a STEP export of two shapes reads back as two parts with their names and colour.</summary>
    [KernelFact]
    public void AStepExport_ReadsBack_WithNamesAndColours()
    {
        using var kernel = KernelForTests.New();
        byte[] step = kernel.Export([new(Box("lid"), "lid", [1, 0, 0]), new(Box("base"))], "step", "mm");
        var import = kernel.ImportStep(step);

        Assert.Equal(["lid", "base"], import.Parts.Select(p => p.Name));
        Assert.Equal([1.0, 0, 0], import.Parts[0].Colour!);
        Assert.All(import.Parts, p => Assert.True(p.Valid));
        Assert.Empty(import.Healing);
    }
}
