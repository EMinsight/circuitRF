using System.Text;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Expressions;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>One module parameter a trap model's Verilog-A reads: its name, value, unit, meaning, the <c>.cmodel</c> field it
/// comes from, and the range the model's own checks allow (<c>from (0:inf)</c>), or null.</summary>
public sealed record VaTrapParameter(string Name, double Value, string Unit, string Meaning, string Field, string? Range = null);

/// <summary>
/// How one trap model is written as Verilog-A (brief-modelcomposer-27 R-mc27-2) — the counterpart, for the export, of
/// <see cref="TrapModelDefinition"/>. A model a developer registers with <see cref="TrapModels"/> exports once it is given
/// one here (<see cref="VaTraps.Register"/>); until then the export refuses it by name.
///
/// <para>Each enabled state becomes an internal node whose voltage is the state, carrying
/// <c>I(phi) &lt;+ ddt(C·V(phi)) − C·Rate</c> (<see cref="RateTrapDynamics"/>' form), so an emitter supplies the rate and the
/// DC value as two analog functions. They read the intrinsic <c>vgs</c>, <c>vds</c> and the device temperature <c>T</c> in
/// °C, and the parameters <see cref="Parameters"/> declares under the names it gives them.</para>
/// </summary>
public interface ITrapVerilogA
{
    /// <summary>The module parameters the functions read, in the order the header lists them.</summary>
    IReadOnlyList<VaTrapParameter> Parameters(ModelTraps traps);

    /// <summary>
    /// The analog function <paramref name="name"/><c>(ph, vgs, vds, T)</c> returning state <paramref name="state"/>'s
    /// <c>dphi/dt</c>, V/s. A construct that cannot be written throws <see cref="VaUntranslatableException"/>.
    /// </summary>
    string Rate(ModelTraps traps, int state, string name);

    /// <summary>The analog function <paramref name="name"/><c>(vgs, vds, T)</c> returning the state's DC value, V.</summary>
    string Dc(ModelTraps traps, int state, string name);
}

/// <summary>The trap models the export writes, by name: <c>CaptureEmission</c> and <c>Custom</c> built in.</summary>
public static class VaTraps
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, ITrapVerilogA> _emitters = new Dictionary<string, ITrapVerilogA>(StringComparer.Ordinal)
    {
        [CaptureEmissionTraps.ModelName] = new CaptureEmissionVa(),
        [CustomTraps.ModelName] = new CustomVa(),
    };

    public static ITrapVerilogA? Find(string model) => _emitters.TryGetValue(model, out var e) ? e : null;

    /// <summary>Gives a registered trap model its writer; a name that already has one is refused.</summary>
    public static void Register(string model, ITrapVerilogA emitter)
    {
        lock (Gate)
        {
            if (_emitters.ContainsKey(model)) throw new ArgumentException($"'{model}' already has a Verilog-A writer.");
            _emitters = new Dictionary<string, ITrapVerilogA>(_emitters, StringComparer.Ordinal) { [model] = emitter };
        }
    }

    /// <summary>An analog function of real inputs returning <paramref name="body"/>'s last assignment.</summary>
    internal static string Function(string name, string comment, IReadOnlyList<string> inputs, IReadOnlyList<string> locals,
                                    IEnumerable<string> body)
    {
        var sb = new StringBuilder();
        sb.Append($"  // {comment}\n");
        sb.Append($"  analog function real {name};\n");
        sb.Append($"    input {string.Join(", ", inputs)};\n");
        sb.Append($"    real {string.Join(", ", inputs)};\n");
        foreach (var chunk in locals.Chunk(12)) sb.Append($"    real {string.Join(", ", chunk)};\n");
        sb.Append("    begin\n");
        foreach (string line in body) sb.Append($"      {line}\n");
        sb.Append("    end\n");
        sb.Append("  endfunction\n");
        return sb.ToString();
    }
}

/// <summary>
/// <see cref="CaptureEmissionTraps"/> as Verilog-A, line for line: emission towards the voltage, capture through the
/// gap's exponential continued by its tangent beyond <see cref="CaptureEmissionTraps.ExpLimit"/>, and thermally
/// activated emission (T in kelvin, <c>Tref</c> in °C) for a state whose <c>Ea</c> the model states.
/// </summary>
internal sealed class CaptureEmissionVa : ITrapVerilogA
{
    private static readonly CaptureEmissionTraps Definition = new();

    private static string Suffix(ModelTraps traps, int state) => traps.States[state] == CaptureEmissionTraps.GateLag ? "1" : "2";

    public IReadOnlyList<VaTrapParameter> Parameters(ModelTraps traps)
    {
        var list = new List<VaTrapParameter>();
        // The ranges are CaptureEmissionTraps.Build's refusals: times, the gap and Vc positive, Ea not negative, Tref above
        // absolute zero.
        void Add(string name)
        {
            var p = Definition.Parameters.First(x => x.Name == name);
            double value = traps.Parameter(name) ?? p.Default!.Value;
            string range = name.StartsWith("Ea", StringComparison.Ordinal) ? "from [0:inf)"
                         : name == "Tref" ? $"from ({Num(CmodelVerilogA.TempUnset)}:inf)"
                         : "from (0:inf)";
            list.Add(new VaTrapParameter(name, value, Ascii(p.Unit), p.Meaning, $"Traps.Parameters.{name}", range));
        }
        for (int k = 0; k < traps.States.Count; k++)
        {
            string n = Suffix(traps, k);
            Add("TauCapt" + n);
            Add("TauEmit" + n);
            if (traps.Parameter("Ea" + n) is not null) Add("Ea" + n);
        }
        Add("Xref");
        Add("Vc");
        Add("Tref");
        return list;
    }

    public string Rate(ModelTraps traps, int state, string name)
    {
        string n = Suffix(traps, state);
        bool gate = n == "1";
        bool activated = traps.Parameter("Ea" + n) is not null;
        string kelvin = Num(273.15);
        var body = new List<string>
        {
            // The capture term f(x) = (Xref/TauCapt)·(e^u − e^(−Xref/Vc)), u = (x − Xref)/Vc, continued by its tangent.
            gate ? "x = ph - vgs;" : "x = vds - ph;",
            "u = (x - Xref)/Vc;",
            $"if (u <= {Num(CaptureEmissionTraps.ExpLimit)}) ex = exp(u);",
            $"else ex = exp({Num(CaptureEmissionTraps.ExpLimit)})*(1.0 + (u - {Num(CaptureEmissionTraps.ExpLimit)}));",
            $"f = (Xref/TauCapt{n})*(ex - exp(-Xref/Vc));",
            // Emission: 1/TauEmit, thermally activated when Ea is given.
            activated
                ? $"tk = T + {kelvin}; tr = Tref + {kelvin}; re = (1.0/TauEmit{n})*(tk/tr)*(tk/tr)*exp(-Ea{n}/{Num(CaptureEmissionTraps.BoltzmannEv)}*(1.0/tk - 1.0/tr));"
                : $"re = 1.0/TauEmit{n};",
            gate ? $"{name} = (vgs - ph)*re - f;" : $"{name} = (vds - ph)*re + f;",
        };
        string what = gate ? "gate-lag phi1: emission towards vgs, capture downwards" : "drain-lag phi2: emission towards vds, capture upwards";
        return VaTraps.Function(name, $"Traps (CaptureEmission) {what}; dphi/dt in V/s", ["ph", "vgs", "vds", "T"],
                                ["x", "u", "ex", "f", "re", "tk", "tr"], body);
    }

    public string Dc(ModelTraps traps, int state, string name)
    {
        bool gate = Suffix(traps, state) == "1";
        return VaTraps.Function(name, $"Traps (CaptureEmission) {traps.States[state]}'s DC value: {(gate ? "vgs" : "vds")}",
                                ["vgs", "vds", "T"], [], [$"{name} = {(gate ? "vgs" : "vds")};"]);
    }
}

/// <summary>
/// <see cref="CustomTraps"/> as Verilog-A: each state's <c>Dynamics</c> and <c>Dc</c> expression through the equation
/// translator (the engine's own domain guards included), its parameters module parameters prefixed <c>Trap_</c>.
/// </summary>
internal sealed class CustomVa : ITrapVerilogA
{
    public const string Prefix = "Trap_";

    public IReadOnlyList<VaTrapParameter> Parameters(ModelTraps traps)
        => [.. traps.Parameters.Select(p => new VaTrapParameter(Prefix + p.Key, p.Value, "-", $"Traps.Parameters.{p.Key}",
                                                                $"Traps.Parameters.{p.Key}"))];

    public string Rate(ModelTraps traps, int state, string name)
        => Write(traps, state, name, CustomTraps.DynamicsKey, ["ph", "vgs", "vds", "T"]);

    public string Dc(ModelTraps traps, int state, string name)
        => Write(traps, state, name, CustomTraps.DcKey, ["vgs", "vds", "T"]);

    private static string Write(ModelTraps traps, int state, string name, string what, IReadOnlyList<string> inputs)
    {
        string s = traps.States[state];
        string key = CustomTraps.Key(s, what);
        string text = traps.Expression(key)!;
        var parameters = traps.Parameters.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        // Value only: no input is differentiated, the compiler does that; the state itself is the argument ph.
        var eq = new VaEquation([], r => r == s ? "ph" : parameters.Contains(r) ? Prefix + r : r);
        var (value, _) = eq.Emit(Parser.Parse(text));
        return VaTraps.Function(name, $"Traps.Expressions.{key} (Custom): {Ascii(text)}", inputs, eq.Locals,
                                [.. eq.Code, $"{name} = {value};"]);
    }
}
