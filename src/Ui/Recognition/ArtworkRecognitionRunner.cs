// The dialog's one door into the recognition — brief-artsch-8-gui-command.md R-as8-4, overview D2.
//
// Three calls, each the function the CLI verb calls and no other: the layout read (RecognitionInput.FromFile), the
// read-only preview the parts table is filled from (ArtworkRecognition.Circuit — `recognize` with no -o), and the
// write (ArtworkRecognition.Run — `recognize --into`). An interface only so a test can RECORD what the dialog hands
// across; nothing here decides anything.

using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Recognition;

/// <summary>What the dialog calls to recognise and to write.</summary>
public interface IArtworkRecognitionRunner
{
    /// <summary>The layout on disk, read as the CLI reads it.</summary>
    RecognitionInput Load(string clayPath);

    /// <summary>Recognise and emit, writing nothing — the parts table and the report.</summary>
    (RecognitionResult Result, RecognitionCircuit? Circuit) Preview(
        RecognitionInput input, RecognitionEmitOptions emit, RunControl? control);

    /// <summary>Recognise, emit, draw and write the schematic.</summary>
    RecognitionRun Run(RecognitionInput input, RecognitionTarget target, RecognitionRunOptions options, RunControl? control);
}

/// <summary>The real one: <see cref="ArtworkRecognition"/>'s entry points, unchanged.</summary>
public sealed class ArtworkRecognitionRunner : IArtworkRecognitionRunner
{
    public static ArtworkRecognitionRunner Instance { get; } = new();

    private ArtworkRecognitionRunner() { }

    public RecognitionInput Load(string clayPath) => RecognitionInput.FromFile(clayPath);

    public (RecognitionResult Result, RecognitionCircuit? Circuit) Preview(
        RecognitionInput input, RecognitionEmitOptions emit, RunControl? control) =>
        ArtworkRecognition.Circuit(input, emit, control);

    public RecognitionRun Run(RecognitionInput input, RecognitionTarget target, RecognitionRunOptions options, RunControl? control) =>
        ArtworkRecognition.Run(input, target, options, control);
}
