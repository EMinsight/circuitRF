using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// Reads and writes the <c>tune</c>, <c>preset</c>, <c>goal</c> and <c>optimize</c> directives — the
/// <c>.cnl</c> spelling of a <see cref="TuningSetup"/>. The grammar is
/// <see cref="AnalysisDirectiveSchema.TuningDirectives"/>; the writer's output reads back to the same
/// setup and writes the same bytes again.
///
/// <para>A malformed line is REFUSED (a <see cref="TuningDirectiveException"/> carrying its
/// <see cref="TuningDirectiveDiagnostics"/> entry, which the reader turns into a
/// <see cref="CnlReadException"/> naming the line). A key the grammar does not know is a WARNING
/// naming the key, and the key is kept, so a file written by a later version round-trips.</para>
/// </summary>
public static class TuningDirectiveText
{
    /// <summary>The four keywords.</summary>
    public static bool IsKeyword(string word) => word is "tune" or "preset" or "goal" or "optimize";

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>Reads one directive's remainder (the text after the keyword) into
    /// <paramref name="tb"/>'s setup. Unknown keys are added to <paramref name="warnings"/>.</summary>
    public static void Read(string keyword, string rest, TestBench tb, List<string> warnings, int lineNumber)
    {
        var setup = tb.Tuning ??= new TuningSetup();
        var spec  = AnalysisDirectiveSchema.FindTuningDirective(keyword)!;
        void Unknown(string key) => warnings.Add(
            TuningDirectiveDiagnostics.UnknownKey(lineNumber, keyword, key, string.Join(", ", spec.Keys.Select(k => k.Name))).Render());

        switch (keyword)
        {
            case "tune":     setup.Variables.Add(ReadTune(rest, spec, Unknown)); break;
            case "preset":   setup.Presets.Add(ReadPreset(rest)); break;
            case "goal":     setup.Goals.Add(ReadGoal(rest, spec, Unknown)); break;
            case "optimize":
                if (setup.Optimizer is not null)
                    throw Refuse(TuningDirectiveDiagnostics.SecondOptimize());
                setup.Optimizer = ReadOptimize(rest, Unknown);
                break;
        }
    }

    private static TunableEntry ReadTune(string rest, TuningDirectiveSpec spec, Action<string> unknown)
    {
        var t = Tokens(rest);
        if (t.Count == 0 || t[0].Text.Contains('='))
            throw Refuse(TuningDirectiveDiagnostics.Malformed("tune", "a key comes first — tune R1.R min=… max=…"));
        if (!TunableKey.TryParse(t[0].Text, out _))
            throw Refuse(TuningDirectiveDiagnostics.KeyMalformed(t[0].Text));

        var e = new TunableEntry { Key = t[0].Text };
        for (int i = 1; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            switch (key.ToLowerInvariant())
            {
                case "min":      e.Min = value; break;
                case "max":      e.Max = value; break;
                case "step":     e.Step = value; break;
                case "scale":    e.Scale = Enum<TuneScale>(key, value, AnalysisDirectiveSchema.ScaleTokens); break;
                case "discrete": e.Discrete = Enum<TuneDiscrete>(key, value, AnalysisDirectiveSchema.DiscreteTokens); break;
                case "tune":     e.Tune = Bool(key, value); break;
                case "opt":      e.Opt = Bool(key, value); break;
                default:
                    (e.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        return e;
    }

    private static TuningPreset ReadPreset(string rest)
    {
        var t = Tokens(rest);
        if (t.Count == 0 || t[0].Text.Contains('=') && !t[0].Text.StartsWith('"'))
            throw Refuse(TuningDirectiveDiagnostics.Malformed("preset", "a name comes first — preset \"wide band\" R1.R=47 Ohm …"));

        var p = new TuningPreset { Name = Unquote(t[0].Text) };
        if (p.Name.Length == 0)
            throw Refuse(TuningDirectiveDiagnostics.Malformed("preset", "its name cannot be empty."));

        bool inValues = false;
        for (int i = 1; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            if (!inValues && key.Equals("created", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                    throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a date written yyyy-MM-ddTHH:mm:ssZ"));
                p.Created = DateTime.SpecifyKind(when, DateTimeKind.Utc);
                continue;
            }
            if (!inValues && key.Equals("lasttuned", StringComparison.OrdinalIgnoreCase))
            {
                p.IsLastTuned = Bool(key, value);
                continue;
            }
            inValues = true;
            if (!TunableKey.TryParse(key, out _))
                throw Refuse(TuningDirectiveDiagnostics.KeyMalformed(key));
            p.Values[key] = value;
        }
        return p;
    }

    private static OptimizationGoal ReadGoal(string rest, TuningDirectiveSpec spec, Action<string> unknown)
    {
        int eq = rest.IndexOf('=');
        if (eq <= 0)
            throw Refuse(TuningDirectiveDiagnostics.Malformed("goal", "a name, '=' and an expression come first — goal G1 = dB(SP1.S(2,1)) … ge -0.5"));
        string name = rest[..eq].Trim();
        if (!IsIdentifier(name)) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "a goal's name is one word."));

        string after = rest[(eq + 1)..];
        var t = Tokens(after);
        if (t.Count == 0) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "it has no expression."));

        var g = new OptimizationGoal { Name = name };

        // The expression: one quoted token, or every token up to the first that this grammar owns.
        int i;
        if (t[0].Text.StartsWith('"'))
        {
            g.Expression = Unquote(t[0].Text);
            i = 1;
        }
        else
        {
            int end = 1;
            while (end < t.Count && !IsGoalStop(t[end].Text)) end++;
            g.Expression = after[t[0].Start..(t[end - 1].Start + t[end - 1].Text.Length)];
            i = end;
        }
        if (g.Expression.Trim().Length == 0) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "it has no expression."));

        bool typed = false;
        string? over = null, lo = null, hi = null;
        for (; i < t.Count; i++)
        {
            string tok = t[i].Text;
            if (TryGoalType(tok, out var type))
            {
                if (typed) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "it states its type twice."));
                typed  = true;
                g.Type = type;
                g.Limit = Limit(t, ref i, name, tok);
                if (type is GoalType.In or GoalType.Out)
                    g.UpperLimit = Limit(t, ref i, name, tok);
                else if (i + 1 < t.Count && t[i + 1].Text == "to")
                {
                    i++;
                    g.LimitAtHi = Limit(t, ref i, name, "to");
                }
                continue;
            }

            var (key, value) = KeyValue(t, ref i, unitsAllowed: true, goalLine: true);
            switch (key.ToLowerInvariant())
            {
                case "analysis": g.Analysis = value; break;
                case "over":     over = value; break;
                case "lo":       lo = value; break;
                case "hi":       hi = value; break;
                case "weight":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                        throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a number"));
                    g.Weight = w;
                    break;
                case "enabled":  g.Enabled = Bool(key, value); break;
                default:
                    (g.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }

        if (!typed)
            throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name,
                $"it states no type; end it with one of {string.Join(", ", AnalysisDirectiveSchema.GoalTypeTokens)} and its limit."));
        if (lo is not null || hi is not null || over is not null)
        {
            if (lo is null || hi is null)
                throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "its range needs both lo= and hi=."));
            g.Range = new GoalRange { Axis = over ?? "freq", Lo = lo, Hi = hi };
        }
        return g;
    }

    private static OptimizerSettings ReadOptimize(string rest, Action<string> unknown)
    {
        var t = Tokens(rest);
        var o = new OptimizerSettings();
        for (int i = 0; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            string lower = key.ToLowerInvariant();
            if (lower.StartsWith(AnalysisDirectiveSchema.AlgorithmOptionPrefix, StringComparison.Ordinal)
                && key.Length > AnalysisDirectiveSchema.AlgorithmOptionPrefix.Length)
            {
                (o.Options ??= new(StringComparer.Ordinal))[key[AnalysisDirectiveSchema.AlgorithmOptionPrefix.Length..]] = value;
                continue;
            }
            switch (lower)
            {
                case "algorithm": o.Algorithm = value; break;
                case "maxiter":   o.MaxIterations = Int(key, value); break;
                case "maxevals":  o.MaxEvaluations = Int(key, value); break;
                case "timelimit": o.TimeLimit = value; break;
                case "seed":      o.Seed = Int(key, value); break;
                case "parallel":  o.Parallelism = Int(key, value); break;
                case "cost":
                    o.Cost = value.ToLowerInvariant() switch
                    {
                        "lsq"     => OptimizerCost.LeastSquares,
                        "minimax" => OptimizerCost.Minimax,
                        _ => throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "one of " + string.Join(", ", AnalysisDirectiveSchema.CostTokens))),
                    };
                    break;
                case "analyses":
                    o.Scope = value.ToLowerInvariant() switch
                    {
                        "goals" => OptimizerScope.GoalAnalyses,
                        "all"   => OptimizerScope.All,
                        _ => throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "one of " + string.Join(", ", AnalysisDirectiveSchema.ScopeTokens))),
                    };
                    break;
                default:
                    (o.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        return o;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>The directive lines for <paramref name="setup"/>, in the order tune, preset, goal,
    /// optimize. Empty for an empty setup.</summary>
    public static IEnumerable<string> Write(TuningSetup setup)
    {
        foreach (var e in setup.Variables) yield return WriteTune(e);
        foreach (var p in setup.Presets)   yield return WritePreset(p);
        foreach (var g in setup.Goals)     yield return WriteGoal(g);
        if (setup.Optimizer is { } o)      yield return WriteOptimize(o);
    }

    private static string WriteTune(TunableEntry e)
    {
        var sb = new StringBuilder("tune ").Append(e.Key);
        Opt(sb, "min", e.Min);
        Opt(sb, "max", e.Max);
        if (e.Scale != TuneScale.Auto) sb.Append(" scale=").Append(AnalysisDirectiveSchema.ScaleTokens[(int)e.Scale]);
        Opt(sb, "step", e.Step);
        if (e.Discrete != TuneDiscrete.None) sb.Append(" discrete=").Append(AnalysisDirectiveSchema.DiscreteTokens[(int)e.Discrete]);
        if (e.Tune) sb.Append(" tune=1");
        if (e.Opt)  sb.Append(" opt=1");
        Extra(sb, e.Extra, "");
        return sb.ToString();
    }

    private static string WritePreset(TuningPreset p)
    {
        var sb = new StringBuilder("preset \"").Append(p.Name).Append('"');
        if (p.Created is { } c) sb.Append(" created=").Append(TuningPreset.FormatCreated(c));
        if (p.IsLastTuned) sb.Append(" lasttuned=1");
        foreach (var (k, v) in p.Values) Opt(sb, k, v);
        return sb.ToString();
    }

    private static string WriteGoal(OptimizationGoal g)
    {
        var sb = new StringBuilder("goal ").Append(g.Name).Append(" = ");
        sb.Append(ExpressionNeedsQuotes(g.Expression) ? $"\"{g.Expression}\"" : g.Expression);
        Opt(sb, "analysis", g.Analysis);
        if (g.Range is { } r)
        {
            Opt(sb, "over", r.Axis);
            Opt(sb, "lo", r.Lo, goalLine: true);
            Opt(sb, "hi", r.Hi, goalLine: true);
        }
        sb.Append(' ').Append(AnalysisDirectiveSchema.GoalTypeTokens[(int)g.Type]).Append(' ').Append(Value(g.Limit, goalLine: true));
        if (g.Type is GoalType.In or GoalType.Out)
            sb.Append(' ').Append(Value(g.UpperLimit ?? "", goalLine: true));
        else if (g.LimitAtHi is { } atHi)
            sb.Append(" to ").Append(Value(atHi, goalLine: true));
        if (g.Weight != 1.0) sb.Append(" weight=").Append(g.Weight.ToString("R", CultureInfo.InvariantCulture));
        if (!g.Enabled) sb.Append(" enabled=false");
        Extra(sb, g.Extra, "");
        return sb.ToString();
    }

    private static string WriteOptimize(OptimizerSettings o)
    {
        var sb = new StringBuilder("optimize algorithm=").Append(o.Algorithm);
        if (o.MaxIterations  is { } it) sb.Append(" maxiter=").Append(it.ToString(CultureInfo.InvariantCulture));
        if (o.MaxEvaluations is { } ev) sb.Append(" maxevals=").Append(ev.ToString(CultureInfo.InvariantCulture));
        if (o.TimeLimit is { } limit) sb.Append(" timelimit=").Append(Value(limit, duration: true));
        if (o.Cost  != OptimizerCost.LeastSquares) sb.Append(" cost=").Append(AnalysisDirectiveSchema.CostTokens[(int)o.Cost]);
        if (o.Scope != OptimizerScope.GoalAnalyses) sb.Append(" analyses=").Append(AnalysisDirectiveSchema.ScopeTokens[(int)o.Scope]);
        if (o.Seed        is { } s) sb.Append(" seed=").Append(s.ToString(CultureInfo.InvariantCulture));
        if (o.Parallelism is { } n) sb.Append(" parallel=").Append(n.ToString(CultureInfo.InvariantCulture));
        Extra(sb, o.Options, AnalysisDirectiveSchema.AlgorithmOptionPrefix);
        Extra(sb, o.Extra, "");
        return sb.ToString();
    }

    private static void Opt(StringBuilder sb, string key, string? value, bool goalLine = false)
    {
        if (value is null) return;
        sb.Append(' ').Append(key).Append('=').Append(Value(value, goalLine));
    }

    private static void Extra(StringBuilder sb, OrderedDictionary<string, string>? map, string prefix)
    {
        if (map is null) return;
        foreach (var (k, v) in map) Opt(sb, prefix + k, v);
    }

    /// <summary>
    /// A value as it must be written to read back as itself: as-is when it is one token or one token
    /// and a unit (the way <c>measure</c> and an instance line write a unit), quoted otherwise.
    /// </summary>
    private static string Value(string text, bool goalLine = false, bool duration = false)
    {
        if (text.Length == 0) return "\"\"";
        var t = Tokens(text);
        bool plain = text == text.Trim() && !text.Contains('"')
                  && (t.Count == 1
                      || t.Count == 2 && text == t[0].Text + " " + t[1].Text
                                      && (duration ? IsTimeUnit(t[1].Text) : IsUnit(t[1].Text, goalLine)));
        return plain ? text : $"\"{text}\"";
    }

    /// <summary>True when the reader would end the expression early: some token after the first is
    /// one this grammar owns.</summary>
    private static bool ExpressionNeedsQuotes(string expression)
    {
        var t = Tokens(expression);
        return t.Count == 0 || t[0].Text.StartsWith('"') || t.Skip(1).Any(x => IsGoalStop(x.Text));
    }

    // ── Shared token handling ────────────────────────────────────────────────

    private readonly record struct Token(string Text, int Start);

    /// <summary>Splits on whitespace outside quotes and outside ( ) [ ] { } — so an expression's own
    /// spacing (<c>max_over(x, freq)</c>) stays inside one token.</summary>
    private static List<Token> Tokens(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            int start = i, depth = 0;
            bool quoted = false;
            while (i < s.Length && (quoted || depth > 0 || !char.IsWhiteSpace(s[i])))
            {
                char c = s[i];
                if (c == '"') quoted = !quoted;
                else if (!quoted && c is '(' or '[' or '{') depth++;
                else if (!quoted && c is ')' or ']' or '}' && depth > 0) depth--;
                i++;
            }
            tokens.Add(new Token(s[start..i], start));
        }
        return tokens;
    }

    /// <summary>Reads <c>key=value</c> at <paramref name="i"/>, then a unit token after it when one
    /// follows. Advances <paramref name="i"/> past whatever it consumed.</summary>
    private static (string Key, string Value) KeyValue(List<Token> t, ref int i, bool unitsAllowed, bool goalLine = false)
    {
        string tok = t[i].Text;
        int eq = tok.IndexOf('=');
        if (eq <= 0) throw Refuse(TuningDirectiveDiagnostics.NotKeyValue(tok));
        string key = tok[..eq];
        string value = tok[(eq + 1)..];
        if (value.StartsWith('"')) return (key, Unquote(value));
        bool duration = key.Equals("timelimit", StringComparison.OrdinalIgnoreCase);
        if (unitsAllowed && value.Length > 0 && i + 1 < t.Count
            && (duration ? IsTimeUnit(t[i + 1].Text) : IsUnit(t[i + 1].Text, goalLine)))
        {
            value += " " + t[i + 1].Text;
            i++;
        }
        return (key, value);
    }

    private static string Limit(List<Token> t, ref int i, string goal, string after)
    {
        if (i + 1 >= t.Count || IsGoalStop(t[i + 1].Text))
            throw Refuse(TuningDirectiveDiagnostics.GoalProblem(goal, $"'{after}' needs a limit after it."));
        i++;
        string v = t[i].Text;
        if (v.StartsWith('"')) return Unquote(v);
        if (i + 1 < t.Count && IsUnit(t[i + 1].Text, goalLine: true))
        {
            v += " " + t[i + 1].Text;
            i++;
        }
        return v;
    }

    /// <summary>A unit token after a value. On a goal line, the grammar's own words are never units —
    /// <c>in</c> is a goal type there and not inches.</summary>
    private static bool IsUnit(string token, bool goalLine)
        => !(goalLine && (token == "to" || AnalysisDirectiveSchema.GoalTypeTokens.Contains(token)))
        && !token.Contains('=') && !token.StartsWith('"')
        && Units.IsRecognizedUnit(UnitNormalizer.ToEngineUnit(token));

    /// <summary>The units a <c>timelimit</c> may carry. Local to this grammar: the shared unit table
    /// has no time units, and adding a bare <c>s</c> there would change what a bare word after an
    /// instance line's value means.</summary>
    public static bool IsTimeUnit(string token) => token is "s" or "ms" or "min" or "h";

    private static bool IsGoalStop(string token)
        => AnalysisDirectiveSchema.GoalTypeTokens.Contains(token) || IsKeyToken(token);

    /// <summary><c>name=…</c> where name is a word — and not <c>a==b</c>.</summary>
    private static bool IsKeyToken(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0 || eq + 1 < token.Length && token[eq + 1] == '=') return false;
        var key = token[..eq];
        return (char.IsLetter(key[0]) || key[0] == '_')
            && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '[' or ']');
    }

    private static bool TryGoalType(string token, out GoalType type)
    {
        int idx = -1;
        for (int k = 0; k < AnalysisDirectiveSchema.GoalTypeTokens.Count; k++)
            if (AnalysisDirectiveSchema.GoalTypeTokens[k] == token) idx = k;
        type = (GoalType)Math.Max(idx, 0);
        return idx >= 0;
    }

    private static TEnum Enum<TEnum>(string key, string value, IReadOnlyList<string> tokens) where TEnum : struct, Enum
    {
        for (int k = 0; k < tokens.Count; k++)
            if (tokens[k].Equals(value, StringComparison.OrdinalIgnoreCase)) return (TEnum)(object)k;
        throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "one of " + string.Join(", ", tokens)));
    }

    private static bool Bool(string key, string value) => value.ToLowerInvariant() switch
    {
        "1" or "true" or "yes"  => true,
        "0" or "false" or "no"  => false,
        _ => throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "1 or 0")),
    };

    private static int Int(string key, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a whole number"));

    private static TuningDirectiveException Refuse(CircuitRF.Diagnostics.Diagnostic d) => new(d);

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
