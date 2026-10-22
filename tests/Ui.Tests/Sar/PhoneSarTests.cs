// ================================================================
//  PhoneSarTests.cs — the gates of brief-sar-12: the phone SAR workflow.
//
//    PlacementTests    a separation and a tilt against a flat phantom's liquid and a generic head's ear spacer give the
//                      intended nearest distance, to 1 DBU, measured again in the re-elaborated document; a group moves as one
//    BandSweepTests    three bands make three setups, each with its own liquid values; the table has three rows, scaled to
//                      the rated power and duty cycle, and a value above a reference value is named, never called a failure
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

internal static class PhoneSarFixtures
{
    public const long Mm = SarFixtures.Mm;

    public static TechMaterial Liquid(string name)
        => MaterialLibraries.LoadShipped(MaterialLibraries.TissueFileName).Single(m => m.Name == name);

    /// <summary>A workspace whose technology holds copper, the shipped shell and the two shipped liquids.</summary>
    public static (string Path, string Cws) Workspace(string root)
    {
        string ws = Path.Combine(root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        var shipped = MaterialLibraries.LoadShipped(MaterialLibraries.TissueFileName);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                .. shipped.Where(m => m.Name is C3dPhantom.ShellMaterial or "Head tissue-simulating liquid" or "Body tissue-simulating liquid"),
            ],
        });
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Phone", "3d");
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "Phone.c3d"), cws);
    }
}

public sealed class PlacementTests : IDisposable
{
    private const long Mm = PhoneSarFixtures.Mm;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar12-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static C3dElaboration Elaborate(C3dDocument doc, string path, string cws)
    {
        C3dPersistence.SaveToFile(path, doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, cws);
        Assert.Empty(e.Refusals);
        return e;
    }

    /// <summary>The nearest distance from the face, measured on the re-elaborated document's own geometry.</summary>
    private static double Distance(C3dDocument doc, string path, string cws, IReadOnlyList<string> movers, string phantom, string face)
        => C3dDevicePlacement.Measure(doc, Elaborate(doc, path, cws), movers, phantom, face)!.Distance;

    [Theory]
    [InlineData("liquid", 10, 0.0)]
    [InlineData("floor", 7, 25.0)]
    public void ASeparationAndTilt_FromAFlatPhantom_GiveTheNearestDistance_To1Dbu(string face, int mm, double tilt)
    {
        var (path, cws) = PhoneSarFixtures.Workspace(_root);
        var doc = new C3dDocument
        {
            Objects =
            [
                C3dPhantom.New(C3dPhantomShape.FlatBox, "flat", new C3dPoint3(0, 0, 100 * Mm), 1000),
                new C3dBox { Name = "phone", Material = "Copper", Min = new(-30 * Mm, -70 * Mm, -5 * Mm), Size = new(60 * Mm, 140 * Mm, 8 * Mm) },
            ],
        };
        Assert.Null(C3dDevicePlacement.Place(doc, Elaborate(doc, path, cws), new C3dPlacementRequest(["phone"], "flat", face, mm * Mm, tilt, C3dAxis.X)));
        Assert.InRange(Distance(doc, path, cws, ["phone"], "flat", face), mm * Mm - 1, mm * Mm + 1);
        Assert.Equal(tilt == 0 ? 0 : 1, doc.Objects[1].Placement.Rotate.Count);

        // The liquid's face is the floor's plus the shell: the same device, the same separation, is the shell's thickness further away.
        if (face == "liquid")
            Assert.InRange(Distance(doc, path, cws, ["phone"], "flat", "floor"), 8 * Mm - 1, 8 * Mm + 1);
    }

    [Fact]
    public void ADevicesGroup_AgainstTheGenericHeadsEar_MovesAsOne_AndKeepsItsTiltedSeparation()
    {
        var (path, cws) = PhoneSarFixtures.Workspace(_root);
        var head = C3dPhantom.New(C3dPhantomShape.Head, "head", default, 1000);
        var doc = new C3dDocument
        {
            Objects =
            [
                head,
                new C3dBox { Name = "body", Material = "Copper", Group = "phone", Min = new(-35 * Mm, 0, -75 * Mm), Size = new(70 * Mm, 8 * Mm, 150 * Mm) },
                new C3dBox { Name = "antenna", Material = "Copper", Group = "phone", Min = new(-30 * Mm, 2 * Mm, 60 * Mm), Size = new(10 * Mm, 4 * Mm, 12 * Mm) },
            ],
        };
        // The ear-left face is the spacer's flat face: the head's y semi-axis plus the spacer's thickness off its centre.
        var ear = C3dDevicePlacement.Faces(head).Single(f => f.Name == "ear-left");
        Assert.Equal((0d, head.SemiAxes.Y + head.Length, 0d), ear.Anchor);
        Assert.Equal((0d, 1d, 0d), ear.Normal);

        var before = doc.Objects[2].Placement.ToTransform().Apply(new C3dPoint3(0, 0, 0));
        Assert.Null(C3dDevicePlacement.Place(doc, Elaborate(doc, path, cws), new C3dPlacementRequest(["phone"], "head", "ear-left", 0, 15, C3dAxis.Y)));
        Assert.InRange(Distance(doc, path, cws, ["phone"], "head", "ear-left"), -1, 1);

        // One rigid motion: the two parts' origins are the same distance apart as before.
        double Gap(C3dDocument d) => Math.Sqrt(Sq(d.Objects[1].Placement.Origin.X - d.Objects[2].Placement.Origin.X)
                                             + Sq(d.Objects[1].Placement.Origin.Y - d.Objects[2].Placement.Origin.Y)
                                             + Sq(d.Objects[1].Placement.Origin.Z - d.Objects[2].Placement.Origin.Z));
        Assert.InRange(Gap(doc), -1, 1);
        Assert.All(doc.Objects.Skip(1), o => Assert.Equal((C3dAxis.Y, 15.0), (o.Placement.Rotate.Single().Axis, o.Placement.Rotate.Single().Deg)));
        Assert.NotEqual(before, doc.Objects[2].Placement.ToTransform().Apply(new C3dPoint3(0, 0, 0)));
    }

    [Fact]
    public void ThePhantomItself_AndASeparationIntoTheShell_AreRefused()
    {
        var (path, cws) = PhoneSarFixtures.Workspace(_root);
        var doc = new C3dDocument
        {
            Objects =
            [
                C3dPhantom.New(C3dPhantomShape.FlatBox, "flat", default, 1000),
                new C3dBox { Name = "phone", Material = "Copper", Min = new(0, 0, -200 * Mm), Size = new(Mm, Mm, Mm) },
            ],
        };
        var e = Elaborate(doc, path, cws);
        Assert.Contains("is a phantom", C3dDevicePlacement.Place(doc, e, new C3dPlacementRequest(["flat"], "flat", "floor", Mm)));
        Assert.Contains("inside the shell", C3dDevicePlacement.Place(doc, e, new C3dPlacementRequest(["phone"], "flat", "liquid", Mm)));
        Assert.Contains("ear-left", C3dDevicePlacement.FaceOf(doc, "flat", "ear-left").Refusal ?? "has no face 'ear-left'");
    }

    private static double Sq(double v) => v * v;
}

public sealed class BandSweepTests : IDisposable
{
    private const long Mm = PhoneSarFixtures.Mm;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar12b-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static C3dDocument Document()
    {
        var doc = SarFixtures.Dipole();
        doc.Objects.Add(C3dPhantom.New(C3dPhantomShape.FlatBox, "flat", new C3dPoint3(0, 0, 0), 1000));
        // The flat phantom's floor 10 mm in front of the dipole, which lies along z at x = 0: turned so its floor faces −x.
        var flat = (C3dPhantom)doc.Objects[^1];
        flat.Size = new C3dPoint3(100 * Mm, 80 * Mm, 60 * Mm);
        flat.Placement = new C3dPlacement { Origin = new C3dPoint3(42 * Mm, 0, 0), Rotate = [new C3dRotation { Axis = C3dAxis.Y, Deg = 90 }] };
        var template = SarFixtures.Setup();
        template.Name = "Template";
        template.Frequency = new FrequencySpec("0.8", "1.0", 5, SweepKind.Linear, "GHz", "GHz");
        template.Sar = new CemSar { FrequenciesGHz = [0.9], MassesGrams = [10] };
        doc.Setups = [EmSetupPersistence.ToEmbedded(template)];
        doc.BandSweeps = [new C3dBandSweep { Name = "Bands", Template = "Template", BandsGHz = [0.9, 1.9, 2.45], DrivePowerW = 0.5, DutyCycle = 0.5 }];
        return doc;
    }

    [Fact]
    public void ThreeBands_MakeThreeSetups_EachWithItsOwnLiquidValues()
    {
        var (path, cws) = PhoneSarFixtures.Workspace(_root);
        var doc = Document();
        var (names, refusal) = C3dBandSweeps.Create(doc, doc.BandSweeps![0]);
        Assert.Null(refusal);
        Assert.Equal(new[] { "Bands 0.9 GHz", "Bands 1.9 GHz", "Bands 2.45 GHz" }, names);
        C3dPersistence.SaveToFile(path, doc);

        var liquid = PhoneSarFixtures.Liquid("Body tissue-simulating liquid");
        var seen = new List<double>();
        foreach (var (name, ghz) in names.Zip(new[] { 0.9, 1.9, 2.45 }))
        {
            var (setup, why) = C3dSetups.Select(doc, name);
            Assert.Null(why);
            Assert.Equal(new[] { ghz }, setup!.Sar!.FrequenciesGHz!);
            Assert.Equal(new[] { 1.0, 10.0 }, setup.Sar.MassesGrams!);
            // The template's 0.8–1.0 GHz about its 0.9 GHz SAR frequency, scaled to the band.
            var f = setup.Frequency.Expand();
            Assert.Equal(0.8 * ghz / 0.9 * 1e9, f.Min(), 1e-3);
            Assert.Equal(1.0 * ghz / 0.9 * 1e9, f.Max(), 1e-3);

            var g = C3dProblemAssembly.Assemble(C3dSetups.ForRun(setup, path), doc, path, cws);
            Assert.True(g.Ok, g.Refusal);
            var want = MaterialDispersion.At(liquid, ghz * 1e9, out _)!.Value;
            var got = g.Problem!.Materials.Single(m => m.Name == liquid.Name);
            Assert.Equal((want.Epsr, want.SigmaSm), (got.Epsr, got.SigmaSm));
            seen.Add(got.SigmaSm);
        }
        Assert.Equal(3, seen.Distinct().Count());

        // Creating again replaces the three setups rather than adding three more.
        Assert.Null(C3dBandSweeps.Create(doc, doc.BandSweeps[0]).Refusal);
        Assert.Equal(4, C3dSetups.Read(doc).Count);
    }

    [Fact]
    public void TheTable_HasOneRowPerBand_AtTheRatedPowerAndDutyCycle_AndNamesAReferenceValueItIsAbove()
    {
        var (path, _) = PhoneSarFixtures.Workspace(_root);
        var doc = Document();
        var sweep = doc.BandSweeps![0];
        sweep.Targets = [new C3dSarTarget { FrequencyGHz = 1.9, MassGrams = 1, PerWattWkg = 8 }];
        string results = Path.Combine(_root, "results");
        var (setups, _) = C3dBandSweeps.Make(doc, sweep);
        foreach (var (setup, perWatt1g) in setups.Zip(new[] { 2.0, 10.0, 4.0 }))
        {
            string dir = Path.Combine(Em3dRunService.RunDirectory(results, C3dSetups.ForRun(setup, path), Em3dSolverId.OpenEms), "p1");
            Directory.CreateDirectory(dir);
            double pacc = 2e-26;
            SarVolumeFiles.WriteRecord(Path.Combine(dir, SarVolumeFiles.RecordFile(0)), new SarRunRecord
            {
                Port = 1, FrequencyHz = setup.Sar!.FrequenciesGHz![0] * 1e9, Index = 1, AcceptedPowerW = pacc,
                Peaks =
                [
                    new SarPeakRecord { Quantity = "SAR1g", Value = perWatt1g * pacc, CentreM = [0.04, 0, 0.001], Object = "flat/liquid" },
                    new SarPeakRecord { Quantity = "SAR10g", Value = perWatt1g / 2 * pacc, CentreM = [0.045, 0, 0], Object = "flat/liquid" },
                ],
            });
        }
        var rows = C3dBandSweeps.Table(doc, sweep, path, results);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Missing));
        // 0.5 W accepted and a 0.5 duty cycle: a quarter of the per-watt value.
        Assert.Equal(10.0 * 0.25, rows[1].Values.Single(v => v.MassGrams == 1).AtRatedWkg, 1e-12);
        Assert.Equal(0.25, rows[1].Values.Single(v => v.MassGrams == 1).FromTarget!.Value, 1e-12);
        // 2.5 W/kg over 1 g is above the 1.6 W/kg reference value; 0.5 W/kg is above none; 10 g's 1.25 W/kg is below 2.0.
        Assert.Equal("1.6 W/kg over 1 g (national regulator)", rows[1].Values.Single(v => v.MassGrams == 1).Above.Single().Name);
        Assert.Empty(rows[0].Values.SelectMany(v => v.Above));
        Assert.Empty(rows[1].Values.Single(v => v.MassGrams == 10).Above);

        var text = C3dBandSweeps.TableText(sweep, rows);
        Assert.Contains(text, l => l.Contains("above the reference value 1.6 W/kg over 1 g", StringComparison.Ordinal));
        Assert.DoesNotContain(text, l => l.Contains("fail", StringComparison.OrdinalIgnoreCase) && !l.Contains("not a pass or fail", StringComparison.Ordinal));

        // A band not run yet is a row that says so.
        Directory.Delete(Path.Combine(results), true);
        Assert.All(C3dBandSweeps.Table(doc, sweep, path, results), r => Assert.Equal("not run yet", r.Missing));
    }
}
