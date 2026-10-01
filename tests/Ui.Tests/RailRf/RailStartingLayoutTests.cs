using System;
using System.IO;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

/// <summary>Tools ▸ railRF starts on the workspace's board when there is one to start on (field report,
/// 2026-10-01) — the active layout, else the one open, else the workspace's one, else none and named.</summary>
public sealed class RailStartingLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-rail-start-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Clay(string cell, string under = "")
    {
        string dir = Path.Combine(_root, under, cell, "layout");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".clay");
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void TheWorkspacesOneLayout_IsTheStart_AndAGeneratedCellIsNotALayout()
    {
        string board = Clay("Board");
        Clay("Pad", ".generated-cells");

        var start = RailStartingLayout.Choose(null, [], _root);

        Assert.Equal(Path.GetFullPath(board), start.Path);
        Assert.Null(start.Note);
    }

    [Fact]
    public void SeveralLayouts_TheActiveOneWins_ElseNoneAndTheyAreNamed()
    {
        string a = Clay("Alpha"), b = Clay("Beta");

        Assert.Equal(Path.GetFullPath(b), RailStartingLayout.Choose(b, [a, b], _root).Path);
        Assert.Equal(Path.GetFullPath(a), RailStartingLayout.Choose(null, [a, null], _root).Path);

        var none = RailStartingLayout.Choose(null, [], _root);
        Assert.Null(none.Path);
        Assert.Equal("This workspace holds 2 layouts (Alpha, Beta) — Open picks the board to start on.", none.Note);
    }
}
