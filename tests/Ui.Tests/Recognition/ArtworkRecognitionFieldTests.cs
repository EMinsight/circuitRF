// brief-artsch-3-board-graph.md §4 — the field boards. Third-party artwork, never committed: each lives
// under the git-ignored testdata/artwork-boards/<board>/ with an expected.json written by hand from the
// board — { "clay": "<path to the .clay, relative to that folder>", "ports": <count> } — and this skips
// with a reason on a fresh clone.

using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ArtworkRecognitionFieldTests
{
    private const string Boards = "testdata/artwork-boards";

    [FixtureFact(Boards, "the field boards are third-party artwork and are kept outside the repository")]
    public void EachFieldBoardFindsGroundDropsStitchingAndCountsItsPorts()
    {
        var boards = Directory.GetDirectories(FixturePaths.Require(Boards))
                              .Where(d => File.Exists(Path.Combine(d, "expected.json"))).ToList();
        Assert.NotEmpty(boards);

        foreach (string dir in boards)
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
            string clay = Path.Combine(dir, expected.RootElement.GetProperty("clay").GetString()!);
            int ports = expected.RootElement.GetProperty("ports").GetInt32();

            var result = ArtworkRecognition.Recognize(RecognitionInput.FromFile(clay));
            string name = Path.GetFileName(dir);

            Assert.True(result.Ok, $"{name}: {result.Refusal}");
            Assert.True(result.Report.Count(RecognitionFindingClass.StitchingViasDropped) > 0, $"{name}: no stitching via dropped");
            Assert.DoesNotContain(result.Board!.Vias, v => v.Class == ViaClass.Stitching && v.Element != ViaElement.None);
            Assert.True(ports == result.Board.Ports.Count,
                        $"{name}: {result.Board.Ports.Count} ports, expected {ports}: {string.Join(", ", result.Board.Ports.Select(p => p.Name))}");
        }
    }
}
