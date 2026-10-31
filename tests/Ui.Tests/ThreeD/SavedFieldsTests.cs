// ================================================================
//  SavedFieldsTests.cs — brief DFR32-3 R-dfr32-3-2, owner decision D1 (b): a driven Palace run still saves its fields by default,
//  and states what they will weigh before Palace starts (the measured 137 MB per frequency per driven port at 146,769
//  tetrahedra, scaled by the mesh); a stated Save fields at list is left alone and counted by its own length. Delete Saved
//  Fields moves only the field folders to the Trash (a fake one here), says the bytes freed, and the 3D view's plot then says
//  why it is empty; the run summary names the folder the fields are in. Synthetic run folders and the committed cavity fixture.
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class SavedFieldsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr323-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheDefault_StillSavesTheCentre_AndStatesItsSize_ScaledFromTheMeasurement()
    {
        var (problem, settings) = PalaceBackendTests.LowerableFor("via");
        Assert.True(PalaceConfigWriter.SavesFields(settings));                       // D1 (b): the default is unchanged
        int ports = problem.Ports.Count;
        string note = PalaceFieldFiles.EstimateNote(problem, settings, 2 * PalaceFieldFiles.MeasuredTetrahedra, "/run")!;
        Assert.Equal((long)Math.Round(137e6 * 2 * ports), PalaceFieldFiles.EstimateBytes(2 * PalaceFieldFiles.MeasuredTetrahedra, 1, ports));
        Assert.Contains($"about {MachineMemory.Format(137e6 * 2 * ports)} (the sweep's centre × {ports} driven ports", note);
        Assert.Contains(Path.Combine("/run", "postpro", "paraview"), note);
    }

    [Fact]
    public void AStatedList_IsLeftAlone_AndEstimatedFromItsOwnCount_AndNoneStatesNothing()
    {
        var (problem, settings) = PalaceBackendTests.LowerableFor("via");
        problem = problem with { Frequency = new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear) };
        var three = settings with { SaveFieldsGHz = [2, 5, 8] };
        Assert.Equal([2.0, 5, 8], PalaceConfigWriter.SaveFrequenciesGHz(problem.Frequency, three));
        string note = PalaceFieldFiles.EstimateNote(problem, three, PalaceFieldFiles.MeasuredTetrahedra, "/run")!;
        Assert.Contains($"about {MachineMemory.Format(137e6 * 3 * problem.Ports.Count)} (3 frequencies × ", note);
        Assert.Null(PalaceFieldFiles.EstimateNote(problem, settings with { SaveFieldsGHz = [] }, PalaceFieldFiles.MeasuredTetrahedra, "/run"));
    }

    [Fact]
    public void Delete_MovesOnlyTheFieldFolders_SaysTheBytesFreed_AndANewRunForgetsIt()
    {
        string run = Path.Combine(_root, "results", "Board S1.palace");
        Write(Path.Combine(run, "postpro", "paraview", "driven", "Cycle000001", "data.pvtu"), 3000);
        Write(Path.Combine(run, "postpro", "iteration1", "paraview", "left.vtu"), 1000);
        Write(Path.Combine(run, "postpro", "port-S.csv"), 10);
        Write(Path.Combine(run, "mesh.msh"), 10);
        var moved = new List<string>();

        var r = PalaceFieldFiles.Delete(run, "S1", d => { moved.Add(d); Directory.Move(d, Path.Combine(_root, "trash" + moved.Count)); return null; });

        Assert.Equal(4000, r.BytesFreed);
        Assert.Equal([Path.Combine(run, "postpro", "paraview"), Path.Combine(run, "postpro", "iteration1", "paraview")], moved);
        Assert.True(File.Exists(Path.Combine(run, "mesh.msh")) && File.Exists(Path.Combine(run, "postpro", "port-S.csv")));
        Assert.Contains($"freeing {MachineMemory.Format(4000)} (from {PalaceFieldFiles.FolderIn(run)})", r.Sentence);
        Assert.True(PalaceFieldFiles.WereDeleted(run));
        C3dRunStatus.Begin(run);                                                     // the next run's fields are its own
        Assert.False(PalaceFieldFiles.WereDeleted(run));
    }

    [Fact]
    public void TheRunSummary_NamesTheFolderTheFieldsAreIn()
    {
        var s = new PalaceRunSummary(1000, 1000, 5000, 0, null, null, null, true);
        string line = Em3dRunService.CompletionSummary(s, PalaceQuality.Draft, TimeSpan.FromMinutes(3), 589_000_000, "/r/S1.palace/postpro/paraview");
        Assert.Contains($"field files {MachineMemory.Format(589_000_000)} in /r/S1.palace/postpro/paraview", line);
    }

    [Fact]
    public void DeleteSavedFields_OnTheSetupCard_EmptiesItsPlot_WhichSaysWhy()
    {
        var vm = Open();
        string dir = Em3dRunService.RunDirectory(Path.Combine(_root, "results"), vm.ActiveRunSetup!, Em3dSolverId.Palace);
        CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity"), dir);
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);
        vm.RunFinished();
        Until(() => vm.Viewer.FieldSolutions.Count > 0, "the run's fields were never read");
        vm.NewFieldPlot();
        vm.RefreshSavedFields();
        Assert.True(vm.SetupItems.Single().HasSavedFields);
        vm.MoveToTrash = d => { Directory.Move(d, Path.Combine(_root, "trash-" + Guid.NewGuid().ToString("N"))); return null; };

        var r = vm.DeleteSavedFields("S1")!;

        Assert.True(r.Ok && r.BytesFreed > 0);
        Assert.Equal(r.Sentence, vm.StatusMessage);
        Assert.False(vm.SetupItems.Single().HasSavedFields);
        string why = FieldPlotResolver.FieldsDeletedSentence("S1");
        string? said = null;
        Settled(() => (said = vm.FieldPlotProblem(vm.FieldPlot("Field1")!)) == why);
        Assert.Equal(why, said);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private C3dEditorViewModel Open()
    {
        const long um = 1000;
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology { Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }] });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        var none = new EmAirBoxFace(0, null);
        var setup = new EmSetup
        {
            Name = "S1", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * um, 10160 * um, 25000 * um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");
        return vm;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    private void Until(Func<bool> done, string what) => Assert.True(Settled(done), what);

    private bool Settled(Func<bool> done)
        => SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30));
}
