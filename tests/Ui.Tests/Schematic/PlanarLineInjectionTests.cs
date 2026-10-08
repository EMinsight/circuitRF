// brief-artsch-1 R-as1-3: CPWG and SLIN take their substrate from the technology as MLIN does. One test per
// claim, on a synthetic four-conductor board — Top / Core 4.2 / Gnd2 / Prepreg 4.2 / Sig3 / Core 3.5 / Gnd4:
// a CPWG on the top layer binds the plane below it; a SLIN on the inner layer binds both planes and, the
// dielectric between them being 4.2 over 3.5, takes the thickness-weighted εr and says so; a SLIN on a layer
// with one plane is refused — at binding, and again when a netlist naming it is read to be run.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class PlanarLineInjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-planar-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;   // DBU per micron

    internal static Technology FourLayer(double core2Er = 3.5)
    {
        var tech = new Technology
        {
            Name = "Four",
            DefaultDisplayUnit = LayoutUnit.Mil,
            Layers =
            [
                new LayerDef { Key = new LayerKey(1, 0), Name = "TopCu", Color = new CircuitRF.Design.Theming.Rgba(200, 100, 50), ZOrder = 1 },
                new LayerDef { Key = new LayerKey(2, 0), Name = "Gnd2Cu", Color = new CircuitRF.Design.Theming.Rgba(50, 100, 200), ZOrder = 2 },
                new LayerDef { Key = new LayerKey(3, 0), Name = "Sig3Cu", Color = new CircuitRF.Design.Theming.Rgba(50, 200, 100), ZOrder = 3 },
                new LayerDef { Key = new LayerKey(4, 0), Name = "Gnd4Cu", Color = new CircuitRF.Design.Theming.Rgba(100, 50, 200), ZOrder = 4 },
            ],
        };
        void Cu(string name, int key, bool ground) => tech.Stackup.Layers.Add(new StackupLayer
            { Kind = StackupKind.Conductor, Name = name, ThicknessDbu = 35 * Um, SigmaSm = 5.8e7, IsGroundReference = ground, DrawingLayers = [new LayerKey(key, 0)] });
        void Diel(string name, long um, double er, double tanD) => tech.Stackup.Layers.Add(new StackupLayer
            { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = um * Um, Epsr = er, TanD = tanD });

        Cu("Top", 1, false);
        Diel("Core1", 200, 4.2, 0.02);
        Cu("Gnd2", 2, true);
        Diel("Prepreg", 150, 4.2, 0.02);
        Cu("Sig3", 3, false);
        Diel("Core2", 250, core2Er, 0.004);
        Cu("Gnd4", 4, true);
        return tech;
    }

    private static double Value(PlanarLineSubstrateInjection.Binding b, string name)
        => double.Parse(b.Overrides.Single(o => o.Name == name).Expression, CultureInfo.InvariantCulture);

    [Fact]
    public void Cpwg_OnTheTopLayer_BindsThePlaneBelowIt()
    {
        var b = PlanarLineSubstrateInjection.Build(FourLayer(), SymbolKind.Cpwg, "Top", null);

        Assert.Null(b.Refusal);
        Assert.Equal(200e-6, Value(b, "H"), 12);
        Assert.Equal(35e-6, Value(b, "T"), 12);
        Assert.Equal(4.2, Value(b, "Er"), 12);
        Assert.Equal(0.02, Value(b, "TanD"), 12);
        Assert.Equal(5.8e7, Value(b, "Sigma"));
        Assert.Empty(b.Warnings);
    }

    [Fact]
    public void Slin_OnTheInnerLayer_BindsBothPlanes_AndWarnsOnTheMixedDielectric()
    {
        var b = PlanarLineSubstrateInjection.Build(FourLayer(), SymbolKind.Slin, null, null);   // the default layer

        Assert.Null(b.Refusal);
        Assert.Equal(150e-6, Value(b, "H1"), 12);
        Assert.Equal(250e-6, Value(b, "H2"), 12);
        Assert.Equal((4.2 * 150 + 3.5 * 250) / 400, Value(b, "Er"), 12);
        Assert.Equal((0.02 * 150 + 0.004 * 250) / 400, Value(b, "TanD"), 12);
        var warning = Assert.Single(b.Warnings);
        Assert.Contains("Prepreg 4.2", warning);
        Assert.Contains("Core2 3.5", warning);
        Assert.Contains("3.763", warning);   // the weighted εr
        Assert.Contains("19 %", warning);    // (4.2 − 3.5) / 3.7625
    }

    [Fact]
    public void Slin_OnALayerWithOnePlane_IsRefused_AndSoIsTheNetlistNamingIt()
    {
        var tech = FourLayer();
        var b = PlanarLineSubstrateInjection.Build(tech, SymbolKind.Slin, "Top", null);
        Assert.Empty(b.Overrides);
        Assert.NotNull(b.Refusal);
        Assert.Contains("no ground-designated plane above it", b.Refusal);
        Assert.Contains("'Gnd2'", b.Refusal);

        // The run's own reading of a netlist refuses the same line rather than simulating it on the fallback.
        Directory.CreateDirectory(_root);
        string ws = WorkspaceCreate.Create(_root, "ws", null).WorkspaceDir;
        string techPath = Path.Combine(ws, "four.ctech");
        TechPersistence.SaveToFile(techPath, tech);
        string cwsPath = Path.Combine(ws, ".cws");
        var cws = WorkspacePersistence.LoadFromFile(cwsPath);
        cws.DefaultTechRef = "four.ctech";
        WorkspacePersistence.SaveToFile(cwsPath, cws);

        string cnl = Path.Combine(ws, "line.cnl");
        File.WriteAllText(cnl, "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\nSLIN:S1 a b W=0.2mm L=5mm SignalLayer=\"Top\"\n");
        var ex = Assert.Throws<InvalidOperationException>(() => CnlTechnologyBinding.ReadFile(cnl));
        Assert.Contains("SLIN:S1 is refused", ex.Message);
    }
}
