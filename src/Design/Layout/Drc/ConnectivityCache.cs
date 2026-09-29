// brief-railrf-36 R-rail36-3 — one connectivity per artwork, not one per question.
//
// A railRF open and a run asked the same board's partition several times over: the return-net
// resolution and the walk share one (R-rail31-1), but the refusal sentences, the window's net
// preview, the reference-net measurement and a second rail each made their own — and on the six-layer
// field board one partition was minutes. Every `DrcConnectivity` form now reads through here.
//
// ── KEYED ON CONTENT, NEVER ON IDENTITY ─────────────────────────────────────────────────────────
//
// `TechnologyCache` hands back SHARED instances and every caller builds its own `layerRegions`
// dictionary, so object identity would both miss (a fresh dictionary of the same copper) and hit
// wrongly (a technology edited in place). The key is a 128-bit digest of every vertex in the
// dictionary's own enumeration order — order matters, because piece and net numbers follow it —
// plus the stackup fields the partition reads, spelled out. Nothing else reaches `Compute`.
//
// ── SMALL AND BOUNDED ───────────────────────────────────────────────────────────────────────────
//
// A partition holds the board's copper once more (~14 MB on the field board), so only the last few
// are kept. What repeats is the SAME artwork asked by several readers in a row; an LRU of four
// covers that and a rail's own copper asked separately (PdnRailConnectivity's railOnly).

using System.Numerics;
using Clipper2Lib;

namespace CircuitRF.Design.Layout.Drc;

/// <summary>One extraction's whole answer — what every <see cref="DrcConnectivity"/> form reads.</summary>
internal sealed record ConnectivityPartition(
    IReadOnlyList<DrcNetPiece> Pieces, IReadOnlyList<PieceJoin> Joins, GroundReach Ground)
{
    /// <summary>
    /// brief-railrf-38 — every via-layer piece that touched two or more conductor pieces, with ALL of
    /// them. <see cref="Joins"/> keeps only the touches that merged two sets, so a hole beside
    /// another that already joined the same copper records one join or none; this keeps what the
    /// barrel rule actually found, so a reading can take the walk's answer about each hole rather
    /// than re-derive it. Additive: nothing here changes which pieces are one net.
    /// </summary>
    public IReadOnlyList<BarrelTouch> Barrels { get; init; } = [];
}

/// <summary>One via-layer piece and every conductor piece the walk's barrel rule found touching it
/// (brief-railrf-38).</summary>
/// <param name="Via">Index of the via-layer piece in <see cref="ConnectivityPartition.Pieces"/>.</param>
/// <param name="Touched">Indices of the conductor pieces it touched, at most one per conductor.</param>
internal readonly record struct BarrelTouch(int Via, IReadOnlyList<int> Touched);

/// <summary>The last few partitions, keyed on the copper and stackup that produced them.</summary>
internal sealed class ConnectivityCache
{
    private const int Capacity = 4;

    private readonly record struct Key(ulong H1, ulong H2, long Vertices, string Stackup);

    /// <summary>The one every extraction reads through.</summary>
    private static readonly ConnectivityCache Shared = new();

    /// <summary>A test's own cache — see <see cref="BeginIsolated"/>.</summary>
    private static readonly AsyncLocal<ConnectivityCache?> Isolated = new();

    private readonly object _gate = new();
    private readonly LinkedList<(Key Key, Lazy<ConnectivityPartition> Value)> _entries = new();

    /// <summary>The partition of <paramref name="layerRegions"/> under <paramref name="tech"/>,
    /// computed once per content.</summary>
    public static ConnectivityPartition Get(IReadOnlyDictionary<LayerKey, Paths64> layerRegions, Technology tech)
    {
        ArgumentNullException.ThrowIfNull(layerRegions);
        ArgumentNullException.ThrowIfNull(tech);
        return (Isolated.Value ?? Shared).GetOrCompute(layerRegions, tech);
    }

    /// <summary>
    /// Until disposed, extractions on this logical call path read an EMPTY cache of their own —
    /// the same keying and the same eviction, with no other test's boards in it. A counter of
    /// extractions read against the shared cache measures whatever else the test run is doing: with
    /// four entries, a parallel run evicts a test's board between its own two questions.
    /// </summary>
    public static IDisposable BeginIsolated()
    {
        var previous = Isolated.Value;
        Isolated.Value = new ConnectivityCache();
        return new Restore(previous);
    }

    private sealed class Restore(ConnectivityCache? previous) : IDisposable
    {
        public void Dispose() => Isolated.Value = previous;
    }

    private ConnectivityPartition GetOrCompute(IReadOnlyDictionary<LayerKey, Paths64> layerRegions, Technology tech)
    {
        var key = KeyOf(layerRegions, tech);
        Lazy<ConnectivityPartition> entry;
        bool hit = false;

        lock (_gate)
        {
            var node = _entries.First;
            while (node is not null && node.Value.Key != key) node = node.Next;

            if (node is not null)
            {
                _entries.Remove(node);
                _entries.AddFirst(node);
                entry = node.Value.Value;
                hit = true;
            }
            else
            {
                // Two readers asking at once share one computation rather than making two.
                entry = new Lazy<ConnectivityPartition>(
                    () => DrcConnectivity.Compute(layerRegions, tech), LazyThreadSafetyMode.ExecutionAndPublication);
                _entries.AddFirst((key, entry));
                while (_entries.Count > Capacity) _entries.RemoveLast();
            }
        }

        if (hit && ConnectivityCounters.Active is { } c) Interlocked.Increment(ref c.CacheHits);

        try
        {
            return entry.Value;
        }
        catch
        {
            // A failed computation is not an answer to keep.
            lock (_gate)
            {
                for (var node = _entries.First; node is not null; node = node.Next)
                    if (ReferenceEquals(node.Value.Value, entry)) { _entries.Remove(node); break; }
            }
            throw;
        }
    }

    private static Key KeyOf(IReadOnlyDictionary<LayerKey, Paths64> layerRegions, Technology tech)
    {
        // Two independent 64-bit lanes over every number, in enumeration order.
        ulong h1 = 0x243F6A8885A308D3, h2 = 0x13198A2E03707344;
        long vertices = 0;

        void Mix(long v)
        {
            ulong u = unchecked((ulong)v);
            h1 = BitOperations.RotateLeft((h1 ^ u) * 0x9E3779B97F4A7C15, 29);
            h2 = BitOperations.RotateLeft((h2 + u) * 0xC2B2AE3D27D4EB4F, 31) ^ (h2 >> 17);
        }

        foreach (var (layer, paths) in layerRegions)
        {
            Mix(layer.Layer); Mix(layer.Datatype); Mix(paths.Count);
            foreach (var path in paths)
            {
                Mix(path.Count);
                foreach (var pt in path) { Mix(pt.X); Mix(pt.Y); }
                vertices += path.Count;
            }
        }

        return new Key(h1, h2, vertices, StackupKey(tech));
    }

    /// <summary>Every stackup field <see cref="DrcConnectivity.Compute"/> reads, in order.</summary>
    private static string StackupKey(Technology tech)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var l in tech.Stackup.Layers)
        {
            sb.Append((int)l.Kind).Append('|').Append(l.Name).Append('|')
              .Append(l.IsGroundReference ? '1' : '0').Append('|')
              .Append(l.SpanFromLayer).Append('|').Append(l.SpanToLayer).Append('|');
            foreach (var d in l.DrawingLayers) sb.Append(d.Layer).Append('/').Append(d.Datatype).Append(',');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
