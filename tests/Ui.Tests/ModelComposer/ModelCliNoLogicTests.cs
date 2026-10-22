using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-13 R-mc13-2 (overview D15): neither the <c>model</c> verb nor the window re-implements a flow. A
/// comment-stripped source scan of <c>src/Cli/Model.cs</c> and of the window's folders finds none of the calls a flow is
/// made of — those live in <c>src/Design/Modeling</c>, behind the functions both call — and finds the verb calling them.
/// The <c>AuthoringCliVerbTests</c> pattern.
/// </summary>
public sealed class ModelCliNoLogicTests
{
    /// <summary>What a flow is made of: reading data through the cache, de-embedding, a step's own run, writing the document,
    /// the model or a cache array. A shell that calls one of these is doing a flow's work.</summary>
    internal static readonly string[] FlowInternals =
    [
        "CmcFile.Write(", "CmcFile.ToBytes(", "new CmcCache(", "new IntrinsicExtraction(", "DataImport.Import(", "CmcStamps.",
        "NetworkFit.FitFile(", "NetworkFit.Run(", "CurrentFit.FitFile(", "CurrentFit.Run(", "ChargeFit.FitFile(", "ChargeFit.Run(",
        "ThermalFit.FitFile(", "ThermalFit.Run(", "TrapFit.FitFile(", "TrapFit.Run(", "PassiveStep.FitFile(", "PassiveStep.ExportFile(",
        "PassiveCellExport.", "CmodelExport.Assemble(", "CmodelFile.Write(", "AnnTrainer", "ConservationCheck.LargeSignal(",
        "ModelValidation.Run(", "File.WriteAllBytes(", "File.WriteAllText(", "File.Move(",
    ];

    /// <summary>The functions the window's commands call, each of which the verb must call.</summary>
    private static readonly string[] SharedCalls =
    [
        "ModelCommands.New(", "ModelCommands.Import(", "ModelCommands.Deembed(", "ModelCommands.Fit(", "ModelCommands.Explain(",
        "ModelCommands.ExportCell(", "CmodelExport.Export(", "ModelValidation.RunFile(", "ModelValidationReport.Generate(",
        "ModelValidationReport.Write(", "ConservationCheck.RunFile(", "ConservationCheck.RunDevice(", "CmodelVerilogA.Export(",
    ];

    [Fact]
    public void TheVerb_CallsTheSharedFunctions_AndReimplementsNoFlow()
    {
        string verb = Source(Path.Combine("src", "Cli", "Model.cs"));
        foreach (string call in SharedCalls) Assert.Contains(call, verb, StringComparison.Ordinal);
        foreach (string inner in FlowInternals) Assert.DoesNotContain(inner, verb, StringComparison.Ordinal);

        // Not vacuous: the patterns match the code that does hold the flows.
        string commands = Source(Path.Combine("src", "Design", "Modeling", "ModelCommands.cs"));
        Assert.True(FlowInternals.Count(p => commands.Contains(p, StringComparison.Ordinal)) >= 8,
                    "the flow patterns no longer match ModelCommands; they would prove nothing about the verb");
    }

    /// <summary>The window (MC-14, MC-15) is held to the same rule from its first file.</summary>
    [Fact]
    public void TheWindow_ReimplementsNoFlow()
    {
        string root = ModelCliFixture.RepoRoot();
        foreach (string folder in new[] { Path.Combine("src", "Ui", "ModelComposer"), Path.Combine("src", "Ui", "Views", "ModelComposer") })
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string code = Strip(File.ReadAllText(file));
                foreach (string inner in FlowInternals.Where(p => !p.StartsWith("File.", StringComparison.Ordinal)))
                    Assert.False(code.Contains(inner, StringComparison.Ordinal), $"{Path.GetRelativePath(root, file)} calls {inner}");
            }
        }
    }

    private static string Source(string relative) => Strip(File.ReadAllText(Path.Combine(ModelCliFixture.RepoRoot(), relative)));

    /// <summary>Line and block comments removed, so a scan cannot be satisfied by prose.</summary>
    internal static string Strip(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
}
