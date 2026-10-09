// The field boards (D17): third-party artwork that shaped Create Schematic from Artwork, kept as git-ignored fixtures
// under testdata/artwork-boards/<board>/, each with a hand-written expected.json. testdata/artwork-boards/README.md is
// committed and says what goes there; nothing else in the folder ever is. Because the README makes the folder exist on
// every clone, a field test is gated on a BOARD being there — a folder holding an expected.json — not on the folder.

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.Ui.Tests.Recognition;

internal static class FieldBoards
{
    public const string Folder = "testdata/artwork-boards";

    /// <summary>The fixture gate: some board folder holds an expected.json.</summary>
    public const string Gate = Folder + "/*/expected.json";

    public const string Reason =
        "the field boards are third-party artwork and are kept outside the repository (testdata/artwork-boards/README.md)";

    /// <summary>Every board folder that holds an expected.json, in name order.</summary>
    public static IReadOnlyList<string> Dirs() =>
        [.. Directory.GetDirectories(FixturePaths.Require(Folder))
                     .Where(d => File.Exists(Path.Combine(d, "expected.json")))
                     .Order(System.StringComparer.Ordinal)];
}
