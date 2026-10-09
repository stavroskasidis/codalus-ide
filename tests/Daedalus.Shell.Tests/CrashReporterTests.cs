using Avalonia.Headless.XUnit;
using Daedalus.Shell.Infrastructure;

namespace Daedalus.Shell.Tests;

/// <summary>Local, opt-in crash reports (ADR 0009). Runs on the UI thread like the shell tests, which also configure the reporter.</summary>
public sealed class CrashReporterTests
{
    [AvaloniaFact]
    public async Task Reports_are_written_only_when_enabled_and_shown_at_the_next_start()
    {
        await using var f = ShellFixture.Create();
        var crashes = Path.Combine(f.Shell.Paths.Logs, "crashes");
        Assert.Equal(crashes, CrashReporter.Folder);

        // Off by default: nothing is written.
        Assert.Null(CrashReporter.Write("crash", "System.Exception: off"));
        Assert.False(Directory.Exists(crashes) && Directory.EnumerateFiles(crashes, "crash-*").Any());

        await f.Shell.Workbench.Settings.SetAsync(CrashReporter.SettingKey, true, Daedalus.Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        f.Shell.Workbench.Log.AppendLine("a log line before the crash");
        var path = CrashReporter.Write("crash", "System.InvalidOperationException: boom");
        Assert.NotNull(path);
        var report = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("Daedalus Studio", report, StringComparison.Ordinal);
        Assert.Contains("boom", report, StringComparison.Ordinal);
        Assert.Contains("a log line before the crash", report, StringComparison.Ordinal);
        Assert.Single(CrashReporter.ReportsSince(DateTime.UtcNow.AddMinutes(-1)));

        // The next start (same user data) says a report was saved, and marks the reports as seen.
        await using var next = ShellFixture.Create(f.UserData);
        Assert.Contains(next.Shell.Workbench.Notifications.Items, n => n.Message.Contains("crashed last time", StringComparison.Ordinal));
        Assert.Empty(CrashReporter.ReportsSince(File.GetLastWriteTimeUtc(Path.Combine(crashes, ".seen"))));
    }
}
