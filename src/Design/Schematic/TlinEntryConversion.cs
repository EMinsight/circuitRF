using System.Globalization;
using System.Linq;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// TLIN's Electrical / Physical entry switch (brief-artsch-2 R-as2-3): rewrites a TLIN's parameters from
/// one form to the other so the line does not change. The conversion is made at <c>F</c>:
/// E = 2π·F·L·√Eeff/c₀. Loss converts exactly between the angle form's <c>A</c> (∝ f) and the physical
/// form's <c>Ad</c> (∝ f); <c>Ac</c> scales as √f, which the angle form cannot state, so going back it
/// is folded into <c>A</c> at its value at F and the note says so.
///
/// <para>A value that is not a plain number (a variable, an expression) converts as its default — the
/// same best-effort rule the MKLOPF switch states — and the note says that too.</para>
/// </summary>
public static class TlinEntryConversion
{
    private const double DefaultFHz = 1e9;

    /// <summary>True when <paramref name="parameters"/> are in the physical form.</summary>
    public static bool IsPhysical(IEnumerable<EditableParameter> parameters) => parameters.Any(p => p.Name == "L");

    /// <summary>
    /// The physical form's rows as a placed TLIN shows them — <see cref="Switch"/>'s own choices: Z, L, Eeff and F drawn,
    /// the losses not. What a netlist's physical TLIN is drawn with (designer report, round 15: a recognised TLIN came
    /// in with L hidden and unit-less, because only the angle form's rows were known, so its length was nowhere on the
    /// schematic).
    /// </summary>
    public static IReadOnlyList<DefaultParam> PhysicalTemplate { get; } =
    [
        new("Z", "50", "Ω", true, UnitDimension.Resistance),
        new("L", "0", "mm", true, UnitDimension.Length),
        new("Eeff", "1", "", true, UnitDimension.None),
        new("F", "1", "GHz", true, UnitDimension.Frequency),
        new("Ac", "0", "", false, UnitDimension.None),
        new("Ad", "0", "", false, UnitDimension.None),
    ];

    /// <summary>The other form's parameters, and what the switch has to say (empty when nothing).
    /// <paramref name="lengthUnit"/> is the unit a new <c>L</c> is written in — the technology's.</summary>
    public static (List<EditableParameter> Parameters, string Note) Switch(
        IReadOnlyList<EditableParameter> parameters, string lengthUnit)
    {
        var notes = new List<string>();
        var result = parameters.Select(p => p.Clone()).ToList();

        double fHz = Read(result, "F", DefaultFHz, notes);
        if (!result.Any(p => p.Name == "F"))
        {
            notes.Add("No F was stated, so the line was converted at 1 GHz.");
            Insert(result, IndexAfter(result, "Z"), Row("F", "1", "GHz", UnitDimension.Frequency, true));
        }

        if (IsPhysical(result))
        {
            double l = Read(result, "L", 0.0, notes);
            double eeff = Read(result, "Eeff", 1.0, notes);
            double ac = Read(result, "Ac", 0.0, notes);
            double ad = Read(result, "Ad", 0.0, notes);
            double eDeg = 360.0 * fHz * l * Math.Sqrt(eeff) / MicrostripLoss.SpeedOfLight;
            double aDb = (ac + ad) * l;

            int at = result.FindIndex(p => p.Name == "L");
            result.RemoveAll(p => p.Name is "L" or "Eeff" or "Ac" or "Ad");
            Insert(result, at, Row("E", Fmt(eDeg), "deg", UnitDimension.Angle, true));
            if (aDb != 0.0)
                Insert(result, result.Count, Row("A", Fmt(aDb), "", UnitDimension.None, false));
            if (ac != 0.0)
                notes.Add("Ac scales as √f, which the electrical form cannot state; A is its value at F.");
        }
        else
        {
            double eRad = Read(result, "E", Math.PI / 2.0, notes);
            double aDb = Read(result, "A", 0.0, notes);
            double lM = eRad * MicrostripLoss.SpeedOfLight / (2.0 * Math.PI * fHz);   // Eeff = 1
            double scale = Units.Scale(UnitNormalizer.ToEngineUnit(lengthUnit)) ?? 1e-3;

            int at = result.FindIndex(p => p.Name == "E");
            result.RemoveAll(p => p.Name is "E" or "A");
            Insert(result, at, Row("L", Fmt(lM / scale), lengthUnit, UnitDimension.Length, true));
            Insert(result, at + 1, Row("Eeff", "1", "", UnitDimension.None, true));
            Insert(result, result.Count, Row("Ac", "0", "", UnitDimension.None, false));
            Insert(result, result.Count, Row("Ad", Fmt(lM > 0 ? aDb / lM : 0.0), "", UnitDimension.None, false));
        }

        return (result, string.Join(" ", notes));
    }

    /// <summary>A parameter's value in base SI, its own unit applied; <paramref name="fallback"/> when it is
    /// absent, and also — with a note — when it is not a plain number.</summary>
    private static double Read(List<EditableParameter> ps, string name, double fallback, List<string> notes)
    {
        var p = ps.FirstOrDefault(x => x.Name == name);
        if (p is null) return fallback;
        if (!double.TryParse(p.Expression.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
        {
            notes.Add($"{name} = {p.Expression} is not a plain number; the conversion used its default.");
            return fallback;
        }
        return raw * (p.Unit.Length == 0 ? 1.0 : Units.Scale(UnitNormalizer.ToEngineUnit(p.Unit)) ?? 1.0);
    }

    private static EditableParameter Row(string name, string expr, string unit, UnitDimension dim, bool show)
        => new() { Name = name, Expression = expr, Unit = unit, ShowOnSchematic = show, Dimension = dim };

    private static int IndexAfter(List<EditableParameter> ps, string name)
    {
        int i = ps.FindIndex(p => p.Name == name);
        return i < 0 ? ps.Count : i + 1;
    }

    private static void Insert(List<EditableParameter> ps, int at, EditableParameter p)
        => ps.Insert(Math.Clamp(at < 0 ? ps.Count : at, 0, ps.Count), p);

    private static string Fmt(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
