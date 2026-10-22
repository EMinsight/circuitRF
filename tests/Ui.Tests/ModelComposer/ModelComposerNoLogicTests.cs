using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14 (overview D15): MC-13's source scan, extended to the window. Its folders exist and hold code — so
/// <see cref="ModelCliNoLogicTests.TheWindow_ReimplementsNoFlow"/> is not passing over nothing — none of that code holds a
/// flow, and the window reaches the data, the runs and the file through the shared functions the verb calls.
/// </summary>
public sealed class ModelComposerNoLogicTests
{
    private static readonly string[] Folders =
        [Path.Combine("src", "Ui", "ModelComposer"), Path.Combine("src", "Ui", "Views", "ModelComposer")];

    /// <summary>What the window must call rather than hold: the save, the data, the import, the fits, the network.</summary>
    private static readonly string[] SharedCalls =
        ["ModelCommands.Save(", "ModelCommands.Load(", "ModelCommands.Import(", "ModelCommands.Fit(", "ModelCommands.InstallNetwork(",
         "ModelCommands.ChooseNetwork(", "ModelCommands.Preview(",
         // MC-15: the model steps' plots, the check, the validation, the export and the report — each the verb's own call.
         "ModelStepPlots.Build(", "ConservationCheck.RunFile(", "ModelValidation.RunFile(", "CmodelExport.Export(",
         "ModelCommands.ExportCell(", "ModelValidationReport.Generate(", "ModelReportDisplayPreset.WriteDocument(",
         "ModelReportDisplayPreset.WritePdf(",
         // MC-28: Export Verilog-A, the verb's own call.
         "CmodelVerilogA.Export("];

    [Fact]
    public void TheWindowsFolders_HoldNoFlow_AndCallTheSharedFunctions()
    {
        string root = ModelCliFixture.RepoRoot();
        var code = Folders.SelectMany(f => Directory.EnumerateFiles(Path.Combine(root, f), "*.cs", SearchOption.AllDirectories))
                          .ToDictionary(f => Path.GetRelativePath(root, f), f => ModelCliNoLogicTests.Strip(File.ReadAllText(f)));
        Assert.True(code.Count >= 8, $"only {code.Count} source files in the window's folders");

        foreach (var (file, text) in code)
            foreach (string inner in ModelCliNoLogicTests.FlowInternals.Where(p => !p.StartsWith("File.", StringComparison.Ordinal)))
                Assert.False(text.Contains(inner, StringComparison.Ordinal), $"{file} calls {inner}");

        string all = string.Concat(code.Values);
        foreach (string call in SharedCalls) Assert.Contains(call, all, StringComparison.Ordinal);
    }
}
