using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-1 R-ya1-2/3: the statistical part of the tuning block survives <c>.cnl</c> → <c>.csch</c> →
/// <c>.cnl</c> and <c>.csch</c> → <c>.csch</c> byte for byte, and a schematic with no statistical content
/// writes the bytes it wrote before.</summary>
public sealed class StatisticsSetupRoundTripTests
{
    /// <summary>Every distribution and spread form, a correlation, the settings line, a value corner, a
    /// statistical corner and a use=yield goal — each line as the writer writes it.</summary>
    private static readonly string[] Lines =
    [
        "tune R1.R min=10 Ohm max=200 Ohm opt=1 dist=gauss sd=2%",
        "tune C1.C dist=unif tol=0.1 pF",
        "tune L1.L dist=gauss tol=5% sigmas=3 trunc=3",
        "tune mag(ZL) dist=unif lo=45 Ohm hi=55 Ohm stat=0",
        "tune X1.Nf dist=discrete lo=4 hi=8 by=2",
        "tune R2.R dist=lognorm sd=1 Ohm",
        "tune Wline dist=unif lo=90% hi=110%",
        "goal S21 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge 14 use=yield",
        "goal Match = dB(SP1.S(1,1)) analysis=SP1 le -15 use=opt",
        "correlate R1.R R2.R rho=0.9",
        "statistics trials=500 seed=7 sampling=lhs target=95% confidence=99% autostop=0 nonconverged=warn save=scalars " +
            "process=0 mismatch=0 sigmascale=0.5 parallel=4 analyses=all corners=SS_hot,Worst_S21",
        "corner SS_hot temp=85 Vdd=3.0 V R1.R=47 Ohm",
        "corner Worst_S21 enabled=0 trial=417 seed=7 sampling=lhs trials=500",
    ];

    private static IEnumerable<string> Expected => Lines.Select(l => l.Replace(" autostop=0", ""));

    private static IEnumerable<string> SetupLines(string cnl)
        => cnl.Split('\n').Select(l => l.TrimEnd('\r'))
              .Where(l => l.Split(' ')[0] is "tune" or "preset" or "goal" or "optimize" or "correlate" or "statistics" or "corner");

    private static string Cnl(SchematicEditModel m) => CnlWriter.Write(NetExtractor.Extract(m, "tb").TestBench);

    [Fact]
    public void EveryFormRoundTripsByteStable()
    {
        var (lib0, tb0) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\n" + string.Join('\n', Lines));
        Assert.Empty(tb0.ReadWarnings);
        Assert.Equal(Expected, SetupLines(CnlWriter.Write(tb0, lib0)));

        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        model.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1", "3", 21, SweepKind.Linear, "GHz", "GHz")));
        model.Tuning = tb0.Tuning;

        // .csch → .csch
        string csch1 = SchematicPersistence.Serialize(model, "Flat");
        var (m1, _, _) = SchematicPersistence.Deserialize(csch1);
        Assert.Empty(m1.LoadFindings);
        Assert.Equal(csch1, SchematicPersistence.Serialize(m1, "Flat"));

        // .csch → .cnl → .csch → .cnl
        string cnl1 = Cnl(m1);
        Assert.Equal(Expected, SetupLines(cnl1));
        var (lib, tb) = new CnlReader().Read(cnl1);
        var back = NetlistSchematic.Build(lib, tb).Schematic!;
        string csch2 = SchematicPersistence.Serialize(back, "Flat");
        Assert.Equal(JsonNode.Parse(csch1)!["Tuning"]!.ToJsonString(), JsonNode.Parse(csch2)!["Tuning"]!.ToJsonString());
        Assert.Equal(Expected, SetupLines(Cnl(back)));
    }

    [Fact]
    public void ASchematicWithNoStatisticalContentIsWrittenAsBefore()
    {
        // A tuned and optimized bench committed before the statistical fields existed.
        string path = Path.Combine(RepoRoot(), "examples", "Optimization", "LSectionMatch", "schematic", "LSectionMatch.csch");
        string onDisk = File.ReadAllText(path);
        var (m, view, name) = SchematicPersistence.Deserialize(onDisk, Path.GetDirectoryName(path));
        Assert.NotNull(m.Tuning);

        Assert.Equal(onDisk, SchematicPersistence.Serialize(m, name, view.PanX, view.PanY, view.Zoom));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
