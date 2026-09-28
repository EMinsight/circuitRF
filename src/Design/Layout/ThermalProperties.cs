// brief-em3d-73 R-em3d73-2 — a material's electrical and thermal conductivity at a temperature: THE TABLE WINS.
//
// ONE RESOLVER, READ BY THE THERMAL LOWERING AND BY NOTHING THAT EXISTED BEFORE IT. Every EM solver still reads Sigma20
// (and a 3D setup its Alpha20 pair, through C3dElaborator.MaterialValues), so no answer that exists today moves because a
// material gained a table. A thermal run is the first reader, and it reads here.
//
// THE ORDER (owner decision, 2026-09-27). k(T): ThermalKVsTemp when stated, else ThermalK held constant. σ(T): SigmaVsTemp
// when stated, else Sigma20 / (1 + Alpha20·(T − 20)) — wBond's own WireMaterial.SigmaAt, character for character, so a
// wire's σ at 85 °C is the number wBond already uses. A table is piecewise-linear in T and HELD CONSTANT beyond its ends,
// and a value read from a hold says so: a hot spot past the last row is a result the user must be told was extrapolated
// flat, not a silent answer.
//
// THE SLOPES come from the same place — piecewise-constant for a table (the segment's own slope; zero on a hold), the
// formula's derivative otherwise — because brief 77's Newton step needs dk/dT and dσ/dT that agree with the values it is
// differentiating. At a table's interior row the slope is the segment ABOVE it's (the segment the value would move into as
// T rises), which is one consistent choice; a Jacobian needs one, not an average.

using System.Globalization;

namespace CircuitRF.Design.Layout;

/// <summary>A property at one temperature: its value, its slope with temperature, and — when the temperature is outside
/// the table that answered — the note saying the value was held at the table's end.</summary>
/// <param name="Value">In the property's own unit (S/m, W/(m·K)).</param>
/// <param name="Slope">d(Value)/dT, per kelvin.</param>
/// <param name="HeldNote">Non-null when <paramref name="Value"/> is a table's end value held beyond it.</param>
public readonly record struct ThermalPropertyValue(double Value, double Slope, string? HeldNote)
{
    /// <summary>True when the value is a table's end held constant.</summary>
    public bool Held => HeldNote is not null;
}

/// <summary>σ(T) and k(T) of a <see cref="TechMaterial"/>, with their slopes (R-em3d73-2a).</summary>
public static class ThermalProperties
{
    /// <summary>
    /// The thermal conductivity of <paramref name="m"/> at <paramref name="tempC"/>, W/(m·K): its
    /// <see cref="TechMaterial.ThermalKVsTemp"/> when stated, else its <see cref="TechMaterial.ThermalK"/> (constant, slope
    /// 0). Null when it states neither — a thermal run refuses that, naming the material and the object, never a default.
    /// </summary>
    public static ThermalPropertyValue? ThermalKAt(TechMaterial m, double tempC)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.ThermalKVsTemp is { Count: > 0 } table) return FromTable(table, tempC, m.Name, "thermal conductivity");
        return m.ThermalK is { } k ? new ThermalPropertyValue(k, 0, null) : null;
    }

    /// <summary>
    /// The electrical conductivity of <paramref name="m"/> at <paramref name="tempC"/>, S/m: its
    /// <see cref="TechMaterial.SigmaVsTemp"/> when stated, else <c>Sigma20 / (1 + Alpha20·(T − 20))</c> (an unstated
    /// Alpha20 is 0). Null when it states neither.
    /// </summary>
    public static ThermalPropertyValue? SigmaAt(TechMaterial m, double tempC)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.SigmaVsTemp is { Count: > 0 } table) return FromTable(table, tempC, m.Name, "conductivity");
        if (m.Sigma20 is not { } s20) return null;
        double a = m.Alpha20 ?? 0;
        double d = 1.0 + a * (tempC - 20.0);
        return new ThermalPropertyValue(s20 / d, -s20 * a / (d * d), null);
    }

    /// <summary>
    /// A table's value at <paramref name="tempC"/>: linear between the two rows around it, the end row's value beyond
    /// either end (slope 0, with a note naming the material, the temperature and the end). The table is assumed valid —
    /// finite, positive, strictly increasing in temperature — which <see cref="MaterialValidation"/> reports otherwise.
    /// </summary>
    public static ThermalPropertyValue FromTable(IReadOnlyList<TechTemperaturePoint> table, double tempC, string? material,
                                                 string property)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfZero(table.Count, nameof(table));
        var first = table[0];
        var last = table[^1];
        if (tempC < first.TempC || table.Count == 1 && tempC != first.TempC)
            return new ThermalPropertyValue(first.Value, 0, Held(material, property, tempC, tempC < first.TempC ? first : last,
                                                                 tempC < first.TempC ? "lowest" : "highest"));
        if (tempC > last.TempC)
            return new ThermalPropertyValue(last.Value, 0, Held(material, property, tempC, last, "highest"));
        if (table.Count == 1) return new ThermalPropertyValue(first.Value, 0, null);

        // The segment whose lower end is at or below T; at the top row, the last segment.
        int i = 0;
        while (i < table.Count - 2 && table[i + 1].TempC <= tempC) i++;
        var a = table[i];
        var b = table[i + 1];
        double slope = (b.Value - a.Value) / (b.TempC - a.TempC);
        return new ThermalPropertyValue(a.Value + slope * (tempC - a.TempC), slope, null);
    }

    private static string Held(string? material, string property, double tempC, TechTemperaturePoint end, string which)
        => string.Create(CultureInfo.InvariantCulture,
            $"Material \"{material}\"'s {property} is held at its table's {which} row ({end.TempC:0.##} °C) at {tempC:0.##} °C: the table states nothing beyond it, so the end value is used.");

    /// <summary>
    /// R-em3d73-2b — the one comparison <c>check</c> warns on: a record stating both a table and the constant (or
    /// coefficient) it would otherwise use, disagreeing at 20 °C by more than <see cref="DisagreementFraction"/>. Returns
    /// the two numbers when they disagree, else null. The table's own 20 °C value is read by <see cref="FromTable"/>, so a
    /// table that does not reach 20 °C is compared at its held end — which is what a run would use.
    /// </summary>
    public static (double Table, double Constant)? SigmaDisagreementAt20(TechMaterial m)
        => m is { SigmaVsTemp.Count: > 0, Sigma20: { } s20 } ? Disagree(FromTable(m.SigmaVsTemp, 20, m.Name, "").Value, s20) : null;

    /// <inheritdoc cref="SigmaDisagreementAt20"/>
    public static (double Table, double Constant)? ThermalKDisagreementAt20(TechMaterial m)
        => m is { ThermalKVsTemp.Count: > 0, ThermalK: { } k } ? Disagree(FromTable(m.ThermalKVsTemp, 20, m.Name, "").Value, k) : null;

    /// <summary>How far apart a table and its constant may be at 20 °C before <c>check</c> warns: 1 %.</summary>
    public const double DisagreementFraction = 0.01;

    private static (double, double)? Disagree(double table, double constant)
        => double.IsFinite(table) && double.IsFinite(constant) && Math.Abs(table - constant) > DisagreementFraction * Math.Abs(constant)
            ? (table, constant) : null;
}
