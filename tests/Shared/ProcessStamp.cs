using System.Globalization;

namespace Codalus.Tests;

/// <summary>
/// When a process started, as a stamp that two processes read alike. With its id, a stamp tells a program a test
/// started from another process that took the id once the program had gone.
/// <para>
/// On Linux the stamp is the start in clock ticks since boot (<c>/proc/&lt;pid&gt;/stat</c>), which no clock adjustment
/// moves. .NET's start time there is computed from the wall clock, and WSL adjusts that clock: the scripted adapter and
/// DebugStartAgainTests read one program's start 3.24 s apart, so the test took it for another process and left it
/// running after the run (WSL, about 4 runs of the class in 5). Elsewhere the stamp is the start time (UTC ticks),
/// matched within a second.
/// </para>
/// </summary>
internal static class ProcessStamp
{
    private const string Boot = "boot:";

    public static string Of(System.Diagnostics.Process process) =>
        OperatingSystem.IsLinux() && BootTicks(process.Id) is { } ticks
            ? Boot + ticks
            : process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether <paramref name="process"/> is the one <paramref name="stamp"/> was taken of. One whose start can't be read
    /// counts as the same.
    /// </summary>
    public static bool Matches(System.Diagnostics.Process process, string stamp)
    {
        try
        {
            if (stamp.StartsWith(Boot, StringComparison.Ordinal))
            {
                return BootTicks(process.Id) is not { } now || now == stamp[Boot.Length..];
            }

            return !long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                || (process.StartTime.ToUniversalTime() - new DateTime(ticks, DateTimeKind.Utc)).Duration() <= TimeSpan.FromSeconds(1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>The 22nd field of <c>/proc/&lt;pid&gt;/stat</c>: the start in clock ticks since boot; null when it can't be read.</summary>
    private static string? BootTicks(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
            // After the command's closing parenthesis (the command may hold spaces) come fields 3 on.
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 19 ? fields[19] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
