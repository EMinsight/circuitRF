// ================================================================
//  ModelReportDisplayPreset.cs  —  the Model Validation Report as a
//  Data Display document, and its PDF (brief-modelcomposer-15
//  R-mc15-7a, R-mc15-9)
//
//  One tab per report section that holds data, over the report's own
//  `report.npy` (one group per section, ModelValidationReport.Write):
//
//    Coverage  every data set's bias points, Vgs against Vds, and the PA
//              bias — where the model was fitted and checked;
//    DC        each set's drain current, measured (symbols) against the
//              model, versus its drain voltage;
//    Thermal   the junction-temperature rise against dissipated power,
//              the data's (symbols) against the model's Rth;
//    Charge    each charge's difference from the line integral of the
//              data, % of its range, a family over the shorter axis;
//    C-V       Cgs Cgd Cds Cm against vgs and against vds: the model's
//              curves with the fixed voltage as the legend, the
//              measured points as symbols;
//    S         at each picked bias, S21 in dB (rectangular) and S11/S22
//              (Smith), measured against the model;
//    a class   its loadpull contours (Pout, PAE), the performance
//              summary table (Preset, MXP, MXE are the report's lines;
//              the table is the Data Display's own at the report's
//              compression) and the region of convergence on the Smith
//              chart.
//
//  THIS COMPOSES, IT COMPUTES NOTHING — the DoeDisplayPreset pattern.
//  The window and `model report --pdf` both call WriteDocument and
//  Pdf, so the two PDFs are the same bytes; the PDF is written through
//  PlotComposer and PlotDocumentWriter, the functions `render` uses, as
//  a multi-page document (one page per tab) whose metadata carries no
//  title — so it does not depend on the name it is written under.
//  The PDF opens with the report's card (report.json, beside report.npy:
//  the scorecard, where the model is backed by data, how it was fitted,
//  the data) and heads each tab's page with the conditions that
//  reproduce it — ModelReportPages lays that text out.
//
//  The window's "Open in Data Display" is WritePlotDocument: a plot's
//  DataSet as an `.npy` beside a display that selects it.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Modeling;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;

namespace CircuitRF.Render.DataDisplay;

public static class ModelReportDisplayPreset
{
    private const double W = 400, H = 290, Gap = 24;

    /// <summary>Where a report folder's display is written: <c>&lt;folder&gt;.cdd</c> beside it.</summary>
    public static string DocumentPath(string reportFolder) => Path.GetFullPath(reportFolder).TrimEnd('/', '\\') + ".cdd";

    /// <summary>The report's sections that hold data — one tab each, in the report's order.</summary>
    public static IReadOnlyList<string> Tabs(ModelReport report) =>
        [.. report.Sections.Where(s => s.Data.Cubes.Count > 0).Select(s => s.Name)];

    /// <summary>
    /// The display of a report whose <c>report.npy</c> is <paramref name="sourceRef"/> (relative to the display), one tab per
    /// section that holds data.
    /// </summary>
    public static DataDisplayConfig Build(ModelReport report, string sourceRef)
    {
        var tabs = new List<TabConfig>();
        foreach (var section in report.Sections.Where(s => s.Data.Cubes.Count > 0))
        {
            string g = Group(section.Name);
            var plots = section.Name switch
            {
                "Coverage" => Coverage(section.Data, g, sourceRef),
                "DC" => Dc(section.Data, g, sourceRef),
                "Thermal" => Thermal(g, sourceRef),
                "Charge" => Charge(section.Data, g, sourceRef),
                "C-V" => CapacitanceVoltage(section.Data, g, sourceRef),
                "S" => SParams(section.Data, g, sourceRef),
                _ when section.Data.Contains("GammaLoad") => Mode(section.Name, g, sourceRef),
                _ => [],
            };
            Layout(plots);
            tabs.Add(new TabConfig { Name = section.Name, Plots = plots });
        }
        return new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            SelectedDataSource = sourceRef,
            Tabs = tabs,
        };
    }

    /// <summary>
    /// Writes the display of <paramref name="report"/>, already written to <paramref name="reportFolder"/>, beside that
    /// folder (<see cref="DocumentPath"/>). Returns its path.
    /// </summary>
    public static string WriteDocument(ModelReport report, string reportFolder)
    {
        string path = DocumentPath(reportFolder);
        string sourceRef = Path.GetFileName(Path.GetFullPath(reportFolder).TrimEnd('/', '\\')) + "/report.npy";
        File.WriteAllText(path, JsonSerializer.Serialize(Build(report, sourceRef), DataDisplayJson.Options));
        return path;
    }

    /// <summary>
    /// Every tab of the display at <paramref name="cddPath"/> as one PDF page each, drawn by <see cref="PlotComposer"/> over the
    /// data its source names — the bytes the window's Export PDF and <c>model report --pdf</c> both write.
    /// </summary>
    public static byte[] Pdf(string cddPath)
    {
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cddPath), DataDisplayJson.Options)
                     ?? throw new InvalidDataException($"{Path.GetFileName(cddPath)} is not a data display.");
        string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cddPath))!, config.SelectedDataSource ?? ""));
        var sources = new OneSource(source, DataSetImporter.Import(source).DataSet);
        var page = PagePlacement.Letter;
        var current = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme = current.ExportTheme,
            ExportTransparentBackground = current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix = current.AlwaysDisplayDataSourcePrefix,
        };
        var tabs = new List<(string Name, IReadOnlyList<PlacedPlot> Plots)>();
        foreach (var tab in config.Tabs.Where(t => t.Plots.Count > 0))
        {
            var placed = new List<PlacedPlot>();
            foreach (var pc in tab.Plots)
            {
                var plot = PlotConfigLoader.LoadPlot(pc, sources);
                PlotConfigLoader.ResolveDerived(plot, sources);
                placed.Add(PlotPlacement.Place(plot, pc.Left, pc.Top, pc.Width, pc.Height, pc.FreqUnit, false, _ => null));
            }
            tabs.Add((tab.Name, placed));
        }
        if (tabs.Count == 0) throw new InvalidDataException($"{Path.GetFileName(cddPath)} has no plot to draw.");
        // The card, when the report wrote one: a folder written before it existed still draws its plots.
        string card = Path.Combine(Path.GetDirectoryName(source)!, ReportCard.FileName);
        var pages = File.Exists(card)
            ? ModelReportPages.Build(ReportCard.Read(card), tabs, (canvas, plots, p) => PlotComposer.Render(canvas, plots, RenderTheme.Light, settings, p), page)
            : [.. tabs.Select(t => (Action<SkiaSharp.SKCanvas>)(canvas => PlotComposer.Render(canvas, t.Plots, RenderTheme.Light, settings, page)))];
        return PlotDocumentWriter.BuildPdfBytes(pages, page);
    }

    /// <summary>Writes <see cref="Pdf"/> of the display at <paramref name="cddPath"/> to <paramref name="pdfPath"/>, whole or not
    /// at all. Returns the path written.</summary>
    public static string WritePdf(string cddPath, string pdfPath)
    {
        byte[] bytes = Pdf(cddPath);
        pdfPath = Path.GetFullPath(pdfPath);
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
        string tmp = pdfPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, pdfPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return pdfPath;
    }

    /// <summary>
    /// The window's Open in Data Display (R-mc15-9): <paramref name="data"/> written as <c>&lt;name&gt;.npy</c> in
    /// <paramref name="folder"/>, beside a display <c>&lt;name&gt;.cdd</c> that selects it with one empty plot. Returns the display's path.
    /// </summary>
    public static string WritePlotDocument(DataSet data, string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string stem = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        string npy = Path.Combine(folder, stem + ".npy");
        using (var fs = File.Create(npy)) DataSetExporter.ExportNpy(data, fs);
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            SelectedDataSource = Path.GetFileName(npy),
            Tabs = [new TabConfig { Name = name, Plots = [new PlotContainerConfig { PlotType = PlotType.Rect, Left = Gap, Top = Gap, Width = W, Height = H }] }],
        };
        string cdd = Path.Combine(folder, stem + ".cdd");
        File.WriteAllText(cdd, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        return cdd;
    }

    /// <summary>A section's group in <c>report.npy</c> — ModelValidationReport.Write's own rule.</summary>
    private static string Group(string section) => string.Concat(section.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    // ── Sections ───────────────────────────────────────────────────────────────────────

    private static List<PlotContainerConfig> Coverage(DataSet ds, string g, string src)
    {
        var point = new[] { new AxisSlice("point", AxisRole.KeepAsX, 0) };
        // One trace per group, its marker the one the page's text names (ReportMarkers).
        var traces = ModelValidationReport.CoverageGroups.Select((x, i) => (x.Group, Colour: i)).Where(x => ds.Contains($"{x.Group}.Vgs"))
            .Select(x => Trace(src, $"{g}.{x.Group}.Vgs", point, x: $"{g}.{x.Group}.Vds", colour: x.Colour, marker: x.Group switch
            {
                "IV fit" => MarkerType.Circle, "CV fit" => MarkerType.Square, "Checked" => MarkerType.Diamond,
                "Large signal" => MarkerType.TriangleUp, _ => MarkerType.Star,
            })).ToArray();
        // Bias grids are dense: the default symbol would merge a column of points into a bar.
        foreach (var t in traces) t.Properties!.MarkerSize = t.CubeName!.EndsWith("PA bias.Vgs", StringComparison.Ordinal) ? 4 : 2;
        var plot = Rect("Bias points: fitted and checked (device plane)", "Vds (V)", "Vgs (V)", traces);
        plot.Width = 2 * W + Gap;
        plot.Height = 1.4 * H;
        return [plot];
    }

    private static List<PlotContainerConfig> Dc(DataSet ds, string g, string src)
    {
        var plots = new List<PlotContainerConfig>();
        foreach (string set in Prefixes(ds, ".Id.meas"))
        {
            string x = ds.Contains($"{set}.Vds") ? "Vds" : "Vdsi";
            var point = new[] { new AxisSlice("point", AxisRole.KeepAsX, 0) };
            plots.Add(Rect($"DC {set}: Id", $"{x} (V)", "Id (A)",
                Trace(src, $"{g}.{set}.Id.meas", point, x: $"{g}.{set}.{x}", colour: 0, marker: MarkerType.Circle),
                Trace(src, $"{g}.{set}.Id.model", point, x: $"{g}.{set}.{x}", colour: 1, marker: MarkerType.Cross)));
        }
        return plots;
    }

    private static List<PlotContainerConfig> Thermal(string g, string src)
    {
        var point = new[] { new AxisSlice("point", AxisRole.KeepAsX, 0) };
        return
        [
            Rect("Junction rise against dissipated power", "P (W)", "Tj − Ta (°C)",
                Trace(src, $"{g}.Rise.data", point, x: $"{g}.P", colour: 0, marker: MarkerType.Circle),
                Trace(src, $"{g}.Rise.model", point, x: $"{g}.P", colour: 1, marker: MarkerType.Cross)),
        ];
    }

    private static List<PlotContainerConfig> Charge(DataSet ds, string g, string src)
    {
        var plots = new List<PlotContainerConfig>();
        foreach (string q in new[] { "Qg", "Qd" })
        {
            if (!ds.Contains($"d{q}")) continue;
            var axes = ds[$"d{q}"].Axes;
            bool byVds = axes[1].Length <= axes[0].Length;   // the family runs over the shorter axis
            var slice = byVds
                ? new[] { new AxisSlice("vgs", AxisRole.KeepAsX, 0), new AxisSlice("vds", AxisRole.FamilyIterate, 0) }
                : [new AxisSlice("vgs", AxisRole.FamilyIterate, 0), new AxisSlice("vds", AxisRole.KeepAsX, 0)];
            plots.Add(Rect($"Δ{q}: model against the data's line integral", byVds ? "vgs (V)" : "vds (V)", "% of range",
                Trace(src, $"{g}.d{q}", slice, colour: 0, line: true)));
        }
        return plots;
    }

    private static List<PlotContainerConfig> CapacitanceVoltage(DataSet ds, string g, string src)
    {
        var plots = new List<PlotContainerConfig>();
        foreach (var (tag, swept) in new[] { ("vsVgs", "vgs"), ("vsVds", "vds") })
        {
            string family = swept == "vgs" ? "vds" : "vgs";
            foreach (string c in new[] { "Cgs", "Cgd", "Cds", "Cm" })
            {
                if (!ds.Contains($"{tag}.{c}.model")) continue;
                var traces = new List<TraceConfig>
                {
                    Trace(src, $"{g}.{tag}.{c}.model", [new AxisSlice(family, AxisRole.FamilyIterate, 0), new AxisSlice(swept, AxisRole.KeepAsX, 0)],
                          colour: 0, line: true),
                };
                if (ds.Contains($"{tag}.{c}.meas"))
                    traces.Add(Trace(src, $"{g}.{tag}.{c}.meas", [new AxisSlice("point", AxisRole.KeepAsX, 0)], x: $"{g}.{tag}.{swept}",
                                     colour: 1, marker: MarkerType.Circle));
                plots.Add(Rect($"{c} against {swept}", $"{swept} (V)", $"{c} (F)", [.. traces]));
            }
        }
        return plots;
    }

    private static List<PlotContainerConfig> SParams(DataSet ds, string g, string src)
    {
        var plots = new List<PlotContainerConfig>();
        foreach (string set in Prefixes(ds, ".S.meas"))
        {
            int[] picked = ds.Contains($"{set}.Picked") ? [.. ds[$"{set}.Picked"].RealValues.Select(v => (int)v)] : [0];
            double[] vgs = ds[$"{set}.Vgs"].RealValues, vds = ds[$"{set}.Vds"].RealValues;
            foreach (int k in picked)
            {
                string bias = FormattableString.Invariant($"Vgs {vgs[k]:G3} V, Vds {vds[k]:G3} V");
                AxisSlice[] At(int i, int j) =>
                [
                    new AxisSlice("point", AxisRole.PinToIndex, k), new AxisSlice("freq", AxisRole.KeepAsX, 0),
                    new AxisSlice("i", AxisRole.PinToIndex, i), new AxisSlice("j", AxisRole.PinToIndex, j),
                ];
                plots.Add(Rect($"S21 {set} · {bias}", null, "dB",
                    Trace(src, $"{g}.{set}.S.meas", At(1, 0), transform: CubeTransform.dB20, colour: 0, marker: MarkerType.Circle),
                    Trace(src, $"{g}.{set}.S.model", At(1, 0), transform: CubeTransform.dB20, colour: 1, line: true)));
                plots.Add(Smith($"S11, S22 {set} · {bias}",
                    Trace(src, $"{g}.{set}.S.meas", At(0, 0), colour: 0, marker: MarkerType.Circle),
                    Trace(src, $"{g}.{set}.S.model", At(0, 0), colour: 1, line: true),
                    Trace(src, $"{g}.{set}.S.meas", At(1, 1), colour: 2, marker: MarkerType.Circle),
                    Trace(src, $"{g}.{set}.S.model", At(1, 1), colour: 3, line: true)));
            }
        }
        return plots;
    }

    private static List<PlotContainerConfig> Mode(string label, string g, string src)
    {
        TraceConfig Contour(string metric, int colour) => new()
        {
            SourcePath = src,
            ContourTrace = new ContourTraceConfig
            {
                MetricName = metric, LoadpullGroup = g, ConstraintKind = ConstraintKind.Compression, LevelMode = ContourLevelMode.Count,
                ShowIsoLines = true, DrawLabels = true,
            },
            Properties = Colour(colour, line: true, marker: null),
        };
        TraceConfig Column(SummaryColumnKind kind, string metric, string header) => new()
        {
            SourcePath = src,
            SummaryColumn = new SummaryColumnConfig { Kind = kind, MetricName = metric, Header = header },
        };
        var grid = new[] { new AxisSlice("gridPoint", AxisRole.KeepAsX, 0) };
        var contours = Smith($"{label}: Pout and PAE", Contour("Pout_dBm", 0), Contour("PAE", 1));
        var table = new PlotContainerConfig
        {
            PlotType = PlotType.Table, Width = W, Height = 160, CustomTitle = $"{label}: performance", CustomTitleOn = true,
            SummaryLoadpullGroup = g, TableOptimum = TableOptimum.Mxp,
            Traces =
            [
                Column(SummaryColumnKind.Metric, "Pout_dBm", "Pout (dBm)"), Column(SummaryColumnKind.Metric, "Gt_dB", "Gt (dB)"),
                Column(SummaryColumnKind.Metric, "PAE", "PAE (%)"), Column(SummaryColumnKind.Zload, "", "ZL (Ω)"),
            ],
        };
        var roc = Smith($"{label}: region of convergence",
            Trace(src, $"{g}.GammaLoad.converged", grid, colour: 2, marker: MarkerType.Circle),
            Trace(src, $"{g}.GammaLoad.failed", grid, colour: 9, marker: MarkerType.Cross));
        return [contours, table, roc];
    }

    // ── Shared ─────────────────────────────────────────────────────────────────────────

    /// <summary>The per-set prefixes of a section's cubes: <c>&lt;set&gt;</c> for every <c>&lt;set&gt;&lt;suffix&gt;</c>.</summary>
    private static IEnumerable<string> Prefixes(DataSet ds, string suffix) =>
        ds.Cubes.Keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)).Select(k => k[..^suffix.Length]);

    private static void Layout(List<PlotContainerConfig> plots)
    {
        double x = Gap, y = Gap, rowH = 0;
        foreach (var p in plots)
        {
            if (x > Gap && x + p.Width > 2 * (W + Gap) + Gap) { x = Gap; y += rowH + Gap; rowH = 0; }
            p.Left = x;
            p.Top = y;
            x += p.Width + Gap;
            rowH = Math.Max(rowH, p.Height);
        }
    }

    private static TraceConfig Trace(string src, string cube, AxisSlice[] slice, string? x = null, CubeTransform transform = CubeTransform.None,
                                     int colour = 0, bool line = false, MarkerType? marker = null)
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db, false)
        {
            CubeName = cube,
            Slice = slice,
            Transform = transform,
            XSpec = x,
        };
        t.Expression = t.BuildPickerExpression();
        return new TraceConfig
        {
            SourcePath = src,
            CubeName = cube,
            CubeSlice = [.. slice.Select(AxisSliceConfig.From)],
            CubeTransform = transform,
            Expression = t.Expression,
            XSpec = x,
            Properties = Colour(colour, line || marker is null, marker),
        };
    }

    /// <summary>The Data Display's default trace palette, by order: measured symbols, model lines.</summary>
    private static TracePropertiesConfig Colour(int order, bool line, MarkerType? marker)
    {
        int c = TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length];
        return new TracePropertiesConfig
        {
            LineEnabled = line, LineColorIndex = c, MarkerColorIndex = c,
            MarkerEnabled = marker is not null, MarkerType = marker ?? MarkerType.Circle, MarkerSize = marker is null ? 1.5 : 4,
        };
    }

    private static PlotContainerConfig Rect(string title, string? x, string y, params TraceConfig[] traces) => new()
    {
        PlotType = PlotType.Rect, Width = W, Height = H, CustomTitle = title, CustomTitleOn = true,
        CustomXLabel = x ?? "", CustomXLabelOn = x is not null, CustomYLabel = y, CustomYLabelOn = true,
        Traces = PointsOnTop(traces),
    };

    private static PlotContainerConfig Smith(string title, params TraceConfig[] traces) => new()
    {
        PlotType = PlotType.Smith, Width = H, Height = H, CustomTitle = title, CustomTitleOn = true, Traces = PointsOnTop(traces),
    };

    /// <summary>The model's lines first and the measured symbols over them: a plot paints its traces in order.</summary>
    private static List<TraceConfig> PointsOnTop(TraceConfig[] traces) => [.. traces.OrderBy(t => t.Properties.MarkerEnabled ? 1 : 0)];

    /// <summary>One data file as a display's only source.</summary>
    private sealed class OneSource(string path, DataSet data) : IPlotDataSources
    {
        public string? ResolveAbs(string? sourceRef) => path;
        public bool Contains(string absPath) => string.Equals(absPath, path, StringComparison.OrdinalIgnoreCase);
        public SNP? NetworkFor(string absPath) => null;
        public DataSet? DataFor(string absPath) => Contains(absPath) ? data : null;
        public string? AliasFor(string absPath) => null;
        public string? DisplayNameFor(string absPath) => Path.GetFileName(absPath);
        public bool HasMultipleSources => false;
        public DataSet? SelectedData => data;
    }
}
