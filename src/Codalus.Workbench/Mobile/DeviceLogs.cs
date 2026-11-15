using System.Globalization;
using System.Text.Json;

namespace Codalus.Workbench.Mobile;

/// <summary>A log entry's level, from logcat's letters (V D I W E F) and the unified log's message types.</summary>
public enum DeviceLogLevel
{
    Verbose,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>One entry of a device's log (ADR 0104): when, how severe, its tag (or subsystem), its process and its message.</summary>
public sealed record DeviceLogEntry(string Time, DeviceLogLevel Level, string Tag, int ProcessId, string Message)
{
    public int ThreadId { get; init; }

    /// <summary>The process's name, when the log says (the unified log does; logcat doesn't).</summary>
    public string? Process { get; init; }
}

/// <summary>
/// The Device Log's sources (ADR 0104): <c>adb logcat -v threadtime</c> for Android devices and emulators, and the
/// unified log of a booted iOS simulator (<c>xcrun simctl spawn &lt;udid&gt; log stream --style ndjson</c>). Parsing
/// only, so tests feed it recorded output.
/// </summary>
public static class DeviceLogs
{
    /// <summary>How many entries the window keeps (the oldest go first).</summary>
    public const int Capacity = 50_000;

    /// <summary>The entries logcat prints before following: the last ones, so the window starts with some context.</summary>
    public const int Backlog = 2_000;

    /// <summary><c>adb -s serial logcat -v threadtime -T 2000</c>.</summary>
    public static IReadOnlyList<string> LogcatArguments(string serial) =>
        ["-s", serial, "logcat", "-v", "threadtime", "-T", Backlog.ToString(CultureInfo.InvariantCulture)];

    /// <summary><c>xcrun simctl spawn udid log stream --style ndjson --level debug</c>.</summary>
    public static IReadOnlyList<string> SimulatorLogArguments(string udid) =>
        ["simctl", "spawn", udid, "log", "stream", "--style", "ndjson", "--level", "debug"];

    /// <summary>
    /// A <c>logcat -v threadtime</c> line (<c>11-15 10:23:45.123  1234  5678 I Tag     : message</c>), or null for
    /// anything else (<c>--------- beginning of main</c>).
    /// </summary>
    public static DeviceLogEntry? ParseLogcat(string line)
    {
        // date, time, pid, tid, level, then "tag: message" (the tag padded, and it can hold spaces and colons).
        var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6 || parts[0].Length != 5 || parts[0][2] != '-' || !parts[1].Contains(':', StringComparison.Ordinal)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var tid)
            || parts[4].Length != 1 || LevelOf(parts[4][0]) is not { } level)
        {
            return null;
        }

        var rest = parts[5];
        var separator = rest.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0 && rest.EndsWith(':'))
        {
            separator = rest.Length - 1;
        }

        var tag = separator >= 0 ? rest[..separator].Trim() : string.Empty;
        var message = separator >= 0 ? rest[Math.Min(rest.Length, separator + 2)..] : rest;
        return new DeviceLogEntry(parts[0] + " " + parts[1], level, tag, pid, message) { ThreadId = tid };
    }

    /// <summary>A logcat level letter, or null.</summary>
    public static DeviceLogLevel? LevelOf(char letter) => letter switch
    {
        'V' => DeviceLogLevel.Verbose,
        'D' => DeviceLogLevel.Debug,
        'I' => DeviceLogLevel.Info,
        'W' => DeviceLogLevel.Warning,
        'E' => DeviceLogLevel.Error,
        'F' or 'A' => DeviceLogLevel.Fatal,
        _ => null,
    };

    /// <summary>
    /// A line of <c>log stream --style ndjson</c>: a JSON object with <c>timestamp</c>, <c>messageType</c>,
    /// <c>subsystem</c>, <c>category</c>, <c>processImagePath</c>, <c>processID</c>, <c>threadID</c> and
    /// <c>eventMessage</c>; null for anything else (the first line, "Filtering the log data…").
    /// </summary>
    public static DeviceLogEntry? ParseUnifiedLog(string line)
    {
        if (!line.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            long Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
            if (Text("eventMessage") is not { } message)
            {
                return null;
            }

            var timestamp = Text("timestamp") ?? string.Empty;
            var time = timestamp.Length >= 23 ? timestamp[5..10] + " " + timestamp[11..23] : timestamp;
            var level = Text("messageType") switch
            {
                "Debug" => DeviceLogLevel.Debug,
                "Info" => DeviceLogLevel.Info,
                "Error" => DeviceLogLevel.Error,
                "Fault" => DeviceLogLevel.Fatal,
                _ => DeviceLogLevel.Info, // Default
            };
            var process = Text("processImagePath") is { Length: > 0 } image ? Path.GetFileName(image) : null;
            var subsystem = Text("subsystem");
            var category = Text("category");
            var tag = subsystem is { Length: > 0 } ? (category is { Length: > 0 } ? subsystem + ":" + category : subsystem) : process ?? string.Empty;
            return new DeviceLogEntry(time, level, tag, (int)Number("processID"), message) { ThreadId = (int)Number("threadID"), Process = process };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The process ids in <c>adb shell pidof package</c>'s output (one or several, space-separated).</summary>
    public static IReadOnlySet<int> ParsePidof(string output) =>
        output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
            .Where(p => p > 0)
            .ToHashSet();

    /// <summary>
    /// Whether <paramref name="entry"/> passes the window's filters: at least <paramref name="minimum"/>, the text found in
    /// its tag or message (any case), and, with <paramref name="appProcesses"/> or <paramref name="appName"/>, from the app.
    /// </summary>
    public static bool Matches(DeviceLogEntry entry, DeviceLogLevel minimum, string? text, IReadOnlySet<int>? appProcesses, string? appName)
    {
        if (entry.Level < minimum)
        {
            return false;
        }

        if (appProcesses is not null && !appProcesses.Contains(entry.ProcessId))
        {
            return false;
        }

        if (appName is not null && !string.Equals(entry.Process, appName, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrEmpty(text)
               || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
               || entry.Tag.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An entry as a line of a saved log (logcat's threadtime layout).</summary>
    public static string Format(DeviceLogEntry entry) =>
        string.Create(CultureInfo.InvariantCulture, $"{entry.Time} {entry.ProcessId,5} {entry.ThreadId,5} {"VDIWEF"[(int)entry.Level]} {entry.Tag}: {entry.Message}");
}
