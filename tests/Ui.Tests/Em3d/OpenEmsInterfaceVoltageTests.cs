// ================================================================
//  OpenEmsInterfaceVoltageTests.cs — the gate for brief-em3d-125: a strip between two reference planes that lies on an
//  interface (a microstrip under a PEC lid) reads its voltage from the half toward its reference, not the mean of both.
//  Nothing here runs openEMS: the classification is read off the lowering, and S is re-assembled through the product's
//  reader and transform from committed probe files (probes.npz), against oems/probes.py's own re-assembly of the same
//  files (probes-dn.json, written by tools/palace-symmetry-spike/oems/probes.py with the strip → ground voltage).
// ================================================================

using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class OpenEmsInterfaceVoltageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-oiface-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Repo(params string[] parts) => Path.Combine([PalaceBackendTests.RepoRoot(), .. parts]);

    private static string PairB => Repo("testdata", "em3d", "terminal", "palace-modal", "openems", "sym-lossless");
    private static string CoupledFixture => Repo("testdata", "em3d", "terminal", "coupled-microstrip", "openems");
    private static string Example(string cell) => Repo("examples", "3D Wave Ports", cell, "3d", cell + ".c3d");

    [Fact]
    public void Gate1_AStripOnAnInterface_ReadsTheReferenceHalf_AndAStripInOneMedium_KeepsTheMean()
    {
        foreach (var (path, setup) in new[] { (Path.Combine(PairB, "PairB", "3d", "PairB.c3d"), "openEMS"),
                                              (Example("Coupled Microstrip"), "openEMS") })
        {
            var (low, xml) = Lower(path, setup);
            foreach (var p in low.Probes)
            {
                Assert.Equal([$"port{p.Port}_u_dn"], p.U);
                Assert.Equal([$"port{p.Port}_ua_dn"], p.Ua!);
                Assert.Equal([$"port{p.Port}_uc_dn"], p.Uc!);
                // both halves are still written: the model is unchanged, only what the reader averages is
                Assert.Contains($"Name=\"port{p.Port}_u_up\"", xml);
                Assert.Contains($"Name=\"port{p.Port}_u_dn\"", xml);
            }
        }

        var (pair, _) = Lower(Example("Pair"), "openEMS");
        foreach (var p in pair.Probes)
            Assert.Equal([$"port{p.Port}_u_up", $"port{p.Port}_u_dn"], p.U);
    }

    [Theory]
    [InlineData("pairB")]
    [InlineData("coupled")]
    public void Gate2_TheCommittedProbes_ThroughTheReaderAndSolve_AreTheReferenceHalfsS(string fixture)
    {
        var (dir, c3d) = fixture == "pairB" ? (PairB, Path.Combine(PairB, "PairB", "3d", "PairB.c3d")) : (CoupledFixture, Example("Coupled Microstrip"));
        var (low, _) = Lower(c3d, "openEMS");
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "probes-dn.json"))).RootElement;
        double[] f = [.. expected.GetProperty("FrequenciesHz").EnumerateArray().Select(e => e.GetDouble())];

        string runs = Unpack(Path.Combine(dir, "probes.npz"));
        var ports = low.Probes.OrderBy(p => p.Port).ToList();
        var all = new List<IReadOnlyList<FdtdPortProbes>>();
        foreach (var j in ports)
        {
            var read = new List<FdtdPortProbes>();
            foreach (var names in ports)
            {
                var r = OpenEmsRun.ReadPort(Path.Combine(runs, $"p{j.Port}"), names, out string? error);
                Assert.True(r is not null, error);
                read.Add(r!);
            }
            all.Add(read);
        }
        var result = FdtdPortTransform.Solve(all, f, [.. ports.Select(p => p.Port)], [.. ports.Select(_ => new Complex(50, 0))]);
        Assert.Null(result.Error);

        double worst = 0;
        var s = expected.GetProperty("S");
        for (int k = 0; k < f.Length; k++)
            for (int a = 0; a < 4; a++)
                for (int b = 0; b < 4; b++)
                {
                    var e = s[k][a][b];
                    worst = Math.Max(worst, (result.S[k][a, b] - new Complex(e[0].GetDouble(), e[1].GetDouble())).Magnitude);
                }
        output.WriteLine($"{fixture}: {f.Length} frequencies, max |ΔS| against probes.py's strip → ground re-assembly {worst:E2}");
        Assert.True(worst <= 1e-9, $"max |ΔS| {worst:E2}");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private (CsxcadLowering Low, string Xml) Lower(string path, string setupName)
    {
        var doc = C3dPersistence.LoadFromFile(path);
        var (setup, why) = C3dSetups.Select(doc, setupName);
        Assert.True(setup is not null, why);
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var r = C3dProblemAssembly.Assemble(setup!, doc, path, cws, new C3dElaborator());
        Assert.True(r.Ok, r.Refusal);
        var gs = CemOpenEms.ResolveGrid(setup!.OpenEms);
        var grid = FdtdGrid.Build(r.Problem!, gs, long.MaxValue);
        var low = CsxcadWriter.Write(r.Problem!, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        Assert.Equal(4, low.Probes.Count(p => p.IsWave));
        return (low, low.Model!);
    }

    /// <summary>A fixture's probes.npz (arrays p&lt;j&gt;_port&lt;i&gt;_&lt;signal&gt;, each (time, value)) written back as openEMS
    /// probe files p&lt;j&gt;/port&lt;i&gt;_&lt;signal&gt;, in the text form OpenEmsRun.ReadProbe reads.</summary>
    private string Unpack(string npz)
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        using var zip = ZipFile.OpenRead(npz);
        foreach (var entry in zip.Entries)
        {
            var m = Regex.Match(entry.Name, @"^p(\d+)_(port\d+_\w+)\.npy$");
            Assert.True(m.Success, entry.Name);
            using var stream = entry.Open();
            var rows = Npy(stream);
            var sb = new StringBuilder("% time value\n");
            foreach (var (t, v) in rows) sb.Append(t.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                                           .Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            string run = Path.Combine(dir, "p" + m.Groups[1].Value);
            Directory.CreateDirectory(run);
            File.WriteAllText(Path.Combine(run, m.Groups[2].Value), sb.ToString());
        }
        return dir;
    }

    /// <summary>A little-endian float64 (n, 2) .npy array, C order.</summary>
    private static List<(double T, double V)> Npy(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var b = ms.ToArray();
        Assert.Equal(0x93, b[0]);
        int major = b[6];
        int headerLen = major == 1 ? BitConverter.ToUInt16(b, 8) : (int)BitConverter.ToUInt32(b, 8);
        int start = (major == 1 ? 10 : 12) + headerLen;
        string header = Encoding.ASCII.GetString(b, major == 1 ? 10 : 12, headerLen);
        Assert.Contains("'descr': '<f8'", header);
        Assert.Contains("'fortran_order': False", header);
        var rows = new List<(double, double)>();
        for (int at = start; at + 16 <= b.Length; at += 16) rows.Add((BitConverter.ToDouble(b, at), BitConverter.ToDouble(b, at + 8)));
        return rows;
    }
}
