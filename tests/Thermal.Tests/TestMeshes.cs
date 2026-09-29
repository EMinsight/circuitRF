// Meshes for the solver gates: a structured box (no mesher needed), and Gmsh's own meshes of brief 72's cases when a Gmsh
// is installed (the gate skips, saying so, when none is found).

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Thermal.Tests;

internal static class TestMeshes
{
    /// <summary>Surface tags of <see cref="Box"/>'s six faces.</summary>
    public const int XMin = 1, XMax = 2, YMin = 3, YMax = 4, ZMin = 5, ZMax = 6;

    /// <summary>
    /// A box on the grid <paramref name="xs"/> × <paramref name="ys"/> × <paramref name="zs"/> (metres), each cell cut into six
    /// tetrahedra along its main diagonal (Kuhn's subdivision, conforming across cells), its six faces tagged
    /// <see cref="XMin"/> … <see cref="ZMax"/>; first order, to be promoted with <see cref="ThermalMesh.ToSecondOrder"/>.
    /// </summary>
    public static ThermalMesh Box(double[] xs, double[] ys, double[] zs, Func<double, double, double, int>? region = null)
    {
        int nx = xs.Length, ny = ys.Length, nz = zs.Length;
        int Id(int i, int j, int k) => (k * ny + j) * nx + i;
        var nodes = new double[3 * nx * ny * nz];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int v = Id(i, j, k);
                    nodes[3 * v] = xs[i]; nodes[3 * v + 1] = ys[j]; nodes[3 * v + 2] = zs[k];
                }
        var tets = new List<int>();
        var regions = new List<int>();
        int[][] perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        for (int k = 0; k + 1 < nz; k++)
            for (int j = 0; j + 1 < ny; j++)
                for (int i = 0; i + 1 < nx; i++)
                {
                    int r = region?.Invoke((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2, (zs[k] + zs[k + 1]) / 2) ?? 0;
                    foreach (var p in perms)
                    {
                        int[] at = [i, j, k];
                        tets.Add(Id(at[0], at[1], at[2]));
                        foreach (int axis in p)
                        {
                            at[axis]++;
                            tets.Add(Id(at[0], at[1], at[2]));
                        }
                        regions.Add(r);
                    }
                }
        var tris = new List<int>();
        var tags = new List<int>();
        // a face's two triangles share the diagonal from its low corner to its high one, which is a tetrahedron edge
        void Face(int a, int b, int c, int d, int tag) { tris.AddRange([a, b, d, a, c, d]); tags.AddRange([tag, tag]); }
        for (int k = 0; k + 1 < nz; k++)
            for (int j = 0; j + 1 < ny; j++)
            {
                Face(Id(0, j, k), Id(0, j + 1, k), Id(0, j, k + 1), Id(0, j + 1, k + 1), XMin);
                Face(Id(nx - 1, j, k), Id(nx - 1, j + 1, k), Id(nx - 1, j, k + 1), Id(nx - 1, j + 1, k + 1), XMax);
            }
        for (int k = 0; k + 1 < nz; k++)
            for (int i = 0; i + 1 < nx; i++)
            {
                Face(Id(i, 0, k), Id(i + 1, 0, k), Id(i, 0, k + 1), Id(i + 1, 0, k + 1), YMin);
                Face(Id(i, ny - 1, k), Id(i + 1, ny - 1, k), Id(i, ny - 1, k + 1), Id(i + 1, ny - 1, k + 1), YMax);
            }
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                Face(Id(i, j, 0), Id(i + 1, j, 0), Id(i, j + 1, 0), Id(i + 1, j + 1, 0), ZMin);
                Face(Id(i, j, nz - 1), Id(i + 1, j, nz - 1), Id(i, j + 1, nz - 1), Id(i + 1, j + 1, nz - 1), ZMax);
            }
        return new ThermalMesh(nodes, 1, [.. tets], [.. regions], [.. tris], [.. tags]);
    }

    public static double[] Linspace(double a, double b, int cells) => [.. Enumerable.Range(0, cells + 1).Select(i => a + (b - a) * i / cells)];

    public static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    public static string Testdata(params string[] parts) => Path.Combine([RepoRoot(), "testdata", "thermal", .. parts]);

    /// <summary>A Gmsh on this machine: CIRCUITRF_GMSH, else on PATH, else the usual install places; null when none.</summary>
    public static readonly Lazy<string?> Gmsh = new(() =>
    {
        if (Environment.GetEnvironmentVariable("CIRCUITRF_GMSH") is { Length: > 0 } env && File.Exists(env)) return env;
        string exe = OperatingSystem.IsWindows() ? "gmsh.exe" : "gmsh";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);
        return dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    });

    /// <summary>Meshes <paramref name="geo"/> (micrometres) with Gmsh at order 2 (or <paramref name="order"/>), one thread, MSH 2.2,
    /// and reads it: volume physical groups become regions in the order <paramref name="volumes"/> names them, surface groups keep
    /// their tags.</summary>
    public static (ThermalMesh Mesh, MshMesh Raw) Mesh(string geo, IReadOnlyList<string> volumes, int order = 2)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-thermal-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "m.geo"), geo);
            var psi = new ProcessStartInfo(Gmsh.Value!) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "m.geo", "-3", "-order", order.ToString(CultureInfo.InvariantCulture), "-nt", "1", "-format", "msh22", "-o", "m.msh" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("gmsh failed: " + se.Result + so.Result);
            var raw = MshReader.Read(Path.Combine(dir, "m.msh"));
            Assert.Equal(order, raw.Order);
            var tagOf = volumes.Select(v => raw.PhysicalNames.Single(kv => kv.Value == v).Key).ToList();
            var region = raw.TetPhysical.Select(t => tagOf.IndexOf(t)).ToArray();
            Assert.DoesNotContain(-1, region);
            var nodes = raw.Nodes.Select(x => x * 1e-6).ToArray();
            var mesh = new ThermalMesh(nodes, order, order == 2 ? raw.TetsHigh : raw.Tets, region, order == 2 ? raw.TrianglesHigh : raw.Triangles, raw.TrianglePhysical).Compact(out _);
            return (mesh, raw);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    public static int Tag(MshMesh raw, string name) => raw.PhysicalNames.Single(kv => kv.Value == name).Key;
}

/// <summary>Skips, saying so, unless a Gmsh is found.</summary>
public sealed class GmshFactAttribute : FactAttribute
{
    public GmshFactAttribute()
    {
        if (TestMeshes.Gmsh.Value is null) Skip = "needs Gmsh (on PATH, or CIRCUITRF_GMSH)";
    }
}

/// <summary>brief-em3d-77 — meshes put side by side, and a face's triangles retagged.</summary>
internal static class MeshEdits
{
    /// <summary><paramref name="a"/> and <paramref name="b"/> as one mesh (sharing nothing), <paramref name="b"/>'s surface tags
    /// moved up by <paramref name="tagOffset"/> and its regions by <paramref name="regionOffset"/>. First order.</summary>
    public static ThermalMesh Merge(ThermalMesh a, ThermalMesh b, int tagOffset, int regionOffset = 0)
    {
        int na = a.NodeCount;
        return new ThermalMesh([.. a.Nodes, .. b.Nodes], 1, [.. a.Tets, .. b.Tets.Select(v => v + na)],
                               [.. a.TetRegion, .. b.TetRegion.Select(r => r + regionOffset)],
                               [.. a.Triangles, .. b.Triangles.Select(v => v + na)], [.. a.TriangleTag, .. b.TriangleTag.Select(t => t + tagOffset)]);
    }

    /// <summary>The mesh with every triangle of tag <paramref name="from"/> whose centroid <paramref name="where"/> accepts
    /// given tag <paramref name="to"/>.</summary>
    public static ThermalMesh Retag(ThermalMesh m, int from, int to, Func<double, double, double, bool> where)
    {
        int nf = m.NodesPerTriangle;
        var tags = (int[])m.TriangleTag.Clone();
        for (int t = 0; t < m.TriangleCount; t++)
        {
            if (tags[t] != from) continue;
            double x = 0, y = 0, z = 0;
            for (int k = 0; k < 3; k++)
            {
                int v = m.Triangles[nf * t + k];
                x += m.Nodes[3 * v] / 3; y += m.Nodes[3 * v + 1] / 3; z += m.Nodes[3 * v + 2] / 3;
            }
            if (where(x, y, z)) tags[t] = to;
        }
        return new ThermalMesh(m.Nodes, m.Order, m.Tets, m.TetRegion, m.Triangles, tags) { Interfaces = m.Interfaces };
    }
}
