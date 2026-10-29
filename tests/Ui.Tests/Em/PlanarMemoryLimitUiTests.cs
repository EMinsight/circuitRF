// The planar solver's memory limit, Ui and run-service side (2026-10-29): the .cem round trip, the panel's
// commit, and the check Simulate makes against this machine before a run fills anything. The engine half
// (what count a limit allows, the mesh verdict, the guards) is tests/Engine.Tests' PlanarMemoryLimitTests.

using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class PlanarMemoryLimitUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    [Fact]
    public void ACemWithNoLimit_GainsNoByte_AndASetOneRoundTripsInGigabytes()
    {
        var setup = new EmSetup { Name = "hero", LayoutRef = "a.clay" };
        string before = EmSetupPersistence.Serialize(setup);
        Assert.DoesNotContain("MemoryLimitGb", before, StringComparison.Ordinal);
        Assert.Equal(before, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(before)));

        setup.PlanarMesh = setup.PlanarMesh with { MemoryLimitBytes = 12_500_000_000 };
        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("\"MemoryLimitGb\": 12.5", json, StringComparison.Ordinal);
        Assert.Equal(12_500_000_000, EmSetupPersistence.Deserialize(json).PlanarMesh.MemoryLimitBytes);
    }

    [Fact]
    public void ThePanel_TakesGigabytes_BlankIsTheDefault_AndNonsenseIsRefusedInPlace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-memlim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "panel.cem");
        var setup = new EmSetup { Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        EmSetupPersistence.SaveToFile(path, setup);
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();

        vm.PlanarMemoryLimitText = "8";
        vm.CommitMeshField("PlanarMemoryLimit");
        Assert.Equal(8_000_000_000, vm.Working.PlanarMesh.MemoryLimitBytes);

        vm.PlanarMemoryLimitText = "lots";
        vm.CommitMeshField("PlanarMemoryLimit");
        Assert.NotNull(vm.MemoryLimitError);
        Assert.Equal(8_000_000_000, vm.Working.PlanarMesh.MemoryLimitBytes);

        vm.PlanarMemoryLimitText = "";
        vm.CommitMeshField("PlanarMemoryLimit");
        Assert.Null(vm.MemoryLimitError);
        Assert.Null(vm.Working.PlanarMesh.MemoryLimitBytes);
    }

    [Fact]
    public void TheCheck_WarnsPast75Percent_AndAsksPastTheMachine()
    {
        const long Machine = 16_000_000_000;
        static PlanarRunSize Dense(long bytes) => new(20_000, 10_000, false, bytes);

        Assert.Null(PlanarMemoryCheck.Evaluate(Dense(10_000_000_000), Machine).Text);
        Assert.Equal(Em3dMemoryLevel.Warning, PlanarMemoryCheck.Evaluate(Dense(13_000_000_000), Machine).Level);
        Assert.Equal(Em3dMemoryLevel.Severe,  PlanarMemoryCheck.Evaluate(Dense(17_000_000_000), Machine).Level);
        // The accelerated path has no closed-form estimate, so it is never asked about.
        Assert.Null(PlanarMemoryCheck.Evaluate(new PlanarRunSize(20_000, 10_000, true, null), Machine).Text);
    }

    [Fact]
    public void PastTheMachine_TheRunIsAsked_AndStartsOnlyOnYes()
    {
        var size = new PlanarRunSize(30_000, 15_000, false, 20_000_000_000);
        const long Machine = 16_000_000_000;

        var warnings = new List<string>();
        string? declined = PlanarMemoryCheck.Admitter(_ => false, warnings, Machine)(size);
        Assert.Contains("It was not started", declined, StringComparison.Ordinal);

        declined = PlanarMemoryCheck.Admitter(null, warnings, Machine)(size);   // headless, no --force
        Assert.Contains("--force", declined, StringComparison.Ordinal);

        Assert.Null(PlanarMemoryCheck.Admitter(_ => true, warnings, Machine)(size));
        Assert.Contains(warnings, w => w.Contains("started anyway, as confirmed", StringComparison.Ordinal));
    }
}
