// ================================================================
//  ModelComposerExampleShippedTests.cs — the Model Composer example as it SHIPS, read from the build output beside the
//  executable rather than from the repository (owner, review 4). The application's examples item group leaves every
//  `.npy` behind, and this example's data and fitted arrays are `.npy`: the shipped copy opened with every step ticked
//  and refused the report, asking for the Current step to be run again. A copy taken from the repository, as
//  ExampleWorkspaceLoadsTests takes one, cannot see that.
// ================================================================

using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Ui.Tests.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ModelComposerExampleShippedTests
{
    private static readonly string Shipped = Path.Combine(AppContext.BaseDirectory, "examples", ModelComposerExampleAuthoring.Folder);

    /// <summary>Every fit a shipped document records has its arrays beside it, and the simulated tables carry their data.</summary>
    [Fact]
    public void EveryShippedFit_HasItsArrays_AndEveryDataFileShips()
    {
        string repo = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder);
        foreach (string npy in Directory.GetFiles(repo, "*.npy", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(repo, npy);
            Assert.True(File.Exists(Path.Combine(Shipped, rel)), $"{rel} is in the example but does not ship");
        }
        foreach (string cmc in Directory.GetFiles(Shipped, "*.cmc", SearchOption.AllDirectories))
        {
            var doc = CmcFile.Read(cmc);
            if (CurrentFit.Record(doc) is not null) Assert.True(CurrentFit.HasArrays(cmc, doc), $"{cmc}: Current's arrays");
            if (ChargeFit.Record(doc) is not null) Assert.True(ChargeFit.HasArrays(cmc, doc), $"{cmc}: Charge's arrays");
        }
    }

    /// <summary>A fit whose arrays are gone is not ticked on the rail: it has to be run again before a report or export.</summary>
    [Fact]
    public async Task AFitWhoseArraysAreGone_IsNotTicked()
    {
        string to = Path.Combine(Directory.CreateTempSubdirectory("crf-mc-shipped-").FullName, ModelComposerExampleAuthoring.Folder);
        foreach (string f in Directory.GetFiles(Shipped, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(Shipped, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        string gan = Path.Combine(to, "synthetic-gan", SyntheticGanDocument.Name);
        var vm = await ComposerFixture.Open(gan);
        Assert.True(vm.Steps.First(s => s.Name == "Current").IsDone);

        foreach (string f in Directory.GetFiles(Path.Combine(CmcCache.FolderFor(gan), CurrentFit.CacheFolder))) File.Delete(f);
        vm = await ComposerFixture.Open(gan);
        Assert.False(vm.Steps.First(s => s.Name == "Current").IsDone);
        Assert.True(vm.Steps.First(s => s.Name == "Charge").IsDone);
    }

    /// <summary>
    /// brief-modelcomposer-28 R-mc28-4: the committed <c>.va</c> is the writer's output for the committed <c>.cmodel</c>, byte
    /// for byte, in the repository and as it ships — what keeps the two in step when either is regenerated. The writer's
    /// own version on the first line is the one exception, so a release does not fail this until somebody re-exports.
    /// </summary>
    [Fact]
    public void TheShippedVerilogA_IsTheWriterOutputOfTheShippedCmodel()
    {
        string repo = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder);
        string cmodel = Path.Combine(repo, ModelComposerExampleAuthoring.GanModel);
        string expected = Unversioned(CmodelVerilogA.Write(CmodelFile.Read(cmodel),
                                                           new VerilogAOptions { SourceName = Path.GetFileName(cmodel) }));
        foreach (string root in new[] { repo, Shipped })
        {
            string va = Path.Combine(root, ModelComposerExampleAuthoring.GanVa);
            Assert.True(File.Exists(va), $"{va} does not exist");
            Assert.Equal(expected, Unversioned(Encoding.UTF8.GetString(File.ReadAllBytes(va))));
        }
    }

    /// <summary>The text with the first line's "by circuitRF &lt;version&gt;," taken out.</summary>
    private static string Unversioned(string va)
    {
        int nl = va.IndexOf('\n'), by = va.IndexOf(" by circuitRF ", StringComparison.Ordinal), comma = va.IndexOf(", from ", StringComparison.Ordinal);
        Assert.True(by > 0 && comma > by && comma < nl, "the first line names the writer's version");
        return va[..by] + va[comma..];
    }
}
