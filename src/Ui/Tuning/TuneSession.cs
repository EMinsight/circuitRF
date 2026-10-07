// ================================================================
//  TuneSession.cs  —  a stream of value changes in, a stream of results
//  out (brief-tuneopt-3, overview D7/D8/D9)
//
//  One prepared design, one evaluation in flight, at most one pending.
//  A newer request REPLACES the pending one and never cancels the
//  in-flight run — cancelling on every slider tick means a slow bench
//  never shows anything while the user drags. Stop, Revert, Push and
//  disposal do cancel it.
//
//  Headless: evaluations run on the thread pool and each result is
//  handed to the UI thread exactly once through the poster the caller
//  supplies. TO-4's Tuning panel is the face on this; nothing here draws.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Ui.Tuning;

/// <summary>Where a session's results go: the Data Display, and on Stop the results file.</summary>
public interface ITuneResultSink
{
    /// <summary>Shows a result in place of the results file (UI thread).</summary>
    void Publish(DataSet data);

    /// <summary>Writes the last displayed result to the results file, stating the tuned values.</summary>
    void Commit(DataSet data, IReadOnlyDictionary<string, string> values);

    /// <summary>Drops whatever was published and any snapshot; the display returns to the file.</summary>
    void Drop();

    /// <summary>Freezes what the display currently shows as a ghost (R-to3-9).</summary>
    void Snapshot();

    /// <summary>Removes the ghost.</summary>
    void ClearSnapshot();
}

public sealed class TuneSession : IDisposable
{
    /// <summary>One evaluation of the design at <paramref name="values"/>, narrowed to
    /// <paramref name="analyses"/> (null = every enabled analysis), honouring <paramref name="control"/>'s
    /// cancellation.</summary>
    public delegate RunResult Evaluator(
        IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? analyses, RunControl control);

    /// <summary>An evaluation slower than this suggests run-on-release (R-to3-3, D7).</summary>
    public static readonly TimeSpan SlowEvaluation = TimeSpan.FromSeconds(2);

    private readonly Evaluator       _evaluate;
    private readonly ITuneResultSink? _sink;
    private readonly Action<Action>  _post;
    private readonly Func<DateTime>  _clock;
    private readonly object          _gate = new();

    private IReadOnlyDictionary<string, string>? _pending;
    private CancellationTokenSource? _inFlightCts;
    private bool  _inFlight;
    private int   _generation;
    private bool  _disposed;
    private bool  _suggested;
    private long  _evaluations;
    private IReadOnlyDictionary<string, string>? _requested;

    public TuneSession(Evaluator evaluate, ITuneResultSink? sink, Action<Action> postToUi,
                       IReadOnlyList<string>? analyses = null, long sweepPoints = 0,
                       Func<DateTime>? clock = null)
    {
        _evaluate   = evaluate;
        _sink       = sink;
        _post       = postToUi;
        _clock      = clock ?? (() => DateTime.UtcNow);
        Analyses    = analyses;
        SweepPoints = sweepPoints;
    }

    /// <summary>
    /// A session over a prepared design: every evaluation is one <see cref="CircuitEvaluation.Evaluate"/>
    /// of <paramref name="circuit"/> with the requested values as tunables, so nothing is re-read.
    /// </summary>
    public static TuneSession ForCircuit(PreparedCircuit circuit, ITuneResultSink? sink, Action<Action> postToUi,
                                         IReadOnlyList<string>? analyses = null)
    {
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Analyses = analyses });
        return new TuneSession(
            (values, scope, control) => CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
            {
                Tunables = values, Analyses = scope, Control = control,
            }),
            sink, postToUi, analyses, plan.SweepPoints);
    }

    // ---- What the session reports (R-to3-2, R-to3-4) -------------------------

    /// <summary>The analyses evaluated, by name; null = every enabled analysis (R-to3-4).</summary>
    public IReadOnlyList<string>? Analyses { get; }

    /// <summary>Points across the parametric sweeps in scope — each request costs all of them.</summary>
    public long SweepPoints { get; }

    /// <summary>The values most recently requested.</summary>
    public IReadOnlyDictionary<string, string>? RequestedValues { get { lock (_gate) return _requested; } }

    /// <summary>The values the displayed result was computed at.</summary>
    public IReadOnlyDictionary<string, string>? DisplayedValues { get; private set; }

    /// <summary>The displayed result.</summary>
    public RunResult? DisplayedResult { get; private set; }

    /// <summary>True while an evaluation is running.</summary>
    public bool IsEvaluating { get { lock (_gate) return _inFlight; } }

    /// <summary>How long the last completed evaluation took.</summary>
    public TimeSpan? LastDuration { get; private set; }

    private DateTime? _displayedAt;

    /// <summary>How old the displayed result is.</summary>
    public TimeSpan? DisplayedAge => _displayedAt is { } at ? _clock() - at : null;

    /// <summary>The sliders are ahead of the display: the requested values are not the displayed ones.</summary>
    public bool IsLagging => !SameValues(RequestedValues, DisplayedValues);

    /// <summary>Evaluations started — the counter the coalescing claim is tested on.</summary>
    public long Evaluations => Interlocked.Read(ref _evaluations);

    /// <summary>When set, only a request marked final (the slider's release) is evaluated (R-to3-3).</summary>
    public bool RunOnRelease { get; set; }

    /// <summary>Raised on the UI thread whenever a result is displayed or the session's state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once, on the UI thread, when an evaluation took longer than
    /// <see cref="SlowEvaluation"/> while run-on-release was off. The session never turns it on itself.</summary>
    public event EventHandler? RunOnReleaseSuggested;

    // ---- Requests (R-to3-1, R-to3-3) -----------------------------------------

    /// <summary>
    /// Asks for an evaluation at <paramref name="values"/>. With nothing running it starts at once;
    /// otherwise it becomes THE pending request, replacing any earlier one. <paramref name="final"/>
    /// marks a slider's release, the only request evaluated while <see cref="RunOnRelease"/> is set.
    /// </summary>
    public void Request(IReadOnlyDictionary<string, string> values, bool final = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _requested = values;
            if (RunOnRelease && !final) return;
            if (_inFlight) { _pending = values; return; }
            StartLocked(values);
        }
    }

    private void StartLocked(IReadOnlyDictionary<string, string> values)
    {
        _inFlight = true;
        var cts        = new CancellationTokenSource();
        _inFlightCts   = cts;
        int generation = _generation;
        Interlocked.Increment(ref _evaluations);
        Task.Run(() => Run(values, cts, generation));
    }

    private void Run(IReadOnlyDictionary<string, string> values, CancellationTokenSource cts, int generation)
    {
        var sw = Stopwatch.StartNew();
        RunResult? result = null;
        try
        {
            result = _evaluate(values, Analyses, new RunControl { Token = cts.Token });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result = new RunResult(RunStatus.EngineError, ex.Message);
        }
        sw.Stop();

        lock (_gate)
        {
            bool current = generation == _generation && !cts.IsCancellationRequested;
            if (current && result is not null)
            {
                var r = result; var d = sw.Elapsed;
                _post(() => Display(values, r, d, generation));
            }
            cts.Dispose();
            if (ReferenceEquals(_inFlightCts, cts)) _inFlightCts = null;
            _inFlight = false;
            if (_pending is { } next && !_disposed)
            {
                _pending = null;
                StartLocked(next);
            }
        }
    }

    private void Display(IReadOnlyDictionary<string, string> values, RunResult result, TimeSpan duration, int generation)
    {
        lock (_gate) if (generation != _generation || _disposed) return;

        LastDuration = duration;
        if (result.Status == RunStatus.Success && result.GroupedResults is { } data)
        {
            DisplayedValues = values;
            DisplayedResult = result;
            _displayedAt    = _clock();
            _sink?.Publish(data);
        }
        Changed?.Invoke(this, EventArgs.Empty);

        if (duration > SlowEvaluation && !RunOnRelease && !_suggested)
        {
            _suggested = true;
            RunOnReleaseSuggested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Ending and interrupting (R-to3-1, R-to3-7) ---------------------------

    /// <summary>Cancels the in-flight evaluation and drops the pending request; any result still on
    /// its way to the UI thread is discarded.</summary>
    private void CancelLocked()
    {
        _generation++;
        _pending = null;
        _inFlightCts?.Cancel();
    }

    /// <summary>
    /// Ends the session: cancels, writes the last displayed result to the results file with
    /// provenance naming the tuned values, then drops the published result.
    /// </summary>
    public void Stop()
    {
        lock (_gate) { CancelLocked(); _disposed = true; }
        if (DisplayedResult?.GroupedResults is { } data && DisplayedValues is { } values)
            _sink?.Commit(data, values);
        _sink?.Drop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels and drops the published result; the display returns to the file as it was.
    /// The session continues.</summary>
    public void Revert()
    {
        lock (_gate) { CancelLocked(); _requested = null; }
        DisplayedValues = null;
        DisplayedResult = null;
        _displayedAt    = null;
        _sink?.Drop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels the in-flight evaluation before the values are written into the design (TO-4
    /// does the writing). The session continues.</summary>
    public void Push()
    {
        lock (_gate) CancelLocked();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Freezes the displayed result as ghosts (R-to3-9).</summary>
    public void Snapshot() => _sink?.Snapshot();

    /// <summary>Removes the ghosts.</summary>
    public void ClearSnapshot() => _sink?.ClearSnapshot();

    /// <summary>Cancels and drops the published result without writing anything.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelLocked();
            _disposed = true;
        }
        _sink?.Drop();
    }

    private static bool SameValues(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        return a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
    }
}
