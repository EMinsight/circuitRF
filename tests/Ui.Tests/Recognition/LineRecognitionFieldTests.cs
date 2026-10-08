// brief-artsch-5-traces-to-line-elements.md §4 — the field boards' line elements. Third-party artwork, never
// committed: each lives under the git-ignored testdata/artwork-boards/<board>/ with an expected.json written by
// hand. AS-5 adds "lines": { "MLIN": [min, max], "MBEND": [min, max], … } — a field board's counts are RANGES,
// since a reading of real copper is a best attempt — and a type it does not name is not checked. A board whose
// file does not state "lines" yet is passed over. Skips with a reason on a fresh clone.

using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class LineRecognitionFieldTests
{
    private const string Boards = "testdata/artwork-boards";

    [FixtureFact(Boards, "the field boards are third-party artwork and are kept outside the repository")]
    public void EachFieldBoardReadsItsLineElementCountsWithinTheirRanges()
    {
        var boards = Directory.GetDirectories(FixturePaths.Require(Boards))
                              .Where(d => File.Exists(Path.Combine(d, "expected.json"))).ToList();
        Assert.NotEmpty(boards);

        int checkedBoards = 0;
        foreach (string dir in boards)
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
            var root = expected.RootElement;
            if (!root.TryGetProperty("lines", out var lines)) continue;
            string clay = Path.Combine(dir, root.GetProperty("clay").GetString()!);

            var result = ArtworkRecognition.Recognize(RecognitionInput.FromFile(clay));
            string name = Path.GetFileName(dir);
            string found = string.Join(", ", result.Lines.Elements.GroupBy(e => e.Type).Select(g => $"{g.Key} {g.Count()}"));
            foreach (var range in lines.EnumerateObject())
            {
                int count = result.Lines.Elements.Count(e => e.Type.ToString() == range.Name);
                int lo = range.Value[0].GetInt32(), hi = range.Value[1].GetInt32();
                Assert.True(count >= lo && count <= hi, $"{name}: {range.Name} {count}, expected {lo}–{hi} (read: {found})");
            }
            checkedBoards++;
        }
        Assert.True(checkedBoards > 0, "no field board's expected.json states \"lines\" yet");
    }
}
