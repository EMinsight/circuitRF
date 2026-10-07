using System;
using System.IO;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>A results file with one real cube over freq, and a trace bound to it (brief-tuneopt-3 gates).</summary>
internal static class TuningDisplayFixture
{
    public static DataSet Results(double level)
    {
        var freq = new Axis("freq", [1e9, 2e9, 3e9], "Hz");
        var ds   = new DataSet();
        ds.Add("Gain", new DataCube([freq], [level, level + 1, level + 2]));
        return ds;
    }

    public static string WriteResults(string dir, double level)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Amp.npy");
        DataSetExporter.Export(Results(level), path, ExportFormat.Npy);
        return path;
    }

    public static string TempDir() => Path.Combine(Path.GetTempPath(), $"crf_tune_{Guid.NewGuid():N}");

    public static Trace BoundTrace(string path)
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path,
            CubeName   = "Gain",
            Slice      = [new AxisSlice("freq", AxisRole.KeepAsX, 0)],
        };
        t.Expression = t.BuildPickerExpression();
        return t;
    }
}
