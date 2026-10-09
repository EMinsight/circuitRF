// brief-artsch-10-silkscreen-ocr.md §3 — designators read off the field boards' silkscreen. Third-party artwork,
// never committed: each lives under the git-ignored testdata/artwork-boards/<board>/ with an expected.json written by
// hand from the board. AS-10 adds two keys beside the earlier phases' —
//   "silkscreen": [ { "refdes": "C4", "x": 15.735, "y": 11.447 }, … ]   the part each label names, centre in mm
//   "silkscreenFraction": 0.8                                           how many of them must be read and placed
// — and a board whose file does not state them yet is passed over. Skips with a reason on a fresh clone.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition.Silkscreen;

public sealed class SilkscreenFieldTests
{
    /// <summary>A part is the one a label names when its centre is this close to the stated one, mm.</summary>
    private const double PlaceToleranceMm = 0.5;

    [FixtureFact(FieldBoards.Gate, FieldBoards.Reason)]
    public void EachFieldBoardReadsAndPlacesItsStatedShareOfDesignators()
    {
        var boards = FieldBoards.Dirs();

        int checkedBoards = 0;
        foreach (string dir in boards)
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
            var root = expected.RootElement;
            if (!root.TryGetProperty("silkscreen", out var labels)) continue;
            double fraction = root.GetProperty("silkscreenFraction").GetDouble();
            string clay = Path.Combine(dir, root.GetProperty("clay").GetString()!);

            var input = RecognitionInput.FromFile(clay);
            var result = ArtworkRecognition.Recognize(input);
            double dbuPerMm = input.View.DbuPerMicron * 1000.0;
            string name = Path.GetFileName(dir);

            var stated = labels.EnumerateArray()
                .Select(l => (Refdes: l.GetProperty("refdes").GetString()!, X: l.GetProperty("x").GetDouble(), Y: l.GetProperty("y").GetDouble()))
                .ToList();
            var right = stated.Where(s => result.Parts.Rows.Any(r =>
                string.Equals(r.Refdes, s.Refdes, StringComparison.OrdinalIgnoreCase)
                && r.Evidence.TryGetValue(PartField.Refdes, out var src) && src == PartEvidenceSource.Silkscreen
                && Math.Abs(r.X / dbuPerMm - s.X) <= PlaceToleranceMm && Math.Abs(r.Y / dbuPerMm - s.Y) <= PlaceToleranceMm)).ToList();

            Assert.True(right.Count >= fraction * stated.Count,
                        $"{name}: {right.Count} of {stated.Count} designators read and placed (need {fraction:P0}); missed " +
                        string.Join(", ", stated.Except(right).Select(s => s.Refdes)) + "; read " +
                        string.Join(", ", result.Parts.Rows.Where(r => r.Evidence.GetValueOrDefault(PartField.Refdes) == PartEvidenceSource.Silkscreen)
                                                           .Select(r => $"{r.Refdes} ({r.X / dbuPerMm:0.###}, {r.Y / dbuPerMm:0.###})")));
            checkedBoards++;
        }
        Assert.True(checkedBoards > 0, "no field board's expected.json states \"silkscreen\" and \"silkscreenFraction\" yet");
    }
}
