// brief-artsch-8-gui-command.md §4 — Show in Artwork resolves the provenance's .clay and the component's anchor; a
// component without FromArtwork has no menu row (and a probe asked for one anyway says why rather than pointing).

using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ArtworkCrossProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crf-as8-probe-{Guid.NewGuid():N}");

    public ArtworkCrossProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ShowInArtwork_ResolvesTheProvenancesLayout_AndTheAnchorAsTheLinesCentreLine()
    {
        string clay = CellCreate.Create(_root, "Board", CellViews.Layout).LayoutPath!;
        string csch = Path.Combine(_root, "Board_model", "schematic", "Board_model.csch");
        var model = new SchematicEditModel
        {
            // Stored as Run stores it: relative to the schematic's folder.
            ArtworkSource = new ArtworkProvenance { Layout = SchematicTechnology.StoredRef(clay, Path.GetDirectoryName(csch)!) },
        };
        var line = new EditableComponent { InstanceName = "TL1", FromArtwork = true };
        line.ArtworkAnchor.AddRange([(0, 0), (1_000_000, 0), (1_000_000, 500_000)]);
        var part = new EditableComponent { InstanceName = "C1", FromArtwork = true };
        part.ArtworkAnchor.Add((2_000_000, 0));

        var t = ArtworkCrossProbe.Resolve(csch, model, line);
        Assert.True(t.Ok, t.Refusal);
        Assert.Equal(Path.GetFullPath(clay), Path.GetFullPath(t.ClayPath!));
        Assert.Equal(line.ArtworkAnchor, t.Anchor);
        var ring = Assert.Single(t.Rings);                       // out and back along the centre line
        Assert.Equal([0L, 0, 1_000_000, 0, 1_000_000, 500_000, 1_000_000, 500_000, 1_000_000, 0, 0, 0], ring);
        Assert.Equal(new Bbox(0, 0, 1_000_000, 500_000), t.Focus);

        var p = ArtworkCrossProbe.Resolve(csch, model, part);
        Assert.True(p.Ok, p.Refusal);
        Assert.True(p.Focus.Contains(2_000_000, 0));            // the part's mark is round its anchor
        Assert.Equal(2, p.Rings.Count);                          // the outline, and the pad
    }

    [Fact]
    public void AComponentWithoutFromArtwork_HasNoMenuRow_AndAProbeOfItSaysWhy()
    {
        var model = new SchematicEditModel { ArtworkSource = new ArtworkProvenance { Layout = "x.clay" } };
        var drawn = new EditableComponent { InstanceName = "C9" };
        drawn.ArtworkAnchor.Add((0, 0));

        Assert.False(ArtworkCrossProbe.Offers(drawn));
        Assert.True(ArtworkCrossProbe.Offers(new EditableComponent { FromArtwork = true }));
        var t = ArtworkCrossProbe.Resolve(Path.Combine(_root, "a.csch"), model, drawn);
        Assert.False(t.Ok);
        Assert.Contains("C9", t.Refusal);
    }
}
