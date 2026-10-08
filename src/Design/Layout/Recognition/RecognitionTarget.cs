// The target cell and the one entry point — brief-artsch-6-emit-and-target-cell.md R-as6-6, R-as6-8;
// overview D4, D12, D20.
//
//   NewCell(name)  — CellCreate.Create with a schematic view only, beside the artwork cell (where New Cell
//                    would put it). A name that exists is a refusal naming it; NameValidator applies.
//   ArtworkCell    — only when the artwork's own cell has NO schematic view: the primary schematic is
//                    written there (CellCreate.WriteSchematicView), to be checked and cleaned up in place.
//   Replace(cell)  — a cell whose primary schematic carries an ArtworkSource block: a history checkpoint
//                    FIRST, then the schematic is rewritten. A schematic without one is never replaced.
//
// Only the schematic is written. NEVER the layout: no links, no SchematicIds, not a byte of the .clay (D5).

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>Where a recognised schematic goes (D4).</summary>
public enum RecognitionTargetKind { NewCell, ArtworkCell, Replace }

/// <summary>A target (R-as6-6).</summary>
/// <param name="CellName">The new cell's name, for <see cref="RecognitionTargetKind.NewCell"/>.</param>
/// <param name="CellDir">The cell to replace the schematic of, for <see cref="RecognitionTargetKind.Replace"/>.</param>
public sealed record RecognitionTarget(RecognitionTargetKind Kind, string? CellName = null, string? CellDir = null)
{
    /// <summary>The checkpoint's intent before a replace (R-as6-6).</summary>
    public const string ReplaceIntent = "Create Schematic from Artwork";

    /// <summary>A new cell beside the artwork's.</summary>
    public static RecognitionTarget NewCell(string name) => new(RecognitionTargetKind.NewCell, CellName: name);

    /// <summary>The artwork's own cell — only when it has no schematic view.</summary>
    public static RecognitionTarget ArtworkCell { get; } = new(RecognitionTargetKind.ArtworkCell);

    /// <summary>The schematic of <paramref name="cellDir"/>, which this command wrote.</summary>
    public static RecognitionTarget Replace(string cellDir) => new(RecognitionTargetKind.Replace, CellDir: cellDir);

    /// <summary>The cell folder a layout view lives in, or null for a loose <c>.clay</c>.</summary>
    public static string? CellOf(string clayPath) =>
        CellFolder.SiblingView(clayPath, ViewType.Layout, ViewType.Schematic).CellDir;

    /// <summary>The name a new cell is offered: <c>&lt;artwork cell&gt;_model</c>.</summary>
    public static string DefaultCellName(string clayPath) =>
        $"{Path.GetFileName(CellOf(clayPath) ?? Path.GetFileNameWithoutExtension(clayPath))}_model";

    /// <summary>Whether writing into the artwork's own cell is offered: it is a cell, and it has no schematic view.</summary>
    public static bool ArtworkCellOffered(string clayPath) =>
        CellOf(clayPath) is { } cell && CellFolder.ResolvePrimary(cell, ViewType.Schematic).State == PrimaryState.NoView;

    /// <summary>Whether <paramref name="cellDir"/>'s primary schematic was written by this command — it carries an
    /// <c>ArtworkSource</c> block — and so may be replaced.</summary>
    public static bool IsReplaceable(string cellDir) =>
        PrimarySchematic(cellDir) is { } path && TryLoad(path)?.ArtworkSource is not null;

    internal static string? PrimarySchematic(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        return primary.ResolvedName is { } name ? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name) : null;
    }

    private static SchematicEditModel? TryLoad(string path)
    {
        try { return SchematicPersistence.LoadFromFile(path).model; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>What one run may be told beyond the recognition (R-as6-8).</summary>
public sealed record RecognitionRunOptions
{
    public RecognitionEmitOptions Emit { get; init; } = new();

    /// <summary>The checkpoint a replace takes first: the schematic's path and the intent in, an error out (null
    /// when it was taken, or there is no history to take one in). Null is the real one —
    /// <see cref="WorkspaceCheckpoints.BeforeWrite"/>, the function behind <c>history checkpoint --intent</c>.
    /// A test passes a counter.</summary>
    public Func<string, string, string?>? Checkpoint { get; init; }

    /// <summary>The time the provenance block records; null is now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>What one run did (R-as6-8).</summary>
/// <param name="Result">The recognition, with its full report.</param>
/// <param name="Circuit">The circuit, or null when nothing got that far.</param>
/// <param name="Schematic">The drawn schematic as written, or null.</param>
/// <param name="SchematicPath">The written <c>.csch</c>, or null.</param>
/// <param name="CellDir">The cell it was written into, or null.</param>
/// <param name="Refusal">Why nothing was written, or null.</param>
public sealed record RecognitionRun(
    RecognitionResult Result, RecognitionCircuit? Circuit, SchematicEditModel? Schematic,
    string? SchematicPath, string? CellDir, string? Refusal)
{
    public bool Ok => Refusal is null && SchematicPath is not null;

    /// <summary>Every guess, omission and what was written.</summary>
    public RecognitionReport Report => Result.Report;

    /// <summary>Whether a history checkpoint was asked for before the write (a replace).</summary>
    public bool CheckpointTaken { get; init; }
}

public static partial class ArtworkRecognition
{
    /// <summary>
    /// R-as6-8 — recognise, emit, draw, write: the one function the GUI command and the CLI verb both call.
    /// A refusal at any step writes nothing.
    /// </summary>
    public static RecognitionRun Run(RecognitionInput input, RecognitionTarget target,
                                     RecognitionRunOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new RecognitionRunOptions();

        var result = Recognize(input, control);
        RecognitionRun Refused(string why, RecognitionCircuit? circuit = null) => new(result, circuit, null, null, null, why);
        if (!result.Ok) return Refused(result.Refusal ?? "The artwork could not be read.");
        if (input.ClayPath is not { } clay)
            return Refused("The layout has not been saved, so there is no cell to write a schematic beside it. Save the layout first.");
        clay = Path.GetFullPath(clay);
        string? artworkCell = RecognitionTarget.CellOf(clay);

        // ── where it goes ────────────────────────────────────────────────────────────────────────────
        string cellDir, cellName, schematicDir;
        string? existing = null;
        switch (target.Kind)
        {
            case RecognitionTargetKind.NewCell:
            {
                string name = (target.CellName ?? "").Trim();
                if (NameValidator.Validate(name) is { } bad) return Refused($"'{name}' cannot be a cell name: {bad}");
                string parentDir = artworkCell is not null ? Path.GetDirectoryName(artworkCell)! : Path.GetDirectoryName(clay)!;
                cellDir = Path.Combine(parentDir, name);
                if (Directory.Exists(cellDir) || File.Exists(cellDir))
                    return Refused($"A cell named '{name}' already exists in {parentDir}; choose another name.");
                cellName = name;
                break;
            }
            case RecognitionTargetKind.ArtworkCell:
            {
                if (artworkCell is null)
                    return Refused("The layout is not in a cell folder, so there is no artwork cell to write into; choose a new cell.");
                if (!RecognitionTarget.ArtworkCellOffered(clay))
                    return Refused($"{Path.GetFileName(artworkCell)} already has a schematic view; choose a new cell.");
                cellDir = artworkCell;
                cellName = Path.GetFileName(artworkCell);
                break;
            }
            default:
            {
                cellDir = Path.GetFullPath(target.CellDir ?? artworkCell ?? "");
                cellName = Path.GetFileName(cellDir);
                existing = RecognitionTarget.PrimarySchematic(cellDir);
                if (existing is null || !RecognitionTarget.IsReplaceable(cellDir))
                    return Refused($"{cellName}'s schematic was not created from artwork; choose a new cell");
                break;
            }
        }
        schematicDir = existing is not null ? Path.GetDirectoryName(existing)! : CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

        // ── the circuit, and its drawing ─────────────────────────────────────────────────────────────
        var circuit = RecognitionEmit.Build(result, input, options.Emit);
        var report = result.Report;
        report.Add(RecognitionFindingClass.EmitOmissions, circuit.Notes.Count, string.Join(" ", circuit.Notes));

        var drawn = NetlistSchematic.Build(new Library("netlist"), circuit.TestBench, schematicDir, circuit.Hints);
        if (drawn.Schematic is not { } model)
            return Refused("The recognised circuit cannot be drawn: " + string.Join(" ", drawn.Refusals), circuit);
        report.Add(RecognitionFindingClass.DrawingNotes, drawn.Notes.Count, string.Join(" ", drawn.Notes));

        RecognitionProvenance.Apply(model, circuit);
        model.TechRef = input.TechnologyPath is { } tech ? SchematicTechnology.StoredRef(Path.GetFullPath(tech), schematicDir) : null;
        model.ArtworkSource = RecognitionProvenance.For(input, schematicDir, options.Now ?? DateTime.UtcNow, options.Emit);

        // ── the write: the schematic only, never the layout ─────────────────────────────────────────
        string path;
        bool checkpoint = false;
        try
        {
            switch (target.Kind)
            {
                case RecognitionTargetKind.NewCell:
                    path = CellCreate.Create(Path.GetDirectoryName(cellDir)!, cellName, CellViews.Schematic, model).SchematicPath!;
                    break;
                case RecognitionTargetKind.ArtworkCell:
                    path = CellCreate.WriteSchematicView(cellDir, cellName, cellName, model);
                    break;
                default:
                    checkpoint = true;
                    var take = options.Checkpoint
                               ?? ((p, intent) => WorkspaceCheckpoints.BeforeWrite(p, intent, CheckpointOrigin.SavePoint)?.Render());
                    if (take(existing!, RecognitionTarget.ReplaceIntent) is { } failed)
                        return Refused($"The history checkpoint a replace takes first could not be taken, so nothing was replaced: {failed}", circuit);
                    path = existing!;
                    SchematicPersistence.SaveToFile(path, model, cellName);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refused($"The schematic could not be written: {ex.Message}", circuit);
        }

        report.Add(RecognitionFindingClass.SchematicWritten, 1,
            (checkpoint ? $"A history checkpoint (\"{RecognitionTarget.ReplaceIntent}\") was taken, then " : "") +
            $"{cellName}'s schematic was {(checkpoint ? "replaced" : "written")}: {path}.");
        return new RecognitionRun(result, circuit, model, path, cellDir, null) { CheckpointTaken = checkpoint };
    }
}
