using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Ui.ModelComposer;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// Designer round 28 — the Import Data dialog opened on a passive document with a transistor's bias pattern filled in, and
/// refused a board's .s6p because its name carried no Vgs/Vds. A passive block has no bias: the pattern starts empty and a
/// file of any name and port count imports as it is.
/// </summary>
public sealed class PassiveImportDialogTests
{
    [Fact]
    public void APassiveDocument_ImportsAFileWhoseNameCarriesNoBias_WithTheDialogsDefaults()
    {
        string root = Directory.CreateTempSubdirectory("crf-passive-import-").FullName;
        try
        {
            double[] f = SyntheticPassiveData.Log(0.1e9, 10e9, 5);
            var s = PassiveTopologies.Find("via").Load().S(f, 50, new Dictionary<string, double> { ["Rv"] = 0.02, ["Lv"] = 0.1e-9, ["Cv"] = 0.04e-12 });
            var mats = s.Select(m => { var a = new Mat<Complex>(2, 2); for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) a[i, j] = m[i, j]; return a; }).ToArray();
            string file = Path.Combine(root, "board_asbuilt.emerge.s2p");
            TouchstoneIO.WriteFile(new SNP(f, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0)), file, precision: "R");

            var vm = new ModelImportViewModel(Path.Combine(root, "board.cmc"), new CmcDocument { Kind = CmcKind.Passive });
            Assert.True(vm.IsPassive);
            Assert.Equal("", vm.Pattern);
            vm.SetSources([file]);

            var set = vm.Build(out string? why);
            Assert.True(set is not null, why);
            Assert.Null(set.Import.Pattern);
            Assert.False(vm.Refused, vm.Line);
        }
        finally { Directory.Delete(root, true); }
    }
}
