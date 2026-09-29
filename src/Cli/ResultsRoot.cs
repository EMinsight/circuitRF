namespace CircuitRF.Cli;

/// <summary>
/// Where a headless run's results live — and so where a verb that reads them looks (brief-em3d-84 R-em3d84-2). The GUI's
/// results root is <c>&lt;workspace&gt;/results</c>, falling back to the scratch recovery session when no workspace is open.
/// Headless there is no recovery session, so a document with no workspace above it falls back to its OWN directory — the same
/// fallback its LayoutRef already uses, rather than a third rule. <c>em</c> writes here, and <c>explain</c> and
/// <c>render --field</c> read here, through this one function.
/// </summary>
internal static class ResultsRoot
{
    public static string For(string documentPath, string? cwsPath)
        => Path.Combine(cwsPath is { } cws ? Path.GetDirectoryName(cws)! : Path.GetDirectoryName(documentPath)!, "results");
}
