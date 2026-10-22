using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The instance switches of a composed FET (brief-modelcomposer-21 R-mc21-1): which parts of the model one instance
/// evaluates. None refits anything — the <c>.cmodel</c> is untouched, and the defaults are the model as fitted, so an
/// instance that states none of them is exactly the instance it always was.
///
/// <para>The ONE place the ten names are read and written: the elaborator, the schematic's extraction and annotation,
/// the parameter editor and <c>explain</c> all go through here, so they cannot disagree about a spelling or a default.
/// Every value is read by <see cref="BooleanParameter"/>; a bare spelling (<c>off</c>, <c>no</c>) is taken as written
/// before anything is evaluated, because to the expression engine it is a variable name.</para>
/// </summary>
public sealed record ComposedSwitches
{
    public const string ChargeName = "Charge", LinearCName = "LinearC", GateLagName = "GateLag",
                        DrainLagName = "DrainLag", SelfHeatingName = "SelfHeating", DispersionName = "Dispersion";

    /// <summary>The four branch switches (brief-modelcomposer-22 R-mc22-3), in <see cref="ChargeBranches"/>' order.</summary>
    public const string LinearCgsName = "LinearCgs", LinearCdgName = "LinearCdg", LinearCdsName = "LinearCds",
                        LinearCmName = "LinearCm";

    /// <summary>The ten names in their fixed order — the order <see cref="Write"/> writes and the editor lists.</summary>
    public static readonly IReadOnlyList<string> Names =
        [ChargeName, LinearCName, LinearCgsName, LinearCdgName, LinearCdsName, LinearCmName,
         GateLagName, DrainLagName, SelfHeatingName, DispersionName];

    /// <summary><c>false</c>: no intrinsic charge at all. The extrinsic network's capacitors stay.</summary>
    public bool Charge { get; init; } = true;

    /// <summary><c>true</c>: both intrinsic charges linearised at the quiescent DC operating point.</summary>
    public bool LinearC { get; init; }

    /// <summary><c>true</c>: the gate–source capacitance <c>C11 + C12</c> held at its bias-point value, the others following
    /// the model (<see cref="BranchLinearisation"/>).</summary>
    public bool LinearCgs { get; init; }

    /// <summary><c>true</c>: the gate–drain capacitance <c>−C12</c> held at its bias-point value.</summary>
    public bool LinearCdg { get; init; }

    /// <summary><c>true</c>: the drain–source capacitance <c>C22 + C12</c> held at its bias-point value.</summary>
    public bool LinearCds { get; init; }

    /// <summary><c>true</c>: the transcapacitance <c>C21 − C12</c> held at its bias-point value.</summary>
    public bool LinearCm { get; init; }

    /// <summary><c>false</c>: every gate-lag trap state follows its DC value at the instantaneous voltages.</summary>
    public bool GateLag { get; init; } = true;

    /// <summary><c>false</c>: every drain-lag trap state follows its DC value at the instantaneous voltages.</summary>
    public bool DrainLag { get; init; } = true;

    /// <summary><c>false</c>: evaluated at the run's ambient, no power into the thermal node.</summary>
    public bool SelfHeating { get; init; } = true;

    /// <summary><c>false</c>: the drain carries <c>IdDC</c> at every frequency; no dispersion node.</summary>
    public bool Dispersion { get; init; } = true;

    public static readonly ComposedSwitches Default = new();

    public bool IsDefault => this == Default;

    /// <summary>The branches the four branch switches name; <see cref="LinearC"/> is not folded in.</summary>
    public ChargeBranches Branches =>
        (LinearCgs ? ChargeBranches.Cgs : 0) | (LinearCdg ? ChargeBranches.Cdg : 0) |
        (LinearCds ? ChargeBranches.Cds : 0) | (LinearCm ? ChargeBranches.Cm : 0);

    /// <summary>The branches this instance freezes: every one under <see cref="LinearC"/>, none when <see cref="Charge"/>
    /// removes the charge.</summary>
    public ChargeBranches FrozenBranches => !Charge ? ChargeBranches.None : LinearC ? ChargeBranches.All : Branches;

    /// <summary>Whether any of the intrinsic charge is linearised — the whole of it, or a branch — so the instance needs the
    /// bias point. <see cref="Charge"/>=false removes it.</summary>
    public bool LinearCharge => FrozenBranches != ChargeBranches.None;

    /// <summary>A switch's default: on for the parts of the model, off for the linearisations.</summary>
    public static bool DefaultOf(string name) => !name.StartsWith(LinearCName, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="name"/> is one of the six, as written (ordinal, like every parameter name).</summary>
    public static bool IsSwitch(string name) => Names.Contains(name, StringComparer.Ordinal);

    /// <summary>A switch's value by name.</summary>
    public bool this[string name] => name switch
    {
        ChargeName      => Charge,
        LinearCName     => LinearC,
        LinearCgsName   => LinearCgs,
        LinearCdgName   => LinearCdg,
        LinearCdsName   => LinearCds,
        LinearCmName    => LinearCm,
        GateLagName     => GateLag,
        DrainLagName    => DrainLag,
        SelfHeatingName => SelfHeating,
        DispersionName  => Dispersion,
        _ => throw new ArgumentException($"'{name}' is not a ComposedFET switch.", nameof(name)),
    };

    /// <summary>These switches with one of them set — what an editor writes back on a toggle. An unknown name changes nothing.</summary>
    public ComposedSwitches With(string name, bool value) => name switch
    {
        ChargeName      => this with { Charge = value },
        LinearCName     => this with { LinearC = value },
        LinearCgsName   => this with { LinearCgs = value },
        LinearCdgName   => this with { LinearCdg = value },
        LinearCdsName   => this with { LinearCds = value },
        LinearCmName    => this with { LinearCm = value },
        GateLagName     => this with { GateLag = value },
        DrainLagName    => this with { DrainLag = value },
        SelfHeatingName => this with { SelfHeating = value },
        DispersionName  => this with { Dispersion = value },
        _ => this,
    };

    /// <summary>
    /// What a model lacks that a switch names (brief-modelcomposer-21 R-mc21-2), as the phrase the run note uses —
    /// <c>has no gate-lag trap state</c> — or null when the model has that part (or the switch is not a part's). Turning
    /// such a switch off changes nothing; the elaborator says so in a note, and an editor disables it with the same words.
    /// </summary>
    public static string? MissingPart(string name, LoadedComposedModel loaded)
        => MissingPart(name, loaded.Model, loaded.Program.Traps);

    /// <summary><see cref="MissingPart(string, LoadedComposedModel)"/> for a model not loaded from a file, with its trap
    /// states compiled (<paramref name="traps"/>, null for a model without traps) — what the Verilog-A export says in its
    /// header.</summary>
    public static string? MissingPart(string name, ComposedModel model, Traps.TrapDynamics? traps)
    {
        bool HasLag(int sign) => traps is not null && Enumerable.Range(0, traps.States.Count).Any(k => Math.Sign(traps.CaptureSign(k)) == sign);
        return name switch
        {
            GateLagName     when !HasLag(-1)            => "has no gate-lag trap state",
            DrainLagName    when !HasLag(+1)            => "has no drain-lag trap state",
            SelfHeatingName when model.Thermal is null  => "does not self-heat",
            DispersionName  when model.Core.IdRF is null => "has no DC/RF dispersion",
            _ => null,
        };
    }

    /// <summary>
    /// The switches an instance line states. <paramref name="evaluate"/> evaluates an override that is not a bare
    /// spelling in the instance's parent scope; anything that is not a yes or no is refused by name with the list.
    /// </summary>
    public static ComposedSwitches Read(IEnumerable<ParameterAssignment> overrides, Func<ParameterAssignment, Value> evaluate)
    {
        var s = Default;
        foreach (var ov in overrides)
        {
            if (!IsSwitch(ov.Name)) continue;
            Value value;
            if (Spelling(ov.Expression) is { } b) value = new Value(b);
            else
            {
                try { value = evaluate(ov); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A word that is neither a spelling nor a name in scope is refused as a switch, with the list —
                    // the evaluator's own complaint (an unresolved name) kept beside it.
                    try { BooleanParameter.Parse(ov.Name, new Value(ov.Expression.Trim())); }
                    catch (InvalidOperationException refusal) { throw new InvalidOperationException($"{refusal.Message} ({ex.Message})", ex); }
                    throw;
                }
            }
            s = s.With(ov.Name, BooleanParameter.Parse(ov.Name, value));
        }
        return s;
    }

    /// <summary>The switches a resolved parameter set carries (the core's own instance).</summary>
    public static ComposedSwitches Read(IReadOnlyDictionary<string, Value> parameters)
    {
        var s = Default;
        foreach (string name in Names)
            if (parameters.TryGetValue(name, out var v)) s = s.With(name, BooleanParameter.Parse(name, v));
        return s;
    }

    /// <summary>The non-default switches as <c>key=value</c> pairs, in <see cref="Names"/>' order; empty for the default.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Write()
        => [.. Names.Where(n => this[n] != DefaultOf(n)).Select(n => new KeyValuePair<string, string>(n, this[n] ? "true" : "false"))];

    /// <summary>The short summary the schematic draws under a switched instance — <c>linear C · no gate lag · isothermal</c>,
    /// or <c>linear Cgs, Cds</c> for branches; empty for the default.</summary>
    public string Describe() => string.Join(" · ", Phrases(sentence: false));

    /// <summary>The run note's form — <c>linear C at the bias point, no gate lag</c>; empty for the default.</summary>
    public string Sentence() => string.Join(", ", Phrases(sentence: true));

    private IEnumerable<string> Phrases(bool sentence)
    {
        if (!Charge)       yield return "no intrinsic charge";
        else if (LinearC)  yield return sentence ? "linear C at the bias point" : "linear C";
        else if (Branches != ChargeBranches.None)
        {
            string names = string.Join(", ", BranchLinearisation.Names(Branches));
            yield return sentence ? $"linear {names} at the bias point" : $"linear {names}";
        }
        if (!GateLag)      yield return "no gate lag";
        if (!DrainLag)     yield return "no drain lag";
        if (!SelfHeating)  yield return "isothermal";
        if (!Dispersion)   yield return "no dispersion";
    }

    /// <summary>
    /// The annotation for switches as a document holds them, each as its raw text (null or blank: not stated). A bare
    /// spelling reads as its value; an expression cannot be evaluated here and is shown as written, <c>GateLag=gl</c>, so
    /// a switch bound to a variable is never mistaken for one left at its default.
    /// </summary>
    public static string Annotate(Func<string, string?> rawOf)
    {
        var literal = Default;
        var shown   = new List<string>();
        foreach (string name in Names)
        {
            string? raw = rawOf(name)?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;
            if (Spelling(raw) is { } b) literal = literal.With(name, b);
            else shown.Add($"{name}={raw}");
        }
        string text = literal.Describe();
        if (text.Length > 0) shown.Insert(0, text);
        return string.Join(" · ", shown);
    }

    /// <summary>Whether raw text states the default (blank, or a spelling of the default) — what extraction leaves out.</summary>
    public static bool StatesDefault(string name, string? raw)
        => string.IsNullOrWhiteSpace(raw) || Spelling(raw) == DefaultOf(name);

    /// <summary>A bare yes-or-no spelling, or null for anything else (an expression, a number other than 0 or 1).</summary>
    public static bool? Spelling(string raw)
    {
        string t = raw.Trim().Trim('"');
        return BooleanParameter.TryParse(new Value(t), out bool b) ? b : null;
    }
}
