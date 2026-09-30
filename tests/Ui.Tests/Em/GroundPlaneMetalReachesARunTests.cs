// ================================================================
//  CL7 — the ground plane reaches a user.
//  `docs/sonnet-briefs/brief-conductor-loss-7-ground-reaches-a-user.md`;
//  findings in `src/Engine/Mom/RESOLVED.md` §CL7.
//
//  CL4 built a conducting ground plane on the general kernel and CL6 built it on the one-slab
//  kernel. Every gate in both passed and §CL4 §9 had to record that NO RUN A USER COULD MAKE GOT
//  ONE, because `PlanarExtractor` wrote `Termination.Pec` into both media. This file is the
//  assertion that it no longer does.
//
//  Its whole subject is the EXTRACTOR — `tests/Engine.Tests/Mom/GroundReachesAUserTests.cs` gates
//  what the termination is worth, on both starters and against kernel A. What is asked here is the
//  question those cannot ask: does the σ a user typed into the Stackup tab arrive?
// ================================================================

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class GroundPlaneMetalReachesARunTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static long Mil(double m) => (long)Math.Round(m * 25.4 * Dbu);

    private static List<StackupLayer> Conductors(Technology t) =>
        [.. t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)];

    /// <summary>A 400 × 14 mil trace on one conductor's own drawing layer — the same fixture shape
    /// <see cref="FourLayerGroundReferenceTests"/> uses, so the two read as one story.</summary>
    private static PlanarExtractionResult Trace(Technology tech, int conductorIndex) =>
        PlanarExtractor.Extract(
            [new RectShape
            {
                Layer = Conductors(tech)[conductorIndex].DrawingLayers[0],
                X1 = 0, Y1 = 0, X2 = Mil(400), Y2 = Mil(14),
            }],
            tech, Dbu);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The flip itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The return plane's own σ and thickness arrive in the medium, on every shipped
    /// technology.</b> Both spellings are checked because both exist and the extractor writes the
    /// same floor into each: a one-level single-dielectric design carries it on
    /// <see cref="GroundedSlab.Floor"/> and stays on the one-slab kernel's own validated tier (CL6's
    /// whole reason for existing), and anything else carries it on the <c>LayerStack</c>'s bottom.
    ///
    /// <para><b>The half-measure is what this rules out.</b> A floor written only where the general
    /// kernel was already selected would give two physically identical designs different ground
    /// physics according to whether the stackup carried one dielectric entry or two — a user adding
    /// a solder mask would silently change their ground model. CL4 §9 refused it and CL6 exists so
    /// that it does not have to be reconsidered; this is the assertion that keeps it refused.</para>
    /// </summary>
    [Theory]
    [InlineData("pcb-2layer_FR-4_70mil_1oz",  0)]
    [InlineData("pcb-2layer_RO4350B_20mil_1oz", 0)]
    [InlineData("pcb-4layer_FR-4_62mil_1oz",  0)]
    [InlineData("pcb-4layer_FR-4_62mil_1oz",  2)]
    [InlineData("mmic-GaAs_2LM_100um",        0)]
    public void TheReturnPlanesMetalArrivesInTheMedium(string technology, int conductorIndex)
    {
        var tech = ShippedTechnologies.Load(technology);
        var r = Trace(tech, conductorIndex);
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        var floor = p.Slab.Floor;
        var stackFloor = p.EffectiveStack.Bottom;
        output.WriteLine($"{technology}, conductor {conductorIndex} " +
                         $"({Conductors(tech)[conductorIndex].Name}):");
        output.WriteLine($"   slab floor  {floor}");
        output.WriteLine($"   stack floor {stackFloor}   (MediumStack " +
                         $"{(p.MediumStack is null ? "null — one-slab kernel" : "given — general kernel")})");

        Assert.Equal(TerminationKind.SurfaceImpedance, floor.Kind);
        Assert.True(floor.ConductivitySm > 0 && !double.IsPositiveInfinity(floor.ConductivitySm));
        Assert.True(floor.ThicknessM > 0);

        // BOTH spellings, and the same metal in each — a design that happens to be stratified must
        // not get a different ground model from one that is not.
        Assert.Equal(TerminationKind.SurfaceImpedance, stackFloor.Kind);
        Assert.Equal(floor.ConductivitySm, stackFloor.ConductivitySm);
        Assert.Equal(floor.ThicknessM, stackFloor.ThicknessM);

        // …and it is the metal of the plane the note NAMES, not of some other conductor.
        var named = Conductors(tech).First(c => r.Notes.Any(n =>
            n.Contains($"returns through '{c.Name}'", StringComparison.Ordinal)));
        Assert.Equal(named.SigmaSm, floor.ConductivitySm);
        output.WriteLine($"   named plane '{named.Name}', σ = {named.SigmaSm:E3} S/m");
    }

    /// <summary>
    /// <b>The run SAYS what the plane is made of, and it says the other thing when there is no
    /// σ.</b> Until CL7 the plane was a perfect conductor and there was nothing to report; it is in
    /// the answer now — of order a fifth to a quarter of an ordinary microstrip's conductor loss —
    /// and the two states are not distinguishable from any published number, so this note is the
    /// only place a run says which it is in.
    ///
    /// <para>A layer with no σ is a legitimate thing to have: it is what every technology written
    /// before conductivity mattered has, and it still gets the perfect plane bit for bit. What it
    /// must not do is get it silently.</para>
    /// </summary>
    [Fact]
    public void TheNoteSaysWhatThePlaneIsMadeOf_AndSaysWhenItIsPerfect()
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var withMetal = Trace(tech, 0);
        Assert.True(withMetal.Ok, withMetal.Refusal);
        string note = Assert.Single(withMetal.Notes,
            n => n.Contains("return plane's own metal is in this solve", StringComparison.Ordinal));
        output.WriteLine(note);
        Assert.Contains("5.8E+07 S/m", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not meshed", note, StringComparison.Ordinal);

        // The same technology with the plane's conductivity cleared.
        var plain = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var ground = plain.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor &&
                                                     l.IsGroundReference);
        ground.SigmaSm = 0;
        var perfect = Trace(plain, 0);
        Assert.True(perfect.Ok, perfect.Refusal);
        string other = Assert.Single(perfect.Notes,
            n => n.Contains("has no conductivity set in technology", StringComparison.Ordinal));
        output.WriteLine(other);
        Assert.Contains("PERFECT conductor", other, StringComparison.Ordinal);

        // …and the medium says the same thing the note does, on both spellings.
        Assert.Equal(TerminationKind.Pec, perfect.Problem!.Slab.Floor.Kind);
        Assert.Equal(TerminationKind.Pec, perfect.Problem!.EffectiveStack.Bottom.Kind);
    }

    /// <summary>
    /// <b>A plane that is SKIPPED OVER or absorbed does not hand its metal to the floor.</b>
    /// `PlanarExtractor`'s own comment records that a conductor band which is neither a level nor the
    /// return plane is absorbed into a neighbouring dielectric — <i>18 µm of copper becomes 18 µm of
    /// FR-4</i> — and the warning that says so is unchanged. What CL7 must not do is let such a band
    /// acquire a floor by accident, which is why <c>FloorFor</c> reads the CHOSEN band and does not
    /// search for metal.
    ///
    /// <para>The fixture is the 4-layer starter with the trace on the top conductor AND on Inner 2,
    /// which is what a board import produces: the levels then STRADDLE Inner 1, the return plane
    /// resolves to the bottom conductor, and Inner 1 is passed over with a warning. Its copper is
    /// 17.5 µm against the bottom plane's 35 µm, so the two are told apart by the floor's thickness
    /// and not only by its σ.</para>
    /// </summary>
    [Fact]
    public void ASkippedPlaneDoesNotBecomeTheFloor()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var conductors = Conductors(tech);
        var r = PlanarExtractor.Extract(
            [.. new[] { 0, 2 }.Select(i => new RectShape
            {
                Layer = conductors[i].DrawingLayers[0],
                X1 = 0, Y1 = 0, X2 = Mil(400), Y2 = Mil(14),
            })],
            tech, Dbu);

        Assert.True(r.Ok, r.Refusal);
        var floor = r.Problem!.Slab.Floor;
        output.WriteLine($"floor = {floor}");
        foreach (var n in r.Notes)
            output.WriteLine("   " + n);

        // The floor is the NAMED return plane's metal — its thickness in particular, which is what
        // separates the planes on this stackup — and not any of the ones the run passed over.
        var named = conductors.First(c => r.Notes.Any(n =>
            n.Contains($"returns through '{c.Name}'", StringComparison.Ordinal)));
        output.WriteLine($"   named plane '{named.Name}' " +
                         $"({named.ThicknessDbu / (double)Dbu:G4} µm, σ {named.SigmaSm:E3} S/m)");
        Assert.Equal(TerminationKind.SurfaceImpedance, floor.Kind);
        Assert.Equal(named.SigmaSm, floor.ConductivitySm);
        Assert.Equal(named.ThicknessDbu / (double)Dbu * 1e-6, floor.ThicknessM, 15);

        // And a designated plane that was PASSED OVER is warned about and is still absorbed into a
        // dielectric — it does not reach the termination.
        var skipped = Assert.Single(r.Notes, n =>
            n.Contains("lying BETWEEN the analysis levels and the return plane", StringComparison.Ordinal));
        Assert.Contains("absorbed into the surrounding dielectric", skipped, StringComparison.Ordinal);
        foreach (var other in conductors.Where(c => c.IsGroundReference && c.Name != named.Name))
            Assert.True(Math.Abs(other.ThicknessDbu / (double)Dbu * 1e-6 - floor.ThicknessM) > 1e-12,
                        $"the floor took '{other.Name}'s thickness rather than the return plane's");
    }

    /// <summary>
    /// <b>It changes the answer, it changes it in the one direction it may, and the size of the
    /// change is ACCOUNTED FOR rather than recorded.</b> The extractor's own problem, solved four
    /// ways — {perfect, real} strip crossed with {perfect, real} ground — so the move the flip
    /// causes is compared against the move the SIGNAL metal causes on the same mesh, at the same
    /// frequency, through the same de-embedding.
    ///
    /// <para><b>The SHARE is REPORTED and deliberately not gated, and that is CL4 §7's rule rather
    /// than a softening.</b> Kernel B reads 27-28% where kernel A reads 21% on the same row, and the
    /// inflation is exactly CL1's measured single-sheet strip deficit in the DENOMINATOR — the
    /// numerator is right and the strip term under-reads by 0.63-0.75, so quoting this share as the
    /// ground's share of the conductor term would be quoting that deficit back with the opposite
    /// sign. The overview's 21% / ~11% / 25% are statements about the PHYSICS, gated against kernel A
    /// in <c>tests/Engine.Tests/Mom/GroundReachesAUserTests.R_cl7_2_…</c> and not here.</para>
    ///
    /// <para><b>What IS gated is the 1/√σ signature, which has no denominator to be wrong.</b> The
    /// same technology with the ground plane's conductivity quartered must multiply the ground term
    /// by √4 and leave the strip's untouched — a structural property of Re(Z_s) that a factor error
    /// in the termination could not reproduce, and that "|S₂₁| went down" would pass straight
    /// through.</para>
    ///
    /// <para><see cref="PlanarProblem.WithPerfectGround"/> is the oracle and
    /// <c>PlanarFillSettings.PerfectConductor</c> is NOT it — that flag makes the STRIP perfect and
    /// nothing else, because the plane is a termination of the Green's function rather than a member
    /// of the fill (CL4 §8). Using it as a ground oracle is the mistake that ate CL4's own first
    /// measurement.</para>
    /// </summary>
    [Fact]
    public void ThePublishedAnswerMoves_ByTheShareTheGroundPlaneIsWorth()
    {
        // A 50 Ω-ish trace, because the share being asserted is a 50 Ω line's. The narrow 14 mil
        // fixture the other tests use is w/h = 0.2 on this stackup, where the strip's own loss
        // dominates by geometry and the plane reads 4.5% — physically right, and not what the
        // overview's 21% is about.
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var r = PlanarExtractor.Extract(
            [new RectShape
            {
                Layer = Conductors(tech)[0].DrawingLayers[0],
                X1 = 0, Y1 = 0, X2 = Mil(400), Y2 = Mil(132),
            }],
            tech, Dbu);
        Assert.True(r.Ok, r.Refusal);

        var problem = r.Problem!;
        double[] freqs = [2e9, 6e9];
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);
        var report = SurfaceMesher.Mesh(problem, ms);
        var box = problem.MetalBounds()!.Value;
        double yc = 0.5 * (box.MinY + box.MaxY);
        var ports = PlanarPorts.ResolveAll(report.Mesh,
        [
            new PlanarPort(1, new EmPoint(box.MinX, yc), PlanarPortSide.MinX, 50.0),
            new PlanarPort(2, new EmPoint(box.MaxX, yc), PlanarPortSide.MaxX, 50.0),
        ]);

        var pecStrip = new PlanarSolveSettings(PlanarFillSettings.Default with { PerfectConductor = true });

        // A1 all perfect, A2 real strip only, B1 real ground only, B2 both.
        var a1 = PlanarSolve.Run(problem.WithPerfectGround(), report.Mesh, ports, freqs, pecStrip);
        var a2 = PlanarSolve.Run(problem.WithPerfectGround(), report.Mesh, ports, freqs);
        var b1 = PlanarSolve.Run(problem,               report.Mesh, ports, freqs, pecStrip);
        var b2 = PlanarSolve.Run(problem,               report.Mesh, ports, freqs);

        double len = box.MaxX - box.MinX;
        output.WriteLine($"400 × 132 mil (≈50 Ω) trace on the 2-layer FR-4 starter, N = " +
                         $"{report.Mesh.Bases.Count}, ℓ = {len * 1e3:F3} mm");
        output.WriteLine($"   {"f",6} {"|S21| all PEC",15} {"real strip",13} {"real ground",13} " +
                         $"{"both",13} | {"α_strip",10} {"α_ground",10} {"ground share",12}");

        for (int k = 0; k < freqs.Length; k++)
        {
            double m1 = a1.Points[k].S[1, 0].Magnitude, m2 = a2.Points[k].S[1, 0].Magnitude;
            double n1 = b1.Points[k].S[1, 0].Magnitude, n2 = b2.Points[k].S[1, 0].Magnitude;

            // α from the published |S₂₁| of a line of known length, differenced against the all-PEC
            // run so every bias the two share cancels — R-cl3-6's rule, one surface over.
            double aStrip  = -Math.Log(m2 / m1) / len;
            double aGround = -Math.Log(n1 / m1) / len;
            double share   = aGround / (aStrip + aGround);

            output.WriteLine($"   {freqs[k] / 1e9,5:0.#}G {m1,15:F9} {m2,13:F9} {n1,13:F9} " +
                             $"{n2,13:F9} | {aStrip,10:E3} {aGround,10:E3} {share,12:P2}");

            Assert.True(n1 < m1, "a conducting ground plane did not reduce |S₂₁|");
            Assert.True(n2 < m2, "…nor on top of the strip's own loss");
            Assert.True(aGround > 0 && aStrip > 0);

            // The two surfaces are ADDITIVE: they do not double-count each other through the
            // solved current, which is what says the floor is a termination and not a second fill.
            double aBoth = -Math.Log(n2 / m1) / len;
            Assert.InRange((aStrip + aGround) / aBoth, 0.97, 1.03);

            _ = share;   // reported above; see the remarks for why it is not asserted
        }

        // ── The 1/√σ signature, on the extractor's own problem ────────────────────────────────
        var quartered = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var plane = quartered.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor &&
                                                        l.IsGroundReference);
        plane.SigmaSm /= 4.0;
        var rq = PlanarExtractor.Extract(
            [new RectShape
            {
                Layer = Conductors(quartered)[0].DrawingLayers[0],
                X1 = 0, Y1 = 0, X2 = Mil(400), Y2 = Mil(132),
            }],
            quartered, Dbu);
        Assert.True(rq.Ok, rq.Refusal);
        Assert.Equal(plane.SigmaSm, rq.Problem!.Slab.Floor.ConductivitySm);

        var c1 = PlanarSolve.Run(rq.Problem!, report.Mesh, ports, freqs, pecStrip);
        for (int k = 0; k < freqs.Length; k++)
        {
            double m1 = a1.Points[k].S[1, 0].Magnitude;
            double aFull    = -Math.Log(b1.Points[k].S[1, 0].Magnitude / m1) / len;
            double aQuarter = -Math.Log(c1.Points[k].S[1, 0].Magnitude / m1) / len;
            output.WriteLine($"   {freqs[k] / 1e9,5:0.#}G  ground σ/4: α_ground {aFull:E3} → " +
                             $"{aQuarter:E3}  ×{aQuarter / aFull:F4} against √4 = 2");
            Assert.InRange(aQuarter / aFull, 1.9, 2.1);
        }
    }
}
