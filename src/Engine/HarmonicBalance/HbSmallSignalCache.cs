using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Everything the WSProbe's harmonic-balance small-signal sweep can compute ONCE and reuse at every
/// operating point of a drive sweep — brief-wsprobe-8 §2 and §3.
///
/// <para><b>The whole argument in one line: the linear partition does not depend on the drive.</b>
/// WSP-5's straightforward path factors the linear partition <c>2·K_ss + 2</c> times per probe
/// frequency per operating point, so a 41-point drive sweep over a 991-point tickle grid at
/// <c>K_ss = 7</c> pays 609,465 sparse factorisations of the whole network — against 991 for the
/// S-parameter sweep of the same circuit. Every one of them is the same matrix it was at the
/// previous drive level. What genuinely changes with the operating point is the device spectra, and
/// those enter only the DENSE conversion matrix. So the sparse work moves here, is keyed by
/// frequency, and the per-operating-point work becomes dense-only (R-wsp8-3).</para>
///
/// <para><b>What is stored, per frequency (R-wsp8-1).</b> At a probe frequency <c>ω_ss</c> the
/// readings the document wants are <c>2N</c> entries of the solution vector — a branch current and a
/// node voltage per probe. Writing <c>R</c> for that <c>2N × n</c> selection of rows, the reading of
/// injection <c>p</c> is
/// <code>
///   r = R·M⁻¹·b_p + R·M⁻¹·B_int·(−I_nl,0)  =  W[p, ·] + T·(−I_nl,0)
/// </code>
/// and <c>R·M⁻¹</c> is <c>2N</c> ROWS of <c>M⁻¹</c>, each obtained by one solve against the
/// TRANSPOSED factorisation. The interface rows give <c>Z_NN</c> (hence <c>Y_NN</c>) and every
/// probe injection's open-circuit interface voltage at once, so the whole per-frequency block is
/// <c>N_int + 2N</c> transposed solves on ONE factorisation — replacing <c>4N</c> forward solves
/// plus <c>N_int</c> Z-column solves PER OPERATING POINT.</para>
///
/// <para><b>The transposed solve is HERMITIAN, and that is a trap.</b> CSparse's
/// <c>SparseLU.SolveTranspose</c> solves <c>Mᴴ y = b</c>, not <c>Mᵀ y = b</c> — measured here, not
/// assumed, because a real test matrix cannot tell the two apart. So the row of <c>M⁻¹</c> this code
/// wants is <c>conj(y)</c>, and the conjugation is applied at every read
/// (<see cref="Row"/>). Omitting it is a sign error on the imaginary part alone: every magnitude
/// stays right, so a Smith-chart plot looks plausible and only the phase is mirrored.</para>
///
/// <para><b>Reuse is verified, never declared (R-wsp8-4).</b> Each entry carries the VALUES of the
/// matrix it was computed from; the next use at that frequency re-stamps and compares them bit for
/// bit, exactly as <see cref="HbLinearExtractor"/> validates its own factorisations. A swept linear
/// element or a loadpull tuner override changes the matrix and the entry is recomputed; nothing else
/// can go stale and no caller has to remember to invalidate. The cost of that guarantee is one
/// stamp and one <c>O(nnz)</c> comparison per frequency per operating point — no factorisation and
/// no solve, which is what <see cref="Counters"/> asserts.</para>
///
/// <para><b>Lifetime.</b> One cache per drive sweep, created by whoever owns the sweep and handed to
/// every operating point. It deliberately does NOT live in <see cref="HbLinearExtractor"/>:
/// <c>ParametricSweepEngine</c> re-elaborates the netlist and builds a fresh <c>HbEngine</c> — and
/// therefore a fresh extractor — at every point, so an extractor-owned cache would be thrown away
/// exactly when it was about to pay for itself. Nothing here holds a netlist, a component or a
/// factorisation; the entries are small dense arrays plus the certificate, and they are valid for
/// any netlist that stamps the same matrix.</para>
/// </summary>
public sealed class HbSmallSignalCache
{
    /// <summary>
    /// The RELATIVE precision a frequency is rounded to before it is used as a cache key OR as the
    /// frequency the network is stamped at. The grid is an arithmetic progression and the sidebands
    /// are exact multiples of <c>ω0</c>, so a coincidence is either exact to the last few bits or
    /// nowhere near; this rounds away the last bits of <c>ω_ss + k·ω0</c> arithmetic and nothing
    /// more.
    ///
    /// <para><b>1e-14 is the floor, not a preference:</b> the rounding is done on the mantissa, so
    /// the rounded integer is up to <c>1/KeyRelTol</c> and must stay below <c>2^53</c> to be exact
    /// in a double. It is ~45 ulps, five orders below the 1e-12 the whole feature is gated at.</para>
    /// </summary>
    private const double KeyRelTol = 1e-14;

    private readonly int _budgetBytes;

    /// <param name="settings">Read for <see cref="AnalysisSettings.WspCacheBudgetMB"/> only.</param>
    /// <param name="degree">How many worker slices to keep. Each parallel chunk of the tickle grid
    /// gets its own, because an <see cref="MnaSystem"/> is stamped into and a dictionary is written;
    /// neither is thread-safe and neither needs to be, since the chunks are disjoint.</param>
    public HbSmallSignalCache(AnalysisSettings? settings = null, int degree = 1)
    {
        int mb = Math.Max(0, (settings ?? AnalysisSettings.Default).WspCacheBudgetMB);
        _budgetBytes = (int)Math.Min(int.MaxValue, (long)mb * 1024 * 1024);
        EnsureDegree(degree);
    }

    private Slice[] _slices = [];
    private int     _degree;

    /// <summary>How many slices this cache holds — the parallel degree it may be used at. Chunk
    /// boundaries decide which slice holds which frequency, so a run at a different degree cannot
    /// reuse the slices of another: asking for a different one EMPTIES the cache rather than
    /// re-chunking it. Within one drive sweep the degree is a function of the grid length and the
    /// settings, so it never changes and nothing is ever emptied.</summary>
    public int Degree => _degree;

    // ── Cumulative counters, over the whole life of the cache ───────────────
    //
    // The per-run Counters say what ONE operating point did; these say what the whole drive sweep
    // did, which is the number brief-wsprobe-8 is actually about. A sweep of P points whose second
    // and later points add nothing to Factorizations is the claim, stated as a count.

    /// <summary>Sparse LU factorisations this cache has performed, over every operating point it
    /// has served.</summary>
    public int Factorizations => _factorizations;
    private int _factorizations;

    /// <summary>Transposed back-substitutions performed, likewise.</summary>
    public int TransposedSolves => _transposed;
    private int _transposed;

    /// <summary>Stamps of the linear partition — the cost of the bit-for-bit certificate.</summary>
    public int Stamps => _stamps;
    private int _stamps;

    /// <summary>Entries reused, and entries computed, over every operating point.</summary>
    public int Hits => _hits;
    private int _hits;

    /// <summary>How many times a worker's <see cref="MnaSystem"/> has built its sparsity pattern.
    /// One per worker over a whole sweep is the structural property R-wsp8-6 is about — the sum is
    /// reported so a single number can be asserted at any degree.</summary>
    public int PatternBuilds
    {
        get { int n = 0; foreach (var s in _slices) n += s.Mna?.PatternBuilds ?? 0; return n; }
    }

    /// <summary>Entries currently held, across every worker slice.</summary>
    public int EntryCount
    {
        get { int n = 0; foreach (var s in _slices) n += s.Entries.Count; return n; }
    }

    /// <inheritdoc cref="Degree"/>
    public void EnsureDegree(int degree)
    {
        degree = Math.Max(1, degree);
        if (degree == _degree) return;
        _degree = degree;
        _slices = new Slice[degree];
        for (int i = 0; i < degree; i++) _slices[i] = new Slice();
    }

    // ── Tiers (R-wsp8-8) ────────────────────────────────────────────────────

    /// <summary>What the cache is currently allowed to keep.</summary>
    public enum Tier
    {
        /// <summary>Everything: the sideband <c>Y_NN</c> entries and the per-<c>ω_ss</c> probe
        /// blocks. The whole point of brief-wsprobe-8.</summary>
        Full,
        /// <summary>Sideband <c>Y_NN</c> only. The probe blocks are recomputed per operating point —
        /// one factorisation and <c>N_int + 2N</c> transposed solves each, still far below the
        /// straightforward path's <c>2·K_ss + 2</c> factorisations per point.</summary>
        SidebandsOnly,
        /// <summary>Nothing is kept. The cost of WSP-5's straightforward path, without its
        /// arithmetic — this is the honest floor when the projected cache does not fit at all.</summary>
        None,
    }

    /// <summary>The tier this cache settled on, decided ONCE from the projected sizes on the first
    /// frequency it was asked for (so every operating point behaves identically), and null until
    /// then.</summary>
    public Tier? ChosenTier { get; private set; }

    /// <summary>The projection the tier was chosen from, in bytes, and the sizes that went into it —
    /// what <c>wsprobe.hb-cache-over-budget</c> reports.</summary>
    public (long Projected, long Sidebands, long ProbeBlocks, int Nnz)? Projection { get; private set; }

    /// <summary>
    /// The sizes of the sweep about to be run, so the cache can project its own footprint before it
    /// stores anything. Called once per sweep; a second call with the same sizes is ignored.
    /// </summary>
    public void Plan(int nInt, int nProbes, int distinctSidebands, int ssPoints)
    {
        _planNInt      = nInt;
        _planProbes    = nProbes;
        _planSidebands = distinctSidebands;
        _planPoints    = ssPoints;
    }

    private int _planNInt, _planProbes, _planSidebands, _planPoints;

    private void ChooseTier(int nnz)
    {
        if (ChosenTier is not null) return;

        int n2   = _planProbes * 2;
        long ySz = 16L * _planNInt * _planNInt + 16L * nnz;                       // Y_NN + certificate
        long pSz = 16L * (n2 * n2 + n2 * _planNInt + _planNInt * n2);             // W + T + I_src
        long sidebands  = ySz * Math.Max(_planSidebands, 1);
        long probeBlock = pSz * Math.Max(_planPoints, 1);

        Projection = (sidebands + probeBlock, sidebands, probeBlock, nnz);
        ChosenTier = sidebands + probeBlock <= _budgetBytes ? Tier.Full
                   : sidebands              <= _budgetBytes ? Tier.SidebandsOnly
                   : Tier.None;
    }

    /// <summary>The one sentence a run says when it did not get the cache it wanted — R-wsp8-8's
    /// "never silently". Null while the cache is running at <see cref="Tier.Full"/>.</summary>
    public string? OverBudgetNote()
    {
        if (ChosenTier is null or Tier.Full || Projection is not { } pr) return null;
        // Reported in the unit the number actually has: a projection that rounds to "0 MB" tells a
        // reader nothing, and the budget is most often exceeded on a small fixture by a budget of 0.
        static string Mb(long b) => b >= 1024L * 1024
            ? $"{b / 1024.0 / 1024.0:F0} MB"
            : b >= 1024 ? $"{b / 1024.0:F0} kB" : $"{b} bytes";
        string kept = ChosenTier == Tier.SidebandsOnly
            ? $"the sideband admittances ({Mb(pr.Sidebands)}) are kept and the per-probe-frequency " +
              "blocks are recomputed at every operating point"
            : "nothing is kept and every operating point recomputes the whole small-signal linear " +
              "partition";
        return $"WSProbe small-signal sweep: the operating-point-independent cache would need " +
               $"{Mb(pr.Projected)} ({Mb(pr.Sidebands)} of sideband admittances plus " +
               $"{Mb(pr.ProbeBlocks)} of probe blocks) and WspCacheBudgetMB allows " +
               $"{Mb(_budgetBytes)}, so {kept}. The results are identical; the run is " +
               "slower. Raise WspCacheBudgetMB if the memory is there.";
    }

    // ── One worker's slice ──────────────────────────────────────────────────

    private sealed class Slice
    {
        public MnaSystem? Mna;
        public int        PatternGeneration = -1;
        public int        Epoch;
        public readonly Dictionary<long, Entry> Entries = [];
    }

    /// <summary>
    /// One frequency's operating-point-independent data. <see cref="YNN"/> is always present;
    /// the probe block is present only at a probe frequency <c>ω_ss</c>, and only at
    /// <see cref="Tier.Full"/>.
    /// </summary>
    private sealed class Entry
    {
        /// <summary>The frequency this entry was computed at — <see cref="Canonical"/> of whatever
        /// was asked for, so that every spelling of one physical frequency stamps the SAME matrix.
        /// Without it the certificate comparison would be asking "did the caller spell the frequency
        /// the same way?" rather than "has the network changed?", which no coincidence would ever
        /// pass, and every shared sideband would be a factorisation again.</summary>
        public required double Omega;

        /// <summary>The epoch this entry was last validated in — see
        /// <see cref="BeginOperatingPoint"/>.</summary>
        public int ValidatedEpoch = -1;
        public required Complex[,] YNN;      // N_int × N_int
        public required Complex[]  Cert;     // the stamped values this was computed from
        public Complex[,]? W;                // 2N × 2N   — reading of injection p with the interface OPEN
        public Complex[,]? T;                // 2N × N_int — reading per unit interface current
        public Complex[,]? ISrc;             // N_int × 2N — the Norton excitation of each injection
    }

    /// <summary>The per-<c>ω_ss</c> block the dense-only solve consumes.</summary>
    public readonly record struct ProbeBlock(
        Complex[,] YNN, Complex[,] W, Complex[,] T, Complex[,] ISrc);

    /// <summary>
    /// The cache key of a frequency: its signed mantissa to 13 significant digits, tagged with its
    /// decimal exponent. Two frequencies that agree to that precision are the same frequency —
    /// adjacent points of a 991-point grid over a decade differ by ~1e-3 relative, so nothing that
    /// is genuinely different is ever merged, which is the direction that matters. The failure mode
    /// left open is the opposite one: two spellings of a value that straddle a decade boundary get
    /// different keys and the coincidence is missed, which costs one redundant factorisation and
    /// nothing else.
    /// </summary>
    private static long Key(double omega)
    {
        if (omega == 0.0) return 0;
        double a = Math.Abs(omega);
        int    e = (int)Math.Floor(Math.Log10(a));
        double m = omega / Math.Pow(10, e);                 // |m| in [1, 10)
        long   q = (long)Math.Round(m / KeyRelTol);         // 15 significant digits
        return (long)e * 10_000_000_000_000_000L + q;
    }

    /// <summary>
    /// The frequency an entry is actually computed at: <paramref name="omega"/> rounded to the same
    /// precision <see cref="Key"/> rounds to.
    ///
    /// <para><b>Why the computation frequency and not just the key.</b> Two spellings of one
    /// physical frequency — <c>ω_ss + k·ω0</c> reached from different <c>(ω_ss, k)</c> pairs —
    /// share a key and differ in their last bits, so they stamp matrices that differ in their last
    /// bits. If the entry were computed at whichever spelling arrived first, the answer would depend
    /// on the ARRIVAL ORDER, which is exactly what changes when the tickle grid is chunked across
    /// workers: R-wsp8-9's bit-identity would hold at one degree and not another, by a few ulps, for
    /// a purely bookkeeping reason. Rounding first makes the frequency a function of the value
    /// alone, so every worker at every degree computes the same entry.</para>
    ///
    /// <para>The cost is a frequency perturbation of at most <c>KeyRelTol/2</c> relative — five
    /// orders below the 1e-12 the feature is gated at, and measured at ~1e-14 against WSP-5's
    /// straightforward path, which uses the requested frequency exactly.</para>
    /// </summary>
    private static double Canonical(double omega)
    {
        if (omega == 0.0) return 0.0;
        double a = Math.Abs(omega);
        int    e = (int)Math.Floor(Math.Log10(a));
        double p = Math.Pow(10, e);
        return Math.Round(omega / p / KeyRelTol) * KeyRelTol * p;
    }

    private Slice SliceFor(int worker) => _slices[Math.Clamp(worker, 0, _degree - 1)];

    /// <summary>
    /// Open a new validation epoch on one worker's slice. Every entry must then be re-validated —
    /// re-stamped and compared bit for bit — the first time it is used, and may be trusted without
    /// a stamp on every later use WITHIN the epoch.
    ///
    /// <para><b>Why an epoch rather than a stamp every time.</b> Nothing mutates the netlist during
    /// a small-signal sweep: the Newton solve is finished before the first probe frequency is
    /// touched. So a second visit to a frequency inside one sweep cannot find a different matrix
    /// than the first visit did, and stamping again to discover that is pure cost — a real one,
    /// since the sidebands of neighbouring probe frequencies coincide by design and a frequency is
    /// typically visited three or four times per sweep.</para>
    ///
    /// <para><b>It is not something a caller has to remember.</b> The epoch is opened by
    /// <see cref="HbSmallSignal.SolveProbeRange"/> itself, which is the one function that runs a
    /// sweep on a slice — there is no path that uses the cache without going through it, and a range
    /// is exactly one operating point on one worker.</para>
    /// </summary>
    public void BeginOperatingPoint(int worker) => SliceFor(worker).Epoch++;

    /// <summary>The row of <c>M⁻¹</c> a transposed solve produced: CSparse solves <c>Mᴴ y = e</c>,
    /// so the row is <c>conj(y)</c>. See the type remarks — this conjugation is the difference
    /// between the right answer and a phase-mirrored one.</summary>
    private static Complex Row(Complex[] y, int i) => i >= 0 ? Complex.Conjugate(y[i]) : Complex.Zero;

    // ── The two things the solve asks for ───────────────────────────────────

    /// <summary>
    /// <c>Y_NN</c> at a SIGNED frequency, R-wsp5-3's rules kept exactly: the DC formulation at zero,
    /// the stamped network above it, and the complex conjugate of <c>|ω|</c>'s below it — so no model
    /// is ever called at a negative frequency, and one cache entry serves <c>+ω</c> and <c>−ω</c>
    /// both.
    /// </summary>
    public Complex[,] YNNAt(
        HbLinearExtractor extractor, double omega, Complex[,] yDc,
        HbSmallSignal.Counters counters, int worker = 0)
    {
        counters.LinearPartitions++;
        if (Math.Abs(omega) < 1e-12) return yDc;

        var y = EntryAt(extractor, Math.Abs(omega), counters, worker, null).YNN;
        if (omega > 0) return y;

        int n = y.GetLength(0), m = y.GetLength(1);
        var c = new Complex[n, m];
        for (int i = 0; i < n; i++)
        for (int k = 0; k < m; k++) c[i, k] = Complex.Conjugate(y[i, k]);
        return c;
    }

    /// <summary>
    /// The whole per-<c>ω_ss</c> block of R-wsp8-1 — <c>Y_NN</c>, <c>W</c>, <c>T</c> and
    /// <c>I_src</c> — computed once for the sweep and validated on every later use.
    /// </summary>
    public ProbeBlock ProbeBlockAt(
        HbLinearExtractor extractor, double omegaSs, Func<int, ProbeGeometry> geometry,
        HbSmallSignal.Counters counters, int worker = 0)
    {
        counters.LinearPartitions++;
        var e = EntryAt(extractor, omegaSs, counters, worker, geometry);
        return new ProbeBlock(e.YNN, e.W!, e.T!, e.ISrc!);
    }

    /// <summary>
    /// Where a probe's injections go and where its readings come from, in the row indices of the
    /// stamped MNA. Built after the stamp, because the branch index is the assembly's, not the
    /// model's.
    /// </summary>
    public readonly record struct ProbeGeometry(int[] InjectRow, bool[] InjectNegate, int[] ReadRow)
    {
        public int Count => InjectRow.Length;
    }

    /// <summary>
    /// The geometry of R-wsp1-5's stamps: per probe, a SERIES injection of <c>−1</c> on its branch
    /// row (the constraint row reads <c>V(nG) − V(nL) = value</c>, so <c>−1</c> gives
    /// <c>v_L − v_G = 1</c>) and a SHUNT injection of <c>+1</c> into its G node; per probe, a branch
    /// current reading and a G-node voltage reading. A grounded G node has no row: its injection is
    /// the zero vector and its reading is zero, exactly as the straightforward path reads it.
    /// </summary>
    public static ProbeGeometry GeometryOf(HbSmallSignal.ProbeSite[] probes, int[] branchRows)
    {
        int np = probes.Length, size = 2 * np;
        var inj = new int[size];
        var neg = new bool[size];
        var rd  = new int[size];
        for (int i = 0; i < np; i++)
        {
            int g = probes[i].GNode > 0 ? probes[i].GNode - 1 : -1;
            inj[2 * i]     = branchRows[i]; neg[2 * i]     = true;   // series: −1 on the branch row
            inj[2 * i + 1] = g;             neg[2 * i + 1] = false;  // shunt:  +1 into the G node
            rd[2 * i]      = branchRows[i];
            rd[2 * i + 1]  = g;
        }
        return new ProbeGeometry(inj, neg, rd);
    }

    // ── The entry, and the only place a factorisation happens ───────────────

    private Entry EntryAt(
        HbLinearExtractor extractor, double omega, HbSmallSignal.Counters counters,
        int worker, Func<int, ProbeGeometry>? geometry)
    {
        var slice = SliceFor(worker);
        slice.Mna ??= extractor.NewSmallSignalMna();

        // Stamp first, always: it is what the certificate is compared against, and it is what
        // assigns the branch indices the geometry is read in. When an entry already claims this
        // key, stamp at ITS frequency — see Entry.Omega.
        long key = Key(omega);
        bool wantProbe = geometry is not null;
        slice.Entries.TryGetValue(key, out var hit);

        // Already validated in this epoch, and carrying what was asked for: nothing can have
        // changed since, so neither the stamp nor the comparison would tell us anything.
        if (hit is not null && hit.ValidatedEpoch == slice.Epoch && (!wantProbe || hit.W is not null))
        {
            counters.CacheHits++;
            Interlocked.Increment(ref _hits);
            return hit;
        }

        double stampOmega = Canonical(omega);
        extractor.StampSmallSignal(slice.Mna, stampOmega);
        counters.Stamps++;
        Interlocked.Increment(ref _stamps);

        // LiveNnz forces the sparsity pattern to be built, and it must be read BEFORE the
        // generation is compared: on the very first stamp the pattern does not exist yet, so
        // PatternBuilds still reads 0 and the entry this call is about to store would be discarded
        // by the next call as though the topology had changed.
        int nnz = slice.Mna.LiveNnz;
        ChooseTier(nnz);

        // A pattern rebuild means the topology this cache was filled for is not the one being
        // stamped now. Nothing about the stored entries is trustworthy then, and the fix is not a
        // per-entry one.
        if (slice.PatternGeneration >= 0 && slice.Mna.PatternBuilds != slice.PatternGeneration)
        {
            slice.Entries.Clear();
            hit = null;
        }
        slice.PatternGeneration = slice.Mna.PatternBuilds;

        if (hit is not null && slice.Mna.MatchesValues(hit.Cert)
            && (!wantProbe || hit.W is not null))
        {
            counters.CacheHits++;
            Interlocked.Increment(ref _hits);
            hit.ValidatedEpoch = slice.Epoch;
            return hit;
        }
        counters.CacheMisses++;

        // The geometry is read AFTER the stamp, because a probe's branch index is the assembly's
        // and only exists once the assembly has been made.
        int before = counters.TransposedSolves;
        var e = Compute(slice.Mna, extractor, stampOmega, counters, geometry?.Invoke(slice.Mna.Size));
        e.ValidatedEpoch = slice.Epoch;
        Interlocked.Increment(ref _factorizations);
        Interlocked.Add(ref _transposed, counters.TransposedSolves - before);

        // At SidebandsOnly a probe frequency still contributes its Y_NN — that half is the
        // expensive one to recompute and it is what the tier was defined to keep.
        switch (ChosenTier)
        {
            case Tier.Full:
                slice.Entries[key] = e;
                break;
            case Tier.SidebandsOnly:
                slice.Entries[key] = wantProbe
                    ? new Entry { Omega = e.Omega, YNN = e.YNN, Cert = e.Cert,
                                  ValidatedEpoch = slice.Epoch }
                    : e;
                break;
            default:
                slice.Entries.Remove(key);
                break;
        }
        return e;
    }

    /// <summary>
    /// One factorisation and <c>N_int</c> (+ <c>2N</c> at a probe frequency) transposed solves — the
    /// whole of R-wsp8-1 and R-wsp8-2.
    /// </summary>
    private static Entry Compute(
        MnaSystem mna, HbLinearExtractor extractor, double omega,
        HbSmallSignal.Counters counters, ProbeGeometry? geom)
    {
        var cert = mna.SnapshotValues();
        var lu   = mna.Factorize();
        counters.SparseFactorizations++;

        int   size    = mna.Size;
        int   nInt    = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;

        var b = new Complex[size];
        var y = new Complex[size];

        // ── The interface rows: Z_NN, and every injection's open-circuit interface voltage ──
        var zNN = new Complex[nInt, nInt];
        var voc = geom is { } g0 ? new Complex[nInt, g0.Count] : null;

        for (int n = 0; n < nInt; n++)
        {
            int rowIdx = ifNodes[n] > 0 ? ifNodes[n] - 1 : -1;
            if (rowIdx < 0) continue;                       // a grounded interface node has no row
            Array.Clear(b);
            b[rowIdx] = Complex.One;
            lu.SolveTranspose(b, y);
            counters.TransposedSolves++;

            for (int m = 0; m < nInt; m++)
                zNN[n, m] = Row(y, ifNodes[m] > 0 ? ifNodes[m] - 1 : -1);

            if (geom is { } g)
                for (int p = 0; p < g.Count; p++)
                    voc![n, p] = Inject(y, g, p);
        }

        // a port the network pins takes the extractor's series R, so this Y_NN is the Newton's own
        var yNN = InvertNN(extractor.RegularizePinned(zNN), nInt);

        Entry entry = new() { Omega = omega, YNN = yNN, Cert = cert };
        if (geom is not { } gm) return entry;

        // ── The reading rows: W and T ──────────────────────────────────────
        int count = gm.Count;
        var w = new Complex[count, count];
        var t = new Complex[count, nInt];

        for (int c = 0; c < count; c++)
        {
            int rowIdx = gm.ReadRow[c];
            if (rowIdx < 0) continue;                       // a grounded G node reads zero
            Array.Clear(b);
            b[rowIdx] = Complex.One;
            lu.SolveTranspose(b, y);
            counters.TransposedSolves++;

            for (int p = 0; p < count; p++) w[p, c] = Inject(y, gm, p);
            for (int n = 0; n < nInt; n++)
                t[c, n] = Row(y, ifNodes[n] > 0 ? ifNodes[n] - 1 : -1);
        }

        // I_src[·, p] = −Y_NN·V_oc[·, p], the extractor's own ISrcFromVoc, done for every injection
        // at once because V_oc for all of them fell out of the same N_int rows.
        var iSrc = new Complex[nInt, count];
        for (int p = 0; p < count; p++)
        for (int n = 0; n < nInt; n++)
        {
            Complex acc = Complex.Zero;
            for (int m = 0; m < nInt; m++) acc += yNN[n, m] * voc![m, p];
            iSrc[n, p] = -acc;
        }

        entry.W = w; entry.T = t; entry.ISrc = iSrc;
        return entry;
    }

    /// <summary>The row's contraction with injection <paramref name="p"/> — one lookup, because an
    /// injection vector has exactly one nonzero.</summary>
    private static Complex Inject(Complex[] y, ProbeGeometry g, int p)
    {
        int r = g.InjectRow[p];
        if (r < 0) return Complex.Zero;
        var v = Row(y, r);
        return g.InjectNegate[p] ? -v : v;
    }

    // ── Dense N×N inversion — the extractor's own routine, kept identical ────

    private static Complex[,] InvertNN(Complex[,] src, int n)
    {
        var a   = (Complex[,])src.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = Complex.One;

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Complex.Abs(a[col, col]);
            for (int row = col + 1; row < n; row++)
            {
                double mag = Complex.Abs(a[row, col]);
                if (mag > best) { best = mag; pivot = row; }
            }
            if (pivot != col)
                for (int j = 0; j < n; j++)
                {
                    (a[col, j],   a[pivot, j])   = (a[pivot, j],   a[col, j]);
                    (inv[col, j], inv[pivot, j]) = (inv[pivot, j], inv[col, j]);
                }

            Complex diag = a[col, col];
            if (Complex.Abs(diag) < 1e-30) continue;
            for (int j = 0; j < n; j++) { a[col, j] /= diag; inv[col, j] /= diag; }

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                Complex factor = a[row, col];
                for (int j = 0; j < n; j++)
                {
                    a[row, j]   -= factor * a[col, j];
                    inv[row, j] -= factor * inv[col, j];
                }
            }
        }
        return inv;
    }

    // ── Counting the sweep's own shape (R-wsp8-5) ───────────────────────────

    /// <summary>
    /// How many DISTINCT frequencies a tickle grid's sidebands actually visit, against how many it
    /// would visit if none of them coincided — R-wsp8-5. When the grid step divides <c>f0</c> the
    /// sidebands of one point ARE the sidebands of others, and the sweep's sparse work collapses by
    /// that ratio; when it does not, nothing coincides and the ratio is 1.
    ///
    /// <para><b>The grid is never altered to achieve it</b> (the AUT-8 rule: a frequency the user
    /// wrote is a frequency the engine uses). This is reported so a designer can SEE what an aligned
    /// step would buy, and choose it.</para>
    ///
    /// <para>Frequencies are folded onto <c>|ω|</c>, because <c>Y(−ω) = conj(Y(ω))</c> is a theorem
    /// and one entry serves both — so this count is below the brief's own
    /// <c>M + 2·K_ss·m</c> arithmetic, which counts signed sidebands. Both numbers are reported;
    /// this is the one that equals the factorisation count.</para>
    /// </summary>
    public static (int Distinct, int Signed, int Total) SidebandCensus(
        double[] ssFreqsHz, HbSmallSignal.ISidebands sb)
    {
        var folded = new HashSet<long>();
        var signed = new HashSet<long>();
        int total  = 0;
        foreach (double f in ssFreqsHz)
        {
            double omegaSs = 2.0 * Math.PI * f;
            if (sb.IsDegenerate(omegaSs)) continue;
            for (int s = 0; s < sb.Count; s++)
            {
                double w = omegaSs + sb.Offset(s);
                total++;
                signed.Add(Key(w));
                folded.Add(Key(Math.Abs(w)));
            }
        }
        return (folded.Count, signed.Count, total);
    }
}
