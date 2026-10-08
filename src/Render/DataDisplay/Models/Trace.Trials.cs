// ================================================================
//  Trace.Trials.cs  —  what a trace carries for seeing WHICH trials
//  fail and why (brief-yield-9): colour by a category, the nominal over
//  the family, an envelope band, a scatter's fit line, and the linked
//  trial selection.
//
//  The authored half (ColorBy, ShowNominal, Envelope, ShowCurves,
//  ShowFitLine) is persisted in the `.cdd`; the resolved half is filled
//  by TrialResolve from the same DataSet the curves came from, in the
//  window and the CLI alike. SelectedTrials is session state, set by
//  the window and never written.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace CircuitRF.Render.DataDisplay
{
    /// <summary>The envelope a family draws (R-ya9-3).</summary>
    public enum EnvelopeKind { Off, MinMax, Percentile, Sigma }

    /// <summary>
    /// An envelope and its parameter: the lower percentile p of a <see cref="EnvelopeKind.Percentile"/> band (P1–P99
    /// is p = 1), or k of a <see cref="EnvelopeKind.Sigma"/> band (mean ± kσ). Written as the CLI spells it —
    /// <c>minmax</c>, <c>p:1</c>, <c>sigma:3</c> — in the <c>.cdd</c> and on <c>plot --trace</c> alike.
    /// </summary>
    public readonly record struct TrialEnvelope(EnvelopeKind Kind, double Parameter)
    {
        public static TrialEnvelope Off => default;

        public bool IsOn => Kind != EnvelopeKind.Off;

        /// <summary>The spelling <see cref="TryParse"/> reads back; null when off.</summary>
        public string? Spelling => Kind switch
        {
            EnvelopeKind.MinMax     => "minmax",
            EnvelopeKind.Percentile => "p:" + Parameter.ToString("R", CultureInfo.InvariantCulture),
            EnvelopeKind.Sigma      => "sigma:" + Parameter.ToString("R", CultureInfo.InvariantCulture),
            _                       => null,
        };

        /// <summary>What a menu or a legend calls it: <c>min–max</c>, <c>P1–P99</c>, <c>mean ± 3σ</c>.</summary>
        public string Label => Kind switch
        {
            EnvelopeKind.MinMax     => "min–max",
            EnvelopeKind.Percentile => $"P{Num(Parameter)}–P{Num(100 - Parameter)}",
            EnvelopeKind.Sigma      => $"mean ± {Num(Parameter)}σ",
            _                       => "off",
        };

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Reads <c>off</c>, <c>minmax</c>, <c>p:&lt;p&gt;</c> (0 ≤ p &lt; 50) or <c>sigma:&lt;k&gt;</c> (k &gt; 0).</summary>
        public static bool TryParse(string? text, out TrialEnvelope envelope, out string? error)
        {
            envelope = Off; error = null;
            string t = (text ?? "").Trim().ToLowerInvariant();
            if (t is "" or "off" or "none") return true;
            if (t is "minmax" or "min-max" or "range") { envelope = new(EnvelopeKind.MinMax, 0); return true; }
            int colon = t.IndexOf(':');
            if (colon > 0 && double.TryParse(t[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                string kind = t[..colon];
                if (kind is "p" or "pctl" && v >= 0 && v < 50) { envelope = new(EnvelopeKind.Percentile, v); return true; }
                if (kind is "sigma" or "s" && v > 0 && double.IsFinite(v)) { envelope = new(EnvelopeKind.Sigma, v); return true; }
            }
            error = $"'{text}' is not an envelope: write minmax, p:<p> for the Pp–P(100−p) band (0 ≤ p < 50), or sigma:<k> for mean ± kσ.";
            return false;
        }
    }

    /// <summary>What one member of a family (or one point of a scatter) is, by its colour-by cube (R-ya9-1).</summary>
    public enum TrialCategory : byte { Pass, Fail, NotEvaluated, Other }

    /// <summary>The members a colour-by counted, over the WHOLE family axis — not only the members drawn.</summary>
    public readonly record struct TrialCounts(int Pass, int Fail, int NotEvaluated);

    /// <summary>An envelope band (R-ya9-3), in the trace's display units: per X point its lower and upper edge and
    /// the line drawn through it (the median of a min–max or percentile band, the mean of a σ band).</summary>
    public sealed record EnvelopeBand(double[] X, double[] Lo, double[] Hi, double[] Centre);

    /// <summary>A scatter's least-squares line (R-ya9-4), y = Intercept + Slope·x in display units, and its R².</summary>
    public sealed record ScatterFit(double Slope, double Intercept, double RSquared);

    public partial class Trace
    {
        /// <summary>The colour-by default on a yield source: the overall pass.</summary>
        public const string ColorByPass = "pass";

        /// <summary>Colour each member of a family over <c>corner</c> by its own corner.</summary>
        public const string ColorByCorner = "corner";

        // ── authored (persisted) ─────────────────────────────────────────────────

        /// <summary>
        /// Colour the family's members (or a scatter's points) by a per-member cube on the family axis (R-ya9-1):
        /// <see cref="ColorByPass"/> (the source's <c>trials.pass</c>), any cube address such as
        /// <c>trials.goal:S21:pass</c>, or <see cref="ColorByCorner"/>. Null: not coloured.
        /// </summary>
        public string? ColorBy { get; set; }

        /// <summary>Draw the nominal's curve over a trial family (R-ya9-2). On by default.</summary>
        public bool ShowNominal { get; set; } = true;

        /// <summary>The family's envelope band (R-ya9-3); off by default.</summary>
        public TrialEnvelope Envelope { get; set; }

        /// <summary>Draw the family's members (R-ya9-3). Off with an envelope draws the band alone.</summary>
        public bool ShowCurves { get; set; } = true;

        /// <summary>Draw a scatter's least-squares line, with its R² in the legend (R-ya9-4).</summary>
        public bool ShowFitLine { get; set; }

        // ── resolved (TrialResolve) ──────────────────────────────────────────────

        /// <summary>Per family curve — or, on a scatter, per point — what its colour-by cube says. Null when the
        /// trace is not coloured.</summary>
        public TrialCategory[]? MemberCategories { get; internal set; }

        /// <summary>Per family curve, its colour slot when coloured by <see cref="ColorByCorner"/>; else null.</summary>
        public int[]? MemberSlots { get; internal set; }

        /// <summary>Pass, fail and did-not-evaluate over the whole family axis; null when not coloured by a pass cube.</summary>
        public TrialCounts? Counts { get; internal set; }

        /// <summary>The nominal's curve, in display units; empty when there is none to draw.</summary>
        public IReadOnlyList<Vector2> NominalPoints { get; internal set; } = Array.Empty<Vector2>();

        /// <summary>The envelope band; null when off or refused.</summary>
        public EnvelopeBand? Band { get; internal set; }

        /// <summary>Why the envelope is not drawn, when it is on and cannot be (a Smith or Polar plot).</summary>
        public string? EnvelopeRefusal { get; internal set; }

        /// <summary>The scatter's fit; null when off or there are too few points.</summary>
        public ScatterFit? Fit { get; internal set; }

        /// <summary>
        /// The trial each family curve is (by <see cref="FamilyCurve.AxisValue"/>), each scatter point is, or each
        /// histogram bar holds — one array per curve, point or bar — so a click can say which trials it hit and a
        /// selection can say which to highlight. Null when this trace draws no trials.
        /// </summary>
        public IReadOnlyList<int[]>? ElementTrials { get; internal set; }

        /// <summary>Which of <see cref="FamilyCurves"/>, <see cref="Points"/> or the bars <see cref="ElementTrials"/>
        /// indexes.</summary>
        public TrialElements ElementKind { get; internal set; }

        /// <summary>
        /// The trials selected in this trace's source (R-ya9-6): those elements draw highlighted and the rest dim.
        /// Session state — set by the window from the shared selection, never persisted, never copied.
        /// </summary>
        public IReadOnlySet<int>? SelectedTrials { get; set; }

        /// <summary>The axis a non-family trace's own samples were on before a "versus" X replaced it — a scatter's
        /// <c>trial</c>. Set by the resolve.</summary>
        internal RfCore.Data.Axis? SampleAxis { get; set; }

        /// <summary>True when the selection is non-empty and this trace carries trials to highlight.</summary>
        public bool HasTrialSelection => SelectedTrials is { Count: > 0 } && ElementTrials is not null;

        /// <summary>True when element <paramref name="k"/> holds a selected trial.</summary>
        public bool ElementSelected(int k)
        {
            if (SelectedTrials is not { Count: > 0 } sel || ElementTrials is not { } el || k < 0 || k >= el.Count) return false;
            foreach (int trial in el[k]) if (sel.Contains(trial)) return true;
            return false;
        }

        /// <summary>The sample index of point <paramref name="p"/> of a single-curve trace, or −1.</summary>
        internal int SampleIndexOf(int p) => p >= 0 && p < _pointSample.Count && _pointSample.Count == Points.Count ? _pointSample[p] : -1;

        /// <summary>The legend's suffix (R-ya9-1, R-ya9-4): <c> — 471 pass · 29 fail</c>, <c> — R² = 0.93</c>.</summary>
        internal string TrialLegendSuffix
        {
            get
            {
                var parts = new List<string>();
                if (Counts is { } c)
                {
                    parts.Add($"{c.Pass} pass · {c.Fail} fail");
                    if (c.NotEvaluated > 0) parts[^1] += $" · {c.NotEvaluated} not evaluated";
                }
                if (Fit is { } f) parts.Add("R² = " + f.RSquared.ToString("0.###", CultureInfo.InvariantCulture));
                return parts.Count == 0 ? "" : " — " + string.Join(" · ", parts);
            }
        }
    }

    /// <summary>What a trace's <see cref="Trace.ElementTrials"/> indexes.</summary>
    public enum TrialElements { None, Curves, Points, Bars }
}
