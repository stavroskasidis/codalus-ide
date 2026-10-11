using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Workbench.Diagnostics;

/// <summary>The tools a profiling session ran (ADR 0045).</summary>
public static class ProfilerTools
{
    public const string Cpu = "cpu";
    public const string Counters = "counters";
}

/// <summary>
/// A saved profiling session (the Performance Profiler hub, ADR 0045): a folder under the solution's
/// <c>.daedalus/profiler/</c> with <c>session.json</c> (this), the CPU trace (<c>cpu.nettrace</c>) and the counters
/// (<c>counters.json</c>, <see cref="CounterExport"/>'s format). <see cref="Folder"/> is where it was read from.
/// </summary>
public sealed record ProfilerSession(
    string Name,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    string Target,
    string? Project,
    string ProcessName,
    int ProcessId,
    IReadOnlyList<string> Tools,
    string? Trace,
    string? Counters)
{
    public const string FileName = "session.json";
    public const string LaunchTarget = "launch";
    public const string ProcessTarget = "process";

    [JsonIgnore]
    public string Folder { get; init; } = string.Empty;

    [JsonIgnore]
    public string? TracePath => Trace is null ? null : Path.Combine(Folder, Trace);

    [JsonIgnore]
    public string? CountersPath => Counters is null ? null : Path.Combine(Folder, Counters);

    [JsonIgnore]
    public string Description =>
        string.Create(CultureInfo.CurrentCulture, $"{StartedAt.LocalDateTime:g} · {DurationSeconds:0.#} s · {string.Join(", ", Tools.Select(t => t == ProfilerTools.Cpu ? "CPU Usage" : ".NET Counters"))}");
}

/// <summary>Where profiling sessions are kept and how they are listed, saved and deleted.</summary>
public static class ProfilerSessionStore
{
    /// <summary>The solution's <c>.daedalus/profiler</c>, or the state folder's <c>profiler</c> without a solution.</summary>
    public static string Root(string? solutionFolder, string stateFolder) =>
        solutionFolder is null ? Path.Combine(stateFolder, "profiler") : Path.Combine(solutionFolder, ".daedalus", "profiler");

    /// <summary>A new, empty folder for a session of <paramref name="target"/> started at <paramref name="at"/>.</summary>
    public static string CreateFolder(string root, string target, DateTimeOffset at)
    {
        var name = at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Sanitize(target);
        var folder = Path.Combine(root, name);
        for (var i = 2; Directory.Exists(folder); i++)
        {
            folder = Path.Combine(root, name + "-" + i.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A folder name that is valid on every OS (the solution may be shared).</summary>
    private static string Sanitize(string text)
    {
        char[] invalid = [.. Path.GetInvalidFileNameChars(), ':', '*', '?', '"', '<', '>', '|', '\\', '/'];
        var clean = new string([.. text.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c)]).Trim('-', '.');
        return clean.Length == 0 ? "session" : clean.Length > 40 ? clean[..40] : clean;
    }

    public static void Save(ProfilerSession session)
    {
        Directory.CreateDirectory(session.Folder);
        var path = Path.Combine(session.Folder, ProfilerSession.FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(session, ProfilerJsonContext.Default.ProfilerSession));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads a session from its folder or its <c>session.json</c>; null when it isn't one.</summary>
    public static ProfilerSession? Load(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, ProfilerSession.FileName) : path;
        try
        {
            return File.Exists(file) && JsonSerializer.Deserialize(File.ReadAllText(file), ProfilerJsonContext.Default.ProfilerSession) is { } session
                ? session with { Folder = Path.GetDirectoryName(Path.GetFullPath(file))! }
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The sessions under <paramref name="root"/>, the newest first.</summary>
    public static IReadOnlyList<ProfilerSession> List(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(root).Select(Load).OfType<ProfilerSession>().OrderByDescending(s => s.StartedAt)];
    }

    public static void Delete(ProfilerSession session)
    {
        if (Directory.Exists(session.Folder) && File.Exists(Path.Combine(session.Folder, ProfilerSession.FileName)))
        {
            Directory.Delete(session.Folder, recursive: true);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(ProfilerSession))]
internal sealed partial class ProfilerJsonContext : JsonSerializerContext;
