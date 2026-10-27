// The picture dialog's one door into the reading — brief-img-5-dialog.md R-im5-9, overview D2.
//
// Each call is the function the CLI calls and no other: the kind (ImageKind.Classify), the read-only trace a layout is
// previewed from (ImageTrace.Trace), the read-only reading a schematic is previewed from (ImageRecognition.Circuit), and
// the two writes (ImageTrace.Run, ImageRecognition.Run) — and for a schematic picture, its reading, its write and its
// drawing over a placed bitmap (SchematicImageRecognition.Circuit, Run, IntoSchematic); for plot pictures (IM-14), the
// session's reading and its file (PlotReading.Read, TouchstoneAssembly.Build). An interface only so a test can RECORD
// what the dialog hands across; nothing here decides anything.

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Recognition;

/// <summary>What the picture dialog calls to read and to write.</summary>
public interface IImageRecognitionRunner
{
    /// <summary>What kind of drawing the picture is (IM-2).</summary>
    ImageKindResult Classify(RasterImage raster);

    /// <summary>A technology file, read as the CLI reads it; null when it cannot be read.</summary>
    Technology? LoadTechnology(string path);

    /// <summary>Trace, writing nothing — Make Layout's preview.</summary>
    ImageTraceResult Trace(ImageTraceInput input, RunControl? control);

    /// <summary>Trace, recognise and emit, writing nothing — Make Schematic's preview.</summary>
    ImageRecognitionCircuit Read(ImageRecognitionInput input, RecognitionEmitOptions emit, RunControl? control);

    /// <summary>Make Layout.</summary>
    ImageTraceRun TraceRun(ImageTraceInput input, ImageTraceTarget target, ImageTraceRunOptions options, RunControl? control);

    /// <summary>Make Schematic, from a layout picture.</summary>
    ImageRecognitionRun RecognitionRun(ImageRecognitionInput input, ImageRecognitionTarget target, ImageRecognitionRunOptions options,
                                       RunControl? control);

    /// <summary>A schematic picture read and emitted, writing nothing — its preview (IM-10).</summary>
    SchematicImageResult ReadSchematic(SchematicImageInput input, RecognitionEmitOptions emit, RunControl? control);

    /// <summary>A schematic picture made into a new cell's schematic (IM-10).</summary>
    SchematicImageRun SchematicRun(SchematicImageInput input, SchematicImageTarget target, SchematicImageRunOptions options, RunControl? control);

    /// <summary>A schematic picture's drawing, made over the placed bitmap it is (IM-10, D14).</summary>
    SchematicImageEdit IntoSchematic(SchematicImageInput input, SchematicEditModel into, SchematicBitmapPlacement placed,
                                     RecognitionEmitOptions emit, RunControl? control);

    /// <summary>Plot pictures read into one session (IM-14) — the plot mode's preview.</summary>
    PlotSession ReadPlots(IReadOnlyList<PlotPictureInput> pictures, PlotReadingOptions options, RunControl? control) =>
        PlotReading.Read(pictures, options, control);

    /// <summary>The Touchstone a session's traces describe, writing nothing (IM-14).</summary>
    TouchstoneAssemblyResult AssemblePlots(PlotSession session, TouchstoneAssemblyOptions options) =>
        TouchstoneAssembly.Build(session, options);
}

/// <summary>The real one: the entry points, unchanged.</summary>
public sealed class ImageRecognitionRunner : IImageRecognitionRunner
{
    public static ImageRecognitionRunner Instance { get; } = new();

    private ImageRecognitionRunner() { }

    public ImageKindResult Classify(RasterImage raster) => ImageKind.Classify(raster);

    public Technology? LoadTechnology(string path) =>
        path.StartsWith(ImageTechnologyChoice.CatalogPrefix, StringComparison.Ordinal)
            ? TechnologyCatalog.Find(path[ImageTechnologyChoice.CatalogPrefix.Length..]) is { } entry ? TechnologyCatalog.Load(entry) : null
            : TechnologyResolver.LoadForPath(path);

    public ImageTraceResult Trace(ImageTraceInput input, RunControl? control) => ImageTrace.Trace(input, control);

    public ImageRecognitionCircuit Read(ImageRecognitionInput input, RecognitionEmitOptions emit, RunControl? control) =>
        ImageRecognition.Circuit(input, emit, control);

    public ImageTraceRun TraceRun(ImageTraceInput input, ImageTraceTarget target, ImageTraceRunOptions options, RunControl? control) =>
        ImageTrace.Run(input, target, options, control);

    public ImageRecognitionRun RecognitionRun(ImageRecognitionInput input, ImageRecognitionTarget target,
                                              ImageRecognitionRunOptions options, RunControl? control) =>
        ImageRecognition.Run(input, target, options, control);

    public SchematicImageResult ReadSchematic(SchematicImageInput input, RecognitionEmitOptions emit, RunControl? control) =>
        SchematicImageRecognition.Circuit(input, emit, control);

    public SchematicImageRun SchematicRun(SchematicImageInput input, SchematicImageTarget target, SchematicImageRunOptions options,
                                          RunControl? control) =>
        SchematicImageRecognition.Run(input, target, options, control);

    public SchematicImageEdit IntoSchematic(SchematicImageInput input, SchematicEditModel into, SchematicBitmapPlacement placed,
                                            RecognitionEmitOptions emit, RunControl? control) =>
        SchematicImageRecognition.IntoSchematic(input, into, placed, emit, control);
}
