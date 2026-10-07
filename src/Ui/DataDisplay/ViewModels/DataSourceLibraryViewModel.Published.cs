// ================================================================
//  DataSourceLibraryViewModel.Published.cs  —  in-memory results under
//  a source's own path, frame coalescing, and snapshots
//  (brief-tuneopt-3 R-to3-5, R-to3-6, R-to3-8, R-to3-9)
//
//  data-display.md §2.2: a source is addressed by PATH. A live tuning
//  session or an optimizer publishes the DataSet it just computed under
//  the schematic's own results path, and every trace bound to that path
//  re-resolves against it with no change to the trace — the path stays
//  the identity, only the bytes come from memory. A publication lasts
//  until the session drops it or the file changes on disk.
//
//  Frames are COALESCED: a publication that arrives while the display is
//  still drawing the previous one replaces whatever was waiting, so the
//  display redraws once more, for the newest, and the intermediates are
//  counted as skipped.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataSourceLibraryViewModel
{
    /// <summary>The chip a display carries while it shows tuned data.</summary>
    public const string TuningChip = "Tuning";

    /// <summary>The chip a display carries while it shows optimizer data (TO-10).</summary>
    public const string OptimizingChip = "Optimizing";

    private readonly Dictionary<string, (DataSet Data, string Chip)> _pendingFrames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _publishedChips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataSet> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private bool _frameInFlight;

    /// <summary>
    /// How a frame's END is waited for: handed the continuation that ends the frame, it runs it once
    /// the display has drawn. The application posts it behind the render pass; null runs it at once,
    /// which is what a headless caller wants.
    /// </summary>
    public Action<Action>? FrameScheduler { get; set; }

    /// <summary>Frames drawn from published DataSets (R-to3-6).</summary>
    public long PublishedRedraws { get; private set; }

    /// <summary>Published DataSets replaced before they were drawn (R-to3-6) — TO-10's "skipped n frames".</summary>
    public long SkippedFrames { get; private set; }

    /// <summary>"Tuning" or "Optimizing" while any source shows published data; null otherwise.</summary>
    public string? PublishedChip => _publishedChips.Values.LastOrDefault();

    /// <summary>Raised when <see cref="PublishedChip"/> may have changed.</summary>
    public event EventHandler? PublicationChanged;

    /// <summary>Raised when a snapshot is taken or cleared.</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>True when <paramref name="absPath"/> currently shows a published DataSet.</summary>
    public bool IsPublished(string absPath) => FindEntry(absPath)?.IsPublished ?? false;

    /// <summary>
    /// Shows <paramref name="data"/> in place of the file at <paramref name="absPath"/> — a newer
    /// version of that source. Only a source this library already holds, loaded from a <c>.npy</c>
    /// that exists, can be published over: a display that names nothing at that path has no trace to
    /// update, and a not-yet-written file has no version to return to on Revert (the session's Stop
    /// writes it, and the ordinary re-run refresh picks it up). Returns false when nothing was taken.
    /// </summary>
    public bool Publish(string absPath, DataSet data, string chip = TuningChip)
    {
        var entry = FindEntry(absPath);
        if (entry is null || entry.Kind != SourceKind.Npy || entry.IsBroken) return false;

        string key = Path.GetFullPath(absPath);
        if (_frameInFlight)
        {
            if (_pendingFrames.Remove(key)) SkippedFrames++;
            _pendingFrames[key] = (data, chip);
            return true;
        }
        DrawFrame(entry, key, data, chip);
        return true;
    }

    /// <summary>Drops the publication at <paramref name="absPath"/>; the display returns to the file
    /// as it was.</summary>
    public void Unpublish(string absPath)
    {
        string key = Path.GetFullPath(absPath);
        _pendingFrames.Remove(key);
        var entry = FindEntry(absPath);
        if (entry is null || !entry.IsPublished) { SetChip(key, null); return; }
        entry.RestoreFile();
        SetChip(key, null);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The published DataSet has just been written to the file (a session's Stop, R-to3-7): it IS the
    /// file's version now. Ends the publication without returning to the older file data and without
    /// re-reading the disk. A frame still waiting is dropped — the session that sent it has ended.
    /// </summary>
    public void KeepPublishedAsFile(string absPath)
    {
        if (FindEntry(absPath) is { } entry) ForgetPublication(entry);
        else SetChip(Path.GetFullPath(absPath), null);
    }

    private void DrawFrame(DataSourceEntryViewModel entry, string key, DataSet data, string chip)
    {
        _frameInFlight = true;
        PublishedRedraws++;
        // A shallow copy: reading an entry's Data materializes virtual Z/Y cubes INTO its DataSet, and
        // the publisher's own DataSet is the one a session later writes to disk.
        entry.ApplyPublished(ShallowCopy(data));
        SetChip(key, chip);
        LibraryChanged?.Invoke(this, EventArgs.Empty);

        if (FrameScheduler is { } schedule) schedule(EndFrame);
        else EndFrame();
    }

    private void EndFrame()
    {
        _frameInFlight = false;
        if (_pendingFrames.Count == 0) return;

        var (key, frame) = _pendingFrames.First();
        _pendingFrames.Remove(key);
        if (FindEntry(key) is { } entry && !entry.IsBroken) DrawFrame(entry, key, frame.Data, frame.Chip);
        else EndFrame();
    }

    /// <summary>The file changed on disk: it is the newer version now.</summary>
    private void ForgetPublication(DataSourceEntryViewModel entry)
    {
        if (entry.FilePath is not { } fp) return;
        string key = Path.GetFullPath(fp);
        _pendingFrames.Remove(key);
        if (!entry.IsPublished) return;
        entry.ForgetPublication();
        SetChip(key, null);
    }

    private void SetChip(string key, string? chip)
    {
        var before = PublishedChip;
        if (chip is null) _publishedChips.Remove(key);
        else              _publishedChips[key] = chip;
        if (!string.Equals(before, PublishedChip, StringComparison.Ordinal))
            PublicationChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Snapshots (R-to3-9) -----------------------------------------------

    /// <summary>
    /// Freezes what <paramref name="absPath"/> currently shows — published or the file's — as the
    /// snapshot every trace bound to it draws a faded ghost of. Session-only: never written, never
    /// exported. Returns false when this library holds nothing at that path.
    /// </summary>
    public bool TakeSnapshot(string absPath)
    {
        if (FindEntry(absPath)?.Data is not { } shown) return false;
        _snapshots[Path.GetFullPath(absPath)] = ShallowCopy(shown);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes the snapshot at <paramref name="absPath"/>.</summary>
    public void ClearSnapshot(string absPath)
    {
        if (_snapshots.Remove(Path.GetFullPath(absPath)))
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The snapshot traces bound to <paramref name="absPath"/> draw ghosts from, or null.</summary>
    public DataSet? SnapshotFor(string? absPath) =>
        absPath is not null && _snapshots.TryGetValue(Path.GetFullPath(absPath), out var s) ? s : null;

    // ---- helpers -------------------------------------------------------------

    private DataSourceEntryViewModel? FindEntry(string absPath)
    {
        string full = Path.GetFullPath(absPath);
        return Entries.FirstOrDefault(e => e.FilePath is { } fp
            && string.Equals(Path.GetFullPath(fp), full, StringComparison.OrdinalIgnoreCase));
    }

    private static DataSet ShallowCopy(DataSet src)
    {
        var copy = new DataSet();
        foreach (var group in src.Groups)
            foreach (var (name, cube) in src.CubesIn(group))
                copy.AddToGroup(group, name, cube);
        return copy;
    }
}
