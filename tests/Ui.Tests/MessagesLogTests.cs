// brief-dfr31-5 R-dfr31-5-4 — the Messages panel's running log: a posted line lands in today's file, with its date,
// and a day's file older than fourteen days is deleted when the application starts.

using System.Globalization;
using CircuitRF.Ui.Messages;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class MessagesLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-messages-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void APostedLine_IsInTodaysFile_AndAFile15DaysOld_IsDeletedOnStart()
    {
        var log = new MessagesLog(_dir);
        var now = new DateTime(2026, 10, 29, 14, 3, 12);
        Directory.CreateDirectory(_dir);
        string stale = log.PathFor(now.AddDays(-15)), kept = log.PathFor(now.AddDays(-14));
        File.WriteAllText(stale, "old\n");
        File.WriteAllText(kept, "recent\n");

        Assert.Equal(1, log.Prune(now));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(kept));

        log.Append(MessageLevel.Warning, "Port P3 sits on no conductor.", "/ws/board.clay", now);

        string today = Path.Combine(_dir, "messages-2026-10-29.log");
        Assert.Equal(today, log.PathFor(now));
        Assert.Equal("2026-10-29 14:03:12  Warning  Port P3 sits on no conductor.  [/ws/board.clay]\n",
                     File.ReadAllText(today));
    }
}
