using System;
using System.Collections.Generic;
using System.Globalization;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// Straight-line Verilog-A assignments evaluated with Verilog-A's own meanings — <c>ln</c> natural, <c>log</c> base 10,
/// <c>pow</c>, <c>c ? a : b</c>, comparisons as 1 or 0 — so a translation is checked by what its text computes and not by
/// how it is spelled (brief-modelcomposer-26 §4). Written for the test and independent of the writer: it shares nothing
/// with it but the language.
/// </summary>
public sealed class VaInterpreter(IDictionary<string, double> variables)
{
    public IDictionary<string, double> Variables { get; } = variables;

    /// <summary>Runs <c>name = expression;</c> statements in order.</summary>
    public void Run(IEnumerable<string> statements)
    {
        foreach (string s in statements)
        {
            int eq = s.IndexOf('=');
            string name = s[..eq].Trim();
            Variables[name] = Eval(s[(eq + 1)..].TrimEnd().TrimEnd(';'));
        }
    }

    public double Eval(string expression)
    {
        _s = expression;
        _p = 0;
        double v = Ternary();
        Skip();
        if (_p != _s.Length) throw new FormatException($"unexpected '{_s[_p..]}' in '{_s}'");
        return v;
    }

    private string _s = "";
    private int _p;

    private void Skip() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }

    private bool Take(string token)
    {
        Skip();
        if (string.CompareOrdinal(_s, _p, token, 0, token.Length) != 0) return false;
        // "<" must not take the first character of "<=", nor "!" that of "!=".
        if (token is "<" or ">" or "!" or "=" && _p + 1 < _s.Length && _s[_p + 1] == '=') return false;
        _p += token.Length;
        return true;
    }

    private double Ternary()
    {
        double c = Or();
        if (!Take("?")) return c;
        double a = Ternary();
        if (!Take(":")) throw new FormatException("':' expected");
        double b = Ternary();
        return c != 0 ? a : b;
    }

    private double Or()
    {
        double v = And();
        while (Take("||")) { double r = And(); v = v != 0 || r != 0 ? 1 : 0; }
        return v;
    }

    private double And()
    {
        double v = Compare();
        while (Take("&&")) { double r = Compare(); v = v != 0 && r != 0 ? 1 : 0; }
        return v;
    }

    private double Compare()
    {
        double l = Sum();
        foreach (string op in (string[])["<=", ">=", "==", "!=", "<", ">"])
            if (Take(op))
            {
                double r = Sum();
                return op switch
                {
                    "<=" => l <= r ? 1 : 0, ">=" => l >= r ? 1 : 0, "==" => l == r ? 1 : 0,
                    "!=" => l != r ? 1 : 0, "<" => l < r ? 1 : 0, _ => l > r ? 1 : 0,
                };
            }
        return l;
    }

    private double Sum()
    {
        double v = Product();
        while (true)
        {
            if (Take("+")) v += Product();
            else if (Take("-")) v -= Product();
            else return v;
        }
    }

    private double Product()
    {
        double v = Unary();
        while (true)
        {
            if (Take("*")) v *= Unary();
            else if (Take("/")) v /= Unary();
            else return v;
        }
    }

    private double Unary()
    {
        if (Take("-")) return -Unary();
        if (Take("+")) return Unary();
        if (Take("!")) return Unary() == 0 ? 1 : 0;
        return Primary();
    }

    private double Primary()
    {
        Skip();
        if (Take("("))
        {
            double v = Ternary();
            if (!Take(")")) throw new FormatException("')' expected");
            return v;
        }
        if (Take("`M_PI")) return Math.PI;
        if (Take("`M_E")) return Math.E;
        int start = _p;
        if (char.IsAsciiDigit(_s[_p]) || _s[_p] == '.')
        {
            while (_p < _s.Length && (char.IsAsciiDigit(_s[_p]) || _s[_p] == '.')) _p++;
            if (_p < _s.Length && _s[_p] is 'e' or 'E')
            {
                _p++;
                if (_s[_p] is '+' or '-') _p++;
                while (_p < _s.Length && char.IsAsciiDigit(_s[_p])) _p++;
            }
            return double.Parse(_s[start.._p], CultureInfo.InvariantCulture);
        }
        while (_p < _s.Length && (char.IsAsciiLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
        string name = _s[start.._p];
        if (name.Length == 0) throw new FormatException($"unexpected '{_s[_p..]}'");
        if (!Take("(")) return Variables.TryGetValue(name, out double v) ? v : throw new KeyNotFoundException(name);
        var args = new List<double> { Ternary() };
        while (Take(",")) args.Add(Ternary());
        if (!Take(")")) throw new FormatException("')' expected");
        return Call(name, args);
    }

    private static double Call(string name, List<double> a) => name switch
    {
        "exp" => Math.Exp(a[0]),
        "ln" => Math.Log(a[0]),
        "log" => Math.Log10(a[0]),
        "sqrt" => Math.Sqrt(a[0]),
        "pow" => Math.Pow(a[0], a[1]),
        "abs" => Math.Abs(a[0]),
        "min" => Math.Min(a[0], a[1]),
        "max" => Math.Max(a[0], a[1]),
        "sin" => Math.Sin(a[0]), "cos" => Math.Cos(a[0]), "tan" => Math.Tan(a[0]),
        "sinh" => Math.Sinh(a[0]), "cosh" => Math.Cosh(a[0]), "tanh" => Math.Tanh(a[0]),
        "asin" => Math.Asin(a[0]), "acos" => Math.Acos(a[0]), "atan" => Math.Atan(a[0]),
        "atan2" => Math.Atan2(a[0], a[1]),
        _ => throw new KeyNotFoundException($"'{name}' is not a Verilog-A function this interpreter knows"),
    };
}
