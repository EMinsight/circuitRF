using System.IO.Compression;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using RfCore.Data;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>brief-modelcomposer-1 R-mc1-1/R-mc1-2: the container is deterministic, and the reader
/// refuses by field.</summary>
public sealed class CmodelFormatTests
{
    [Fact]
    public void WriteReadWrite_IsByteIdentical()
    {
        var model = ComposedTestModels.Model() with
        {
            Symbol = "symbols/gan.csym",
            Arrays = new Dictionary<string, DataCube>
            {
                ["w1"] = new([new Axis("i", [0, 1, 2])], [1.5, -2.25, 3e-12]),
            },
        };

        byte[] first = Bytes(model);
        var read = CmodelFile.Read(new MemoryStream(first));
        byte[] second = Bytes(read);

        Assert.Equal(first, second);
        Assert.Equal([1.5, -2.25, 3e-12], read.Arrays["w1"].RealValues);
    }

    /// <summary>A file written by a zip tool rather than by circuitRF carries an <c>arrays/</c> folder entry before the
    /// files in it; that entry is no array and is not refused.</summary>
    [Fact]
    public void AFolderEntryUnderArrays_IsSkipped()
    {
        var model = ComposedTestModels.Model() with
        {
            Arrays = new Dictionary<string, DataCube> { ["w1"] = new([new Axis("i", [0, 1])], [1.5, -2.25]) },
        };
        using var s = new MemoryStream();
        s.Write(Bytes(model));
        using (var zip = new ZipArchive(s, ZipArchiveMode.Update, leaveOpen: true))
            zip.CreateEntry(CmodelFile.ArraysFolder);
        s.Position = 0;

        Assert.Equal([1.5, -2.25], CmodelFile.Read(s).Arrays["w1"].RealValues);
    }

    [Theory]
    [InlineData("\"Method\": \"Equation\"", "\"Method\": \"Spline\"", "Core.Constituents.Id.Method")]
    [InlineData("\"Fingers\": 4", "\"Fingers\": 0", "BasePeriphery.Fingers")]
    [InlineData("\"Inputs\": [\n          \"vgs\",\n          \"vds\"\n        ],\n        \"Expression\": \"beta",
                "\"Inputs\": [\n          \"vgs\",\n          \"T\"\n        ],\n        \"Expression\": \"beta",
                "Core.Constituents.Id.Inputs")]
    public void ABrokenField_IsRefusedByName(string from, string to, string field)
    {
        var ex = Assert.Throws<CmodelFormatException>(() => ReadEdited(from, to));
        Assert.Equal(field, ex.Field);
        Assert.StartsWith(field + ":", ex.Message);
    }

    [Fact]
    public void ANewerFormatVersion_IsRefused()
    {
        var ex = Assert.Throws<CmodelFormatException>(
            () => ReadEdited("\"FormatVersion\": 1", "\"FormatVersion\": 2"));
        Assert.Equal("FormatVersion", ex.Field);
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void AnUnknownScalingRule_IsRefusedByName()
    {
        var ex = Assert.Throws<CmodelFormatException>(
            () => ReadEdited("\"rg\": \"perFingerInverse\"", "\"rg\": \"quadratic\""));
        Assert.Equal("Network.Scaling.rg", ex.Field);
        Assert.Contains("'quadratic'", ex.Message);
    }

    /// <summary>brief-modelcomposer-29 R-mc29-2: the local law is <c>Extrapolation.Mode: "Local"</c> and round-trips; a
    /// global-law model writes no <c>Mode</c>, as every model before it; an unknown law is refused by name.</summary>
    [Fact]
    public void TheExtrapolationMode_RoundTrips_AndAnUnknownOneIsRefusedByName()
    {
        double[] vgs = [-2, -1, 0, 1], vds = [0, 1, 2, 3, 4];
        var grid = Enumerable.Range(0, 20).Select(i => 0.01 * i).ToArray();
        Axis[] axes = [new("axis1", [0, 1, 2, 3]), new("axis2", [0, 1, 2, 3, 4])];
        var global = ComposedTestModels.Model() with
        {
            Core = ComposedTestModels.Model().Core with
            {
                Id = Constituent.Table("Id", ["vgs", "vds"], new TrainingDomain([(-2, 0), (1, 0), (1, 4), (-2, 4)], [])),
            },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 1e-3 }),
            Arrays = new Dictionary<string, DataCube>
            {
                ["Id.values"] = new(axes, grid), ["Id.coefficients"] = new(axes, grid),
                ["Id.vgs"] = new([new Axis("axis1", [0, 1, 2, 3])], vgs), ["Id.vds"] = new([new Axis("axis1", [0, 1, 2, 3, 4])], vds),
            },
        };
        var local = global with { Extrapolation = ModelExtrapolation.Local(global.Extrapolation!.Gx) };

        Assert.DoesNotContain("\"Mode\": \"Local\"", Encoding.UTF8.GetString(CmodelFile.ModelJson(global)));
        Assert.Contains("\"Mode\": \"Local\"", Encoding.UTF8.GetString(CmodelFile.ModelJson(local)));
        byte[] first = Bytes(local);
        var read = CmodelFile.Read(new MemoryStream(first));
        Assert.Equal(ExtrapolationMode.Local, read.Extrapolation!.Mode);
        Assert.Equal(first, Bytes(read));
        Assert.Equal(ExtrapolationMode.Global, CmodelFile.Read(new MemoryStream(Bytes(global))).Extrapolation!.Mode);

        // the same file with an unknown law in its place
        using var s = new MemoryStream();
        s.Write(first);
        using (var zip = new ZipArchive(s, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(CmodelFile.ModelEntry)!;
            string json;
            using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
            entry.Delete();
            using var w = zip.CreateEntry(CmodelFile.ModelEntry).Open();
            w.Write(Encoding.UTF8.GetBytes(json.Replace("\"Mode\": \"Local\"", "\"Mode\": \"Nearest\"", StringComparison.Ordinal)));
        }
        s.Position = 0;
        var ex = Assert.Throws<CmodelFormatException>(() => CmodelFile.Read(s));
        Assert.Equal("Extrapolation.Mode", ex.Field);
        Assert.Contains("'Nearest'", ex.Message);
    }

    private static byte[] Bytes(ComposedModel m)
    {
        using var s = new MemoryStream();
        CmodelFile.Write(m, s);
        return s.ToArray();
    }

    /// <summary>A valid model with one textual edit to its model.json, read back.</summary>
    private static ComposedModel ReadEdited(string from, string to)
    {
        string json = Encoding.UTF8.GetString(CmodelFile.ModelJson(ComposedTestModels.Model()));
        Assert.Contains(from, json);
        json = json.Replace(from, to);

        using var s = new MemoryStream();
        using (var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var e = zip.CreateEntry(CmodelFile.ModelEntry).Open();
            e.Write(Encoding.UTF8.GetBytes(json));
        }
        s.Position = 0;
        return CmodelFile.Read(s);
    }
}
