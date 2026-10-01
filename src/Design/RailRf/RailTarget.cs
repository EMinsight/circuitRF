namespace CircuitRF.Design.RailRf;

/// <summary>What a target IS. The document says which, and there is no inference (railrf.md §2.2).</summary>
public enum RailTargetKind
{
    /// <summary>Millivolts, and it is what the DC mode is judged against.</summary>
    DropBudget,

    /// <summary>A flat Z_target in milliohms.</summary>
    FlatImpedance,

    /// <summary>A table of (frequency, limit) points. <b>Per observation port</b>, so it lives on the
    /// load row rather than on the rail.</summary>
    Mask,

    /// <summary>ΔI, ΔV and a rise time, from which railRF DERIVES the flat target and the top of the
    /// band that matters — see <see cref="RailTransientSpec"/>.</summary>
    Transient,
}

/// <summary>One row of a piecewise mask: a frequency in HERTZ and a limit in OHMS, both base SI.</summary>
public readonly record struct RailMaskPoint(double FrequencyHz, double LimitOhms);

/// <summary>
/// A transient specification, and the arithmetic railRF derives from it.
///
/// <para><b>The derivation belongs here and not in the window</b> (§2.2, and brief 1 §5): the same
/// document opened headlessly has to produce the same target, so it is a pure function of the three
/// stated numbers and nothing else. It is tested as one — two reads of the same document derive the
/// same flat Z and the same band top.</para>
///
/// <para>All three fields are base SI: amps, volts, seconds. <b>The rise time is optional</b>
/// (designer feedback round 11): ΔV and ΔI alone state the flat Z, and a designer who knows the
/// ripple and the load step but not the load's edge rate still has a target. Without one the target
/// says nothing about the band, and the rail's own band stands.</para>
/// </summary>
public sealed record RailTransientSpec(double DeltaIAmps, double DeltaVVolts, double? RiseTimeSeconds)
{
    /// <summary>
    /// The flat impedance target this transient implies, in OHMS: <c>ΔV / ΔI</c>. The whole of the
    /// classic PDN target — the rail may not move more than ΔV while the load steps by ΔI, and an
    /// impedance is what enforces that at every frequency in the band.
    /// </summary>
    public double FlatTargetOhms => DeltaVVolts / DeltaIAmps;

    /// <summary>
    /// The top of the band that matters, in HERTZ: the knee frequency <c>0.35 / t_rise</c>.
    ///
    /// <para>0.35 is the 10–90 % rise time's own bandwidth relation, and it is written as a named
    /// constant rather than folded into the expression precisely because it is a CONVENTION: a
    /// reader who assumes 0.5 (a 20–80 % rise) gets a band top 43 % too high, and nothing in the
    /// answer would look wrong.</para>
    /// </summary>
    public double? BandTopHz => RiseTimeSeconds is { } tr ? KneeFactor / tr : null;

    /// <summary>The 10–90 % rise-time-to-knee-frequency factor. See <see cref="BandTopHz"/>.</summary>
    public const double KneeFactor = 0.35;

    /// <summary>Null when the three numbers can be used, or the sentence saying why not.</summary>
    public string? Refusal(string where)
    {
        if (!(DeltaIAmps > 0))
            return $"{where}'s transient target states ΔI = {DeltaIAmps} A. A current step is what " +
                   "the impedance target divides by; state a positive one.";
        if (!(DeltaVVolts > 0))
            return $"{where}'s transient target states ΔV = {DeltaVVolts} V. State the voltage the " +
                   "rail is allowed to move by, as a positive number.";
        if (RiseTimeSeconds is { } tr && !(tr > 0))
            return $"{where}'s transient target states a rise time of {tr} s. The band top is " +
                   "derived from it, so it must be positive — or leave it out, and the rail's own " +
                   "band stands.";
        return null;
    }

    /// <summary>
    /// The usual first step of a PDN budget: the rail may ripple by <paramref name="ripplePercent"/> of
    /// <paramref name="railVolts"/> while the load steps by <paramref name="deltaIAmps"/>. ΔV is
    /// computed HERE and stored — the document keeps ΔV and ΔI, never the voltage and percentage they
    /// came from, so the target is the number the user chose and does not move when a source row is
    /// edited later. The window and <c>rail --target-transient</c> both call this.
    /// </summary>
    public static RailTransientSpec FromRipple(double railVolts, double ripplePercent, double deltaIAmps,
                                               double? riseTimeSeconds)
        => new(deltaIAmps, railVolts * ripplePercent / 100.0, riseTimeSeconds);

    /// <summary>
    /// ΔI where the user states none: the sum over the rail's loads of each one's peak current, or its
    /// DC current where it states no peak. Null when no load draws anything — an observation port
    /// steps by nothing, and a target divided by a guessed current is a guessed target.
    /// </summary>
    public static double? LoadStepOf(RailSpec rail)
    {
        double sum = 0;
        bool any = false;
        foreach (var l in rail.Loads)
            if ((l.PeakCurrentA ?? l.DcCurrentA) is { } a && a > 0) { sum += a; any = true; }
        return any ? sum : null;
    }

    /// <summary>The rail voltage a ripple percentage is taken of: the first source row stating an
    /// open-circuit voltage, or null when none does.</summary>
    public static double? RailVoltageOf(RailSpec rail)
        => rail.Sources.Select(s => s.OpenCircuitVoltageV).FirstOrDefault(v => v is > 0);

    /// <summary>
    /// The derived target with its arithmetic shown, as the window's Target card and the CLI both
    /// print it: <c>Z 5.143 Ω = 180 mV / 35 mA · band to 35 MHz (0.35 / 10 ns)</c>. The arithmetic is
    /// on the page because a derived number whose inputs are invisible cannot be checked — and the
    /// commonest slip (V / I, the load's DC resistance) is 20× too large at a 5 % ripple.
    /// </summary>
    public string Describe()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string z = $"Z {Engine.Pdn.PdnMask.Ohms(FlatTargetOhms)} = {Si(DeltaVVolts, "V")} / {Si(DeltaIAmps, "A")}";
        return RiseTimeSeconds is { } tr
            ? $"{z} · band to {Engine.Pdn.PdnMask.Hertz(KneeFactor / tr)} ({KneeFactor.ToString(inv)} / {Si(tr, "s")})"
            : $"{z} · no rise time, so the rail's own band stands";
    }

    /// <summary>
    /// A rise time with its unit — <c>10ns</c>, <c>1.5 µs</c>, <c>200ps</c>; a bare number is seconds. Its
    /// own small table because the expression engine's unit table does not read a time (<c>10ns</c> is
    /// refused there), and the window and <c>rail --target-transient</c> must read the same spellings.
    /// </summary>
    public static bool TryParseSeconds(string? text, out double seconds)
    {
        seconds = 0;
        string t = (text ?? "").Trim();
        int i = t.Length;
        while (i > 0 && !char.IsAsciiDigit(t[i - 1]) && t[i - 1] != '.') i--;
        if (!double.TryParse(t[..i].Trim(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double n))
            return false;
        double? scale = t[i..].Trim() switch
        {
            "" or "s"          => 1,
            "ms"               => 1e-3,
            "us" or "µs" or "μs" => 1e-6,
            "ns"               => 1e-9,
            "ps"               => 1e-12,
            _                  => null,
        };
        if (scale is not { } k) return false;
        seconds = n * k;
        return true;
    }

    private static string Si(double v, string unit)
    {
        double m = Math.Abs(v);
        var (scale, prefix) = m >= 1 || m == 0 ? (1.0, "") : m >= 1e-3 ? (1e3, "m") : m >= 1e-6 ? (1e6, "µ")
                            : m >= 1e-9 ? (1e9, "n") : (1e12, "p");
        return (v * scale).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + prefix + unit;
    }
}

/// <summary>
/// One target. Four kinds (§2.2's own list), and the document says which — <see cref="Kind"/> picks
/// exactly one of the four payloads and the other three are null.
///
/// <para><b>A rail carries a drop budget AND one frequency-domain target; they are not
/// alternatives.</b> The DC answer is judged against millivolts and the frequency answer against
/// milliohms, and a board that states only one of them is simply a board with one of the two
/// questions unanswered. So <see cref="RailSpec.DropBudget"/> and
/// <see cref="RailSpec.ImpedanceTarget"/> are separate slots rather than one field of four kinds,
/// and <see cref="RailLoad.Mask"/> is a third — a mask is per observation port.</para>
/// </summary>
public sealed record RailTarget
{
    public RailTargetKind Kind { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.DropBudget"/>. MILLIVOLTS —
    /// the one field on this record that is not base SI, because it is what the window's own row
    /// says and a budget of 0.08 V reads as a typo where 80 mV does not.</summary>
    public double? DropBudgetMillivolts { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.FlatImpedance"/>.
    /// MILLIOHMS, for <see cref="DropBudgetMillivolts"/>' reason.</summary>
    public double? FlatMilliohms { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.Mask"/>. Base SI throughout
    /// — hertz and ohms — because nothing types these row by row.</summary>
    public IReadOnlyList<RailMaskPoint>? Mask { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.Transient"/>.</summary>
    public RailTransientSpec? Transient { get; init; }

    public static RailTarget OfDropBudget(double millivolts) =>
        new() { Kind = RailTargetKind.DropBudget, DropBudgetMillivolts = millivolts };

    public static RailTarget OfFlatImpedance(double milliohms) =>
        new() { Kind = RailTargetKind.FlatImpedance, FlatMilliohms = milliohms };

    public static RailTarget OfMask(IEnumerable<RailMaskPoint> points) =>
        new() { Kind = RailTargetKind.Mask, Mask = [.. points] };

    public static RailTarget OfTransient(double deltaIAmps, double deltaVVolts, double? riseTimeSeconds) =>
        new() { Kind = RailTargetKind.Transient,
                Transient = new RailTransientSpec(deltaIAmps, deltaVVolts, riseTimeSeconds) };

    /// <summary>
    /// The flat impedance target in OHMS this target implies, or null where it implies none. A
    /// <see cref="RailTargetKind.FlatImpedance"/> states it; a <see cref="RailTargetKind.Transient"/>
    /// DERIVES it; a mask has a limit per frequency rather than one number, and a drop budget is the
    /// DC question.
    /// </summary>
    public double? FlatTargetOhms => Kind switch
    {
        RailTargetKind.FlatImpedance => FlatMilliohms is { } m ? m * 1e-3 : null,
        RailTargetKind.Transient     => Transient?.FlatTargetOhms,
        _                            => null,
    };

    /// <summary>The top of the band this target implies, in HERTZ, or null where it implies none.
    /// Only a transient does (§2.2) — the band is otherwise the rail's own.</summary>
    public double? BandTopHz => Kind == RailTargetKind.Transient ? Transient?.BandTopHz : null;

    /// <summary>
    /// Null when exactly the payload <see cref="Kind"/> names is set, or the refusal sentence.
    /// <paramref name="allowed"/> is what the SLOT accepts — a rail's DC slot takes a drop budget
    /// and nothing else — so a mask written into it is named rather than silently carried into a
    /// solve that would ignore it.
    /// </summary>
    public string? Refusal(string where, params RailTargetKind[] allowed)
    {
        if (allowed.Length > 0 && Array.IndexOf(allowed, Kind) < 0)
            return $"{where} holds a {Name(Kind)} target, which does not belong there — that slot " +
                   $"takes {string.Join(" or ", allowed.Select(Name))}.";

        int stated = (DropBudgetMillivolts is not null ? 1 : 0)
                   + (FlatMilliohms        is not null ? 1 : 0)
                   + (Mask                 is not null ? 1 : 0)
                   + (Transient            is not null ? 1 : 0);

        if (stated != 1)
            return $"{where} is a {Name(Kind)} target stating {stated} values. A target carries " +
                   "exactly the one its kind names.";

        bool matched = Kind switch
        {
            RailTargetKind.DropBudget    => DropBudgetMillivolts is not null,
            RailTargetKind.FlatImpedance => FlatMilliohms        is not null,
            RailTargetKind.Mask          => Mask                 is not null,
            RailTargetKind.Transient     => Transient            is not null,
            _                            => false,
        };
        if (!matched)
            return $"{where} calls itself a {Name(Kind)} target but states a different quantity.";

        if (Kind == RailTargetKind.Mask && Mask!.Count < 2)
            return $"{where}'s mask has {Mask.Count} point(s). A piecewise mask needs at least two " +
                   "so it spans a band.";

        return Transient?.Refusal(where);
    }

    /// <summary>
    /// <b>Value equality, including the mask's POINTS.</b>
    ///
    /// <para>A record's generated <c>Equals</c> compares each member with its own — and
    /// <see cref="Mask"/> is an <see cref="IReadOnlyList{T}"/>, whose own is REFERENCE equality. So
    /// two targets read from the same bytes would compare unequal, silently, and the round trip that
    /// is supposed to prove the format carries everything would prove nothing. Worse downstream:
    /// brief 16 compares two documents for a living, and "these two masks differ" is exactly the kind
    /// of false finding a comparison tool must not produce.</para>
    /// </summary>
    public bool Equals(RailTarget? other) =>
        other is not null
        && Kind                 == other.Kind
        && DropBudgetMillivolts == other.DropBudgetMillivolts
        && FlatMilliohms        == other.FlatMilliohms
        && Transient            == other.Transient
        && (ReferenceEquals(Mask, other.Mask)
            || (Mask is not null && other.Mask is not null && Mask.SequenceEqual(other.Mask)));

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind);
        h.Add(DropBudgetMillivolts);
        h.Add(FlatMilliohms);
        h.Add(Transient);
        foreach (var p in Mask ?? []) h.Add(p);
        return h.ToHashCode();
    }

    /// <summary>The wire spelling, and what a refusal calls it.</summary>
    public static string Name(RailTargetKind k) => k switch
    {
        RailTargetKind.DropBudget    => "drop-budget",
        RailTargetKind.FlatImpedance => "flat-impedance",
        RailTargetKind.Mask          => "mask",
        RailTargetKind.Transient     => "transient",
        _                            => "unknown",
    };
}
