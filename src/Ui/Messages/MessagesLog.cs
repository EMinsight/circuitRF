using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// brief-dfr31-5 R-dfr31-5-4 — the Messages panel's running log: every line posted, with its date, appended to
/// <c>&lt;state dir&gt;/logs/messages-YYYY-MM-DD.log</c> (the directory <c>crash-reports</c> sits in), so a day's work
/// outlives the session and can be sent with a report. Append-only and written through per line, so a crash loses
/// nothing already said; a file older than <see cref="KeepDays"/> days is deleted when the application starts.
///
/// <para><b>Nothing here may throw</b>, on <see cref="Diagnostics.CrashReporter"/>'s terms: no disk, a read-only state
/// directory or a full volume means no log, never a failed post.</para>
/// </summary>
public sealed class MessagesLog(string dir)
{
    public const string DirName = "logs";

    /// <summary>A file this many days old is kept; one a day older is deleted on start.</summary>
    public const int KeepDays = 14;

    private const string Prefix = "messages-";
    private const string DateFormat = "yyyy-MM-dd";

    private readonly Lock _gate = new();

    public string Dir { get; } = dir;

    /// <summary>This process's log, from <see cref="Start"/>; null in a test or a host that never started one.</summary>
    public static MessagesLog? Current { get; private set; }

    /// <summary>Starts the log for this process and deletes the old files. Idempotent.</summary>
    public static void Start()
    {
        if (Current is not null) return;
        try
        {
            var log = new MessagesLog(AppDataRoot.SubDir(DirName));
            log.Prune(DateTime.Now);
            Current = log;
        }
        catch (Exception) { /* no state directory: no log */ }
    }

    /// <summary>The day's file.</summary>
    public string PathFor(DateTime day) =>
        Path.Combine(Dir, Prefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log");

    /// <summary>Appends one message to the file for <paramref name="at"/>'s day.</summary>
    public void Append(MessageLevel level, string text, string? filePath, DateTime at)
    {
        var line = new StringBuilder()
            .Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append("  ").Append(level.ToString().PadRight(7))
            .Append("  ").Append(text.ReplaceLineEndings("\n                               "));
        if (!string.IsNullOrEmpty(filePath)) line.Append("  [").Append(filePath).Append(']');
        line.Append('\n');

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                // Opened per line and shared: circuitRF, harmonicaRF and wBond write into the same directory.
                using var stream = new FileStream(PathFor(at), FileMode.Append, FileAccess.Write,
                                                  FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line.ToString());
                stream.Write(bytes);
            }
            catch (Exception) { /* see the class summary */ }
        }
    }

    /// <summary>Deletes every day's file more than <see cref="KeepDays"/> days before <paramref name="today"/>. Returns how
    /// many went. Judged by the date in the NAME, which is the day its lines were written.</summary>
    public int Prune(DateTime today)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(Dir)) return 0;
            var oldest = today.Date.AddDays(-KeepDays);
            foreach (var path in Directory.EnumerateFiles(Dir, Prefix + "*.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(path)[Prefix.Length..];
                if (!DateTime.TryParseExact(stem, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    || day >= oldest) continue;
                try { File.Delete(path); deleted++; }
                catch (Exception) { /* open elsewhere: next start */ }
            }
        }
        catch (Exception) { /* see the class summary */ }
        return deleted;
    }
}
