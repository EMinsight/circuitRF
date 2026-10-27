using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Ui.ModelComposer;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// brief-dfr28-2: a new passive document chooses its data before it is named — the Save dialog follows the importer, in the
/// data's folder with its stem, and a cancelled Save imports nothing — and Propose on a block with more ports than any
/// built-in topology says what it can be fitted with.
/// </summary>
public sealed class PassiveStartTests
{
    [Fact]
    public async Task Import_OnAnUnsavedDocument_AsksForTheDataFirst_ThenSavesBesideIt()
    {
        string root = Directory.CreateTempSubdirectory("crf-passive-start-").FullName;
        try
        {
            string file = WriteVia(Path.Combine(root, "board_asbuilt.s2p"));
            var asked = new List<string>();
            var prompts = new List<ModelComposerViewModel.SavePrompt>();
            string? answer = null;
            var vm = new ModelComposerViewModel
            {
                ShowImporter = importer =>
                {
                    asked.Add("importer");
                    importer.SetSources([file]);
                    return Task.FromResult(importer.Build(out _));
                },
                PickSavePath = prompt =>
                {
                    asked.Add("save");
                    prompts.Add(prompt);
                    return Task.FromResult(answer);
                },
            };
            vm.ChooseKindCommand.Execute("Passive");

            // R-dfr28-2-1: the importer first, then one Save in the data's folder with its stem; Cancel imports nothing.
            Assert.False(await vm.ImportAsync());
            Assert.Equal(["importer", "save"], asked);
            Assert.Equal(root, prompts[0].Folder);
            Assert.Equal("board_asbuilt", prompts[0].Name);
            Assert.Equal(ModelComposerViewModel.SaveBeforeImportTitle, prompts[0].Title);
            Assert.Empty(vm.Document.DataSets);
            Assert.Null(vm.DocumentPath);

            // Saved somewhere else, the sources are relative to where the document went.
            answer = Path.Combine(root, "models", "board.cmc");
            Directory.CreateDirectory(Path.GetDirectoryName(answer)!);
            Assert.True(await vm.ImportAsync(), vm.StatusText);
            Assert.Equal(answer, vm.DocumentPath);
            Assert.Equal(["../board_asbuilt.s2p"], vm.Document.DataSets.Single().Sources);
            vm.Dispose();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Propose_OnAThreePortBlock_WithNothingDrawn_OffersNothing_AndSaysWhy()
    {
        double[] f = SyntheticPassiveData.Log(0.1e9, 10e9, 5);
        var s = f.Select(_ =>
        {
            var m = new Complex[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = i == j ? -1.0 / 3 : 2.0 / 3;
            return m;
        }).ToArray();
        var block = new PassiveBlock("board", f, 50, s);
        var notes = new List<string>();

        var proposals = PassiveFit.Propose(block, PassiveAnalysis.Run(block), notes: notes);

        Assert.Empty(proposals);
        Assert.Equal(["No built-in topology has 3 ports: draw one (pins 1…3 are the ports) or place the file itself as an S-parameter component."],
                     notes);
    }

    private static string WriteVia(string file)
    {
        double[] f = SyntheticPassiveData.Log(0.1e9, 10e9, 5);
        var s = PassiveTopologies.Find("via").Load().S(f, 50, new Dictionary<string, double> { ["Rv"] = 0.02, ["Lv"] = 0.1e-9, ["Cv"] = 0.04e-12 });
        var mats = s.Select(m => { var a = new Mat<Complex>(2, 2); for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) a[i, j] = m[i, j]; return a; }).ToArray();
        TouchstoneIO.WriteFile(new SNP(f, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0)), file, precision: "R");
        return file;
    }
}
