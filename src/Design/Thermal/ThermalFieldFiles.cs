// brief-em3d-74 R-em3d74-5b — the temperature field, one ParaView step per sweep point, in the SAME layout and encoding the
// pinned Palace writes, so the 3D viewer's field reader (src/Render/Scene3D/Fields: FieldRun, VtuReader) opens it with no
// new reader:
//
//   postpro/paraview/thermal/thermal.pvd                       the collection: timestep = the 0-based sweep point
//   postpro/paraview/thermal/Cycle000001/data.pvtu             one per step, naming its one piece
//   postpro/paraview/thermal/Cycle000001/proc000000.vtu        an UnstructuredGrid, every array INLINE base64
//                                                              (a UInt32 byte count encoded on its own, then the data)
//
// Point data T_C (°C, Float32); cell data `material` (the region's index) and `attribute` (its mesh tag, as Palace's).
// Cells are VTK_LAGRANGE_TETRAHEDRON (71) with 4 or 10 nodes; VTK numbers a quadratic tetrahedron's last two mid-edge
// nodes (1,3) then (2,3), Gmsh (2,3) then (1,3), so those two are swapped on the way out. Points are in micrometres —
// the mesh's own unit, which is also the reader's default length unit when no config.json says otherwise.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CircuitRF.Thermal;

namespace CircuitRF.Design.Thermal;

public static class ThermalFieldFiles
{
    /// <summary>The problem folder under a run directory's <c>postpro/paraview</c>.</summary>
    public const string Problem = "thermal";

    public static string Folder(string runDir) => Path.Combine(runDir, "postpro", "paraview", Problem);

    /// <summary>Writes every step and the collection; replaces what an earlier run wrote there.</summary>
    public static string Write(string runDir, ThermalMesh mesh, IReadOnlyList<double[]> steps, int[] regionTags)
    {
        string folder = Folder(runDir);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var pvd = new StringBuilder();
        pvd.Append("<?xml version=\"1.0\"?>\n<VTKFile type=\"Collection\" version=\"2.2\" byte_order=\"LittleEndian\">\n<Collection>\n");
        for (int s = 0; s < steps.Count; s++)
        {
            string cycle = $"Cycle{s + 1:D6}";
            Directory.CreateDirectory(Path.Combine(folder, cycle));
            File.WriteAllText(Path.Combine(folder, cycle, "proc000000.vtu"), Piece(mesh, steps[s], regionTags));
            File.WriteAllText(Path.Combine(folder, cycle, "data.pvtu"), Parallel());
            pvd.Append(CultureInfo.InvariantCulture, $"<DataSet timestep=\"{s}\" group=\"\" part=\"0\" file=\"{cycle}/data.pvtu\" name=\"mesh\"/>\n");
        }
        pvd.Append("</Collection>\n</VTKFile>\n");
        string path = Path.Combine(folder, Problem + ".pvd");
        File.WriteAllText(path, pvd.ToString());
        return path;
    }

    private static string Parallel() => """
        <?xml version="1.0"?>
        <VTKFile type="PUnstructuredGrid" version="2.2" byte_order="LittleEndian">
        <PUnstructuredGrid GhostLevel="0">
        <PPoints>
        	<PDataArray type="Float32" Name="Points" NumberOfComponents="3" format="binary"/>
        </PPoints>
        <PCells>
        	<PDataArray type="Int32" Name="connectivity" NumberOfComponents="1" format="binary"/>
        	<PDataArray type="Int32" Name="offsets" NumberOfComponents="1" format="binary"/>
        	<PDataArray type="UInt8" Name="types" NumberOfComponents="1" format="binary"/>
        </PCells>
        <PPointData>
        <PDataArray type="Float32" Name="T_C" NumberOfComponents="1" format="binary" />
        </PPointData>
        <PCellData>
        	<PDataArray type="Int32" Name="attribute" NumberOfComponents="1" format="binary"/>
        	<PDataArray type="Int32" Name="material" NumberOfComponents="1" format="binary"/>
        </PCellData>
        <Piece Source="proc000000.vtu"/>
        </PUnstructuredGrid>
        </VTKFile>

        """;

    private static string Piece(ThermalMesh mesh, double[] t, int[] regionTags)
    {
        int n = mesh.NodeCount, ne = mesh.TetCount, nn = mesh.NodesPerTet;
        var points = new float[3 * n];
        for (int i = 0; i < 3 * n; i++) points[i] = (float)(mesh.Nodes[i] * 1e6);
        var conn = new int[nn * ne];
        for (int e = 0; e < ne; e++)
            for (int k = 0; k < nn; k++)
            {
                int src = nn == 10 && k == 8 ? 9 : nn == 10 && k == 9 ? 8 : k;
                conn[nn * e + k] = mesh.Tets[nn * e + src];
            }
        var offsets = new int[ne];
        for (int e = 0; e < ne; e++) offsets[e] = nn * (e + 1);
        var types = new byte[ne];
        System.Array.Fill(types, (byte)71);
        var material = (int[])mesh.TetRegion.Clone();
        var attribute = mesh.TetRegion.Select(r => regionTags[r]).ToArray();
        var temp = new float[n];
        for (int i = 0; i < n; i++) temp[i] = (float)t[i];

        var s = new StringBuilder();
        s.Append("<?xml version=\"1.0\"?>\n<VTKFile type=\"UnstructuredGrid\" version=\"2.2\" byte_order=\"LittleEndian\">\n<UnstructuredGrid>\n");
        s.Append(CultureInfo.InvariantCulture, $"<Piece NumberOfPoints=\"{n}\" NumberOfCells=\"{ne}\">\n");
        s.Append("<Points>\n");
        Array(s, "Float32", "Points", 3, MemoryMarshal.AsBytes(points.AsSpan()));
        s.Append("</Points>\n<Cells>\n");
        Array(s, "Int32", "connectivity", 1, MemoryMarshal.AsBytes(conn.AsSpan()));
        Array(s, "Int32", "offsets", 1, MemoryMarshal.AsBytes(offsets.AsSpan()));
        Array(s, "UInt8", "types", 1, types);
        s.Append("</Cells>\n<PointData>\n");
        Array(s, "Float32", "T_C", 1, MemoryMarshal.AsBytes(temp.AsSpan()));
        s.Append("</PointData>\n<CellData>\n");
        Array(s, "Int32", "attribute", 1, MemoryMarshal.AsBytes(attribute.AsSpan()));
        Array(s, "Int32", "material", 1, MemoryMarshal.AsBytes(material.AsSpan()));
        s.Append("</CellData>\n</Piece>\n</UnstructuredGrid>\n</VTKFile>\n");
        return s.ToString();
    }

    private static void Array(StringBuilder s, string type, string name, int comps, ReadOnlySpan<byte> data)
    {
        s.Append(CultureInfo.InvariantCulture, $"<DataArray type=\"{type}\" Name=\"{name}\" NumberOfComponents=\"{comps}\" format=\"binary\">\n");
        Span<byte> header = stackalloc byte[4];
        MemoryMarshal.Write(header, (uint)data.Length);
        s.Append(Convert.ToBase64String(header)).Append(Convert.ToBase64String(data)).Append("\n</DataArray>\n");
    }
}
