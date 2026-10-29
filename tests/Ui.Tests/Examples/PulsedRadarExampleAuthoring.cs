// ================================================================
//  PulsedRadarExampleAuthoring.cs — the Pulsed Radar example (brief ENV-PCW §5-6), BUILT with the functions the window,
//  `circuitrf model` and `circuitrf netlist --to-schematic` call rather than written by hand. Skipped unless
//  CRF_AUTHOR_PULSED_RADAR=1, because it WRITES examples/Pulsed Radar (the README is kept). It is kept so the workspace
//  can be rebuilt after a format change, and so the steps the README describes are the ones that produced the files.
//
//  The steps, in the order a user takes them:
//    New Workspace ▸ the GaN two-shell network as a cell of its own ▸ for radar-gan: New (Active, 8 × 250 µm), the
//    open/short pads, one import per data set, the settings (Lg, Ld, Ls free; the access resistances and inner
//    capacitances typed; Rth and its three-pole ladder typed; drain lag), Fit all, Export ▸ the benches, drawn from their
//    netlists.
//
//  The data is synthetic (SyntheticGan.Radar8x250) and every data set's Note says so. The pulse bench's schematic was laid
//  out by hand after it was drawn: a rebuild keeps it rather than redrawing it (HandLaidOut), and
//  PulsedRadarExampleTests.TheHandLaidOutPulseBench_IsItsNetlist holds it to the netlist here.
// ================================================================

using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ModelComposer;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class PulsedRadarExampleAuthoring(ITestOutputHelper output)
{
    public const string Folder = "Pulsed Radar";
    public const string DeviceDir = "radar-gan";
    public const string Document = "radar_gan_8x250.cmc";
    public const string Model = DeviceDir + "/radar_gan_8x250.cmodel";
    /// <summary>The export's provenance timestamp, fixed so a rebuild writes the same bytes.</summary>
    public const string Created = "2026-10-24T00:00:00Z";

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_PULSED_RADAR") != "1")
                Skip = "re-builds examples/Pulsed Radar only with CRF_AUTHOR_PULSED_RADAR=1";
        }
    }

    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string examples = Path.Combine(PalaceBackendTests.RepoRoot(), "examples");
        string ws = Workspace;
        string readme = Path.Combine(ws, "README.md");
        string? keptReadme = File.Exists(readme) ? File.ReadAllText(readme) : null;
        var kept = KeepHandLaidOut(ws);
        if (Directory.Exists(ws)) Directory.Delete(ws, true);

        WorkspaceCreate.Create(examples, Folder, null);
        if (keptReadme is not null) File.WriteAllText(readme, keptReadme);
        BuildDevice(ws);
        BuildBenches(ws);
        RestoreHandLaidOut(ws, kept);
        BuildTheDisplay();
    }

    /// <summary>The device alone, into the workspace as it stands: its exported model.</summary>
    [AuthorFact]
    public void BuildTheDevice()
    {
        string dir = Path.Combine(Workspace, DeviceDir);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        BuildDevice(Workspace);
    }

    /// <summary>The benches alone, redrawn from their netlists; a hand-laid-out schematic is kept.</summary>
    [AuthorFact]
    public void BuildTheBenches()
    {
        var kept = KeepHandLaidOut(Workspace);
        foreach (string cell in Benches.Select(b => b.Cell))
            if (Directory.Exists(Path.Combine(Workspace, cell))) Directory.Delete(Path.Combine(Workspace, cell), true);
        BuildBenches(Workspace);
        RestoreHandLaidOut(Workspace, kept);
    }

    /// <summary>The benches whose schematic was laid out by hand after it was drawn (owner, 2026-10-28). A rebuild draws
    /// them, so the cell folder is complete, and then puts the hand-laid-out schematic back over the drawing.</summary>
    public static readonly string[] HandLaidOut = ["PulsedPA_pCW"];

    public static string SchematicOf(string ws, string cell) => Path.Combine(ws, cell, "schematic", cell + ".csch");

    private static Dictionary<string, byte[]> KeepHandLaidOut(string ws) =>
        HandLaidOut.Where(c => File.Exists(SchematicOf(ws, c))).ToDictionary(c => c, c => File.ReadAllBytes(SchematicOf(ws, c)));

    private static void RestoreHandLaidOut(string ws, Dictionary<string, byte[]> kept)
    {
        foreach (var (cell, bytes) in kept) File.WriteAllBytes(SchematicOf(ws, cell), bytes);
    }

    // ── radar-gan ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The raw files, the composer document, its fits and its network cell are built in a scratch workspace, and only the
    /// exported model ships: the benches read nothing else, the model carries its own network, and the rest regenerates
    /// from <see cref="SyntheticGan.Radar8x250"/> exactly. The Model Composer example is the one that shows a document
    /// and its data; here they were 1.7 MB of files a user of this example never opens.
    /// </summary>
    private void BuildDevice(string ws)
    {
        var truth = SyntheticGan.Radar8x250;
        string root = Directory.CreateTempSubdirectory("crf-radar-gan-").FullName;
        try
        {
            string scratch = WorkspaceCreate.Create(root, Folder, null).WorkspaceDir;
            string dir = Path.Combine(scratch, DeviceDir);
            SyntheticGan.WriteRaw(dir, truth);
            string cmc = SyntheticGanDocument.Create(dir, truth, Document);
            ModelComposerExampleAuthoring.UseNetworkCell(scratch, cmc);
            SyntheticGanDocument.Configure(cmc, truth: truth);
            foreach (var l in ModelCommands.Deembed(cmc)) output.WriteLine(l.Text);
            foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
            Directory.CreateDirectory(Path.Combine(ws, DeviceDir));
            CmodelExport.Export(cmc, Path.Combine(ws, Model), created: Created);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // ── the benches ───────────────────────────────────────────────────────────

    /// <summary>
    /// The stage both benches share: the 12 mm device (N = 48 of the 2 mm cell) at 3 GHz, 48 V class AB. In: a DC block, a
    /// low-pass L match (shunt C, series L) to about 1.5 + j4.5 Ω, and the stabilisation a stage this size needs: 1 Ω in
    /// series, 10 Ω ∥ 50 pF, 5 Ω + 20 pF to ground at the gate, and 50 Ω in the gate feed. Without them it is unstable from
    /// 0.1 to 8 GHz (K &lt; 1, |S11| &gt; 0 dB near 0.4 GHz) and its gain is set by the regeneration, not the device.
    /// Out: the drain choke from the supply node, which the 0.2 Ω supply and a 470 µF storage capacitor feed (the
    /// supply sag), a 3 Ω + 1 nF damper, and an L match to about 4 + j4 Ω at the drain, the truth's power optimum on a
    /// 16-load scan at 36 dBm (5.5 + j6 Ω is its efficiency optimum, and dissipates half as much). The T pin is the net <c>tj</c>, left unwired: its voltage is the junction's rise above
    /// <c>temp</c>, in °C, which both benches read.
    /// </summary>
    public const string Stage = """
        C:CBI  in  a  C=100 pF
        C:CMI  a  0  C=5.6 pF
        L:LMI  a  g1  L=0.6 nH
        R:RGS  g1  g2  R=1 Ohm
        R:RPAR  g2  g  R=10 Ohm
        C:CPAR  g2  g  C=50 pF
        R:RST  g  gst  R=5 Ohm
        C:CST  gst  0  C=20 pF
        L:LG  g  gr  L=10 nH
        R:RGB  gr  gb  R=50 Ohm
        C:CGB  gb  0  C=100 pF
        Vdc:VG  gb  0  Vdc=VGS V
        ComposedFET:X1  g  d  0  tj  File="radar-gan/radar_gan_8x250.cmodel"  N=48
        L:LD  dsup  d  L=10 nH
        C:CDB  dsup  0  C=100 pF
        R:RDD  dsup  dd  R=3 Ohm
        C:CDD  dd  0  C=1 nF
        C:CSTORE  dsup  0  C=470 uF
        R:RSUP  vs  dsup  R=0.2 Ohm
        IProbe:IDC  VDD  vs
        Vdc:VS  VDD  0  Vdc=VDD V
        L:LMO  d  m  L=0.932 nH
        C:CMO  m  0  C=3.598 pF
        C:CBO  m  out  C=100 pF
        R:RL  out  0  R=50 Ohm
        """;

    /// <summary>The stage under CW, driven from small signal to past saturation: the steady state a pulse never reaches.</summary>
    public const string PulsedPA = """
        Pin = 34
        RFfreq = 3 GHz
        VGS = -2.6
        VDD = 48
        temp = 25
        P1Tone:P1  src  0  Num=1  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg
        IProbe:Iin  src  in
        """ + "\n" + Stage + "\n" + """
        analysis HB1 type=hb Tone="RFfreq" ToneUnit=GHz MaxHarm=5
        analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Start=20 Stop=34 Step=1 Inner=HB1
        measure Pin_deliv_W = real(0.5*HB1.V("src",1)*conj(HB1.I("Iin",1)))
        measure Pout_W = real(0.5*HB1.V("out",1)*conj(HB1.V("out",1)))/50
        measure Pout_dBm = 10*log10(Pout_W*1000)
        measure Gt_dB = Pout_dBm - Pin
        measure PDC_W = real(HB1.V("VDD",0)*HB1.I("IDC",0))
        measure DE = Pout_W/PDC_W*100
        measure PAE = (Pout_W - Pin_deliv_W)/PDC_W*100
        measure Tj_rise = real(HB1.V("tj",0))
        labelednets in g d out VDD tj
        """;

    /// <summary>The same stage on a pulsed-CW source: 100 µs pulses every 1 ms (10 % duty), three whole PRIs, so the supply
    /// has recharged the storage capacitor after the last pulse and DE_pct counts what the pulses drew. The output power is
    /// measured where it is delivered (brief ENV-MEAS-3): <c>Iout</c> in series with the load, its first node <c>out</c> as
    /// before, so the net and every voltage read there keep their meaning and only the load moves to a net of its own.</summary>
    public static readonly string PulsedPApCW = """
        Pin = 34
        RFfreq = 3 GHz
        VGS = -2.6
        VDD = 48
        temp = 25
        MOD_Source:SRC1  in  0  Fc=RFfreq  Pavg=Pin dBm  Z=50 Ohm  Carrier[1]="PCW;PRI=1ms;PW=100us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"
        """ + "\n" + Stage.Replace("R:RL  out  0", "IProbe:Iout  out  load\nR:RL  load  0", StringComparison.Ordinal) + "\n" + """
        analysis ENV1 type=env Stop=3e-3 Accuracy=draft MaxHarm=5
        labelednets in g d dsup out tj
        """;

    /// <summary>Every bench: its cell and its netlist.</summary>
    public static readonly (string Cell, string Netlist)[] Benches = [("PulsedPA", PulsedPA), ("PulsedPA_pCW", PulsedPApCW)];

    private static void BuildBenches(string ws)
    {
        foreach (var (cell, netlist) in Benches)
            ModelComposerExampleAuthoring.Draw(ws, cell, netlist, null, Model);
    }

    // ── the display ───────────────────────────────────────────────────────────

    public const string PulseBench = "PulsedPA_pCW";

    /// <summary>
    /// <c>PulsedPA_pCW.cdd</c>, which Simulate opens: the pulse bench run once by <c>circuitrf env</c>, each plot authored
    /// on that result by <c>circuitrf plot --write-cdd</c>, and the four laid out on one tab — Pout(t) over the run, the
    /// junction's rise over the same time, one pulse top zoomed, and the pulse table. The CLI's <c>-o</c> file names its
    /// cubes bare; Simulate's results file (<c>results/PulsedPA_pCW.npy</c>) groups them under the analysis, so each trace
    /// is pointed at <c>ENV1.&lt;cube&gt;</c>.
    /// </summary>
    [AuthorFact]
    public void BuildTheDisplay()
    {
        string ws = Workspace;
        string sch = Path.Combine(ws, PulseBench, "schematic", PulseBench + ".csch");
        string tmp = Directory.CreateTempSubdirectory("crf-radar-cdd-").FullName;
        try
        {
            string npy = Path.Combine(tmp, PulseBench + ".npy");
            Assert.Equal(0, CliEntry.Run(["env", sch, "-o", npy]));
            var plots = new JsonArray();
            void Plot(string name, double left, double top, params string[] args)
            {
                string cdd = Path.Combine(tmp, name + ".cdd");
                Assert.Equal(0, CliEntry.Run(["plot", npy, "-o", Path.Combine(tmp, name + ".svg"), .. args, "--write-cdd", cdd]));
                var plot = JsonNode.Parse(File.ReadAllText(cdd))!["Tabs"]![0]!["Plots"]![0]!.DeepClone();
                plot["Left"] = left; plot["Top"] = top;
                foreach (var t in plot["Traces"]!.AsArray())
                {
                    t!["SourcePath"] = PulseBench + ".npy";
                    t["CubeName"] = "ENV1." + (string)t["CubeName"]!;
                    // rise and fall are tens of nanoseconds: in engineering notation, not as 0.000 s
                    if (((string)t["CubeName"]!).EndsWith("_s", StringComparison.Ordinal)) t["FormatString"] = "S";
                }
                plots.Add(plot);
            }
            Plot("pout", 0, 0, "--trace", "cube=Pout_t_dBm[\"Iout\",:]", "--title", "Output power over the run",
                 "--ylabel", "Pout (dBm)", "--xlabel", "Time (s)", "--y", "30:52");
            Plot("rise", 660, 0, "--trace", "cube=real(V[:,\"tj\",0])", "--title", "Junction temperature rise",
                 "--ylabel", "Rise above 25 °C (K)", "--xlabel", "Time (s)");
            Plot("top", 0, 420, "--trace", "cube=Pout_t_dBm[\"Iout\",:]", "--title", "Pulse 1, its top",
                 "--ylabel", "Pout (dBm)", "--xlabel", "Time (s)", "--x", "0.000008:0.000114", "--y", "48.7:49.4");
            Plot("table", 660, 420, "--type", "table", "--trace", "cube=PulseTop_dBm[\"Iout\",:]", "--trace", "cube=PulseDroop_dB[\"Iout\",:]",
                 "--trace", "cube=PulsePhaseDroop_deg[\"Iout\",:]", "--trace", "cube=PulseRise_s[\"Iout\",:]",
                 "--trace", "cube=PulseFall_s[\"Iout\",:]", "--title", "Per pulse");
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(tmp, "pout.cdd")))!;
            doc["SelectedDataSource"] = PulseBench + ".npy";
            doc["Tabs"]![0]!["Name"] = "Pulsed CW run";
            doc["Tabs"]![0]!["Plots"] = plots;
            File.WriteAllText(Path.Combine(ws, PulseBench + ".cdd"),
                doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }
}
