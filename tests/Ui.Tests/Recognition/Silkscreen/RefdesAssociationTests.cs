// brief-artsch-10-silkscreen-ocr.md §3, R-as10-4 — designators given to parts one to one. Six parts in a row, a
// label above each; two labels sit a little nearer the neighbouring part than their own, which is what a dense board
// looks like. Taking each label's nearest part would give that neighbour two labels; the optimal assignment gives
// every part its own. A seventh label far from every part names none.

using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition.Silkscreen;

public sealed class RefdesAssociationTests
{
    private const long Mm = 1_000_000;

    [Fact]
    public void LabelsNearerTheWrongPartAreAssignedToTheirOwn()
    {
        // Parts 1 mm × 0.5 mm, 3 mm apart, centred on y = 0.
        var parts = Enumerable.Range(0, 6)
            .Select(i => new RefdesCandidate(new Bbox(i * 3 * Mm - Mm / 2, -Mm / 4, i * 3 * Mm + Mm / 2, Mm / 4))).ToArray();

        // Labels 1 mm above their parts; C2's and C4's are pushed right, past the midpoint to the next part.
        long[] labelX = [0, 3 * Mm, 7_600_000, 9 * Mm, 13_600_000, 15 * Mm];
        var claims = labelX.Select((x, i) => new PartClaim(PartEvidenceSource.Silkscreen, x, Mm, $"C{i}", 0)).ToList();
        claims.Add(new PartClaim(PartEvidenceSource.Silkscreen, 7 * Mm, 20 * Mm, "C9", 0));

        // The premise: two labels are nearer the wrong part.
        Assert.True(RefdesAssociation.Distance(claims[2].X, claims[2].Y, parts[3].Body) < RefdesAssociation.Distance(claims[2].X, claims[2].Y, parts[2].Body));
        Assert.True(RefdesAssociation.Distance(claims[4].X, claims[4].Y, parts[5].Body) < RefdesAssociation.Distance(claims[4].X, claims[4].Y, parts[4].Body));

        var assigned = RefdesAssociation.Assign(claims, parts);

        Assert.Equal([0, 1, 2, 3, 4, 5, -1], assigned);
    }
}
