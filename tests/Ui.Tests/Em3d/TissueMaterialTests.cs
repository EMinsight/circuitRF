using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-sar-2 — tissue materials: the Cole-Cole model on a material record, the shipped tissue library,
//  and the one frequency a 3D run evaluates them at. One test per claim (§4's gates).
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The shipped Gabriel tissues, through <see cref="MaterialDispersion"/>, against the Gabriel 1996 model evaluated by an
/// INDEPENDENT implementation (a separate Python script, run once on 2026-10-18; the values are typed in here). Paper III
/// prints its parameters, not a table of values at these frequencies, so the gate is four significant figures — what the
/// widely quoted values carry (muscle 55.03 / 0.943 S/m and fat 5.46 / 0.0510 S/m at 900 MHz are the familiar ones). It
/// holds the shipped numbers and the formula together: a mistyped τ or a dropped term moves a row by far more.
/// </summary>
public sealed class ColeColeTests
{
    private static readonly IReadOnlyList<TechMaterial> Tissues = MaterialLibraries.LoadShipped(MaterialLibraries.TissueFileName);

    [Theory]
    [InlineData("Muscle", 64, 72.23, 0.6882)]
    [InlineData("Muscle", 128, 63.49, 0.7192)]
    [InlineData("Muscle", 298, 58.23, 0.7700)]
    [InlineData("Muscle", 900, 55.03, 0.9430)]
    [InlineData("Muscle", 1800, 53.55, 1.341)]
    [InlineData("Fat", 64, 6.506, 0.03528)]
    [InlineData("Fat", 128, 5.922, 0.03687)]
    [InlineData("Fat", 298, 5.635, 0.03954)]
    [InlineData("Fat", 900, 5.462, 0.05104)]
    [InlineData("Fat", 1800, 5.349, 0.07839)]
    [InlineData("Bone (cortical)", 64, 16.68, 0.05953)]
    [InlineData("Bone (cortical)", 128, 14.72, 0.06735)]
    [InlineData("Bone (cortical)", 298, 13.45, 0.08249)]
    [InlineData("Bone (cortical)", 900, 12.45, 0.1433)]
    [InlineData("Bone (cortical)", 1800, 11.78, 0.2752)]
    public void TheShippedTissue_GivesGabrielsValues_ToFourFigures(string tissue, double mhz, double epsr, double sigma)
    {
        var m = Tissues.Single(t => t.Name == tissue);
        var v = MaterialDispersion.At(m, mhz * 1e6, out string? refusal);
        Assert.Null(refusal);
        Assert.InRange(v!.Value.Epsr, epsr * (1 - 1e-3), epsr * (1 + 1e-3));
        Assert.InRange(v.Value.SigmaSm, sigma * (1 - 1e-3), sigma * (1 + 1e-3));
    }
}

public sealed class TissueLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar2-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static IReadOnlyList<TechMaterial> Shipped() => MaterialLibraries.LoadShipped(MaterialLibraries.TissueFileName);

    /// <summary>R-sar2-4: it ships, every record cites its source, and check finds nothing in it — so no record's plain numbers
    /// disagree with its own model (R-sar2-1's rule) and no model is malformed.</summary>
    [Fact]
    public void TheLibraryLoads_EveryRecordCitesItsSource_AndCheckFindsNothing()
    {
        Assert.Contains(MaterialLibraries.TissueFileName, MaterialLibraries.ShippedLibraryNames());
        var all = Shipped();
        Assert.All(all, m => Assert.False(string.IsNullOrWhiteSpace(m.Source), m.Name));
        Assert.Equal(16, all.Count(m => m.ColeCole is not null));
        Assert.Empty(MaterialValidation.Validate(all));
    }

    /// <summary>R-sar2-1: a plain number that drifts more than 1 % from the model at 1 GHz is a warning naming both.</summary>
    [Fact]
    public void APlainNumberThatDisagreesWithItsModel_IsAWarning()
    {
        var muscle = Shipped().Single(m => m.Name == "Muscle");
        muscle.Epsr *= 1.05;
        var p = Assert.Single(MaterialValidation.Validate([muscle]));
        Assert.Equal(TechValidation.Ids.MaterialModelDisagrees, p.Id);
        Assert.Equal(DiagnosticSeverity.Warning, p.Severity);
    }

    /// <summary>§4: the .cmat round-trips, and what a build without the model reads — the plain numbers — is what this build
    /// gives when no frequency is asked (the record with its model keys stripped is the same material to it).</summary>
    [Fact]
    public void TheLibraryRoundTrips_AndWithNoFrequencyAModelIsItsPlainNumbers()
    {
        string once = MaterialLibraryPersistence.Serialize(Shipped());
        Assert.Equal(once, MaterialLibraryPersistence.Serialize(MaterialLibraryPersistence.Deserialize(once)));

        var muscle = Shipped().Single(m => m.Name == "Muscle");
        var node = JsonNode.Parse(MaterialLibraryPersistence.Serialize([muscle]))!;
        node["Materials"]![0]!.AsObject().Remove("ColeCole");
        var plain = MaterialLibraryPersistence.Deserialize(node.ToJsonString())[0];
        Assert.Equal(C3dElaborator.MaterialValues(plain, 20), C3dElaborator.MaterialValues(muscle, 20));
        Assert.Equal((muscle.Epsr!.Value, muscle.Sigma20!.Value), (plain.Epsr!.Value, plain.Sigma20!.Value));
    }

    /// <summary>R-sar2-3: a 3D run evaluates a tissue once, at the sweep's centre, before lowering — the solver gets that εr and
    /// σ with no loss tangent, the tissue needs no Role, and the run's notes say so in one line.</summary>
    [Fact]
    public void A3dRun_EvaluatesATissueOnce_AtTheSweepsCentre_AndSaysSo()
    {
        var muscle = Shipped().Single(m => m.Name == "Muscle");
        var (doc, path, cws) = View(muscle);
        var g = C3dProblemAssembly.Assemble(Setup("100", "496"), doc, path, cws);
        Assert.True(g.Ok, g.Refusal);

        var expected = MaterialDispersion.At(muscle, 298e6, out _)!.Value;
        var got = g.Problem!.Materials.Single(m => m.Name == "Muscle");
        Assert.Equal((expected.Epsr, expected.SigmaSm, 0.0), (got.Epsr, got.SigmaSm, got.TanD));
        Assert.Equal(Em3dRole.Dielectric, g.Problem.Solids.Single().Role);
        Assert.Single(g.Notes, n => n.StartsWith("Frequency-dependent materials are evaluated once, at 298 MHz (the sweep's centre)",
                                                 StringComparison.Ordinal));
    }

    /// <summary>R-sar2-5: a target table is never extrapolated — asked below its first row, the run is refused naming both.</summary>
    [Fact]
    public void ATargetTable_AskedOutsideItsRows_IsRefused()
    {
        var head = Shipped().Single(m => m.Name == "Head tissue-simulating liquid");
        var (doc, path, cws) = View(head);
        var g = C3dProblemAssembly.Assemble(Setup("50", "150"), doc, path, cws);   // centre 100 MHz, below the 150 MHz row
        Assert.False(g.Ok);
        Assert.Contains("is tabulated from 150 MHz to 5.8 GHz, and it is asked for at 100 MHz", g.Refusal);
    }

    private static EmSetup Setup(string startMhz, string stopMhz) => new()
    {
        Name = "S", Solver3D = Em3dSolverId.OpenEms,
        Frequency = new FrequencySpec(startMhz, stopMhz, 5, SweepKind.Linear, "MHz", "MHz"),
    };

    /// <summary>A workspace whose technology defines <paramref name="material"/>, and a 3D view of one 10 mm cube of it.</summary>
    private (C3dDocument Doc, string Path, string Cws) View(TechMaterial material)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology { Name = "tech", Materials = [material] });
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        var doc = new C3dDocument
        {
            Objects = [new C3dBox { Name = "phantom", Material = material.Name, Min = new C3dPoint3(0, 0, 0),
                                    Size = new C3dPoint3(10_000_000, 10_000_000, 10_000_000) }],
        };
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return (doc, path, cws);
    }
}
