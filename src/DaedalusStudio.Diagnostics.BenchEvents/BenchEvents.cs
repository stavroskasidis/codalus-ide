using System.Diagnostics;
using System.Text.Json;

namespace DaedalusStudio.Diagnostics.BenchEvents;

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

    public static void Emit(string name, ReadOnlySpan<(string Key, long Value)> fields) => Emit(name, fields, []);

    /// <summary>An event with numeric <paramref name="fields"/> and string <paramref name="text"/> fields (the harness keeps both).</summary>
    public static void Emit(string name, ReadOnlySpan<(string Key, long Value)> fields, ReadOnlySpan<(string Key, string Value)> text)
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

            foreach (var (key, value) in text)
            {
                writer.WriteString(key, value);
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

    /// <summary>
    /// Every project's compiler inputs are known: design-time builds (or their cache) finished once (M34b). Fields
    /// <c>cache_hits</c> and <c>cache_misses</c>, and <c>cache_miss_reasons</c> (text, <c>Name: reason</c> joined by
    /// <c>"; "</c>) when some missed (M34c).
    /// </summary>
    public const string DesignTimeDone = "designtime_done";

    /// <summary>The language service compiled every project once (first background pass finished).</summary>
    public const string LanguageReady = "language_ready";

    /// <summary>The open document shows semantic highlighting for the visible range.</summary>
    public const string HighlightReady = "highlight_ready";

    /// <summary>One keystroke: <c>latency_us</c> from the OS input timestamp to the frame showing it.</summary>
    public const string KeyLatency = "key_latency";

    /// <summary>One frame during a scroll scenario: <c>frame_us</c> since the previous frame.</summary>
    public const string ScrollFrame = "scroll_frame";

    public const string ScenarioDone = "scenario_done";

    /// <summary>The Test Explorer's tree changed: <c>tests</c> is the number of tests in it.</summary>
    public const string TestTree = "test_tree";

    /// <summary>One filter applied in the Test Explorer: <c>frame_us</c> until the next frame was rendered.</summary>
    public const string TestFilterFrame = "test_filter_frame";

    /// <summary>The first result of a test run: <c>elapsed_us</c> since the run was requested.</summary>
    public const string TestFirstResult = "test_first_result";

    /// <summary>
    /// A NuGet search page: <c>frame_us</c> from the host's response to the rendered list, <c>elapsed_us</c> from the
    /// request (M9).
    /// </summary>
    public const string PackageSearch = "package_search";

    /// <summary>
    /// A package update in the package manager: <c>elapsed_us</c> from Update to the project's new compiler inputs (what
    /// IntelliSense sees), <c>restore_us</c> of it spent in restore (M9).
    /// </summary>
    public const string PackageInstall = "package_install";

    /// <summary>Project properties opened: <c>elapsed_us</c> from the command to the first page rendered with its values (M9).</summary>
    public const string PropertiesOpen = "properties_open";

    /// <summary>A frame while a terminal prints a large file: <c>frame_us</c> since the previous frame (M10).</summary>
    public const string TerminalFrame = "terminal_frame";

    /// <summary>
    /// A terminal printed a file: <c>elapsed_us</c> until the program exited and its output was parsed, <c>lines</c> printed,
    /// <c>memory_bytes</c> of managed memory the terminal holds afterwards (screen and scrollback) (M10).
    /// </summary>
    public const string TerminalCat = "terminal_cat";

    /// <summary>A keystroke in an interactive shell: <c>latency_us</c> from writing it to the echo on screen, rendered (M10).</summary>
    public const string TerminalEcho = "terminal_echo";
}
