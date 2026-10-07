// ================================================================
//  CnlTechnologyBindingTests.cs — a hand-written .cnl's microstrip takes the workspace technology.
//
//  Found by an agent driving circuitRF through MCP alone: in a GaAs workspace, a .cnl line
//  `MLIN … SignalLayer="Metal1"` simulated on 1.6 mm of εr 4.4, and `check` reported nothing. The
//  three claims below are the binding's whole contract: the technology is applied, a line that states
//  its own substrate is left alone, and a layer named with no technology to resolve it is refused.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests;

public sealed class CnlTechnologyBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cnlbind-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const string Line = "MLIN:T1 a b W=30um L=500um SignalLayer=\"Metal1\"";

    private string Write(string dir, string text)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "t.cnl");
        File.WriteAllText(path, "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\n" + text + "\n");
        return path;
    }

    private string GaAsWorkspace()
    {
        Directory.CreateDirectory(_root);
        return WorkspaceCreate.Create(_root, "ws", "mmic-GaAs_2LM_100um").WorkspaceDir;
    }

    private static Instance Mlin(TestBench tb) => tb.Instances.Single(i => i.Reference == "MLIN");

    private static string? Param(Instance i, string name) => i.Overrides.LastOrDefault(o => o.Name == name)?.Expression;

    /// <summary>The substrate is the one the SCHEMATIC extractor would stamp for the same layer —
    /// the same resolver, reached from the same folder — and the layer choice never reaches the
    /// engine. A note says where the numbers came from.</summary>
    [Fact]
    public void InsideATechnologyWorkspace_TheLineTakesThatTechnologysSubstrate()
    {
        string ws   = GaAsWorkspace();
        string path = Write(Path.Combine(ws, "cell"), Line);

        var (_, tb) = CnlTechnologyBinding.ReadFile(path);
        var mlin = Mlin(tb);

        var expected = MicrostripSubstrateInjection.BuildOverrides(
            MicrostripSubstrateInjection.ResolveWorkspaceTechnology(Path.GetDirectoryName(path)), out _, "Metal1");
        Assert.NotEmpty(expected);
        foreach (var e in expected) Assert.Equal(e.Expression, Param(mlin, e.Name));

        Assert.Null(Param(mlin, "SignalLayer"));
        Assert.NotEqual(1.6e-3, double.Parse(Param(mlin, "H")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(tb.ReadNotes, n => n.Contains("MLIN:T1") && n.Contains("mmic-GaAs_2LM_100um"));
    }

    /// <summary>A line that states its whole substrate — which is what a schematic's extraction
    /// writes — keeps it, even inside a technology.</summary>
    [Fact]
    public void ALineThatStatesItsSubstrate_KeepsIt()
    {
        string ws   = GaAsWorkspace();
        string path = Write(Path.Combine(ws, "cell"),
            "MLIN:T1 a b W=30um L=500um H=1.6mm T=35um Er=4.4 Sigma=5.8e7 TanD=0.02");

        var (_, tb)    = CnlTechnologyBinding.ReadFile(path);
        var (_, plain) = CnlReader.ReadFile(path);

        Assert.Equal(Mlin(plain).Overrides.Select(o => (o.Name, o.Expression)),
                     Mlin(tb).Overrides.Select(o => (o.Name, o.Expression)));
        Assert.Empty(tb.ReadNotes);
    }

    /// <summary>Naming a layer asks for a stackup. With no technology to resolve it, the run is
    /// refused rather than given the standalone default, which is the wrong answer that started
    /// this.</summary>
    [Fact]
    public void NamingALayer_WithNoTechnology_IsRefused()
    {
        string path = Write(Path.Combine(_root, "loose"), Line);

        var ex = Assert.Throws<InvalidOperationException>(() => CnlTechnologyBinding.ReadFile(path));
        Assert.Contains("SignalLayer 'Metal1'", ex.Message);
        Assert.Contains("H, T, Er, Sigma and TanD", ex.Message);
    }
}
