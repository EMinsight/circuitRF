using System.Globalization;

namespace CircuitRF.Core.Tests.Devices.Planar;

/// <summary>
/// The two reference tables of testdata/planar-lines (brief-artsch-1 R-as1-6), produced outside circuitRF:
/// <c>implementation-references.txt</c> by an independent restatement of the closed forms, and
/// <c>physics-references.txt</c> by a 2-D field solve. See that directory's README for provenance.
/// </summary>
internal static class PlanarReferenceData
{
    /// <summary>One implementation row: f, then the geometry in the file's column order, then Z0, εeff, αc, αd.</summary>
    public sealed record ImplementationRow(string Kind, double F, double W, double GOrH1, double HOrH2, double T,
        double Er, double Sigma, double TanD, double Roughness, double Z0, double Eeff, double AlphaC, double AlphaD);

    /// <summary>One physics row: the geometry, then the field solve's Z0 and εeff.</summary>
    public sealed record PhysicsRow(string Kind, double W, double GOrH1, double HOrH2, double T, double Er, double Z0, double Eeff);

    public static IReadOnlyList<ImplementationRow> Implementation(string kind) =>
        [.. Rows("implementation-references.txt", kind).Select(v => new ImplementationRow(kind,
            v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12]))];

    public static IReadOnlyList<PhysicsRow> Physics(string kind) =>
        [.. Rows("physics-references.txt", kind).Select(v => new PhysicsRow(kind, v[0], v[1], v[2], v[3], v[4], v[5], v[6]))];

    private static IEnumerable<double[]> Rows(string file, string kind) =>
        File.ReadLines(Path.Combine(Directory(), file))
            .Where(l => l.StartsWith(kind + " ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                          .Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray());

    private static string Directory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, "testdata", "planar-lines");
            if (System.IO.Directory.Exists(cand)) return cand;
        }
        throw new DirectoryNotFoundException("testdata/planar-lines not found above the test assembly");
    }

    /// <summary>|a − b| / |b|, or |a| when the reference is exactly zero.</summary>
    public static double Rel(double a, double b) => b == 0 ? Math.Abs(a) : Math.Abs(a - b) / Math.Abs(b);
}
