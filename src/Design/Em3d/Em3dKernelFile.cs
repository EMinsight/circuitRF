// brief-em3d-65 — a file a 3D backend hands a solver beside its script: a kernel solid's B-rep for Gmsh (R-em3d65-2a), or its
// tessellation as PLY for openEMS's PolyhedronReader (R-em3d65-3b). The bytes are the writer's, so the file is exactly what
// the script names — and, for openEMS, what the pre-run check hashes it against (F0 Q8: CSXCAD skips a file it cannot read
// with a warning and exit code 0, so a missing or damaged one would be solved as if the solid were absent).

using System.Security.Cryptography;

namespace CircuitRF.Design.Em3d;

/// <param name="FileName">The name the script uses, relative to the directory it runs in.</param>
/// <param name="Bytes">The file's content.</param>
/// <param name="Solid">The solid it states (the first, when solids share one).</param>
/// <param name="DeflectionM">An openEMS tessellation's linear deflection, metres; null for a B-rep.</param>
public sealed record Em3dKernelFile(string FileName, byte[] Bytes, string Solid = "", double? DeflectionM = null)
{
    /// <summary>SHA-256 of <see cref="Bytes"/>, lower-case hex.</summary>
    public string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Bytes));

    /// <summary>Writes the file into <paramref name="dir"/> unless it already holds exactly these bytes.</summary>
    public void WriteInto(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (File.Exists(path) && new FileInfo(path).Length == Bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(Bytes)) return;
        File.WriteAllBytes(path, Bytes);
    }
}
