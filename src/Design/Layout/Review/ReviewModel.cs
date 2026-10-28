// The Layout Review's result model (brief-review-2 R-rev2-3 / R-rev2-4). A review finding is a
// person's aid, never an error: a place on the board a reviewer would want to look at, with the
// sentence saying why. It carries its marker the way a DrcViolation does — rings of DBU in the
// artwork's own world coordinates — so the panel, the report and the canvas read one thing.

namespace CircuitRF.Design.Layout.Review;

/// <summary>A review finding is a Warning, or a Note for a check that could not run. Never an error.</summary>
public enum ReviewSeverity { Warning, Note }

/// <summary>Whether a check ran.</summary>
public enum ReviewCheckState { Ran, NotRun, Cancelled }

/// <summary>
/// One place on the board a reviewer would want to look at.
/// </summary>
/// <param name="CheckId">The check that marked it — a <see cref="ReviewCheckDef.Id"/>.</param>
/// <param name="Layer">The drawing layer it is on, or null for a Note and for a finding of the board
/// rather than of a layer (an edge-stitching gap). A finding between two layers names the upper one and
/// says both in its sentence.</param>
/// <param name="Sentence">What a reviewer reads, lengths in the layout's display unit.</param>
/// <param name="MeasuredText">The measured value alone, formatted, or null.</param>
/// <param name="MarkerRings">The marked region, flat DBU vertex lists, as <c>DrcViolation.MarkerRings</c>.</param>
/// <param name="Marker">Bounding box of <paramref name="MarkerRings"/>.</param>
/// <param name="Key">The stable identity an acceptance names — see <see cref="KeyOf"/>.</param>
public sealed record ReviewFinding(
    string                CheckId,
    LayerKey?             Layer,
    ReviewSeverity        Severity,
    string                Sentence,
    string?               MeasuredText,
    IReadOnlyList<long[]> MarkerRings,
    Bbox                  Marker,
    string                Key)
{
    /// <summary>The acceptance that matched this finding, or null. An accepted finding is still
    /// reported; it does not count.</summary>
    public ReviewAcceptance? Accepted { get; init; }

    public bool IsAccepted => Accepted is not null;

    /// <summary>The acceptance the check's own run applied, for a check whose findings are accepted
    /// elsewhere (<see cref="ReviewCheckDef.SettingsOwner"/>) — an impedance finding's, made from the
    /// Impedance review's acceptances of its trace. <see cref="ReviewAcceptance.Apply"/> keeps it as
    /// <see cref="Accepted"/>; null for every other check.</summary>
    public ReviewAcceptance? RunAccepted { get; init; }

    /// <summary>The technology's name for <see cref="Layer"/>, or null for a Note.</summary>
    public string? LayerName { get; init; }

    /// <summary>
    /// The key of a finding: check, layer and the marker's exact bounding box, on
    /// <c>DrcEngine.KeyFor</c>'s terms — DBU are integers and the flatten and Clipper2 are
    /// deterministic, so a re-run over unchanged artwork reproduces it bit for bit, and moving the
    /// marked copper makes a different finding (its acceptance becomes stale).
    /// </summary>
    public static string KeyOf(string checkId, LayerKey? layer, Bbox marker) =>
        $"{checkId}|{(layer is { } l ? $"{l.Layer}/{l.Datatype}" : "-")}|" +
        $"{marker.MinX},{marker.MinY},{marker.MaxX},{marker.MaxY}";
}

/// <summary>One check's line in a result.</summary>
/// <param name="NotRunReason">Why it did not run, or null when it ran.</param>
/// <param name="FindingCount">Its Warning findings, accepted ones included.</param>
/// <param name="AcceptedCount">How many of those an acceptance matched.</param>
public sealed record ReviewCheckRow(
    string           Id,
    string           Title,
    ReviewCheckState State,
    string?          NotRunReason,
    int              FindingCount,
    int              AcceptedCount)
{
    /// <summary>What the check says of its run beyond its findings, or null — the impedance check's scope
    /// and how many traces passed, so a clean result reads as a result rather than as nothing.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// What one review found (R-rev2-4). <see cref="Findings"/> are ordered by check (the overview's table
/// order), then layer in stackup order, then marker position, so two runs over unchanged artwork
/// produce identical output.
/// </summary>
public sealed record LayoutReviewResult(
    IReadOnlyList<ReviewCheckRow> Checks,
    IReadOnlyList<ReviewFinding>  Findings,
    string?                       TechnologyName,
    IReadOnlyList<string>         Diagnostics,
    bool                          Cancelled)
{
    /// <summary>The layout's display unit and resolution, for anything that formats a marker.</summary>
    public LayoutUnit DisplayUnit { get; init; } = LayoutUnit.Um;
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>Why nothing was reviewed — no technology — or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>Acceptances saved on the layout that matched no finding of a check that ran. Listed,
    /// never dropped.</summary>
    public IReadOnlyList<ReviewAcceptance> StaleAcceptances { get; init; } = [];

    /// <summary>The Impedance analysis the <c>impedance</c> check ran, its acceptances applied, or null when
    /// it did not run — what an Accept in the Review panel writes a <c>TraceImpedanceAcceptance</c> from,
    /// finding the trace by <see cref="ReviewFinding.Key"/> (brief-review-5 R-rev5-4).</summary>
    public CircuitRF.Design.Layout.Em.TraceImpedanceReport? Impedance { get; init; }

    public int WarningCount  => Findings.Count(f => f.Severity == ReviewSeverity.Warning && !f.IsAccepted);
    public int AcceptedCount => Findings.Count(f => f.IsAccepted);
    public int NoteCount     => Findings.Count(f => f.Severity == ReviewSeverity.Note);

    public static LayoutReviewResult Refused(string why) => new([], [], null, [], false) { Refusal = why };
}
