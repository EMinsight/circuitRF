// ================================================================
//  AppearanceTests.cs — brief-em3d-105: an appearance on a material and on a .c3d object or instance, resolved in one place,
//  drawn through the scene's slot table, and never a solver input.
//
//    Gate 1  round trip: a .cmat with every appearance key, a .c3d with object and instance overrides (Like included).
//    Gate 2  validation: each range refusal, a Like naming nothing (warning), a Like cycle (refusal); a .c3d object's too.
//    Gate 3  not a solver input: the equality comparison ignores it, SerializeForRun drops it, an old kept document holding
//            one normalises equal.
//    Gate 4  the resolver's order: every precedence step and every role default, with provenance; εr and σ move nothing;
//            an elaborated nested instance arrives innermost first.
//    Gate 5  slots: equal appearances share one, the 257th distinct one falls back and is counted.
//    Gate 6  staleness — in RunInputsTests.
//    Gate 7  the library: Copper's base colour in linear light, its Color unchanged.
//  Pixels were not seen: these read the model, the resolver's answer and the scene's bytes.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class AppearanceTests : IDisposable
{
    private const long Um = 1000;
    private const double Mm = 1e-3;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-105-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── Gate 1 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_EveryKey_RoundTrips_InACmatAndInAC3d()
    {
        const string cmat = """
            { "FormatVersion": 1, "Materials": [ { "Name": "Glass", "Epsr": 3.8, "Appearance": { "BaseColor": "#F0F4F8",
              "Metallic": 0, "Roughness": 0.05, "Transmission": 0.9, "Ior": 1.46, "Clearcoat": 0.5, "ClearcoatRoughness": 0.1,
              "AttenuationColor": "#D9861C", "AttenuationDistance": 0.002, "Like": "Quartz", "FutureKey": 7 } } ] }
            """;
        string once = MaterialLibraryPersistence.Serialize(MaterialLibraryPersistence.Deserialize(cmat));
        Assert.Equal(once, MaterialLibraryPersistence.Serialize(MaterialLibraryPersistence.Deserialize(once)));
        foreach (string key in TechAppearance.Keys.Append("FutureKey")) Assert.Contains($"\"{key}\"", once);

        var doc = new C3dDocument
        {
            Objects = [Box("trace", 0, new TechAppearance { Like = "Gold", Roughness = 0.1 })],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../die", Appearance = new TechAppearance { Clearcoat = 0.4 } }],
        };
        string text = C3dPersistence.Serialize(doc);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
        Assert.Contains("\"Like\": \"Gold\"", text);
        Assert.Contains("\"Clearcoat\": 0.4", text);
        Assert.DoesNotContain("Appearance", C3dPersistence.Serialize(new C3dDocument { Objects = [Box("plain", 0)] }));
    }

    // ── Gate 2 ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{ "Metallic": 1.5 }""", "tech.material.appearance-invalid", "Metallic 1.5 is outside 0 to 1")]
    [InlineData("""{ "Roughness": -0.1 }""", "tech.material.appearance-invalid", "Roughness -0.1 is outside 0 to 1")]
    [InlineData("""{ "Transmission": 2 }""", "tech.material.appearance-invalid", "Transmission 2 is outside 0 to 1")]
    [InlineData("""{ "Ior": 0.9 }""", "tech.material.appearance-invalid", "Ior 0.9 is outside 1 to 3")]
    [InlineData("""{ "Clearcoat": 1.01 }""", "tech.material.appearance-invalid", "Clearcoat 1.01 is outside 0 to 1")]
    [InlineData("""{ "ClearcoatRoughness": 3 }""", "tech.material.appearance-invalid", "ClearcoatRoughness 3 is outside 0 to 1")]
    [InlineData("""{ "AttenuationDistance": 0 }""", "tech.material.appearance-invalid", "AttenuationDistance 0 is not a positive number of metres")]
    [InlineData("""{ "BaseColor": "gold" }""", "tech.material.appearance-invalid", "BaseColor \"gold\" is not #rrggbb")]
    [InlineData("""{ "AttenuationColor": "#12345" }""", "tech.material.appearance-invalid", "AttenuationColor \"#12345\" is not #rrggbb")]
    [InlineData("""{ "Like": "Unobtainium" }""", "tech.material.appearance-like-unknown", "Unobtainium")]
    public void Gate2_EachFault_IsReportedOnce_NamingTheMaterialAndKey(string appearance, string id, string phrase)
    {
        var m = MaterialLibraryPersistence.Deserialize($$"""{ "FormatVersion": 1, "Materials": [ { "Name": "Gold", "Sigma20": 4.1e7, "Appearance": {{appearance}} } ] }""");
        var p = Assert.Single(MaterialValidation.Validate(m));
        Assert.Equal(id, p.Id);
        Assert.Equal(id.EndsWith("like-unknown") ? CircuitRF.Diagnostics.DiagnosticSeverity.Warning : CircuitRF.Diagnostics.DiagnosticSeverity.Error, p.Severity);
        Assert.Contains("\"Gold\"", p.Message);
        Assert.Contains(phrase, p.Message);
    }

    [Fact]
    public void Gate2_ALikeCycle_IsOneRefusalNamingTheChain_AndAnObjectIsHeldToTheSameRule()
    {
        var cycle = new List<TechMaterial>
        {
            new() { Name = "A", Appearance = new TechAppearance { Like = "B" } },
            new() { Name = "B", Appearance = new TechAppearance { Like = "C" } },
            new() { Name = "C", Appearance = new TechAppearance { Like = "A" } },
            new() { Name = "D", Appearance = new TechAppearance { Like = "A" } },     // leads into the cycle, is not in it
        };
        var p = Assert.Single(MaterialValidation.Validate(cycle));
        Assert.Equal(TechValidation.Ids.AppearanceLikeCycle, p.Id);
        Assert.Contains("\"A\" → \"B\" → \"C\" → \"A\"", p.Message);
        // A Like into another library of the technology is in scope there, not in the file alone.
        var own = new List<TechMaterial> { new() { Name = "Plated", Appearance = new TechAppearance { Like = "Gold" } } };
        Assert.Single(MaterialValidation.Validate(own));
        Assert.Empty(MaterialValidation.Validate(own, [.. own, new TechMaterial { Name = "Gold" }]));

        var doc = new C3dDocument
        {
            Objects = [Box("trace", 0, new TechAppearance { Roughness = 4, Like = "Nothing" }),
                       new C3dPolyline { Name = "line", Appearance = new TechAppearance { Metallic = 1 } }],
        };
        var found = C3dValidation.Validate(doc, name => name == "Copper");
        Assert.Contains(found, d => d.Id == "c3d.appearance.invalid" && d.Render().Contains("Roughness 4 is outside 0 to 1"));
        Assert.Contains(found, d => d.Id == "c3d.appearance.like-unknown");
        Assert.Contains(found, d => d.Id == "c3d.appearance.polyline");
    }

    // ── Gate 3 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AppearanceIsNoSolverInput()
    {
        var plain = new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, ThermalK = 400 };
        var shiny = new TechMaterial { Name = "Copper", Sigma20 = 5.8e7, ThermalK = 400, Appearance = new TechAppearance { Roughness = 0.01, Like = "Gold" } };
        Assert.True(MaterialLibraries.SameValues(plain, shiny));
        Assert.Empty(MaterialLibraries.Conflicts("t", [plain], [("lib.cmat", [shiny])]));

        var doc = new C3dDocument { Objects = [Box("trace", 0)], Instances = [new C3dInstance { Name = "U1", CellRef = "../../die" }] };
        string forRun = C3dPersistence.SerializeForRun(doc);
        doc.Objects[0].Appearance = new TechAppearance { Like = "Gold" };
        doc.Instances[0].Appearance = new TechAppearance { Roughness = 0.2 };
        Assert.Equal(forRun, C3dPersistence.SerializeForRun(doc));
        Assert.Equal("Gold", doc.Objects[0].Appearance!.Like);                 // restored after the run's spelling

        // A run kept before Appearance was classified as display holds it: it normalises equal, so it is not stale.
        Assert.False(C3dRunDocument.IsStale(C3dPersistence.Serialize(doc), doc));
    }

    // ── Gate 4 ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One request through every step: the object and its Like, two instances (innermost first), the material from a
    /// library and the material's own Like, and a role default.</summary>
    [Theory]
    [InlineData("Roughness", "0.1", "object")]
    [InlineData("BaseColor", "#FFE39D", "Like 'Gold'")]                                   // the object's Like
    [InlineData("Clearcoat", "0.4", "instance 'U1/U3'")]                                  // innermost wins
    [InlineData("ClearcoatRoughness", "0.3", "instance 'U1'")]
    [InlineData("Transmission", "0.2", "instance 'U1'")]
    [InlineData("Metallic", "1", "material 'Copper' (generic-materials.cmat)")]           // Gold states none
    [InlineData("Ior", "1.46", "Like 'Glass'")]                                           // the material's Like
    [InlineData("AttenuationColor", "#D9861C", "Like 'Glass'")]
    [InlineData("AttenuationDistance", "inf", AppearanceResolver.RoleDefault)]
    public void Gate4_EachField_TakesTheFirstStatementInOrder(string field, string value, string provenance)
    {
        var a = AppearanceResolver.Resolve(Composite(Library()));
        Assert.Equal(provenance, a.Provenance[field]);
        var v = a.Values;
        switch (field)
        {
            case "BaseColor": Assert.Equal(Srgb(value), v.BaseColor); break;
            case "AttenuationColor": Assert.Equal(Srgb(value), v.AttenuationColor); break;
            default:
                double got = field switch
                {
                    "Roughness" => v.Roughness, "Clearcoat" => v.Clearcoat, "ClearcoatRoughness" => v.ClearcoatRoughness,
                    "Transmission" => v.Transmission, "Metallic" => v.Metallic, "Ior" => v.Ior, _ => v.AttenuationDistance,
                };
                Assert.Equal(value == "inf" ? double.PositiveInfinity : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), got);
                break;
        }
    }

    [Theory]
    [InlineData(AppearanceRole.Conductor, Em3dRole.Conductor, 1, 0.30, 1.5)]
    [InlineData(AppearanceRole.Via, Em3dRole.Conductor, 1, 0.25, 1.5)]
    [InlineData(AppearanceRole.Wire, Em3dRole.Conductor, 1, 0.20, 1.5)]
    [InlineData(AppearanceRole.Dielectric, Em3dRole.Dielectric, 0, 0.50, 1.5)]
    [InlineData(AppearanceRole.Body, Em3dRole.Conductor, 1, 0.30, 1.5)]
    [InlineData(AppearanceRole.Body, Em3dRole.Dielectric, 0, 0.50, 1.5)]
    [InlineData(AppearanceRole.Sheet, Em3dRole.Conductor, 1, 0.30, 1.5)]
    public void Gate4_EachRole_HasItsDefault_AndBaseColorFallsToColourThenPalette(AppearanceRole role, Em3dRole materialRole,
                                                                                 double metallic, double roughness, double ior)
    {
        var tech = new Technology { Name = "t", Materials = [new TechMaterial { Name = "Tinted", Color = "#336699" }, new TechMaterial { Name = "Bare" }] };
        var tinted = AppearanceResolver.Resolve(new AppearanceRequest(tech, "Tinted", role, materialRole, (10, 20, 30)));
        Assert.Equal((metallic, roughness, ior, 0.0, 0.0), (tinted.Values.Metallic, tinted.Values.Roughness, tinted.Values.Ior,
                                                            tinted.Values.Transmission, tinted.Values.Clearcoat));
        Assert.Equal(AppearanceColour.FromSrgb(0x33, 0x66, 0x99), tinted.Values.BaseColor);
        Assert.Equal("material 'Tinted' (technology 't') Color", tinted.Provenance["BaseColor"]);
        Assert.Equal(AppearanceResolver.RoleDefault, tinted.Provenance["Roughness"]);

        var bare = AppearanceResolver.Resolve(new AppearanceRequest(tech, "Bare", role, materialRole, (10, 20, 30)));
        Assert.Equal(AppearanceColour.FromSrgb(10, 20, 30), bare.Values.BaseColor);
        Assert.StartsWith(AppearanceResolver.RoleDefault, bare.Provenance["BaseColor"]);
    }

    [Fact]
    public void Gate4_NoPhysicalValueMovesALook_AndAnElaboratedInstanceArrivesInnermostFirst()
    {
        var before = AppearanceResolver.Resolve(Composite(Library())).Values;
        var tech = Library();
        var copper = tech.FindMaterial("Copper")!;
        (copper.Epsr, copper.Sigma20, copper.TanD, copper.ThermalK) = (9.8, 1, 0.5, 3);
        Assert.Equal(before, AppearanceResolver.Resolve(Composite(tech)).Values);

        string top = NestedWorkspace(out string ws);
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, Path.Combine(ws, ".cws"));
        var chip = e.Provenance["U1/U3/chip"];
        Assert.Equal(0.5, chip.Appearance!.Metallic);
        Assert.Equal(["U1/U3", "U1"], chip.InstanceAppearances.Select(i => i.Instance));
        var look = AppearanceResolver.Resolve(new AppearanceRequest(e.Technology, "Copper", AppearanceRole.Conductor, Em3dRole.Conductor,
                                                                    (150, 150, 155), AppearanceOverride.Of(e.Provenance)("U1/U3/chip"))).Values;
        Assert.Equal((0.5, 0.7, 0.9), (look.Metallic, look.Roughness, look.Clearcoat));
        // The instance applies to every part inside it: the mid cell's own frame takes U1's roughness.
        Assert.Equal(["U1"], e.Provenance["U1/frame"].InstanceAppearances.Select(i => i.Instance));
    }

    // ── Gate 5 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_EqualAppearancesShareASlot_AndThe257thFallsBackAndIsCounted()
    {
        // 'twin' and 'pair' state the same; with 'base' at its role default that is three objects in two slots.
        var small = Scene3DBuilder.Build(Problem(Solid("base", 0), Solid("twin", 1), Solid("pair", 2)), 1, options: new Scene3DBuildOptions(
            DrawAirBox: false, Appearance: n => n == "base" ? null : Override(new TechAppearance { Roughness = 0.05 })));
        Assert.Equal(2, small.Appearances.Length);
        var twin = small.Objects.Single(o => o.Name == "twin");
        Assert.Equal(twin.AppearanceSlot, small.Objects.Single(o => o.Name == "pair").AppearanceSlot);
        Assert.NotEqual(twin.AppearanceSlot, small.Objects.Single(o => o.Name == "base").AppearanceSlot);
        Assert.All(small.ShadeVertices.Skip(twin.FirstVertex).Take(twin.VertexCount), v => Assert.Equal((uint)twin.AppearanceSlot, v.Slot));
        Assert.Equal(0, small.AppearanceFallbacks);

        // 257 distinct: the role default (the first solid's) and 256 roughnesses that are not it.
        var solids = Enumerable.Range(0, 257).Select(k => Solid("s" + k, k)).ToArray();
        var full = Scene3DBuilder.Build(Problem(solids), 1, options: new Scene3DBuildOptions(DrawAirBox: false,
            Appearance: n => n == "s0" ? null : Override(new TechAppearance { Roughness = int.Parse(n[1..]) / 1000.0 })));
        Assert.Equal(Scene3DBuilder.AppearanceSlots, full.Appearances.Length);
        Assert.Equal(1, full.AppearanceFallbacks);
        int defaultSlot = full.Objects.Single(o => o.Name == "s0").AppearanceSlot;
        var fell = Assert.Single(full.Objects.Where(o => o.Name.StartsWith('s') && o.AppearanceSlot == defaultSlot && o.Name != "s0"));
        Assert.Equal(AppearanceResolver.ConductorRoughness, full.Appearances[fell.AppearanceSlot].Roughness);
    }

    /// <summary>R-em3d105-7c — explain, as a process, prints each field with the statement that decided it.</summary>
    [Fact]
    public void Explain_Object_PrintsEachFieldAndItsProvenance()
    {
        string top = NestedWorkspace(out _);
        var (exit, stdout, stderr) = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), [],
                                                                            "explain", top, "--object", "U1/U3/chip");
        Assert.True(exit == 0, stderr);
        foreach (string expected in new[] { "appearance U1/U3/chip Roughness", "instance 'U1/U3'", "instance 'U1'", "object" })
            Assert.True(stdout.Contains(expected, StringComparison.Ordinal), $"'{expected}' is not in:\n{stdout}");
        var missing = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), [],
                                                             "explain", top, "--object", "nothing");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("is no object of", missing.StdOut + missing.StdErr);
    }

    // ── Gate 7 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_TheLibrarysCopper_IsATrueReflectance_AndItsColourIsUnchanged()
    {
        var generic = MaterialLibraries.LoadGeneric();
        var tech = new Technology { Name = "t", LibraryMaterials = [.. generic.Select(m => new LibraryMaterial(m, MaterialLibraries.ShippedPrefix + MaterialLibraries.GenericFileName))] };
        var copper = AppearanceResolver.Resolve(new AppearanceRequest(tech, "Copper", AppearanceRole.Conductor, Em3dRole.Conductor, (0, 0, 0)));
        Assert.Equal(0.955, copper.Values.BaseColor.R, 1 / 255.0);
        Assert.Equal(0.638, copper.Values.BaseColor.G, 1 / 255.0);
        Assert.Equal(0.538, copper.Values.BaseColor.B, 1 / 255.0);
        Assert.Equal(1, copper.Values.Metallic);
        Assert.Equal("material 'Copper' (generic-materials.cmat)", copper.Provenance["BaseColor"]);
        Assert.Null(generic.Single(m => m.Name == "Copper").Color);            // the default view's copper is the palette's, as before
        Assert.Empty(MaterialValidation.Validate(generic));                     // every shipped appearance is in range
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cell/3d/Cell.c3d places U1 (mid) at Roughness 0.6 and Clearcoat 0.9, which places U3 (leaf) at Roughness 0.7;
    /// the leaf's chip states Metallic 0.5. Returns the top document's path.</summary>
    private string NestedWorkspace(out string ws)
    {
        ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology { Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }] });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        Save(ws, "leaf", new C3dDocument { Objects = [Box("chip", 0, new TechAppearance { Metallic = 0.5 })] });
        Save(ws, "mid", new C3dDocument
        {
            Objects = [Box("frame", 1)],
            Instances = [new C3dInstance { Name = "U3", CellRef = "../../leaf", Appearance = new TechAppearance { Roughness = 0.7 } }],
        });
        return Save(ws, "Cell", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../mid", Appearance = new TechAppearance { Roughness = 0.6, Clearcoat = 0.9 } }],
        });
    }

    private static C3dBox Box(string name, long x, TechAppearance? look = null)
        => new() { Name = name, Material = "Copper", Appearance = look, Min = new C3dPoint3(x * 200 * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };

    private static string Save(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    /// <summary>Copper and Gold from the generic library (as a technology reaches them), Glass of the technology's own.</summary>
    private static Technology Library()
    {
        const string lib = MaterialLibraries.ShippedPrefix + MaterialLibraries.GenericFileName;
        return new Technology
        {
            Name = "t",
            Materials = [new TechMaterial { Name = "Glass", Color = "#00FF00", Appearance = new TechAppearance { Ior = 1.46, AttenuationColor = "#D9861C" } }],
            LibraryMaterials =
            [
                new LibraryMaterial(new TechMaterial { Name = "Copper", Sigma20 = 5.8e7,
                    Appearance = new TechAppearance { BaseColor = "#FAD1C2", Metallic = 1, Roughness = 0.35, Like = "Glass" } }, lib),
                new LibraryMaterial(new TechMaterial { Name = "Gold", Sigma20 = 4.1e7, Appearance = new TechAppearance { BaseColor = "#FFE39D", Roughness = 0.25 } }, lib),
            ],
        };
    }

    private static AppearanceRequest Composite(Technology tech) => new(tech, "Copper", AppearanceRole.Conductor, Em3dRole.Conductor, (150, 150, 155),
        new AppearanceOverride(new TechAppearance { Roughness = 0.1, Like = "Gold" },
        [
            new AppearanceInstanceStatement("U1/U3", new TechAppearance { Clearcoat = 0.4, Roughness = 0.9 }),
            new AppearanceInstanceStatement("U1", new TechAppearance { Clearcoat = 0.8, ClearcoatRoughness = 0.3, Transmission = 0.2 }),
        ]));

    private static AppearanceOverride Override(TechAppearance a) => new(a, []);

    private static AppearanceColour Srgb(string hex)
    {
        var (r, g, b) = MaterialValidation.ParseColour(hex)!.Value;
        return AppearanceColour.FromSrgb(r, g, b);
    }

    private static Em3dSolid Solid(string name, int k)
        => new(name, "Copper", Em3dRole.Conductor, new Em3dBox(new Point3(k * 2 * Mm, 0, 0), new Point3((k * 2 + 1) * Mm, 1 * Mm, 1 * Mm)), 0);

    private static Em3dProblem Problem(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new Point3(-1, -1, -1), new Point3(1, 1, 1), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
    }
}
