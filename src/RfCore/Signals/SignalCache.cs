// ================================================================
//  SignalCache.cs  —  generate a modulated signal once, in the background
//
//  Keyed by SignalSpec.CacheKey() (canonical JSON + generator version).
//  In memory: one Task per key, so the Signal Designer's preview and a
//  simulation that needs the same signal share a single generation.
//  On disk: <dir>/<key>.carriers.bin (each carrier's native record,
//  float64 I/Q) + <key>.json (the spec), so a re-opened design or a
//  headless run does not regenerate.  Trimmed LRU to a size cap.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace RfCore.Signals
{
    public static class SignalCache
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task<ModulatedSignal>>> Live = new();

        /// <summary>Cache directory; set by the host (workspace) or defaults to the per-user local app data.</summary>
        public static string Directory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "circuitRF", "signals");

        public static long MaxBytes { get; set; } = 2L << 30;

        /// <summary>Starts (or joins) the generation of <paramref name="spec"/>; never blocks the caller.</summary>
        public static Task<ModulatedSignal> GetAsync(SignalSpec spec, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            // The generation is SHARED by every caller asking for this key (a Designer preview and a
            // simulation can wait on the same signal), so it never takes a caller's token: one caller
            // cancelling would cancel it for all of them. Each caller stops WAITING on its own token instead.
            string key = spec.CacheKey();
            var lazy = Live.GetOrAdd(key, _ => new Lazy<Task<ModulatedSignal>>(
                () => Task.Run(() => LoadOrGenerate(spec, key, progress, CancellationToken.None))));
            var t = lazy.Value;
            if (t.IsFaulted || t.IsCanceled) { Live.TryRemove(key, out _); return GetAsync(spec, progress, ct); }
            return ct.CanBeCanceled ? t.WaitAsync(ct) : t;
        }

        public static ModulatedSignal Get(SignalSpec spec) => GetAsync(spec).GetAwaiter().GetResult();

        /// <summary>True when <paramref name="spec"/> is already in memory (generated) or on disk — a request
        /// for it returns at once, so a caller has no long-running work to report.</summary>
        public static bool IsCached(SignalSpec spec)
        {
            string key = spec.CacheKey();
            if (Live.TryGetValue(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) return true;
            return File.Exists(Path.Combine(Directory, key + ".carriers.bin"));
        }

        private static ModulatedSignal LoadOrGenerate(SignalSpec spec, string key, IProgress<double>? progress, CancellationToken ct)
        {
            string bin = Path.Combine(Directory, key + ".carriers.bin");
            if (File.Exists(bin))
            {
                try
                {
                    var s = Read(spec, bin);
                    File.SetLastAccessTimeUtc(bin, DateTime.UtcNow);
                    progress?.Report(1.0);
                    return s;
                }
                catch (Exception) { /* corrupt or stale: regenerate below */ }
            }
            var sig = SignalComposer.Generate(spec, progress, ct);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                Write(sig, bin);
                File.WriteAllText(Path.Combine(Directory, key + ".json"), spec.ToJson());
                Trim();
            }
            catch (IOException) { /* a read-only cache never fails a run */ }
            catch (UnauthorizedAccessException) { }
            return sig;
        }

        private static void Write(ModulatedSignal s, string path)
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(s.RecordSeconds);
            bw.Write(s.Carriers.Count);
            foreach (var c in s.Carriers)
            {
                bw.Write(c.NativeRateHz); bw.Write(c.OccupiedBwHz); bw.Write(c.OffsetHz); bw.Write(c.RelativePower);
                bw.Write(c.Native.Length);
                foreach (var v in c.Native) { bw.Write(v.Real); bw.Write(v.Imaginary); }
            }
        }

        private static ModulatedSignal Read(SignalSpec spec, string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));
            double rec = br.ReadDouble();
            int nc = br.ReadInt32();
            if (nc != spec.Carriers.Count) throw new InvalidDataException("carrier count mismatch");
            var list = new List<CarrierRecord>();
            for (int i = 0; i < nc; i++)
            {
                double rate = br.ReadDouble(), occ = br.ReadDouble(), off = br.ReadDouble(), rel = br.ReadDouble();
                int n = br.ReadInt32();
                var x = new Complex[n];
                for (int k = 0; k < n; k++) x[k] = new Complex(br.ReadDouble(), br.ReadDouble());
                list.Add(new CarrierRecord { Spec = spec.Carriers[i], Native = x, NativeRateHz = rate, OccupiedBwHz = occ, OffsetHz = off, RelativePower = rel });
            }
            return new ModulatedSignal(spec, list, rec);
        }

        private static void Trim()
        {
            var di = new DirectoryInfo(Directory);
            var files = di.GetFiles("*.carriers.bin").OrderByDescending(f => f.LastAccessTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > MaxBytes)
                {
                    try { f.Delete(); File.Delete(Path.ChangeExtension(Path.ChangeExtension(f.FullName, null), ".json")); }
                    catch (IOException) { }
                }
            }
        }

        /// <summary>Drops the in-memory entries (tests; a workspace switch).</summary>
        public static void ClearMemory() => Live.Clear();
    }
}
