// brief-dfr32-3 — what a Palace run's saved fields weigh, and the one way to drop them.
//
// A driven run saves the field once per driven port at each save frequency (the sweep's centre when the setup names none), for
// the 3D view's field plots, under <run>/postpro/paraview. On a 12-port board that came to 589 MB per run, and most multi-port
// S-parameter runs are never opened as field plots. The owner kept the default (fields are saved) and asked for two things
// instead: the size stated before Palace starts, beside the memory estimate, and a command that moves the field folders to the
// Trash while keeping the mesh, the Touchstone and the .npy.
//
// The estimate is a measurement scaled, not a model: one saved frequency took 137 MB per driven port on the bond-wire example's
// 146,769-tetrahedron mesh (element order 2), and the files are written per element, so the figure is scaled by the mesh's
// tetrahedra and multiplied by frequencies × ports.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>What <see cref="PalaceFieldFiles.Delete"/> did: the bytes it moved to the Trash, and the sentence that says so.</summary>
public sealed record PalaceFieldDeletion(long BytesFreed, int Directories, IReadOnlyList<string> Failed, string Sentence)
{
    public bool Ok => Failed.Count == 0 && Directories > 0;
}

public static class PalaceFieldFiles
{
    /// <summary>The measured cost of one saved frequency for one driven port, bytes (the bond-wire example).</summary>
    public const double MeasuredBytesPerFrequencyPerPort = 137e6;

    /// <summary>The mesh that measurement was made on, tetrahedra.</summary>
    public const long MeasuredTetrahedra = 146_769;

    /// <summary>The saved fields' size for <paramref name="frequencies"/> save frequencies × <paramref name="ports"/> driven ports
    /// on a mesh of <paramref name="tetrahedra"/>, bytes: the measurement scaled by the mesh.</summary>
    public static long EstimateBytes(long tetrahedra, int frequencies, int ports)
        => tetrahedra <= 0 || frequencies <= 0 || ports <= 0 ? 0
         : (long)Math.Round(MeasuredBytesPerFrequencyPerPort * frequencies * ports * tetrahedra / MeasuredTetrahedra);

    /// <summary>Where a run in <paramref name="runDir"/> keeps its saved fields.</summary>
    public static string FolderIn(string runDir)
        => Path.Combine(runDir, PalaceConfigWriter.OutputDirectory, PalaceConfigWriter.FieldDirectory);

    /// <summary>
    /// The line a driven run states once the mesh is known, before Palace starts: what its saved fields will weigh, where they go,
    /// and how to have none. Null for a problem that saves none, or one that is not driven (an eigenmode or static run saves one
    /// field per mode or terminal, which this measurement does not cover).
    /// </summary>
    public static string? EstimateNote(Em3dProblem problem, PalaceSettings settings, long tetrahedra, string runDir)
    {
        if (problem.Type != Em3dProblemType.Driven || problem.IsStatic || !PalaceConfigWriter.SavesFields(settings)) return null;
        int frequencies = PalaceConfigWriter.SaveFrequenciesGHz(problem.Frequency, settings).Count;
        int ports = problem.Ports.Count;
        long bytes = EstimateBytes(tetrahedra, frequencies, ports);
        if (bytes <= 0) return null;
        string at = settings.SaveFieldsGHz is null ? "the sweep's centre" : $"{frequencies} frequenc{(frequencies == 1 ? "y" : "ies")}";
        return $"Saved fields: about {MachineMemory.Format(bytes)} ({at} × {ports} driven port{(ports == 1 ? "" : "s")} on " +
               $"{tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra; adaptive refinement adds to it), in " +
               $"{FolderIn(runDir)}. Set Save fields at to none to save no fields, or use Delete Saved Fields on the setup after the run.";
    }

    /// <summary>The field directories a run in <paramref name="runDir"/> holds now: the saved fields, and any adaptive pass's field
    /// output that was not pruned. Empty when there are none.</summary>
    public static IReadOnlyList<string> Directories(string runDir)
    {
        var dirs = new List<string>();
        try
        {
            string folder = FolderIn(runDir);
            if (Directory.Exists(folder)) dirs.Add(folder);
            string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
            if (Directory.Exists(post))
                foreach (string pass in Directory.EnumerateDirectories(post, "iteration*"))
                    dirs.AddRange(Directory.EnumerateDirectories(pass));      // a pass's subfolders are field output (PalaceRun)
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return dirs;
    }

    /// <summary>The bytes <paramref name="dirs"/> hold.</summary>
    public static long BytesIn(IEnumerable<string> dirs)
    {
        long n = 0;
        foreach (string d in dirs)
            try { foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return n;
    }

    /// <summary>The note a run directory keeps once its fields were deleted; removed when a new run begins
    /// (<see cref="C3dRunStatus.Begin"/>).</summary>
    public const string DeletedMarkerFile = "fields-deleted.txt";

    public static string MarkerIn(string runDir) => Path.Combine(runDir, DeletedMarkerFile);

    /// <summary>Whether the run in <paramref name="runDir"/> had its saved fields deleted, and has not run since.</summary>
    public static bool WereDeleted(string? runDir)
        => runDir is not null && File.Exists(MarkerIn(runDir)) && !Directory.Exists(FolderIn(runDir));

    /// <summary>
    /// Delete Saved Fields: moves each field directory of the run in <paramref name="runDir"/> to the Trash through
    /// <paramref name="moveToTrash"/> (null on success, else why not), keeping the mesh, the configuration, the CSVs, the
    /// Touchstone and the <c>.npy</c>, and leaves a note so the field plots can say why they are empty.
    /// </summary>
    public static PalaceFieldDeletion Delete(string runDir, string setupName, Func<string, string?> moveToTrash)
    {
        var dirs = Directories(runDir);
        if (dirs.Count == 0)
            return new(0, 0, [], $"Setup {setupName} has no saved fields to delete (looked in {FolderIn(runDir)}).");
        long freed = 0;
        int moved = 0;
        var failed = new List<string>();
        foreach (string d in dirs)
        {
            long bytes = BytesIn([d]);
            if (moveToTrash(d) is { } why) { failed.Add($"{d}: {why}"); continue; }
            freed += bytes;
            moved++;
        }
        if (moved > 0)
            try
            {
                File.WriteAllText(MarkerIn(runDir), $"Saved fields moved to the Trash {DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)}: " +
                                                    $"{freed.ToString(CultureInfo.InvariantCulture)} bytes.{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        string sentence = moved == 0
            ? $"The saved fields of setup {setupName} could not be moved to the Trash: {string.Join("; ", failed)}"
            : $"Moved the saved fields of setup {setupName} to the Trash, freeing {MachineMemory.Format(freed)} (from {FolderIn(runDir)}). " +
              "The mesh, the Touchstone and the results are kept; its field plots are empty until it is run again." +
              (failed.Count > 0 ? $" Not moved: {string.Join("; ", failed)}" : "");
        return new(freed, moved, failed, sentence);
    }
}
