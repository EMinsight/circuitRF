using System;
using System.IO;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The <c>src/Ui</c> half of gate 2 of brief-automation-2-schematic-below-the-firewall.md.
///
/// <para><c>tests/Firewall.Tests/SchematicChainBelowTheFirewallTests</c> asserts that the
/// <c>.csch → SchematicEditModel → NetExtractor → CnlWriter</c> chain can be assembled and run with
/// nothing from <c>src/Ui</c> on the path, against a committed golden. This test asserts the SAME
/// golden from the GUI's side, through the GUI's own loader — so the two cannot drift apart without
/// one of them going red, and "the headless netlist and the one a Simulate press produces are the
/// same bytes" is a checked statement rather than an intention (R-aut0-6).</para>
///
/// <para>The three calls below are exactly the three <c>DocRunData.Run</c> makes before it hands the
/// <c>.cnl</c> to <c>SchematicRunService</c> (<c>src/Ui/Diagnostics/Fixtures/DocRunData.cs</c>), with
/// its own TestBench name and header — both of which land in the written bytes. What it adds over the
/// headless test is the loader: this side reads the <c>.csch</c> as an EMBEDDED RESOURCE of
/// <c>CircuitRF.Ui</c>, the headless side reads the authored FILE, so the pair also pins the two to
/// the same content.</para>
/// </summary>
public class SchematicChainMatchesTheHeadlessOneTests
{
    private const string DocSchematicStem = "Example_SParam_LC";
    private const string GoldenRelPath    = "testdata/schematic-chain/Example_SParam_LC.cnl";
    private const string TestBenchName    = "ExampleSParam";
    private const string CnlHeader        = "generated for the user documentation from Example_SParam_LC";

    [Fact]
    public void TheGuisOwnChainWritesTheSameCnlAsTheHeadlessOne()
    {
        var model     = ShippedSchematicTemplates.LoadDocSchematic(DocSchematicStem);
        var extracted = NetExtractor.Extract(model, TestBenchName);
        string cnl    = CnlWriter.Write(extracted.TestBench, extracted.Library, CnlHeader);

        string golden = Path.Combine(FindRepoRoot(), GoldenRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(golden), $"The golden netlist is missing: {golden}");

        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), cnl.Replace("\r\n", "\n"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null,
            "Could not locate the repo root (the folder holding circuitRF.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    /// <summary>
    /// R-aut2-9: the two values <c>src/Ui</c> hands DOWN to the moved code are actually installed in a
    /// running process, not merely written.
    ///
    /// <para>Both are <c>[ModuleInitializer]</c>s (<c>UiWBondDefaultsInstaller</c>,
    /// <c>UiVerilogACacheInstaller</c>) rather than calls from <c>App.Initialize</c>, because
    /// <c>src/Ui</c> has THREE entry points — circuitRF, harmonicaRF and wBond are the same assembly
    /// with a different <c>Main</c> — and an installer wired into one <c>Main</c> is an installer the
    /// other two silently do without. A module initializer runs before any type in the assembly is
    /// touched, on every path, which is why this one test covers all three: what it proves is that
    /// loading <c>CircuitRF.Ui</c> at all is enough.</para>
    ///
    /// <para>The failure this guards is silent by construction. Unset, both hooks fall back to
    /// something reasonable — the shipped wire, and no label cache — so a lost installer costs
    /// a user their setting and their cache with nothing said anywhere.</para>
    /// </summary>
    [Fact]
    public void TheHooksSrcUiInstallsIntoTheMovedCode_AreActuallyInstalled()
    {
        Assert.Equal(WBondDefaults.NewWire, WBondPlacement.NewWireSettings());
        Assert.Equal(AppDataRoot.SubDir("cache"), VerilogAModelIntrospection.CacheDirectory());
    }
}
