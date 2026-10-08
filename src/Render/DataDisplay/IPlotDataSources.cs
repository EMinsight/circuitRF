// ================================================================
//  IPlotDataSources.cs  —  the one thing a `.cdd` needs that is not in
//  the `.cdd`: its data
//
//  RND-4 (R-rnd4-2). A Data Display document names its sources; it does
//  not contain them. In the application that lookup is
//  `DataSourceLibraryViewModel` — an observable collection of entries
//  with aliases, lazy loading, broken-entry placeholders, a toolbar
//  selection and a change event. None of that belongs below the
//  firewall, and none of it is what the RESOLUTION needs: the resolution
//  needs a path for a reference, and a DataSet or an SNP for a path.
//
//  So this is the seam. src/Ui implements it over the library;
//  `circuitrf render` implements it over the files the caller named with
//  `--data` and the ones the document resolves beside itself. Both
//  resolve traces through the same code, which is the whole point.
// ================================================================

using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public interface IPlotDataSources
{
    /// <summary>
    /// The absolute path a trace's logical source reference names. Null or
    /// <see cref="DataSourceRef.Selected"/> means the selected source; a rooted ref is itself;
    /// anything else is relative to wherever this implementation keeps results.
    /// Null when it cannot be resolved at all.
    /// </summary>
    string? ResolveAbs(string? sourceRef);

    /// <summary>Whether this path is a source at all — including one known to be missing.</summary>
    bool Contains(string absPath);

    /// <summary>
    /// The network view of a source: its S-parameters as an <see cref="SNP"/>, or null when it has
    /// none (a simulated cube-only run has none by design). Null for an unknown path.
    /// </summary>
    SNP? NetworkFor(string absPath);

    /// <summary>
    /// A source's <see cref="DataSet"/>. Null when the path is unknown, unreadable, or when
    /// building it throws — a trace whose source cannot be built reports itself unresolvable
    /// exactly like one whose source is missing.
    /// </summary>
    DataSet? DataFor(string absPath);

    /// <summary>The user-facing alias for a source, or null/empty when it has none.</summary>
    string? AliasFor(string absPath);

    /// <summary>The name shown for a source when it has no alias — a file name, usually.</summary>
    string? DisplayNameFor(string absPath);

    /// <summary>True when more than one network source is loaded — drives the file-name prefix on labels.</summary>
    bool HasMultipleSources { get; }

    /// <summary>
    /// The DataSet of the SELECTED source. This is what a loadpull summary table reads: unlike a
    /// contour, a summary column has no per-trace source of its own.
    /// </summary>
    DataSet? SelectedData { get; }

    /// <summary>
    /// True while a source shows a DataSet a running analysis publishes in memory rather than the file (a Monte Carlo
    /// run feeding the display, brief-yield-8 R-ya8-7). False by default: a set of files is never live.
    /// </summary>
    bool IsLive(string absPath) => false;
}
