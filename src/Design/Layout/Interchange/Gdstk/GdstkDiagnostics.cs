using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

/// <summary>
/// Every failure of the gdstk route, as a coded diagnostic (brief-oasis-gdstk.md §5: "the gdstk worker
/// stopped while reading &lt;file&gt;" is a coded diagnostic, not a new allow-list line). The pattern is
/// <c>EmDiagnostics</c>': the ids are the durable part, the templates may be reworded freely. A worker
/// refusal is FORWARDED — its code and detail travel as arguments — rather than re-authored here.
/// </summary>
public static class GdstkDiagnostics
{
    /// <summary>D6: discovery found no worker. <paramref name="reason"/> is the locator's sentence.</summary>
    public static Diagnostic NotFound(string reason) => Diagnostic.Create(
        "gdstk.worker.not-found", DiagnosticSeverity.Error, "{reason}", ("reason", reason));

    public static Diagnostic StartFailed(string path, string why) => Diagnostic.Create(
        "gdstk.worker.start-failed", DiagnosticSeverity.Error,
        "The gdstk worker {path} could not be started: {why}.", ("path", path), ("why", why));

    public static Diagnostic WrongProtocol(string path, int theirs, int ours) => Diagnostic.Create(
        "gdstk.worker.protocol", DiagnosticSeverity.Error,
        "The gdstk worker {path} speaks protocol {theirs}; this circuitRF speaks {ours}. Rebuild it with tools/gdstk-worker/build.sh.",
        ("path", path), ("theirs", theirs), ("ours", ours));

    /// <summary>The worker ended mid-request. <paramref name="doing"/> finishes "while …": "reading /a/b.gds".</summary>
    public static Diagnostic Stopped(string doing, string exitCode, string lastWords) => Diagnostic.Create(
        "gdstk.worker.stopped", DiagnosticSeverity.Error,
        lastWords.Length > 0
            ? "The gdstk worker stopped while {doing} (exit code {exitCode}): {lastWords}"
            : "The gdstk worker stopped while {doing} (exit code {exitCode}).",
        ("doing", doing), ("exitCode", exitCode), ("lastWords", lastWords));

    public static Diagnostic TimedOut(string doing, double seconds) => Diagnostic.Create(
        "gdstk.worker.timed-out", DiagnosticSeverity.Error,
        "The gdstk worker took longer than {seconds} s while {doing}, and was stopped.",
        ("doing", doing), ("seconds", Math.Round(seconds, 1)));

    public static Diagnostic OutOfMemory(string doing, long megabytes) => Diagnostic.Create(
        "gdstk.worker.out-of-memory", DiagnosticSeverity.Error,
        "The gdstk worker used more than {megabytes} MB while {doing}, and was stopped.",
        ("doing", doing), ("megabytes", megabytes));

    public static Diagnostic AlreadyStopped(string doing) => Diagnostic.Create(
        "gdstk.worker.already-stopped", DiagnosticSeverity.Error,
        "The gdstk worker had already stopped before {doing}.", ("doing", doing));

    /// <summary>A worker refusal, forwarded: <paramref name="what"/> is this side's sentence head
    /// ("gdstk could not read /a/b.oas"), <paramref name="code"/> and <paramref name="detail"/> the worker's.</summary>
    public static Diagnostic Refused(string what, string code, string detail) => Diagnostic.Create(
        "gdstk.refused", DiagnosticSeverity.Error, "{what}: {detail} ({code}).",
        ("what", what), ("code", code), ("detail", detail));

    /// <summary>The worker's reply did not hold what the protocol says it holds.</summary>
    public static Diagnostic Malformed(string problem) => Diagnostic.Create(
        "gdstk.reply.malformed", DiagnosticSeverity.Error,
        "The gdstk worker's reply does not follow its protocol: {problem}", ("problem", problem));

    public static Diagnostic ExpansionLimit(long count, long total, int limit) => Diagnostic.Create(
        "gdstk.import.expansion-limit", DiagnosticSeverity.Error,
        "A repetition of {count} would bring the elements placed one by one to {total}, above the limit of {limit}. Nothing was imported.",
        ("count", count), ("total", total), ("limit", limit));

    public static Diagnostic DanglingReference(string format, string references) => Diagnostic.Create(
        "gdstk.export.dangling-reference", DiagnosticSeverity.Error,
        "Export {format} (gdstk) cannot write a reference to a cell the file does not hold: {references}. Nothing was written.",
        ("format", format), ("references", references));
}
