// ================================================================
//  LibraryDataSources.cs  —  the data-source library, seen as the four
//  questions a trace resolution actually asks
//
//  RND-4 (R-rnd4-2). `IPlotDataSources` lives in CircuitRF.Render because
//  that is where the resolution went; this is the application's
//  implementation of it, over the live `DataSourceLibraryViewModel`.
//  `circuitrf render` has its own, over the files the caller named.
//
//  The one thing that is more than a forward is `DataFor`: reading an
//  entry's DataSet MATERIALIZES its virtual Z and Y cubes, which is N
//  matrix inversions and can throw. It is caught here — where the crash
//  trail is — and reported as a resolve failure, so a trace whose source
//  cannot be BUILT reports itself unresolvable exactly like one whose
//  source is missing, rather than taking the session down.
// ================================================================

using System;
using System.Linq;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

internal sealed class LibraryDataSources(DataSourceLibraryViewModel? library) : IPlotDataSources
{
    private DataSourceEntryViewModel? Entry(string? absPath) =>
        library is null || absPath is null
            ? null
            : library.Entries.FirstOrDefault(e =>
                  string.Equals(e.FilePath, absPath, StringComparison.OrdinalIgnoreCase));

    public string? ResolveAbs(string? sourceRef) => library?.ResolveAbs(sourceRef);

    public bool Contains(string absPath) => Entry(absPath) is not null;

    public SNP? NetworkFor(string absPath)
    {
        // NetworkView first, Snp as the fallback — the SAME order the picker's own bind uses, so a
        // trace restored from a .cdd and a trace picked in the card end up holding the SAME object
        // for the same source.
        var e = Entry(absPath);
        return e?.NetworkView ?? e?.Snp;
    }

    public DataSet? DataFor(string absPath)
    {
        var entry = Entry(absPath);
        if (entry is null) return null;
        try { return entry.Data; }
        catch (Exception ex)
        {
            Diagnostics.CrashReporter.Note(
                $"trace resolve FAILED (building source): src='{TraceResolve.SafeFileName(entry.FilePath)}' "
                + $"— {TraceResolve.DescribeException(ex)}");
            return null;
        }
    }

    public string? AliasFor(string absPath)       => library?.AliasFor(absPath);
    public string? DisplayNameFor(string absPath) => Entry(absPath)?.DisplayName;

    public bool HasMultipleSources => library?.HasMultipleSources ?? false;

    public bool IsLive(string absPath) => library?.IsPublished(absPath) ?? false;

    public DataSet? SelectedData
    {
        get
        {
            var sel = library?.SelectedEntry;
            if (sel?.FilePath is not { } p) return null;
            return DataFor(p);
        }
    }
}
