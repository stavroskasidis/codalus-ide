using System.Diagnostics;
using System.Text.Json;

namespace Daedalus.Diagnostics.BenchEvents;

/// <summary>
/// Writes benchmark events as JSON lines to the file named by <c>DAEDALUS_BENCH_EVENTS</c>.
/// Every line has <c>ev</c> (name), <c>wall_us</c> (Unix epoch microseconds, comparable across processes)
/// and <c>mono_us</c> (process-local monotonic microseconds, for intervals). The format is shared with the
/// Rust prototype and parsed by the benchmark harness; see benchmarks/README.md.
/// </summary>
public static class BenchEvents
{
    public const string EnvironmentVariable = "DAEDALUS_BENCH_EVENTS";

    private static readonly Lock s_gate = new();
    private static readonly Stream? s_stream = Open();
    private static readonly long s_monoOrigin = Stopwatch.GetTimestamp();

    public static bool Enabled => s_stream is not null;

    /// <summary>Microseconds on the process-local monotonic clock.</summary>
    public static long MonotonicMicroseconds() => (long)Stopwatch.GetElapsedTime(s_monoOrigin).TotalMicroseconds;

    /// <summary>Converts a <see cref="Stopwatch.GetTimestamp"/> value to the monotonic microsecond clock.</summary>
    public static long ToMonotonicMicroseconds(long stopwatchTimestamp) =>
        (long)Stopwatch.GetElapsedTime(s_monoOrigin, stopwatchTimestamp).TotalMicroseconds;

    public static void Emit(string name) => Emit(name, default(ReadOnlySpan<(string Key, long Value)>));

    public static void Emit(string name, string key, long value) => Emit(name, [(key, value)]);

    public static void Emit(string name, ReadOnlySpan<(string Key, long Value)> fields)
    {
        if (s_stream is null)
        {
            return;
        }

        var wall = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        var mono = MonotonicMicroseconds();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ev", name);
            writer.WriteNumber("wall_us", wall);
            writer.WriteNumber("mono_us", mono);
            foreach (var (key, value) in fields)
            {
                writer.WriteNumber(key, value);
            }

            writer.WriteEndObject();
        }

        lock (s_gate)
        {
            s_stream.Write(buffer.WrittenSpan);
            s_stream.WriteByte((byte)'\n');
            s_stream.Flush();
        }
    }

    private static FileStream? Open()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }
}

/// <summary>Event names shared by every prototype and the harness.</summary>
public static class BenchEventNames
{
    /// <summary>Entered <c>Main</c>.</summary>
    public const string MainEntered = "main_entered";

    /// <summary>The main window has rendered its first frame.</summary>
    public const string FirstFrame = "first_frame";

    /// <summary>First frame rendered and the UI thread is idle: the window accepts input.</summary>
    public const string Interactive = "interactive";

    /// <summary>The solution's structure (from the solution file) is shown.</summary>
    public const string SolutionStructure = "solution_structure";

    public const string SolutionLoaded = "solution_loaded";

    /// <summary>The language service compiled every project once (first background pass finished).</summary>
    public const string LanguageReady = "language_ready";

    /// <summary>The open document shows semantic highlighting for the visible range.</summary>
    public const string HighlightReady = "highlight_ready";

    /// <summary>One keystroke: <c>latency_us</c> from the OS input timestamp to the frame showing it.</summary>
    public const string KeyLatency = "key_latency";

    /// <summary>One frame during a scroll scenario: <c>frame_us</c> since the previous frame.</summary>
    public const string ScrollFrame = "scroll_frame";

    public const string ScenarioDone = "scenario_done";
}
