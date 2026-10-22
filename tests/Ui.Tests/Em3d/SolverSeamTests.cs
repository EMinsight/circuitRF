using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-1 §4 — the 3D solver seam. The behaviour it moved (every writer golden, the refusal
//  sentences, the CLI's byte identity, the Both runs) is gated by the classes that already held it;
//  these hold the seam itself: one list of solvers, no solver named outside it, and the format's
//  spellings unchanged.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class SolverSeamTests
{
    [Fact]
    public void TheRegistry_ListsPalaceThenOpenEmsThenEMerge_EachWithItsOwnResultToken()
    {
        Assert.Equal([PalaceSolver.Id, OpenEmsSolver.Id, EmergeSolver.Id], Em3dSolvers.All.Select(d => d.Id));
        Assert.Equal(Em3dSolvers.All.Count, Em3dSolvers.All.Select(d => d.ResultToken).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("Palace")]
    [InlineData("OpenEms")]
    [InlineData("Both")]
    public void Solver3D_ReadsAndWritesBackEachSpelling_ByteIdentically(string spelling)
    {
        var expected = spelling switch
        {
            "Palace"  => Em3dSolverChoice.One(PalaceSolver.Id),
            "OpenEms" => Em3dSolverChoice.One(OpenEmsSolver.Id),
            _         => Em3dSolverChoice.Compare([PalaceSolver.Id, OpenEmsSolver.Id], PalaceSolver.Id),
        };
        string text = EmSetupPersistence.Serialize(new EmSetup { Name = "S", Solver3D = expected });
        Assert.Contains($"\"Solver3D\": \"{spelling}\"", text, StringComparison.Ordinal);

        var read = EmSetupPersistence.Deserialize(text);
        Assert.Equal(expected, read.Solver3D);
        Assert.Equal(text, EmSetupPersistence.Serialize(read));
    }

    [Fact]
    public void ASolverNoIntegrationAnswersTo_IsRefused_NamingTheOnesThatDo()
    {
        string text = EmSetupPersistence.Serialize(new EmSetup { Name = "S", Solver3D = PalaceSolver.Id })
                                         .Replace("\"Solver3D\": \"Palace\"", "\"Solver3D\": \"Retired\"", StringComparison.Ordinal);
        var e = Assert.ThrowsAny<Exception>(() => EmSetupPersistence.Deserialize(text));
        Assert.Contains("Solver3D 'Retired'", e.Message, StringComparison.Ordinal);
        Assert.Contains("Palace, OpenEms, Emerge, Both or Compare", e.Message, StringComparison.Ordinal);
    }

    /// <summary>R-emi1-2a — a solver is named by its id only in the registry, its descriptor's folder and the format.</summary>
    [Fact]
    public void NoSolverIsNamedByItsId_OutsideTheRegistryTheDescriptorsAndTheFormat()
    {
        string src = Path.Combine(RepoRoot(), "src");
        string solvers = Path.Combine(src, "Design", "Em3d", "Solvers") + Path.DirectorySeparatorChar;
        string format = Path.Combine(src, "Design", "Layout", "Em", "EmSetupPersistence.cs");
        var named = new Regex(@"\bEm3dSolver\.\w+|\bEm3dSolverId\.(Palace|OpenEms)\b");

        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.StartsWith(solvers, StringComparison.Ordinal) && f != format)
            .Where(f => named.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>Line and block comments removed, so a source scan cannot be satisfied by prose.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
