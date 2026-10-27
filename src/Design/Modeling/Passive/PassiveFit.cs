// Fitting a passive topology to an S-parameter block, and proposing topologies (brief-modelcomposer-12 R-mc12-4,
// R-mc12-5).
//
// THE RESIDUAL is S of the topology — solved by the engine at each trial point, referenced to the block's own Z0 —
// minus the measured S, real and imaginary parts of every entry at every frequency, each frequency weighted (uniform, or
// a band emphasised). Its sum of squares is the cost. The search is MC-5's (BoxLeastSquares): the variables as
// coordinates of the unit box, logarithmic for a positive quantity; Levenberg–Marquardt from the starting values; the
// registry's global algorithm per seeded start, each polished by Levenberg–Marquardt; the identifiability report at the
// solution. Bounds are ≥ 0 by default; a document's locks, ties and bounds apply exactly as they do to a network.
//
// PROPOSALS. Every library topology whose port count matches gets its analytic starting values (PassiveTopologies) and
// a short seeded fit — Levenberg–Marquardt, one short global search from there, Levenberg–Marquardt again — and they are
// ranked by an information criterion that penalises element count,
//     AIC = n·ln(RSS/n) + 2k,
// n the number of real residuals, k the number of fitted variables. RSS is floored at n·(1e-6)² — a |ΔS| of a part in a
// million, below any measurement's precision — so two fits that both reproduce the data exactly are told apart by their
// element count alone rather than by which optimiser happened to stop nearer zero. The top three are offered. A topology
// the user draws takes the same fit from its own drawn values.

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Engine;
using CircuitRF.Engine.Optimization;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>How the residual is weighted over frequency (R-mc12-5).</summary>
public enum FrequencyWeighting { Uniform, Band }

/// <summary>The passive fit's settings, each with its stated default.</summary>
public sealed record PassiveFitSettings
{
    public const double DefaultEmphasis = 10, DefaultPoorRelativeError = 0.2, DefaultPoorCorrelation = 0.95;
    public const int DefaultStarts = 2, DefaultBudgetPerVariable = 100, DefaultProposalBudgetPerVariable = 40;
    public const string DefaultAlgorithm = "cmaes";

    public FrequencyWeighting Weighting { get; init; } = FrequencyWeighting.Uniform;
    /// <summary>The emphasised band, Hz, for <see cref="FrequencyWeighting.Band"/>.</summary>
    public (double Lo, double Hi)? Band { get; init; }
    /// <summary>How much more a frequency inside the band weighs in the cost than one outside it.</summary>
    public double Emphasis { get; init; } = DefaultEmphasis;
    public int Starts { get; init; } = DefaultStarts;
    public ulong Seed { get; init; } = 1;
    /// <summary><c>cmaes</c> or <c>de</c> — the registry's global algorithms.</summary>
    public string Algorithm { get; init; } = DefaultAlgorithm;
    /// <summary>Evaluations each start's global search may spend; null is <see cref="DefaultBudgetPerVariable"/> per variable.</summary>
    public int? Budget { get; init; }
    /// <summary>Evaluations per variable a proposal's short global search spends.</summary>
    public int ProposalBudget { get; init; } = DefaultProposalBudgetPerVariable;
    public double PoorRelativeError { get; init; } = DefaultPoorRelativeError;
    public double PoorCorrelation { get; init; } = DefaultPoorCorrelation;

    public static PassiveFitSettings Default { get; } = new();

    private static readonly string[] Keys =
        ["Weighting", "Band", "Emphasis", "Starts", "Seed", "Algorithm", "Budget", "ProposalBudget", "PoorRelativeError", "PoorCorrelation"];

    public static PassiveFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        string w = Str(o, "Weighting", $"{f}.Weighting", required: false) ?? nameof(FrequencyWeighting.Uniform);
        if (!Enum.TryParse<FrequencyWeighting>(w, ignoreCase: false, out var weighting) || !Enum.IsDefined(weighting))
            throw new CmcFormatException($"{f}.Weighting", $"'{w}' is not Uniform or Band.");
        (double, double)? band = null;
        if (o["Band"] is { } bn)
        {
            if (bn is not JsonArray a || a.Count != 2) throw new CmcFormatException($"{f}.Band", "it is not a list of two frequencies in Hz.");
            double lo = NumberOf(a[0], $"{f}.Band[0]"), hi = NumberOf(a[1], $"{f}.Band[1]");
            if (!(lo >= 0 && hi > lo)) throw new CmcFormatException($"{f}.Band", $"{G(lo)} to {G(hi)} Hz is not a band.");
            band = (lo, hi);
        }
        var s = new PassiveFitSettings
        {
            Weighting = weighting, Band = band,
            Emphasis = Num(o, "Emphasis", $"{f}.Emphasis") ?? DefaultEmphasis,
            Starts = (int)(Num(o, "Starts", $"{f}.Starts") ?? DefaultStarts),
            Seed = (ulong)(Num(o, "Seed", $"{f}.Seed") ?? 1),
            Algorithm = Str(o, "Algorithm", $"{f}.Algorithm", required: false) ?? DefaultAlgorithm,
            Budget = Num(o, "Budget", $"{f}.Budget") is { } b ? (int)b : null,
            ProposalBudget = (int)(Num(o, "ProposalBudget", $"{f}.ProposalBudget") ?? DefaultProposalBudgetPerVariable),
            PoorRelativeError = Num(o, "PoorRelativeError", $"{f}.PoorRelativeError") ?? DefaultPoorRelativeError,
            PoorCorrelation = Num(o, "PoorCorrelation", $"{f}.PoorCorrelation") ?? DefaultPoorCorrelation,
        };
        if (s.Weighting == FrequencyWeighting.Band && s.Band is null) throw new CmcFormatException($"{f}.Band", "Band weighting needs the band.");
        if (s.Weighting == FrequencyWeighting.Uniform && s.Band is not null) throw new CmcFormatException($"{f}.Band", "it applies to Band weighting only.");
        if (!(s.Emphasis >= 1)) throw new CmcFormatException($"{f}.Emphasis", "it is below 1.");
        if (s.Starts < 1) throw new CmcFormatException($"{f}.Starts", "it is below 1.");
        if (s.Budget is < 1) throw new CmcFormatException($"{f}.Budget", "it is below 1.");
        if (s.ProposalBudget < 1) throw new CmcFormatException($"{f}.ProposalBudget", "it is below 1.");
        if (!NetworkFitSettings.Algorithms.Contains(s.Algorithm))
            throw new CmcFormatException($"{f}.Algorithm", $"'{s.Algorithm}' is not {string.Join(" or ", NetworkFitSettings.Algorithms)}.");
        if (!(s.PoorRelativeError > 0)) throw new CmcFormatException($"{f}.PoorRelativeError", "it is not above 0.");
        if (!(s.PoorCorrelation is > 0 and < 1)) throw new CmcFormatException($"{f}.PoorCorrelation", "it is not between 0 and 1.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Weighting != FrequencyWeighting.Uniform) o["Weighting"] = Weighting.ToString();
        if (Band is { } b) o["Band"] = new JsonArray(b.Lo, b.Hi);
        if (Emphasis != DefaultEmphasis) o["Emphasis"] = Emphasis;
        if (Starts != DefaultStarts) o["Starts"] = Starts;
        if (Seed != 1) o["Seed"] = Seed;
        if (Algorithm != DefaultAlgorithm) o["Algorithm"] = Algorithm;
        if (Budget is { } bu) o["Budget"] = bu;
        if (ProposalBudget != DefaultProposalBudgetPerVariable) o["ProposalBudget"] = ProposalBudget;
        if (PoorRelativeError != DefaultPoorRelativeError) o["PoorRelativeError"] = PoorRelativeError;
        if (PoorCorrelation != DefaultPoorCorrelation) o["PoorCorrelation"] = PoorCorrelation;
        return o;
    }

    /// <summary>The weight of each frequency's residual (the square root of its weight in the cost).</summary>
    internal double[] Weights(double[] f)
    {
        double inside = Math.Sqrt(Emphasis);
        return [.. f.Select(x => Weighting == FrequencyWeighting.Band && Band is { } b && x >= b.Lo && x <= b.Hi ? inside : 1.0)];
    }
}

/// <summary>A fitted topology: every variable's value in base SI, how well the data determines each, and the fit's
/// quality — its cost, its |ΔS| and its information criterion.</summary>
public sealed record PassiveFitResult(
    string Topology,
    IReadOnlyDictionary<string, double> Values,
    IdentifiabilityReport Report,
    /// <summary>The weighted sum of squared residuals.</summary>
    double Cost,
    /// <summary>√(mean over frequencies and entries of |ΔS|²), unweighted.</summary>
    double RmsDeltaS,
    double MaxDeltaS,
    double Aic,
    int FreeVariables,
    int Residuals,
    IReadOnlyList<string> Notes,
    long Evaluations);

/// <summary>One ranked proposal; the first three are offered.</summary>
public sealed record PassiveProposal(int Rank, string Title, PassiveFitResult Fit, bool Offered);

public static class PassiveFit
{
    /// <summary>The RSS floor per real residual — a |ΔS| of a part in a million — under which two fits are told apart by
    /// their element count alone.</summary>
    public const double ResidualFloor = 1e-6;

    /// <summary>How many proposals are offered.</summary>
    public const int Offered = 3;

    /// <summary>Bounds a library topology's variables default to: 0 to this many times its starting value.</summary>
    public const double DefaultMaxFactor = 1000;

    /// <summary><c>n·ln(max(RSS, n·floor²)/n) + 2k</c> — the stated criterion.</summary>
    public static double Aic(int n, double rss, int k) =>
        n * Math.Log(Math.Max(rss, n * ResidualFloor * ResidualFloor) / n) + 2 * k;

    /// <summary>
    /// Fits <paramref name="network"/> to <paramref name="block"/> (R-mc12-5). <paramref name="starts"/> replace the
    /// drawing's values (a library topology's seeds, a family neighbour's solution); <paramref name="positive"/> names
    /// variables that are positive quantities though no R, L or C carries them. <paramref name="quick"/> is a
    /// proposal's short fit. Throws <see cref="ModelingException"/> for a refusal and <see cref="OperationCanceledException"/>
    /// when cancelled.
    /// </summary>
    public static PassiveFitResult Fit(PassiveBlock block, PassiveNetwork network, PassiveFitSettings? settings = null,
                                       IReadOnlyDictionary<string, NetworkVariableSettings>? variables = null,
                                       IReadOnlyDictionary<string, double>? starts = null,
                                       IReadOnlyCollection<string>? positive = null, bool quick = false,
                                       string? topology = null, RunControl? rc = null)
        => Fit(block, network, settings, variables, starts, positive, quick ? Depth.Quick : Depth.Full, topology, rc);

    /// <summary>How far a fit searches: Levenberg–Marquardt alone, a proposal's short search, or the full multi-start one.</summary>
    internal enum Depth { Polish, Quick, Full }

    internal static PassiveFitResult Fit(PassiveBlock block, PassiveNetwork network, PassiveFitSettings? settings,
                                         IReadOnlyDictionary<string, NetworkVariableSettings>? variables,
                                         IReadOnlyDictionary<string, double>? starts, IReadOnlyCollection<string>? positive,
                                         Depth depth, string? topology, RunControl? rc)
    {
        settings ??= PassiveFitSettings.Default;
        var p = Problem.Build(block, network, settings, variables ?? new Dictionary<string, NetworkVariableSettings>(),
                              starts, positive);
        return p.Fit(settings, depth, topology ?? Path.GetFileNameWithoutExtension(network.SchematicPath), rc);
    }

    /// <summary>
    /// Why Propose found nothing for a block whose port count the library has no topology for and none is drawn (designer
    /// round 28: a board's six-port EM result) — the library is one- and two-port, and the SnP component takes the file as
    /// it is.
    /// </summary>
    public static string NoTopologyNote(int ports) =>
        $"No built-in topology has {ports} ports: draw one (pins 1…{ports} are the ports) or place the file itself as an S-parameter component.";

    /// <summary>
    /// Ranked proposals for <paramref name="block"/> (R-mc12-4): every library topology with its port count, each with
    /// analytic starting values and a short seeded fit, plus each of <paramref name="drawn"/> (user topologies, from their
    /// own values and <paramref name="drawnVariables"/>' bounds, locks and ties), ranked by <see cref="Aic"/>. A candidate
    /// that cannot be fitted is left out with a note in <paramref name="notes"/>.
    /// </summary>
    public static IReadOnlyList<PassiveProposal> Propose(PassiveBlock block, PassiveAnalysisResult analysis,
                                                         PassiveFitSettings? settings = null,
                                                         IEnumerable<PassiveNetwork>? drawn = null,
                                                         List<string>? notes = null, RunControl? rc = null,
                                                         IReadOnlyDictionary<string, NetworkVariableSettings>? drawnVariables = null)
    {
        settings ??= PassiveFitSettings.Default;
        var drawnList = drawn?.ToList() ?? [];
        if (drawnList.Count == 0 && !PassiveTopologies.All.Any(t => t.Ports == block.Ports))
        {
            notes?.Add(NoTopologyNote(block.Ports));
            return [];
        }
        var fits = new List<(string Title, PassiveFitResult Fit)>();
        foreach (var t in PassiveTopologies.All.Where(t => t.Ports == block.Ports))
        {
            rc?.ThrowIfCancellationRequested();
            rc?.BeginStage($"Proposal: {t.Title}", 0, "evaluation(s)");
            try
            {
                fits.Add((t.Title, Fit(block, t.Load(), settings, starts: t.Seed(analysis), positive: t.Positive,
                                       quick: true, topology: t.Id, rc: rc)));
            }
            catch (ModelingException ex) { notes?.Add($"{t.Title}: {ex.Message}"); }
        }
        foreach (var d in drawnList)
        {
            rc?.ThrowIfCancellationRequested();
            string name = Path.GetFileName(d.SchematicPath);
            rc?.BeginStage($"Proposal: {name}", 0, "evaluation(s)");
            if (d.Ports != block.Ports) { notes?.Add($"{name}: it has {d.Ports} pins and the block {block.Ports} ports."); continue; }
            try { fits.Add((name, Fit(block, d, settings, drawnVariables, quick: true, rc: rc))); }
            catch (ModelingException ex) { notes?.Add($"{name}: {ex.Message}"); }
        }
        return [.. fits.OrderBy(x => x.Fit.Aic).Select((x, i) => new PassiveProposal(i + 1, x.Title, x.Fit, i < Offered))];
    }

    /// <summary>
    /// The weighted residual of a model's S against a block: real and imaginary parts of every entry at every frequency,
    /// frequency k weighted by <paramref name="w"/>[k]. A non-finite entry makes the trial point a failed evaluation.
    /// </summary>
    internal static double[] Residual(Complex[][,] model, PassiveBlock block, double[] w)
    {
        int n = block.Ports, nf = block.FrequenciesHz.Length;
        var r = new double[2 * nf * n * n];
        int at = 0;
        for (int k = 0; k < nf; k++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var d = model[k][i, j] - block.S[k][i, j];
                    r[at++] = w[k] * d.Real;
                    r[at++] = w[k] * d.Imaginary;
                }
        foreach (double x in r)
            if (!double.IsFinite(x)) throw new ArithmeticException();
        return r;
    }

    /// <summary>√(mean |ΔS|²) and max |ΔS| of a model against a block, unweighted.</summary>
    internal static (double Rms, double Max) DeltaS(Complex[][,] model, PassiveBlock block)
    {
        int n = block.Ports, nf = block.FrequenciesHz.Length;
        double sq = 0, max = 0;
        for (int k = 0; k < nf; k++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double d = (model[k][i, j] - block.S[k][i, j]).Magnitude;
                    sq += d * d;
                    max = Math.Max(max, d);
                }
        return (Math.Sqrt(sq / (nf * n * n)), max);
    }

    /// <summary>
    /// A topology's variables as a fit sees them: <paramref name="starts"/> in place of the drawn values, each positive
    /// variable no R, L or C carries (a skin coefficient, a line's impedance and length) given an element's treatment —
    /// a logarithmic coordinate and bounds from 0 — and every bound not set defaulting to 0 … 1000× the start.
    /// </summary>
    internal static FitVariables Variables(PassiveNetwork network, IReadOnlyDictionary<string, NetworkVariableSettings> variables,
                                           IReadOnlyDictionary<string, double>? starts, IReadOnlyCollection<string>? positive,
                                           List<string> notes)
    {
        var vars = network.Variables
            .Select(v => v with
            {
                Kind = v.Kind == NetworkElementKind.Other && positive?.Contains(v.Name) == true ? NetworkElementKind.Resistance : v.Kind,
                Value = starts is not null && starts.TryGetValue(v.Name, out var s) ? s : v.Value,
            })
            .ToList();
        var fv = FitVariables.Build(vars, variables, Defaults, "Variables", notes);
        if (fv.Free.Length == 0) throw new ModelingException("Variables", "every variable is locked or tied; there is nothing to fit.");
        return fv;
    }

    private static NetworkVariableSettings Defaults(NetworkVariable v) => new()
    {
        Min = v.Kind == NetworkElementKind.Other ? null : 0,
        Max = double.IsFinite(v.Value) && v.Value != 0 ? DefaultMaxFactor * Math.Abs(v.Value) : null,
    };

    // ── The problem ───────────────────────────────────────────────────────────

    private sealed class Problem
    {
        private readonly PassiveBlock _block;
        private readonly PassiveNetwork _network;
        private readonly FitVariables _v;
        private readonly double[] _w;
        private readonly List<string> _notes;
        private readonly EvaluationCounter _counter = new();

        private Problem(PassiveBlock block, PassiveNetwork network, FitVariables v, double[] w, List<string> notes)
        {
            _block = block;
            _network = network;
            _v = v;
            _w = w;
            _notes = notes;
        }

        public static Problem Build(PassiveBlock block, PassiveNetwork network, PassiveFitSettings settings,
                                    IReadOnlyDictionary<string, NetworkVariableSettings> variables,
                                    IReadOnlyDictionary<string, double>? starts, IReadOnlyCollection<string>? positive)
        {
            if (network.Ports != block.Ports)
                throw new ModelingException(Path.GetFileName(network.SchematicPath),
                                            $"it has {network.Ports} pins and the block {block.Ports} ports.");
            if (settings.Weighting == FrequencyWeighting.Band && settings.Band is { } band
                && !block.FrequenciesHz.Any(f => f >= band.Lo && f <= band.Hi))
                throw new ModelingException("Band", $"no frequency of {block.Name} lies in {G(band.Lo)} to {G(band.Hi)} Hz.");
            var notes = new List<string>();
            var fv = Variables(network, variables, starts, positive, notes);
            return new Problem(block, network, fv, settings.Weights(block.FrequenciesHz), notes);
        }

        private Complex[][,] Model(double[] freeValues) =>
            _network.S(_block.FrequenciesHz, _block.Z0, _v.Named(_v.Compose(freeValues)));

        private double[] Residuals(double[] freeValues) => Residual(Model(freeValues), _block, _w);

        public PassiveFitResult Fit(PassiveFitSettings settings, Depth depth, string topology, RunControl? rc)
        {
            var lsq = new BoxLeastSquares(_v.Free, Residuals, _counter);
            var all = lsq.All;
            var best = lsq.Polish(_v.FreeStart, all, rc);
            var spread = new double[all.Length];
            if (depth == Depth.Quick)
            {
                var alg = OptimizerFactory.Create(settings.Algorithm, lsq.Encode(best.Free, all), settings.Seed, Options(settings, all.Length))!;
                var global = lsq.Drive(alg, all, best.Free, settings.ProposalBudget * all.Length, rc);
                var polished = lsq.Polish(global.Free, all, rc);
                if (polished.Cost < best.Cost) best = polished;
            }
            else if (depth == Depth.Full)
            {
                int budget = settings.Budget ?? PassiveFitSettings.DefaultBudgetPerVariable * all.Length;
                var (values, sp) = lsq.MultiStart(best.Free, settings.Starts, settings.Seed, settings.Algorithm, budget, rc);
                double cost = lsq.Cost(values);
                if (cost < best.Cost) (best, spread) = (new Solved(values, cost), sp);
            }
            rc?.ThrowIfCancellationRequested();
            if (!double.IsFinite(best.Cost))
                throw new ModelingException(topology, "it could not be solved at any trial point.");
            var report = lsq.Identify(best.Free, spread, settings.PoorRelativeError, settings.PoorCorrelation);

            var (rms, max) = DeltaS(Model(best.Free), _block);
            int m = 2 * _block.FrequenciesHz.Length * _block.Ports * _block.Ports;
            return new PassiveFitResult(topology, _v.Named(_v.Compose(best.Free)), report, best.Cost, rms, max,
                                        Aic(m, best.Cost, all.Length), all.Length, m, _notes, _counter.Count);
        }

        private static Dictionary<string, string> Options(PassiveFitSettings s, int n)
        {
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            if (s.Algorithm == DifferentialEvolution.AlgorithmId)
                o["budget"] = (s.ProposalBudget * n).ToString(CultureInfo.InvariantCulture);
            return o;
        }
    }
}
