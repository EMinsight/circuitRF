namespace CircuitRF.Design.Layout.PCells;

/// <summary>The plane a test pad faces on one side, and the dielectric between them (brief-dfr26-7 R-dfr26-7-4).</summary>
/// <param name="Plane">The conductor the pad's capacitance is taken to.</param>
/// <param name="HeightMeters">The summed dielectric thickness between the pad and it.</param>
/// <param name="EpsR">Those dielectrics as one, in series (<c>TestPadFormulas.SeriesDielectric</c>).</param>
/// <param name="Passed">The conductors between the pad and the plane that were passed: cleared under the pad, or
/// with no metal there.</param>
/// <param name="Cut">The first of them, when it is one of the pad's <c>ClearLayers</c>: the plane the pad faces over
/// the annulus outside its <c>ClearD</c> hole (brief-dfr27-1 R-dfr27-1-1).</param>
public sealed record TestPadFace(StackupLayer Plane, double HeightMeters, double EpsR, IReadOnlyList<string> Passed,
                                 TestPadCutPlane? Cut = null);

/// <summary>A cleared plane between a test pad and the plane it faces (brief-dfr27-1 R-dfr27-1-1).</summary>
/// <param name="Plane">The conductor.</param>
/// <param name="HeightMeters">The dielectric between the pad and it.</param>
/// <param name="EpsR">That dielectric in series.</param>
/// <param name="ThicknessMeters">Its copper.</param>
public sealed record TestPadCutPlane(StackupLayer Plane, double HeightMeters, double EpsR, double ThicknessMeters);

/// <summary>One plane a test pad's via passes, and where along the via it sits.</summary>
/// <param name="Plane">The conductor.</param>
/// <param name="PositionMeters">Mid-plane distance from the via's pin-1 conductor.</param>
/// <param name="LengthMeters">The barrel the plane owns: half the dielectric to each neighbouring conductor.</param>
/// <param name="EpsR">That dielectric's εr, thickness-weighted.</param>
/// <param name="Cleared">The plane is one of the pad's <c>ClearLayers</c>, so its clearance is <c>ClearD</c>
/// rather than the via's own antipad.</param>
public sealed record TestPadBarrelPlane(StackupLayer Plane, double PositionMeters, double LengthMeters, double EpsR, bool Cleared);

/// <summary>Where an unused pad on a test pad's via would sit, and what is next to it on each side (brief-dfr26-8
/// R-dfr26-8-1). Its diameter and antipad are the component's (<c>InnerPadD</c>, <c>AntipadD</c>) or, read from
/// artwork, what is drawn.</summary>
/// <param name="Conductor">The conductor the via passes.</param>
/// <param name="PositionMeters">Mid-plane distance from the via's pin-1 conductor.</param>
/// <param name="ThicknessMeters">The conductor's copper thickness.</param>
/// <param name="EpsR">The dielectric to each neighbouring conductor, thickness-weighted.</param>
/// <param name="GapAMeters">The dielectric to the next conductor towards pin 1.</param>
/// <param name="SideA">What that conductor is.</param>
/// <param name="GapBMeters">The dielectric to the next conductor towards the pad.</param>
/// <param name="SideB">What that conductor is.</param>
public sealed record TestPadInnerPadSlot(
    StackupLayer Conductor, double PositionMeters, double ThicknessMeters, double EpsR,
    double GapAMeters, CircuitRF.Core.Devices.TestPadSide SideA, double GapBMeters, CircuitRF.Core.Devices.TestPadSide SideB);

/// <summary>
/// A test pad resolved against a technology's stackup — what both its model and the impedance tool's pad report
/// are built from (brief-dfr26-7 R-dfr26-7-4/5).
/// </summary>
/// <param name="Pad">The conductor the pad is on.</param>
/// <param name="Via">The via's span from <c>ViaFrom</c> to the pad's conductor, or null for a side-fed pad.</param>
/// <param name="Faces">The plane the pad faces on each side that has one.</param>
/// <param name="Planes">The planes the via passes (empty for a side-fed pad).</param>
/// <param name="InnerPads">Where the via would carry an unused pad: every conductor it passes that <c>ClearLayers</c>
/// does not name (empty for a side-fed pad). Whether there IS one is the component's <c>InnerPadD</c>.</param>
public sealed record ResolvedTestPad(
    StackupLayer Pad,
    ResolvedViaSpan? Via,
    IReadOnlyList<TestPadFace> Faces,
    IReadOnlyList<TestPadBarrelPlane> Planes,
    IReadOnlyList<TestPadInnerPadSlot>? InnerPads = null)
{
    public IReadOnlyList<TestPadInnerPadSlot> InnerPadSlots => InnerPads ?? [];

    public double ViaLengthMeters => Via?.LengthMeters ?? 0.0;
}

public static partial class SubstrateResolver
{
    /// <summary>
    /// Resolves a test pad (brief-dfr26-7 R-dfr26-7-4).
    ///
    /// <para><b>The plane the pad faces</b>, on each side: walking away from the pad, the first conductor that
    /// <paramref name="facesPad"/> says has metal under it — by default every conductor NOT named in
    /// <paramref name="clearLayers"/>, since the stackup alone cannot say what is drawn there. The dielectric
    /// layers walked over are summed and combined in series; the conductors passed are named. A pad on an
    /// outer conductor has one side only.</para>
    ///
    /// <para><b>The via</b>, when <paramref name="viaFed"/>: <see cref="ResolveViaSpan"/> from
    /// <paramref name="viaFromName"/> to the pad's conductor, the same span a placed <c>VIA</c> would resolve. The
    /// planes it passes are the conductors strictly between that are ground references or named in
    /// <paramref name="clearLayers"/>, each with the barrel it owns and where it sits along the via.</para>
    ///
    /// <para><b>Defaults.</b> An unnamed pad conductor is the lowest conductor that is not a ground reference
    /// (the board's far side); an unnamed <paramref name="viaFromName"/> is the topmost conductor. A name that is
    /// not a conductor reports and takes the default.</para>
    /// </summary>
    public static (ResolvedTestPad? Pad, SubstrateResolutionFailure? Failure, IReadOnlyList<string> Warnings) ResolveTestPad(
        Technology? technology, string? padLayerName, string? viaFromName, bool viaFed,
        IReadOnlyCollection<string> clearLayers, Func<StackupLayer, bool>? facesPad = null)
    {
        var warn = new List<string>();
        if (technology is null)
            return (null, new SubstrateResolutionFailure("no technology resolved for this document"), warn);

        var layers = technology.Stackup.Layers;
        var conductors = layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count == 0)
            return (null, new SubstrateResolutionFailure($"technology '{technology.Name}' stackup has no conductor layer"), warn);

        var pad = DefaultPadConductor(conductors);
        if (padLayerName is { Length: > 0 } pn)
        {
            if (conductors.FirstOrDefault(c => string.Equals(c.Name, pn, StringComparison.OrdinalIgnoreCase)) is { } named) pad = named;
            else warn.Add($"no conductor named '{pn}' in technology '{technology.Name}' — the pad is on '{pad.Name}', the lowest signal conductor");
        }
        foreach (string c in clearLayers)
            if (!conductors.Any(l => string.Equals(l.Name, c, StringComparison.OrdinalIgnoreCase)))
                warn.Add($"ClearLayers names '{c}', which is not a conductor of technology '{technology.Name}'; it is ignored");

        bool Cleared(StackupLayer l) => clearLayers.Any(c => string.Equals(c, l.Name, StringComparison.OrdinalIgnoreCase));
        facesPad ??= l => !Cleared(l);

        // ── The faces ─────────────────────────────────────────────────────────────────────────
        int iPad = layers.IndexOf(pad);
        var faces = new List<TestPadFace>();
        foreach (int step in new[] { -1, 1 })
        {
            var dielectric = new List<(double, double)>();
            var passed = new List<string>();
            TestPadCutPlane? cut = null;
            for (int i = iPad + step; i >= 0 && i < layers.Count; i += step)
            {
                var l = layers[i];
                if (l.Kind == StackupKind.Dielectric) dielectric.Add((DbuToMeters(l.ThicknessDbu, FallbackDbuPerMicron), l.Epsr));
                else if (l.Kind == StackupKind.Conductor)
                {
                    if (facesPad(l))
                    {
                        var (h, er) = CircuitRF.Core.Devices.TestPadFormulas.SeriesDielectric(dielectric);
                        if (h > 0) faces.Add(new TestPadFace(l, h, er, passed, cut));
                        break;
                    }
                    if (passed.Count == 0 && Cleared(l))
                    {
                        var (hc, erc) = CircuitRF.Core.Devices.TestPadFormulas.SeriesDielectric(dielectric);
                        if (hc > 0) cut = new TestPadCutPlane(l, hc, erc, DbuToMeters(l.ThicknessDbu, FallbackDbuPerMicron));
                    }
                    passed.Add(l.Name);
                }
            }
        }

        // ── The via ───────────────────────────────────────────────────────────────────────────
        if (!viaFed) return (new ResolvedTestPad(pad, null, faces, []), null, warn);

        var from = FindSignalConductor(technology, viaFromName, warn)!;
        if (ReferenceEquals(from, pad))
            return (new ResolvedTestPad(pad, null, faces, []), null,
                    [.. warn, $"the via would run from '{pad.Name}' to itself, so the pad is taken as side-fed; choose a different ViaFrom"]);
        var (span, failure, viaWarn) = ResolveViaSpan(technology, from.Name, pad.Name, toGround: false);
        warn.AddRange(viaWarn);
        if (span is null) return (null, failure, warn);

        int iFrom = layers.IndexOf(span.From), lo = Math.Min(iFrom, iPad), hi = Math.Max(iFrom, iPad);
        var z = MidPlanes(layers);
        var planes = new List<TestPadBarrelPlane>();
        for (int p = lo + 1; p < hi; p++)
        {
            var l = layers[p];
            if (l.Kind != StackupKind.Conductor || !(l.IsGroundReference || Cleared(l))) continue;
            int above = p, below = p;
            for (int i = p - 1; i >= lo; i--) { above = i; if (layers[i].Kind == StackupKind.Conductor) break; }
            for (int i = p + 1; i <= hi; i++) { below = i; if (layers[i].Kind == StackupKind.Conductor) break; }
            var (tA, erA) = Dielectric(layers, above, p);
            var (tB, erB) = Dielectric(layers, p, below);
            double t = (tA + tB) / 2;
            if (t > 0)
                planes.Add(new TestPadBarrelPlane(l, Math.Abs(z[p] - z[iFrom]), t, (tA * erA + tB * erB) / (tA + tB), Cleared(l)));
        }
        var inner = InnerPadSlots(technology, span.From, pad, l => !Cleared(l));
        return (new ResolvedTestPad(pad, span, faces, planes, inner), null, warn);
    }

    /// <summary>
    /// The unused-pad slots of a via from <paramref name="from"/> to <paramref name="to"/> (brief-dfr26-8
    /// R-dfr26-8-1): each conductor strictly between them for which <paramref name="carriesPad"/> holds, with where
    /// it sits along the via, its copper, and what its neighbouring conductor on each side is — the via's own end,
    /// a conductor carrying a pad, or a plane with only the barrel in its antipad.
    /// </summary>
    public static IReadOnlyList<TestPadInnerPadSlot> InnerPadSlots(
        Technology technology, StackupLayer from, StackupLayer to, Func<StackupLayer, bool> carriesPad)
    {
        var layers = technology.Stackup.Layers;
        int iFrom = layers.IndexOf(from), iTo = layers.IndexOf(to);
        if (iFrom < 0 || iTo < 0) return [];
        int lo = Math.Min(iFrom, iTo), hi = Math.Max(iFrom, iTo), toward = iFrom < iTo ? -1 : 1;
        var z = MidPlanes(layers);
        var slots = new List<TestPadInnerPadSlot>();
        for (int p = lo + 1; p < hi; p++)
        {
            var l = layers[p];
            if (l.Kind != StackupKind.Conductor || !carriesPad(l)) continue;
            (double Gap, double EpsR, CircuitRF.Core.Devices.TestPadSide Side) Neighbour(int step)
            {
                int n = p;
                for (int i = p + step; i >= lo && i <= hi; i += step) { n = i; if (layers[i].Kind == StackupKind.Conductor) break; }
                var (t, er) = Dielectric(layers, p, n);
                var side = n == iFrom || n == iTo ? CircuitRF.Core.Devices.TestPadSide.ViaEnd
                         : carriesPad(layers[n]) ? CircuitRF.Core.Devices.TestPadSide.Pad
                         : CircuitRF.Core.Devices.TestPadSide.Plane;
                return (t, er, side);
            }
            var a = Neighbour(toward);
            var b = Neighbour(-toward);
            double tSum = a.Gap + b.Gap;
            slots.Add(new TestPadInnerPadSlot(l, Math.Abs(z[p] - z[iFrom]), DbuToMeters(l.ThicknessDbu, FallbackDbuPerMicron),
                                              tSum > 0 ? (a.Gap * a.EpsR + b.Gap * b.EpsR) / tSum : 1.0,
                                              a.Gap, a.Side, b.Gap, b.Side));
        }
        return slots;
    }

    /// <summary>The lowest conductor that is not a ground reference — a test point's usual place, the board's far
    /// side — else the lowest conductor.</summary>
    private static StackupLayer DefaultPadConductor(List<StackupLayer> conductors)
        => conductors.LastOrDefault(c => !c.IsGroundReference) ?? conductors[^1];
}
