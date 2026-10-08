// brief-artsch-4-parts-and-parts-table.md §4 — the field boards' parts. Third-party artwork, never
// committed: each lives under the git-ignored testdata/artwork-boards/<board>/ with an expected.json
// written by hand from the board. AS-4 adds three keys beside AS-3's "clay" and "ports" —
// { "parts": <two-terminal parts read>, "series": <count>, "shunt": <count> } — and a board whose file
// does not state them yet is passed over. Skips with a reason on a fresh clone.

using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PartReadingFieldTests
{
    private const string Boards = "testdata/artwork-boards";

    [FixtureFact(Boards, "the field boards are third-party artwork and are kept outside the repository")]
    public void EachFieldBoardReadsItsPartCountAndSeriesShuntSplit()
    {
        var boards = Directory.GetDirectories(FixturePaths.Require(Boards))
                              .Where(d => File.Exists(Path.Combine(d, "expected.json"))).ToList();
        Assert.NotEmpty(boards);

        int checkedBoards = 0;
        foreach (string dir in boards)
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
            var root = expected.RootElement;
            if (!root.TryGetProperty("parts", out var parts)) continue;
            string clay = Path.Combine(dir, root.GetProperty("clay").GetString()!);

            var result = ArtworkRecognition.Recognize(RecognitionInput.FromFile(clay));
            string name = Path.GetFileName(dir);
            var twoPad = result.Parts.Rows.Where(r => r.PadCount == 2).ToList();
            int series = twoPad.Count(r => r.Connection == PartConnection.Series);
            int shunt = twoPad.Count(r => r.Connection == PartConnection.Shunt);

            Assert.True((parts.GetInt32(), root.GetProperty("series").GetInt32(), root.GetProperty("shunt").GetInt32())
                        == (twoPad.Count, series, shunt),
                        $"{name}: {twoPad.Count} parts ({series} series, {shunt} shunt): " +
                        string.Join(", ", twoPad.Select(r => $"{r.Refdes} {PartsTable.ConnectionText(r.Connection)}")));
            checkedBoards++;
        }
        Assert.True(checkedBoards > 0, "no field board's expected.json states \"parts\", \"series\" and \"shunt\" yet");
    }
}
