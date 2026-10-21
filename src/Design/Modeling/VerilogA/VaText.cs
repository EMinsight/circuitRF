using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>
/// The spelling rules every line of an exported module follows (brief-modelcomposer-26 R-mc26-1, R-mc26-2): numbers that
/// read back as the same double, identifiers a Verilog-A compiler accepts, and the words it reserves.
/// </summary>
public static partial class VaText
{
    /// <summary>
    /// A double in shortest round-trip form, invariant culture, with a lower-case exponent where one is needed (<c>1e-08</c>).
    /// <b>Always a real literal</b>: a whole number gains <c>.0</c>, because <c>1/2</c> between two integer literals is
    /// integer division in Verilog-A and is zero.
    /// </summary>
    public static string Num(double x)
    {
        if (!double.IsFinite(x)) throw new ArgumentException($"{x} has no Verilog-A literal.", nameof(x));
        string s = x.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
        if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0) s += ".0";
        return s;
    }

    /// <summary><see cref="Num"/> as an operand: a negative literal is parenthesised, so <c>a*-1</c> never appears.</summary>
    public static string Operand(double x) => x < 0 || (x == 0 && double.IsNegative(x)) ? $"({Num(x)})" : Num(x);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Whether <paramref name="name"/> is a simple identifier (a letter or underscore, then letters, digits and
    /// underscores) and no reserved word.</summary>
    public static bool IsIdentifier(string name) => IdentifierPattern().IsMatch(name) && !IsReserved(name);

    /// <summary>Whether <paramref name="name"/> is a keyword, a built-in function or a standard discipline or nature name.</summary>
    public static bool IsReserved(string name) => Reserved.Contains(name);

    /// <summary>
    /// A <c>.cmodel</c>'s file name as a module name: every character that cannot appear in an identifier becomes <c>_</c>,
    /// and a leading digit gains an <c>m_</c> prefix. A reserved word gains the same prefix.
    /// </summary>
    public static string ModuleNameFrom(string fileStem)
    {
        var sb = new StringBuilder(fileStem.Length);
        foreach (char c in fileStem) sb.Append(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? c : '_');
        string s = sb.Length == 0 ? "composed_fet" : sb.ToString();
        if (char.IsAsciiDigit(s[0]) || IsReserved(s)) s = "m_" + s;
        return s;
    }

    /// <summary>Text with every character outside printable ASCII replaced, for a comment: some compilers read a source
    /// file as ASCII.</summary>
    public static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c switch
            {
                '°' => "deg",
                'µ' or 'μ' => "u",
                'Ω' => "Ohm",
                '−' or '–' or '—' => "-",
                '·' or '×' => "*",
                >= ' ' and <= '~' => c.ToString(),
                _ => "?",
            });
        return sb.ToString();
    }

    /// <summary>
    /// Verilog-AMS 2.4's keywords (Annex B), its analog operators, built-in mathematical functions and system parameters
    /// that a module may not redeclare, and the disciplines and natures <c>disciplines.vams</c> declares — every identifier
    /// an exported module's own names must not take.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        // Annex B keywords.
        "above", "abs", "absdelay", "absdelta", "abstol", "access", "acos", "acosh", "ac_stim", "aliasparam", "always",
        "analog", "analysis", "and", "asin", "asinh", "assert", "assign", "atan", "atan2", "atanh", "automatic", "begin",
        "branch", "buf", "bufif0", "bufif1", "case", "casex", "casez", "ceil", "cell", "cmos", "config", "connect",
        "connectmodule", "connectrules", "continuous", "cos", "cosh", "cross", "ddt", "ddt_nature", "ddx", "deassign",
        "default", "defparam", "design", "disable", "discipline", "discrete", "domain", "driver_update", "edge", "else",
        "end", "endcase", "endconfig", "endconnectrules", "enddiscipline", "endfunction", "endgenerate", "endmodule",
        "endnature", "endparamset", "endprimitive", "endspecify", "endtable", "endtask", "event", "exclude", "exp",
        "final_step", "flicker_noise", "floor", "flow", "for", "force", "forever", "fork", "from", "function", "generate",
        "genvar", "ground", "highz0", "highz1", "hypot", "idt", "idt_nature", "idtmod", "if", "ifnone", "incdir", "include",
        "inf", "initial", "initial_step", "inout", "input", "instance", "integer", "join", "laplace_nd", "laplace_np",
        "laplace_zd", "laplace_zp", "large", "last_crossing", "liblist", "library", "limexp", "ln", "localparam", "log",
        "macromodule", "max", "medium", "merged", "min", "module", "nand", "nature", "negedge", "net_resolution", "nmos",
        "noise_table", "noise_table_log", "nor", "noshowcancelled", "not", "notif0", "notif1", "or", "output", "parameter",
        "paramset", "pmos", "posedge", "potential", "pow", "primitive", "pull0", "pull1", "pulldown", "pullup",
        "pulsestyle_onevent", "pulsestyle_ondetect", "rcmos", "real", "realtime", "reg", "release", "repeat", "resolveto",
        "rnmos", "rpmos", "rtran", "rtranif0", "rtranif1", "scalared", "sin", "sinh", "showcancelled", "signed", "slew",
        "small", "specify", "specparam", "split", "sqrt", "string", "strong0", "strong1", "supply0", "supply1", "table",
        "tan", "tanh", "task", "time", "timer", "tran", "tranif0", "tranif1", "transition", "tri", "tri0", "tri1",
        "triand", "trior", "trireg", "units", "unsigned", "use", "uwire", "vectored", "wait", "wand", "weak0", "weak1",
        "while", "white_noise", "wire", "wor", "wreal", "xnor", "xor", "zi_nd", "zi_np", "zi_zd", "zi_zp",
        // Operators, functions and the electrical and thermal disciplines and natures every module includes.
        "ac_stim", "limit", "electrical", "magnetic", "thermal", "kinematic", "kinematic_v", "rotational",
        "rotational_omega", "voltage", "current", "Voltage", "Current", "Flux", "Temperature", "Power", "Charge",
        "Magneto_Motive_Force", "Position", "Velocity", "Acceleration", "Force", "Angle", "Angular_Velocity",
        "Angular_Force", "Impulse", "Angular_Acceleration", "Jerk", "Angular_Jerk", "Current_noise", "Voltage_noise",
        "V", "I",
    };
}
