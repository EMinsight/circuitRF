// Gate for docs/sonnet-briefs/brief-gerber-import-target-technology.md §6 — the Layer Mapping dialog's
// Technology row (R-gt-8, D2-D4). LayerMappingDialog is a Window and is not constructed here (this
// suite calls no Avalonia runtime API); its rules live in GerberTechnologyChoices and LayerMappingRows,
// which the dialog calls, and the one thing only the dialog decides — that no other caller shows the
// row — is held by a source scan, as LayerMappingDialogSourceTests holds that dialog's other rules.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Diagnostics.Fixtures;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Tests;

public sealed class LayerMappingDialogTechnologyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("layer-mapping-tech-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<TechnologyCatalogEntry> Shipped() => ShippedTechnologies.All
        .Select(e => new TechnologyCatalogEntry(e.Id, ShippedTechnologies.Load(e).Name, TechnologyOrigin.Shipped, e.ResourceName, null))
        .ToList();

    private string Install(string id) => WorkspaceCreate.InstallTechnology(_root, id);

    [Fact]
    public void TheTechnologyRow_IsShownOnlyByTheGerberConstructor()
    {
        string xaml = ReadRepoFile("src/Ui/Views/Dialogs/LayerMappingDialog.axaml");
        Assert.Matches(new Regex(@"x:Name=""TechnologyRow""[^>]*IsVisible=""False"""), xaml);

        string code = ReadRepoFile("src/Ui/Views/Dialogs/LayerMappingDialog.axaml.cs");
        int shown = code.IndexOf("TechnologyRow.IsVisible = true", StringComparison.Ordinal);
        Assert.Equal(shown, code.LastIndexOf("TechnologyRow.IsVisible = true", StringComparison.Ordinal));

        int gerberCtor = code.IndexOf("GerberMappingRequest request,", StringComparison.Ordinal);
        int nextMember = code.IndexOf("private GerberTechnologyChoice? SelectedChoice", StringComparison.Ordinal);
        Assert.InRange(shown, gerberCtor, nextMember);
    }

    [Fact]
    public void TheDefault_IsTheWorkspaceTechnologyWhenItsCopperMatches_AndNewWhenItDoesNot()
    {
        string six = Install(DocGerberFixtures.SixLayerId);
        string four = Install(DocGerberFixtures.FourLayerId);
        var choices = GerberTechnologyChoices.Build(_root, copperCount: 6, Shipped());

        var matched = choices[GerberTechnologyChoices.DefaultIndex(choices, six)];
        Assert.Equal(GerberTechnologyChoiceKind.Workspace, matched.Kind);
        Assert.Equal(Path.GetFullPath(six), Path.GetFullPath(matched.Path!));

        Assert.Equal(0, GerberTechnologyChoices.DefaultIndex(choices, four));
        Assert.Equal(GerberTechnologyChoiceKind.New, choices[0].Kind);
    }

    [Fact]
    public void ATechnologyWithADifferentCopperCount_IsListedDisabled_AndSaysItsCount()
    {
        string four = Install(DocGerberFixtures.FourLayerId);
        var choices = GerberTechnologyChoices.Build(_root, copperCount: 6, Shipped());

        var row = choices.Single(c => c.Kind == GerberTechnologyChoiceKind.Workspace && c.Path == four);
        Assert.False(row.IsEnabled);
        Assert.EndsWith("4 copper", row.Label);
    }

    [Fact]
    public void UsingATechnology_OffersNoAddToTechnology()
    {
        Install(DocGerberFixtures.SixLayerId);
        var choices = GerberTechnologyChoices.Build(_root, copperCount: 6, Shipped());
        var use = choices.First(c => c.Kind == GerberTechnologyChoiceKind.Workspace);
        var row = new LayerMappingRow(new LayerKey(900, 0), "Mystery", 3, null, LayerMatchKind.NoMatch,
            new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.KeepUnknown));

        var vms = LayerMappingRows.Build([row], use.Technology, use.AllowsAddToTechnology);

        Assert.DoesNotContain(vms[0].Actions, a => a.Action == LayoutFragment.LayerReconciliationAction.AddToTechnology);
    }

    [Fact]
    public void ACatalogChoice_IsCopiedOnlyOnContinue_AndAnIdenticalCopyIsReused()
    {
        var catalogRow = GerberTechnologyChoices.Build(_root, copperCount: 6, Shipped())
            .Single(c => c.Kind == GerberTechnologyChoiceKind.Catalog && c.CatalogId == DocGerberFixtures.SixLayerId);
        string copy = Path.Combine(_root, "tech", DocGerberFixtures.SixLayerId + ".ctech");
        Assert.False(File.Exists(copy));

        var first = GerberTechnologyChoices.Commit(catalogRow, _root);
        Assert.Equal(Path.GetFullPath(copy), first.UsePath);
        var written = File.GetLastWriteTimeUtc(copy);
        File.SetLastWriteTimeUtc(copy, written.AddMinutes(-5));

        var second = GerberTechnologyChoices.Commit(catalogRow, _root);
        Assert.Equal(first, second);
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(copy));
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }
}
