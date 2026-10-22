// brief-emi-6 §6 — Compare: the format (1), the setup panel (4) and `em --solver`'s words (5). The comparison's arithmetic
// and notes (2, 3) are tests/Engine.Tests/Em3d/Em3dComparisonTests.cs. Nothing here needs a solver installed.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class CompareChoiceTests
{
    private static readonly Em3dSolverChoice PalaceAndEmerge = Em3dSolverChoice.Compare([PalaceSolver.Id, EmergeSolver.Id], EmergeSolver.Id);

    // ── 1. the format ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Both_IsWrittenBackAsBoth_WithNoCompareBlock()
    {
        string json = EmSetupPersistence.Serialize(new EmSetup { Solver3D = Em3dSolvers.Both });
        Assert.Contains("\"Solver3D\": \"Both\"", json);
        Assert.DoesNotContain("\"Compare\"", json);
        Assert.Equal(Em3dSolvers.Both, EmSetupPersistence.Deserialize(json).Solver3D);
    }

    [Fact]
    public void ACompareBlock_RoundTrips_AndItsOrderIsTheRegistrys()
    {
        string json = EmSetupPersistence.Serialize(new EmSetup { Solver3D = PalaceAndEmerge });
        Assert.Contains("\"Solver3D\": \"Compare\"", json);
        var back = EmSetupPersistence.Deserialize(json);
        Assert.Equal(PalaceAndEmerge, back.Solver3D);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));

        var typedOutOfOrder = EmSetupPersistence.Deserialize(
            """{ "Solver3D": "compare", "Compare": { "Solvers": [ "EMerge", "OpenEms" ] } }""");
        Assert.Equal([OpenEmsSolver.Id, EmergeSolver.Id], typedOutOfOrder.Solver3D.Solvers);
        Assert.Equal(OpenEmsSolver.Id, typedOutOfOrder.Solver3D.Reference);     // no Palace: the first chosen
    }

    [Fact]
    public void AnUnknownSolverInTheBlock_IsRefused_ListingTheRegistry()
    {
        var e = Assert.ThrowsAny<Exception>(() => EmSetupPersistence.Deserialize(
            """{ "Solver3D": "Compare", "Compare": { "Solvers": [ "Palace", "Hfx" ] } }"""));
        Assert.Contains("'Hfx'", e.Message);
        Assert.Contains("Palace, OpenEms or Emerge", e.Message);
    }

    // ── 4. the panel ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUninstalledSolver_IsDisabled_WithItsTooltip_InThePickerAndTheBoxes()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = PalaceAndEmerge });
        vm.UpdateSolverAvailability(Installed(emerge: false));

        var row = vm.Solver3DChoiceList.Single(r => !r.IsCompare && r.Value == EmergeSolver.Id);
        Assert.False(row.IsEnabled);
        Assert.Equal(EmSetupEditorViewModel.NotInstalledTip, row.DisabledReason);
        var box = vm.CompareSolverRows.Single(r => r.Solver.Id == EmergeSolver.Id);
        Assert.False(box.IsEnabled);
        Assert.Equal(EmSetupEditorViewModel.NotInstalledTip, box.DisabledReason);
        Assert.True(vm.CompareSolverRows.Single(r => r.Solver.Id == PalaceSolver.Id).IsEnabled);
    }

    [Fact]
    public void AnEigenmodeSetup_DisablesOpenEms_WithItsRefusalsFirstSentence()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = Em3dSolvers.Both, Problem3D = Em3dProblemType.Eigenmode });
        vm.UpdateSolverAvailability(Installed());

        var box = vm.CompareSolverRows.Single(r => r.Solver.Id == OpenEmsSolver.Id);
        Assert.False(box.IsEnabled);
        Assert.StartsWith("This setup asks for an eigenmode solve on both solvers", box.DisabledReason);
        Assert.EndsWith("has no eigensolver.", box.DisabledReason);
    }

    [Fact]
    public void FewerThanTwoRunnableChecked_DisablesRun_SayingWhy()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = PalaceAndEmerge });
        vm.UpdateSolverAvailability(Installed());
        Assert.True(vm.CanRun);

        vm.UpdateSolverAvailability(Installed(emerge: false));
        Assert.False(vm.CanRun);
        Assert.StartsWith("A comparison runs two or more solvers, and only 1 of the checked ones can run here", vm.SimulateTip);
        Assert.Contains("EMerge: " + EmSetupEditorViewModel.NotInstalledTip, vm.SimulateTip);
        Assert.Null(vm.BlockingReason);                                     // a tooltip, never the banner
    }

    [Fact]
    public void TickingAndTheReference_EditTheFile_AndTheLastTwoStayTicked()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = Em3dSolvers.Both });
        vm.UpdateSolverAvailability(Installed());
        Assert.Same(vm.Solver3DChoiceList.Single(r => r.IsCompare), vm.Solver3DChoice);

        vm.CompareSolverRows.Single(r => r.Solver.Id == EmergeSolver.Id).IsChecked = true;
        Assert.Equal([PalaceSolver.Id, OpenEmsSolver.Id, EmergeSolver.Id], vm.Working.Solver3D.Solvers);
        vm.CompareReference = Em3dSolvers.Get(EmergeSolver.Id);
        Assert.Equal(EmergeSolver.Id, vm.Working.Solver3D.Reference);

        vm.CompareSolverRows.Single(r => r.Solver.Id == OpenEmsSolver.Id).IsChecked = false;
        var openEmsAndPalaceGone = vm.CompareSolverRows.Single(r => r.Solver.Id == PalaceSolver.Id);
        openEmsAndPalaceGone.IsChecked = false;                                   // would leave one: refused
        Assert.True(openEmsAndPalaceGone.IsChecked);
        Assert.Equal(PalaceAndEmerge, vm.Working.Solver3D);
    }

    // ── the EMerge section ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEmergeSection_ShowsForAnyChoiceRunningEmerge_TitledInAComparison()
    {
        var alone = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = EmergeSolver.Id });
        Assert.True(alone.IsEmergeSetup);
        Assert.False(alone.ShowEmergeTitle);
        var compared = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = PalaceAndEmerge });
        Assert.True(compared.IsEmergeSetup && compared.ShowEmergeTitle && compared.ShowPalaceTitle);
        Assert.False(compared.IsOpenEmsSetup);
        Assert.False(new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = Em3dSolvers.Both }).IsEmergeSetup);
    }

    [Fact]
    public void AnEmergeField_WritesTheSection_BlankIsThePreset_AndARefusedValueWritesNothing()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup { Solver3D = EmergeSolver.Id });
        Assert.Equal("0.15", vm.EmergeDefaultMaxElementWavelengths);           // Standard's, from EmergeSettings

        vm.EmergeMaxElementWavelengthsText = "0.6";
        vm.CommitEmergeField("Emerge.MaxElementWavelengths");
        Assert.NotNull(vm.EmergeFieldError);
        Assert.Null(vm.Working.Emerge);

        vm.EmergeToleranceText = "1e-4";
        vm.CommitEmergeField("Emerge.Tolerance");
        vm.EmergeAdaptiveSweep = false;
        Assert.Null(vm.EmergeFieldError);
        Assert.Equal(1e-4, vm.Working.Emerge!.Tolerance);
        Assert.False(vm.Working.Emerge.AdaptiveSweep);
        Assert.Equal(2, vm.UndoRedo.Entries.Count());

        vm.EmergeQualityChoice = EmSetupEditorViewModel.PalaceQualityChoices.Single(c => c.Value == PalaceQuality.Draft);
        Assert.Equal("0.2", vm.EmergeDefaultMaxElementWavelengths);
        vm.EmergeToleranceText = "";
        vm.CommitEmergeField("Emerge.Tolerance");
        vm.EmergeAdaptiveSweep = true;
        vm.EmergeQualityChoice = EmSetupEditorViewModel.PalaceQualityChoices.Single(c => c.Value == PalaceQuality.Standard);
        Assert.Null(vm.Working.Emerge);                                          // every field blank: no section
    }

    // ── 5. `em --solver` ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("emerge,palace", null, "Palace+Emerge@Palace")]
    [InlineData("emerge,openems", "emerge", "OpenEms+Emerge@Emerge")]
    [InlineData("both", null, "Palace+OpenEms@Palace")]
    [InlineData("compare", null, "Palace+Emerge@Emerge")]
    [InlineData("openems", null, "OpenEms")]
    public void TheSolverWords_NameTheChoice(string value, string? reference, string expected)
    {
        var (choice, refusal) = Em3dSolvers.ParseCliSolvers(value, reference, PalaceAndEmerge);
        Assert.Null(refusal);
        Assert.Equal(expected, choice!.IsCompare
            ? string.Join("+", Em3dSolvers.Of(choice).Select(d => d.Spelling)) + "@" + Em3dSolvers.Get(choice.Reference).Spelling
            : choice.ToString());
    }

    [Theory]
    [InlineData("palace,hfx", null, "'hfx', which names no 3D solver")]
    [InlineData("palace,palace", null, "palace twice")]
    [InlineData("palace,", null, "two or more")]
    [InlineData("palace", "palace", "names one solver")]
    [InlineData("palace,openems", "emerge", "not one of the solvers compared")]
    public void TheRefusals_SayWhy(string value, string? reference, string said)
    {
        var (choice, refusal) = Em3dSolvers.ParseCliSolvers(value, reference, Em3dSolverChoice.One(PalaceSolver.Id));
        Assert.Null(choice);
        Assert.Contains(said, refusal);
    }

    [Fact]
    public void Compare_OnASetupThatComparesNothing_IsRefused()
        => Assert.Contains("--solver compare runs the setup's own comparison",
                           Em3dSolvers.ParseCliSolvers("compare", null, Em3dSolverChoice.One(PalaceSolver.Id)).Refusal);

    private static IReadOnlyDictionary<Em3dSolverId, bool> Installed(bool emerge = true)
        => Em3dSolvers.All.ToDictionary(d => d.Id, d => d.Id != EmergeSolver.Id || emerge);
}
