// brief-em3d-28 gate 7 — the .msh reader: node, tetrahedron and boundary counts equal Gmsh's own. MOVED here unchanged with
// the reader itself (brief-em3d-74 R-em3d74-1b: src/Render/Scene3D → src/Engine/Em3d), from Ui.Tests' Scene3DGateTests.

using System.Runtime.CompilerServices;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

public sealed class MshReaderTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static string? Find(string relativePath)
    {
        string p = Path.Combine(RepoRoot(), relativePath);
        return File.Exists(p) ? p : null;
    }

    private static string Testdata(params string[] parts) => Path.Combine([RepoRoot(), "testdata", .. parts]);

    /// <summary>The counts Gmsh itself printed reading the mesh back (count.geo's Printf lines, and its
    /// own "Info    : N elements" line).</summary>
    private static (int Nodes, int Triangles, int Tets, long Elements) GmshCounts(string log)
    {
        int Get(string key) => int.Parse(File.ReadLines(log).Single(l => l.StartsWith(key + " ", StringComparison.Ordinal))[(key.Length + 1)..]);
        string el = File.ReadLines(log).Single(l => l.StartsWith("Info", StringComparison.Ordinal) && l.EndsWith(" elements", StringComparison.Ordinal));
        long elements = long.Parse(el[(el.IndexOf(':') + 1)..^" elements".Length].Trim());
        return (Get("nodes"), Get("triangles"), Get("tetrahedra"), elements);
    }

    /// <summary>F0 case A's mesh is 15.6 MB and not committed: the gate SKIPS, saying so, when neither
    /// <c>CRF_F0_CASE_A_MSH</c> nor tools/Viewer3dSpike/data/case.msh is there.</summary>
    private sealed class F0CaseAMeshFactAttribute : FactAttribute
    {
        public const string RelativePath = "tools/Viewer3dSpike/data/case.msh";

        public static string? Path => Environment.GetEnvironmentVariable("CRF_F0_CASE_A_MSH") is { Length: > 0 } env && File.Exists(env)
            ? env : Find(RelativePath);

        public F0CaseAMeshFactAttribute()
        {
            if (Path is null)
                Skip = $"Missing fixture '{RelativePath}' (or CRF_F0_CASE_A_MSH) — tools/Viewer3dSpike/README §1 regenerates it";
        }
    }

    [Theory]
    [InlineData("small-binary.msh")]
    [InlineData("small-ascii.msh")]
    public void Gate7_TheReadersCounts_AreGmshsOwn(string file)
    {
        var mesh = MshReader.Read(Testdata("em3d", "viewer", "small-mesh", file));
        var (nodes, tris, tets, elements) = GmshCounts(Testdata("em3d", "viewer", "small-mesh", "gmsh-counts.log"));
        Assert.Equal((nodes, tris, tets, elements), (mesh.NodeCount, mesh.TriangleCount, mesh.TetCount, mesh.ElementCount));
        Assert.Equal(file.Contains("binary"), mesh.Binary);
        Assert.Equal(["air", "outer_pec", "substrate", "via", "via_wall"], mesh.PhysicalNames.Values.Order());
    }

    [F0CaseAMeshFact]
    public void Gate7_F0CaseA_GivesGmshsCounts_AndPalacesElementCount()
    {
        var mesh = MshReader.Read(F0CaseAMeshFactAttribute.Path!);
        var (nodes, tris, tets, elements) = GmshCounts(Testdata("em3d", "f0", "A-bondwire", "palace-round", "gmsh-counts.log"));
        Assert.Equal((nodes, tris, tets, elements), (mesh.NodeCount, mesh.TriangleCount, mesh.TetCount, mesh.ElementCount));
        Assert.Equal(146_769, mesh.TetCount);   // palace.log's element total for this run (brief 21's summary)
    }

    [Fact]
    public void Gate7_AMalformedFile_RefusesWithTheLineNumber()
    {
        string text = File.ReadAllText(Testdata("em3d", "viewer", "small-mesh", "small-ascii.msh"));
        int at = text.IndexOf("$Elements", StringComparison.Ordinal);
        int line = text[..at].Count(c => c == '\n') + 1;      // the $Elements line; its count follows
        string broken = text[..at] + "$Elements\n12x\n" + text[(text.IndexOf('\n', text.IndexOf('\n', at) + 1) + 1)..];
        using var s = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(broken));
        var e = Assert.Throws<InvalidDataException>(() => MshReader.Read(s, "broken.msh"));
        Assert.Contains($"broken.msh, line {line + 1}", e.Message);
        Assert.Contains("'12x'", e.Message);
    }
}
