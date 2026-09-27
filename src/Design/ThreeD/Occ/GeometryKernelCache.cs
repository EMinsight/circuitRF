// brief-em3d-63 R-em3d63-6 — the cache: an unchanged tree makes no call.
//
// KEY (R-em3d63-6a): SHA-256 over the request, its own parameters, the canonical tree, and the kernel's
// identity (protocol + OCCT + worker version) — so a different kernel never serves another's result.
//
// TWO LEVELS (R-em3d63-6b). In memory, per process, bounded by bytes and least-recently-used: undo, redo,
// reopening an editor and re-elaborating after an unrelated edit all land here. On disk, under the user's
// state directory, bounded and oldest-access-first, holding B-reps and tessellations only: it makes
// reopening a document with a large part instant after a restart. Every disk file is a reply FRAME, written
// to a temporary name and renamed — a killed process never leaves a half-file a later run would trust — and
// one that does not decode is deleted and rebuilt.
//
// A FAILING TREE IS CACHED AS FAILING (R-em3d63-6c), for the process's lifetime: without it, every
// re-elaboration after a crash would crash the worker again, in a loop the user sees as a stutter.

using System.Collections.Concurrent;

namespace CircuitRF.Design.ThreeD.Occ;

internal sealed class GeometryKernelCache(long memoryBudget, long diskBudget, Func<string?> directory)
{
    private sealed record Entry(string Key, object Value, long Bytes);

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private long _memoryBytes;
    private readonly ConcurrentDictionary<string, GeometryKernelException> _failed = new(StringComparer.Ordinal);

    public long MemoryBytes { get { lock (_gate) return _memoryBytes; } }

    public bool TryMemory<T>(string key, out T value)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node) && node.Value.Value is T v)
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                value = v;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void PutMemory(string key, object value, long bytes)
    {
        lock (_gate)
        {
            if (_index.Remove(key, out var old))
            {
                _lru.Remove(old);
                _memoryBytes -= old.Value.Bytes;
            }
            _index[key] = _lru.AddLast(new Entry(key, value, bytes));
            _memoryBytes += bytes;
            while (_memoryBytes > memoryBudget && _lru.First is { } first && !ReferenceEquals(first.Value.Value, value))
            {
                _lru.RemoveFirst();
                _index.Remove(first.Value.Key);
                _memoryBytes -= first.Value.Bytes;
            }
        }
    }

    public void ClearMemory()
    {
        lock (_gate)
        {
            _lru.Clear();
            _index.Clear();
            _memoryBytes = 0;
        }
    }

    public GeometryKernelException? Failed(string key) => _failed.TryGetValue(key, out var e) ? e : null;

    public void RecordFailure(string key, GeometryKernelException e) => _failed[key] = e;

    public void ForgetFailures() => _failed.Clear();

    // ── disk ─────────────────────────────────────────────────────────────────────────────────────

    private string? Dir => directory();

    private string? PathOf(string key, string kind) => Dir is { } d ? Path.Combine(d, $"{key}.{kind}") : null;

    /// <summary>The cached reply for <paramref name="key"/>, or null. A file that does not decode is deleted.</summary>
    public GeometryKernelMessage? ReadDisk(string key, string kind)
    {
        if (PathOf(key, kind) is not { } path || !File.Exists(path)) return null;
        try
        {
            var m = GeometryKernelFrame.Decode(File.ReadAllBytes(path));
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return m;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>Writes a reply for <paramref name="key"/>: to a temporary name, then renamed into place. Never throws —
    /// a cache that cannot be written is a cache that misses.</summary>
    public void WriteDisk(string key, string kind, GeometryKernelMessage reply)
    {
        if (PathOf(key, kind) is not { } path) return;
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, GeometryKernelFrame.Encode(reply));
            File.Move(tmp, path, overwrite: true);
            Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public long DiskBytes()
    {
        if (Dir is not { } d || !Directory.Exists(d)) return 0;
        try { return new DirectoryInfo(d).EnumerateFiles().Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public void ClearDisk()
    {
        if (Dir is not { } d || !Directory.Exists(d)) return;
        foreach (var f in new DirectoryInfo(d).EnumerateFiles())
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Bounds the directory: oldest access first, down to 90 % of the budget. A stray temporary file
    /// older than an hour is a killed writer's, and goes too.</summary>
    private void Trim()
    {
        if (Dir is not { } d) return;
        var files = new DirectoryInfo(d).EnumerateFiles().ToList();
        foreach (var f in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.Ordinal) && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        long total = files.Where(f => f.Exists).Sum(f => f.Length);
        if (total <= diskBudget) return;
        foreach (var f in files.Where(f => f.Exists && !f.Name.EndsWith(".tmp", StringComparison.Ordinal))
                               .OrderBy(f => f.LastAccessTimeUtc).ThenBy(f => f.LastWriteTimeUtc))
        {
            if (total <= diskBudget * 9 / 10) break;
            long len = f.Length;
            try { f.Delete(); total -= len; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
