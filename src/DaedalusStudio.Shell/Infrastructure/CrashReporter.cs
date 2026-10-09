using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DaedalusStudio.Shell.Infrastructure;

/// <summary>
/// Local, opt-in crash reports (setting <c>diagnostics.crashReports</c>, off by default). A report is a text file in
/// <c>&lt;logs&gt;/crashes</c>: version, OS, runtime, the exception and the tail of the shell's log. Nothing leaves the
/// machine; users attach a report to an issue themselves.
/// </summary>
public static class CrashReporter
{
    public const string SettingKey = "diagnostics.crashReports";

    private static Func<bool> s_enabled = () => false;
    private static Func<string> s_logTail = () => string.Empty;
    private static string? s_folder;
    private static int s_installed;

    public static string? Folder => s_folder;

    /// <summary>Hooks process-wide unhandled exceptions. Safe to call once per process (later calls only update the configuration).</summary>
    public static void Install(string logsFolder, Func<bool> enabled, Func<string> logTail)
    {
        s_folder = Path.Combine(logsFolder, "crashes");
        s_enabled = enabled;
        s_logTail = logTail;
        if (Interlocked.Exchange(ref s_installed, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Write("crash", ex.ToString());
                }
            };
        }
    }

    /// <summary>Writes a report (a crash, a recovered UI error, or a helper process that died); returns its path, or null when disabled.</summary>
    public static string? Write(string kind, string details)
    {
        if (s_folder is not { } folder)
        {
            return null;
        }

        try
        {
            if (!s_enabled())
            {
                return null;
            }

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{kind}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt");
            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"Daedalus Studio {ShellInfo.Version} ({kind})")
                .AppendLine(CultureInfo.InvariantCulture, $"Time (UTC): {DateTime.UtcNow:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
                .AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}")
                .AppendLine()
                .AppendLine(details)
                .AppendLine()
                .AppendLine("--- Recent log ---")
                .AppendLine(s_logTail());
            File.WriteAllText(path, report.ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // never crash while reporting a crash
        }
    }

    /// <summary>Reports written after <paramref name="since"/> (shown at the next start).</summary>
    public static IReadOnlyList<string> ReportsSince(DateTime since)
    {
        if (s_folder is not { } folder || !Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "crash-*.txt").Where(f => File.GetLastWriteTimeUtc(f) > since).Order(StringComparer.Ordinal)];
    }
}
