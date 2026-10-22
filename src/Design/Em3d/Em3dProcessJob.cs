// brief-emi-10 R-emi10-1/2 — on Windows, the Job Object a solver's process tree runs in.
//
// It does three things the macOS and Linux readers do by other means: it IS the tree (every process the solver starts joins
// the job), it gives each member's commit charge (PrivateUsage, which counts what has been paged out) and working set, and it
// holds the ceiling ITSELF (JOB_OBJECT_LIMIT_JOB_MEMORY): the kernel refuses the allocation that would cross it and posts
// JOB_OBJECT_MSG_JOB_MEMORY_LIMIT to the job's completion port, which the loop reads every tick — so a burst between two
// polls cannot overshoot. KILL_ON_JOB_CLOSE stops anything of the tree still running when the run lets go of the job.
//
// The solver is assigned to the job just after it starts (Process.Start cannot start one suspended). A child it starts in
// that instant would escape the job; an interpreter or MPI launcher takes far longer than that to start its first child.
// These are calls into kernel32, the operating system's own library — nothing is shipped (owner decision O1).

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CircuitRF.Design.Em3d;

internal sealed class Em3dProcessJob : IDisposable
{
    private IntPtr _job, _port;
    private long? _limit;

    private Em3dProcessJob(IntPtr job, IntPtr port) { _job = job; _port = port; }

    /// <summary>A job holding <paramref name="p"/>, limited to <paramref name="ceilingBytes"/> of commit (null: none). Null off
    /// Windows, or when the job could not be made or the process not assigned to it.</summary>
    public static Em3dProcessJob? Attach(Process p, long? ceilingBytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return null;
            var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            var made = new Em3dProcessJob(job, port);
            if (port != IntPtr.Zero)
            {
                var assoc = new AssociateCompletionPort { CompletionKey = job, CompletionPort = port };
                SetInformationJobObject(job, JobObjectAssociateCompletionPortInformation, ref assoc, Marshal.SizeOf<AssociateCompletionPort>());
            }
            if (!made.SetLimit(ceilingBytes) || !AssignProcessToJobObject(job, p.Handle))
            {
                made.Dispose();
                return null;
            }
            return made;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>Sets the job's commit limit; null lifts it (a run forced past the size check).</summary>
    public bool SetLimit(long? bytes)
    {
        var info = new ExtendedLimitInformation();
        info.Basic.LimitFlags = KillOnJobClose | (bytes is null ? 0u : JobMemory);
        if (bytes is { } b) info.JobMemoryLimit = (UIntPtr)(ulong)b;
        bool ok = SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>());
        if (ok) _limit = bytes;
        return ok;
    }

    /// <summary>The ceiling the job holds now.</summary>
    public long? Limit => _limit;

    /// <summary>True once the kernel has refused an allocation at the limit.</summary>
    public bool LimitReached()
    {
        if (_port == IntPtr.Zero) return false;
        while (GetQueuedCompletionStatus(_port, out uint message, out _, out _, 0))
            if (message == MsgJobMemoryLimit) return true;
        return false;
    }

    /// <summary>The job's highest commit, bytes.</summary>
    public long Peak
    {
        get
        {
            var info = new ExtendedLimitInformation();
            return QueryInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>(), out _)
                ? (long)(ulong)info.PeakJobMemoryUsed : 0;
        }
    }

    /// <summary>Every member's working set and commit charge, summed.</summary>
    public TreeMemory? Read()
    {
        var buffer = new byte[8 + 1024 * IntPtr.Size];
        if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, buffer.Length, out _)) return null;
        int n = Math.Min(BitConverter.ToInt32(buffer, 4), 1024);
        long resident = 0, footprint = 0;
        for (int k = 0; k < n; k++)
        {
            long pid = IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, 8 + 8 * k) : BitConverter.ToInt32(buffer, 8 + 4 * k);
            var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                var c = new MemoryCountersEx { cb = (uint)Marshal.SizeOf<MemoryCountersEx>() };
                if (!K32GetProcessMemoryInfo(h, ref c, c.cb)) continue;
                resident += (long)(ulong)c.WorkingSetSize;
                footprint += (long)(ulong)c.PrivateUsage;
            }
            finally { CloseHandle(h); }
        }
        return new(resident, footprint);
    }

    public void Dispose()
    {
        if (_port != IntPtr.Zero) { CloseHandle(_port); _port = IntPtr.Zero; }
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    // ── kernel32 ─────────────────────────────────────────────────────────────────────────────

    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectAssociateCompletionPortInformation = 7;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobMemory = 0x200;
    private const uint KillOnJobClose = 0x2000;
    private const uint MsgJobMemoryLimit = 10;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AssociateCompletionPort
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCountersEx
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                       QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

#pragma warning disable SYSLIB1054   // DllImport, as Em3dMemory.cs says why
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref AssociateCompletionPort info, int length);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length, out int returned);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, [Out] byte[] info, int length, out int returned);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(IntPtr fileHandle, IntPtr existingPort, UIntPtr key, uint threads);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint milliseconds);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref MemoryCountersEx counters, uint cb);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
