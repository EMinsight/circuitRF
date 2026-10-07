// The routes a stream-format layout file takes in and out of circuitRF (brief-oasis-gdstk.md §7b,
// R-oas-3b): circuitRF's own GDSII reader and writer, and the gdstk worker for GDSII and OASIS. The GUI's
// File ▸ Import / Export entries and `convert --engine` name a route and call THESE two functions, so
// neither the view model nor the CLI holds an import or export of its own (the Authoring.cs rule). Each
// route ends in, or starts from, the same shared code — StreamLayoutImport after the read and
// GdsiiExport.Analyze's plan plus StreamLowering before the write — so only the bytes differ.

using CircuitRF.Design.Layout.Interchange.Gdstk;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>Which reader or writer a stream-format layout file goes through.</summary>
public enum StreamRoute
{
    /// <summary>circuitRF's own GDSII reader and writer: the default everywhere (D5).</summary>
    Gdsii,
    /// <summary>GDSII through the gdstk worker: the opt-in second route.</summary>
    GdsiiGdstk,
    /// <summary>OASIS, which only gdstk reads and writes.</summary>
    OasisGdstk,
}

public static class StreamInterchange
{
    /// <summary>What a menu entry, a dialog title and a message call the route: "GDSII", "GDSII (gdstk)",
    /// "OASIS (gdstk)". The "(gdstk)" is deliberate: when the two GDSII routes disagree on a file, which
    /// one was used is the first question.</summary>
    public static string DisplayName(this StreamRoute route) => route switch
    {
        StreamRoute.GdsiiGdstk => "GDSII (gdstk)",
        StreamRoute.OasisGdstk => "OASIS (gdstk)",
        _ => "GDSII",
    };

    /// <summary>The file format alone, for a sentence about the file rather than the route.</summary>
    public static string FormatName(this StreamRoute route) => route == StreamRoute.OasisGdstk ? "OASIS" : "GDSII";

    public static bool UsesGdstk(this StreamRoute route) => route != StreamRoute.Gdsii;

    /// <summary>The file extension a save picker proposes, without the dot.</summary>
    public static string Extension(this StreamRoute route) => route == StreamRoute.OasisGdstk ? "oas" : "gds";

    /// <summary>The patterns an open picker filters on.</summary>
    public static IReadOnlyList<string> FilePatterns(this StreamRoute route) =>
        route == StreamRoute.OasisGdstk ? ["*.oas", "*.oasis"] : ["*.gds", "*.gdsii", "*.sf"];

    /// <summary>
    /// Why <paramref name="route"/> cannot run in this build, or null when it can: the gdstk routes need
    /// the worker (D6), and the GUI's disabled entries and <c>convert</c>'s refusal say this sentence.
    /// Discovery is a few file-existence checks, so asking is cheap.
    /// </summary>
    public static string? Unavailable(this StreamRoute route) =>
        route.UsesGdstk() && GdstkWorker.Locate() is { Found: false } miss ? miss.Reason : null;

    /// <summary>
    /// Imports every cell of <paramref name="path"/> through <paramref name="route"/>. Every other
    /// parameter is <see cref="GdsiiImport.Import"/>'s. A gdstk failure throws <see cref="GdstkException"/>
    /// having created nothing; a <paramref name="token"/> cancellation reaches the gdstk route only (the
    /// native reader reads in one pass).
    /// </summary>
    public static GdsiiImport.ImportResult Import(
        StreamRoute route,
        string path,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        bool preferSourceResolution,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        PinInferenceRules? pinRules = null,
        GdstkWorkerOptions? worker = null,
        CancellationToken token = default)
    {
        if (route == StreamRoute.Gdsii)
        {
            using var stream = File.OpenRead(path);
            return GdsiiImport.Import(stream, parentDir, destTech, destDbuPerMicron, preferSourceResolution, resolveLayerMapping, pinRules);
        }
        return GdstkImport.Import(path, GdstkFormatOf(route), parentDir, destTech, destDbuPerMicron,
            preferSourceResolution, resolveLayerMapping, pinRules, worker, token);
    }

    /// <summary>
    /// Writes <paramref name="plan"/> (<see cref="GdsiiExport.Analyze"/>'s) to <paramref name="filePath"/>
    /// through <paramref name="route"/>, and returns what the lowering did — the plan's own counts on every
    /// route, plus whatever the gdstk route adds to say. Refuses, writing nothing, exactly as
    /// <see cref="GdsiiExport.Write"/> and <see cref="GdstkExport.Write"/> do.
    /// </summary>
    public static GdsiiExportSummary Write(
        StreamRoute route, string filePath, GdsiiExport.ExportPlan plan,
        GdstkWorkerOptions? worker = null, CancellationToken token = default)
    {
        if (route.UsesGdstk())
            return GdstkExport.Write(filePath, plan, GdstkFormatOf(route), options: null, worker, token);

        return GdsiiExport.Write(filePath, plan);
    }

    /// <summary>
    /// The plan's instance references that <paramref name="route"/> cannot write: none for circuitRF's
    /// own writer, which writes an unresolved reference as a dangling structure name, and every one for
    /// gdstk, which numbers references through cell pointers and refuses a cell the file does not hold.
    /// The export dialog blocks on these rather than letting the write fail after the save picker.
    /// </summary>
    public static IReadOnlyList<string> BlockingReferences(this StreamRoute route, GdsiiExport.ExportPlan plan) =>
        route.UsesGdstk() ? plan.UnresolvedInstanceReferences : [];

    private static GdstkFormat GdstkFormatOf(StreamRoute route) =>
        route == StreamRoute.OasisGdstk ? GdstkFormat.Oasis : GdstkFormat.Gdsii;
}
