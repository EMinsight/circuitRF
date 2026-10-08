using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

public enum ValueKind { Real, Complex, Bool, String, Cube, All }

/// <summary>
/// A kinded value produced by the expression engine: Real, Complex, Bool, String, Cube, or All.
/// Real and Complex map directly to the DataKind used in the result model.
/// Bool is internal to conditionals and never the final value of a parameter.
/// String is storage-only (no operators, no coercions) — used for SnP/N-port config
/// params (File, Type, InterpMode, InterpDomain, ExtrapMode). A String value is a type error
/// anywhere a number is required.
/// Cube is a DataCube operand produced by a measurement accessor; arithmetic broadcasts element-wise.
/// All is a sentinel used in accessor slice positions to keep an entire axis (mirrors DataCube.All).
/// </summary>
public readonly struct Value
{
    public ValueKind Kind { get; }

    private readonly double    _real;
    private readonly Complex   _complex;
    private readonly bool      _bool;
    private readonly string?   _string;
    private readonly DataCube? _cube;

    public static readonly Value Zero        = new(0.0);
    public static readonly Value One         = new(1.0);
    public static readonly Value Pi          = new(Math.PI);
    public static readonly Value E           = new(Math.E);
    public static readonly Value J           = new(Complex.ImaginaryOne);
    public static readonly Value True        = new(true);
    public static readonly Value False       = new(false);
    public static readonly Value AllSentinel = new(ValueKind.All);

    public Value(double real)       { Kind = ValueKind.Real;    _real    = real; }
    public Value(Complex complex)   { Kind = ValueKind.Complex; _complex = complex; }
    public Value(bool b)            { Kind = ValueKind.Bool;    _bool    = b; }
    public Value(string s)          { Kind = ValueKind.String;  _string  = s; }
    public Value(DataCube cube)     { Kind = ValueKind.Cube;    _cube    = cube; }
    private Value(ValueKind kind)   { Kind = kind; }   // for AllSentinel

    public double    AsReal()    => Kind == ValueKind.Real    ? _real    : throw new InvalidCastException($"Value is {Kind}, not Real");
    public Complex   AsComplex() => Kind == ValueKind.Complex ? _complex : throw new InvalidCastException($"Value is {Kind}, not Complex");
    public bool      AsBool()    => Kind == ValueKind.Bool    ? _bool    : throw new InvalidCastException($"Value is {Kind}, not Bool");
    public string    AsString()  => Kind == ValueKind.String  ? _string! : throw new InvalidCastException($"Value is {Kind}, not String");
    public DataCube  AsCube()    => Kind == ValueKind.Cube    ? _cube!   : throw new InvalidCastException($"Value is {Kind}, not Cube");

    /// <summary>Returns the value as Complex regardless of kind (promotes Real; errors on Bool/String).</summary>
    public Complex ToComplex() => Kind switch
    {
        ValueKind.Real    => new Complex(_real, 0),
        ValueKind.Complex => _complex,
        ValueKind.String  => throw new InvalidCastException("Cannot convert String to Complex"),
        _                 => throw new InvalidCastException($"Cannot convert {Kind} to Complex")
    };

    public override string ToString() => Kind switch
    {
        ValueKind.Real    => _real.ToString("G"),
        ValueKind.Complex => _complex.ToString(),
        ValueKind.Bool    => _bool.ToString(),
        ValueKind.String  => _string ?? "",
        ValueKind.Cube    => "<DataCube>",
        ValueKind.All     => "All",
        _                 => "?"
    };

    // ── Promotion helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Promotes two operands to a common numeric kind for arithmetic.
    /// Returns the higher kind: if either is Complex, both become Complex.
    /// Throws if either is Bool.
    /// </summary>
    public static ValueKind CommonArithmeticKind(Value a, Value b)
    {
        if (a.Kind == ValueKind.Bool   || b.Kind == ValueKind.Bool)
            throw new ExpressionException("Bool value used in arithmetic context");
        if (a.Kind == ValueKind.String || b.Kind == ValueKind.String)
            throw new ExpressionException("String value used in arithmetic context");
        if (a.Kind == ValueKind.Cube   || b.Kind == ValueKind.Cube)
            throw new ExpressionException("Cube operand must go through Add/Sub/Mul/Div — not CommonArithmeticKind");
        return (a.Kind == ValueKind.Complex || b.Kind == ValueKind.Complex)
            ? ValueKind.Complex
            : ValueKind.Real;
    }

    // ── Arithmetic ───────────────────────────────────────────────────────────

    public static Value Add(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube)
        {
            if (a.Kind == ValueKind.Cube && b.Kind == ValueKind.Cube) return new Value(a._cube! + b._cube!);
            if (a.Kind == ValueKind.Cube) return b.Kind == ValueKind.Complex ? new Value(a._cube! + b._complex) : new Value(a._cube! + b._real);
            return a.Kind == ValueKind.Complex ? new Value(b._cube! + a._complex) : new Value(b._cube! + a._real);
        }
        var kind = CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(a._real + b._real)
            : new Value(a.ToComplex() + b.ToComplex());
    }

    public static Value Sub(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube)
        {
            if (a.Kind == ValueKind.Cube && b.Kind == ValueKind.Cube) return new Value(a._cube! - b._cube!);
            if (a.Kind == ValueKind.Cube) return b.Kind == ValueKind.Complex ? new Value(a._cube! - b._complex) : new Value(a._cube! - b._real);
            return a.Kind == ValueKind.Complex ? new Value(a._complex - b._cube!) : new Value(a._real - b._cube!);
        }
        var kind = CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(a._real - b._real)
            : new Value(a.ToComplex() - b.ToComplex());
    }

    public static Value Mul(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube)
        {
            if (a.Kind == ValueKind.Cube && b.Kind == ValueKind.Cube) return new Value(a._cube! * b._cube!);
            if (a.Kind == ValueKind.Cube) return b.Kind == ValueKind.Complex ? new Value(a._cube! * b._complex) : new Value(a._cube! * b._real);
            return a.Kind == ValueKind.Complex ? new Value(b._cube! * a._complex) : new Value(b._cube! * a._real);
        }
        var kind = CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(a._real * b._real)
            : new Value(a.ToComplex() * b.ToComplex());
    }

    public static Value Div(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube)
        {
            if (a.Kind == ValueKind.Cube && b.Kind == ValueKind.Cube) return new Value(a._cube! / b._cube!);
            if (a.Kind == ValueKind.Cube) return b.Kind == ValueKind.Complex ? new Value(a._cube! / b._complex) : new Value(a._cube! / b._real);
            return a.Kind == ValueKind.Complex ? new Value(a._complex / b._cube!) : new Value(a._real / b._cube!);
        }
        var kind = CommonArithmeticKind(a, b);
        if (kind == ValueKind.Real)
        {
            if (b._real == 0) throw new ExpressionException("Division by zero");
            return new Value(a._real / b._real);
        }
        var bc = b.ToComplex();
        if (bc == Complex.Zero) throw new ExpressionException("Division by zero");
        return new Value(a.ToComplex() / bc);
    }

    public static Value Pow(Value a, Value b)
    {
        var kind = CommonArithmeticKind(a, b);
        if (kind == ValueKind.Real)
        {
            // negative base with non-integer exponent → promote to Complex
            double result = Math.Pow(a._real, b._real);
            if (double.IsNaN(result) && a._real < 0)
                return new Value(Complex.Pow(new Complex(a._real, 0), new Complex(b._real, 0)));
            return new Value(result);
        }
        return new Value(Complex.Pow(a.ToComplex(), b.ToComplex()));
    }

    public static Value Negate(Value a) => a.Kind switch
    {
        ValueKind.Real    => new Value(-a._real),
        ValueKind.Complex => new Value(-a._complex),
        ValueKind.Cube    => new Value(-a._cube!),
        _                 => throw new ExpressionException($"Cannot negate a {a.Kind}")
    };

    public static Value UnaryPlus(Value a) => a.Kind switch
    {
        ValueKind.Real    => a,
        ValueKind.Complex => a,
        ValueKind.Cube    => a,
        _                 => throw new ExpressionException($"Cannot apply unary + to a {a.Kind}")
    };

    // ── Comparison ───────────────────────────────────────────────────────────

    public static Value LessThan(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return CompareCubes(a, b, "<", d => d < 0);
        RequireReal(a, "<"); RequireReal(b, "<");
        return new Value(a._real < b._real);
    }

    public static Value LessOrEqual(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return CompareCubes(a, b, "<=", d => d <= 0);
        RequireReal(a, "<="); RequireReal(b, "<=");
        return new Value(a._real <= b._real);
    }

    public static Value GreaterThan(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return CompareCubes(a, b, ">", d => d > 0);
        RequireReal(a, ">"); RequireReal(b, ">");
        return new Value(a._real > b._real);
    }

    public static Value GreaterOrEqual(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return CompareCubes(a, b, ">=", d => d >= 0);
        RequireReal(a, ">="); RequireReal(b, ">=");
        return new Value(a._real >= b._real);
    }

    public static Value Equal(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return CompareCubes(a, b, "==", d => d == 0);
        if (a.Kind == ValueKind.Bool || b.Kind == ValueKind.Bool)
            throw new ExpressionException("Cannot compare Bool values with ==");

        // String equality is well defined and is the one operation a String value supports.
        // It stays deliberately narrow: == and != only, both sides String, ordinal comparison, no
        // coercion in either direction. Everything else about String remains storage-only, so a
        // string in an arithmetic context is still an error.
        if (a.Kind == ValueKind.String || b.Kind == ValueKind.String)
        {
            if (a.Kind != b.Kind)
                throw new ExpressionException(
                    $"Cannot compare a String with a {(a.Kind == ValueKind.String ? b.Kind : a.Kind)} using ==");
            return new Value(string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal));
        }

        _ = CommonArithmeticKind(a, b); // validates kinds
        return a.Kind == ValueKind.Real && b.Kind == ValueKind.Real
            ? new Value(a._real == b._real)
            : new Value(a.ToComplex() == b.ToComplex());
    }

    public static Value NotEqual(Value a, Value b)
    {
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return CompareCubes(a, b, "!=", d => d != 0);
        var eq = Equal(a, b);
        return new Value(!eq._bool);
    }

    // ── Logic ────────────────────────────────────────────────────────────────

    public static Value Not(Value a)
    {
        if (a.Kind == ValueKind.Cube) return new Value(MapTruth(a._cube!, "!", t => t == 0 ? 1 : 0));
        RequireBool(a, "!");
        return new Value(!a._bool);
    }

    public static Value And(Value a, Value b)
    {
        RequireBool(a, "&&"); RequireBool(b, "&&");
        return new Value(a._bool && b._bool);
    }

    public static Value Or(Value a, Value b)
    {
        RequireBool(a, "||"); RequireBool(b, "||");
        return new Value(a._bool || b._bool);
    }

    // ── Comparisons over cubes ───────────────────────────────────────────────

    /// <summary>
    /// A comparison with a cube on either side is taken ELEMENT BY ELEMENT and gives a real cube of 1 (true) and 0
    /// (false), broadcast as arithmetic is — so <c>yield_over(goal:G:worst &gt; 14)</c> counts the trials above a
    /// limit (docs/design/measurements.md "Reductions over an axis"). Real values only, as for scalars; a NaN element (a trial with no
    /// value) stays NaN, so a reduction that skips NaN skips it rather than counting it false.
    /// </summary>
    private static Value CompareCubes(Value a, Value b, string op, Func<double, bool> holds)
    {
        foreach (var v in new[] { a, b })
            if (v.Kind == ValueKind.Cube ? v._cube!.DataKind != RfCore.Data.DataKind.Real : v.Kind != ValueKind.Real)
                throw new ExpressionException($"Operator '{op}' requires real operands, got {(v.Kind == ValueKind.Cube ? "a complex cube" : v.Kind)}");
        var diff = Sub(a, b).AsCube();
        var d = diff.RealValues;
        var r = new double[d.Length];
        for (int i = 0; i < d.Length; i++) r[i] = double.IsNaN(d[i]) ? double.NaN : holds(d[i]) ? 1 : 0;
        return new Value(new RfCore.Data.DataCube([.. diff.Axes], r));
    }

    /// <summary>
    /// <c>&amp;&amp;</c>, <c>||</c> with a cube on either side: element by element over truth values (non-zero is
    /// true; a Bool scalar broadcasts), no short circuit — every element needs both sides.
    /// </summary>
    internal static Value LogicCubes(Value a, Value b, string op)
    {
        static RfCore.Data.DataCube Truth(Value v, string op) => v.Kind switch
        {
            ValueKind.Cube => MapTruth(v._cube!, op, t => t),
            ValueKind.Bool => RfCore.Data.DataCube.Scalar(v._bool ? 1.0 : 0.0),
            _              => throw new ExpressionException($"Operator '{op}' requires Bool or a cube of truth values, got {v.Kind}"),
        };
        var l = Truth(a, op);
        var r = Truth(b, op);
        // a·b is AND over 0/1; a + b − a·b is OR. NaN propagates through both, as a comparison's does.
        var prod = l * r;
        return new Value(op == "&&" ? prod : l + r - prod);
    }

    private static RfCore.Data.DataCube MapTruth(RfCore.Data.DataCube c, string op, Func<double, double> f)
    {
        if (c.DataKind != RfCore.Data.DataKind.Real)
            throw new ExpressionException($"Operator '{op}' requires real operands, got a complex cube");
        var d = c.RealValues;
        var r = new double[d.Length];
        for (int i = 0; i < d.Length; i++) r[i] = double.IsNaN(d[i]) ? double.NaN : f(d[i] != 0 ? 1 : 0);
        return new RfCore.Data.DataCube([.. c.Axes], r);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    private static void RequireReal(Value v, string op)
    {
        if (v.Kind != ValueKind.Real)
            throw new ExpressionException($"Operator '{op}' requires real operands, got {v.Kind}");
    }

    private static void RequireBool(Value v, string op)
    {
        if (v.Kind != ValueKind.Bool)
            throw new ExpressionException($"Operator '{op}' requires Bool operands, got {v.Kind}");
    }
}
