using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit.v3;

[assembly: Codalus.Tests.TestWatchdog]

namespace Codalus.Tests;

/// <summary>
/// Reports a hang while the test process still runs: once a test has run for <c>CODALUS_TEST_WATCHDOG_MINUTES</c>
/// (default 10; 0 turns it off), or no test has started or finished for that long (a fixture's constructor or
/// disposal), it writes the test's name, the process tree and every process's managed stacks (<c>dotnet-stack</c>,
/// when installed) to the standard error. It only reports: HangDump ends the run later (<c>eng/ci-test.sh</c>).
/// CI's macOS Shell shards used to hang silently until the job's timeout, with nothing to say which test.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TestWatchdogAttribute : BeforeAfterTestAttribute
{
    // xUnit makes the assembly's attributes before the first test class (only in the process that runs the tests, not
    // HangDump's controller): a hang in the first constructor or fixture is reported too.
    public TestWatchdogAttribute() => TestWatchdog.Start();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => TestWatchdog.Started(test.UniqueID, test.TestDisplayName);

    public override void After(MethodInfo methodUnderTest, IXunitTest test) => TestWatchdog.Finished(test.UniqueID, test.TestDisplayName);
}

internal static class TestWatchdog
{
    public const string Variable = "CODALUS_TEST_WATCHDOG_MINUTES";

    private static readonly TimeSpan s_limit = ReadLimit();
    private static readonly ConcurrentDictionary<string, (string Name, long Started)> s_running = new();
    private static readonly Lock s_gate = new();
    private static readonly Lock s_writing = new();
    private static Thread? s_thread;
    private static long s_lastActivity = Stopwatch.GetTimestamp();
    private static string? s_lastFinished;

    public static void Start()
    {
        lock (s_gate)
        {
            if (s_thread is null && s_limit > TimeSpan.Zero)
            {
                // A thread of its own, not the thread pool: a hang that starves the pool must not silence the watchdog.
                Volatile.Write(ref s_lastActivity, Stopwatch.GetTimestamp());
                s_thread = new Thread(Watch) { IsBackground = true, Name = "Test watchdog" };
                s_thread.Start();
            }
        }
    }

    public static void Started(string id, string name)
    {
        Start();
        s_running[id] = (name, Stopwatch.GetTimestamp());
        Volatile.Write(ref s_lastActivity, Stopwatch.GetTimestamp());
    }

    public static void Finished(string id, string name)
    {
        s_running.TryRemove(id, out _);
        Volatile.Write(ref s_lastFinished, name);
        Volatile.Write(ref s_lastActivity, Stopwatch.GetTimestamp());
    }

    private static TimeSpan ReadLimit()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ? TimeSpan.FromMinutes(minutes) : TimeSpan.FromMinutes(10);
    }

    private static void Watch()
    {
        var poll = TimeSpan.FromTicks(Math.Clamp(s_limit.Ticks / 10, TimeSpan.TicksPerSecond, 15 * TimeSpan.TicksPerSecond));
        string? reported = null;
        while (true)
        {
            Thread.Sleep(poll);
            var now = Stopwatch.GetTimestamp();
            var running = s_running.ToArray().OrderBy(t => t.Value.Started).ToList();
            string key;
            string subject;
            TimeSpan stuck;
            if (running.Count > 0)
            {
                var (id, (name, started)) = running[0];
                stuck = Stopwatch.GetElapsedTime(started, now);
                key = id;
                subject = $"{name} has been running for {Format(stuck)}";
            }
            else
            {
                var last = Volatile.Read(ref s_lastActivity);
                stuck = Stopwatch.GetElapsedTime(last, now);
                key = "idle " + last.ToString(CultureInfo.InvariantCulture);
                subject = $"No test has started or finished for {Format(stuck)} (the last to finish: {Volatile.Read(ref s_lastFinished) ?? "none"}): a test class's constructor or Dispose, a fixture, or the runner";
            }

            if (stuck >= s_limit && key != reported)
            {
                reported = key;
                try
                {
                    Report(subject, running.Select(t => $"[{Format(Stopwatch.GetElapsedTime(t.Value.Started, now))}] {t.Value.Name}").ToList());
                }
#pragma warning disable CA1031 // The watchdog must never be what fails or ends the test run.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Write($"Test watchdog: the report on {subject} failed: {ex}{Environment.NewLine}");
                }
            }
        }
    }

    private static void Report(string subject, IReadOnlyList<string> running)
    {
        var actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        var text = new StringBuilder();
        text.AppendLine();
        if (actions)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"::warning title=Test watchdog::{subject}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"===== Test watchdog ({Variable}={s_limit.TotalMinutes.ToString(CultureInfo.InvariantCulture)}): {subject} =====");
        text.AppendLine(CultureInfo.InvariantCulture, $"Process {Environment.ProcessId}, at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z. Tests running:");
        foreach (var line in running.DefaultIfEmpty("(none)"))
        {
            text.AppendLine("  " + line);
        }

        var processes = ProcessTree(out var table);
        text.AppendLine(OperatingSystem.IsMacOS() ? "Processes (pid, ppid, elapsed, command):" : "Processes (pid, ppid, elapsed, %cpu, command):");
        text.AppendLine(table);
        Write(text.ToString());

        var tool = FindTool("dotnet-stack");
        foreach (var (pid, command) in processes)
        {
            text.Clear();
            if (actions)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"::group::Managed stacks of {pid} {Shorten(command)}");
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"----- Managed stacks of {pid} {Shorten(command)} -----");
            text.AppendLine(tool is null
                ? "dotnet-stack isn't installed (dotnet tool install -g dotnet-stack), so there are no stacks."
                : Run(tool, ["report", "-p", pid.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(60)));
            if (actions)
            {
                text.AppendLine("::endgroup::");
            }

            Write(text.ToString());
        }

        Write($"===== Test watchdog: end of the report on {subject} ====={Environment.NewLine}");
    }

    /// <summary>This process and the processes it started (<c>ps</c>; this process alone on Windows), with a table of them.</summary>
    private static List<(int Pid, string Command)> ProcessTree(out string table)
    {
        var self = Environment.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            table = "  (the processes this one started aren't listed on Windows)";
            return [(self, Environment.CommandLine)];
        }

        var rows = new List<(int Pid, int Parent, string Line, string Command)>();
        // No %cpu on macOS: ps reads each process's task information for it, which blocks for minutes while ReportCrash
        // holds a crashed process's (the hangs this reports came with one).
        var columns = OperatingSystem.IsMacOS() ? "pid=,ppid=,etime=,args=" : "pid=,ppid=,etime=,pcpu=,args=";
        var count = columns.Split(',').Length;
        foreach (var line in Run("ps", ["-A", "-o", columns], TimeSpan.FromSeconds(20)).Split('\n'))
        {
            var fields = line.Split(' ', count, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // The ps listing itself, a child of this process, isn't one of the tests' processes.
            if (fields.Length == count && !fields[^1].StartsWith("ps -A -o ", StringComparison.Ordinal) && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var pid) && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent))
            {
                rows.Add((pid, parent, line.TrimEnd(), fields[^1]));
            }
        }

        var tree = new List<(int Pid, string Command)>();
        var lines = new StringBuilder();
        var queue = new Queue<int>([self]);
        while (queue.TryDequeue(out var pid) && tree.Count < 32)
        {
            if (rows.FirstOrDefault(r => r.Pid == pid) is { Line: not null } row)
            {
                tree.Add((pid, row.Command));
                lines.AppendLine("  " + Shorten(row.Line, 300));
            }
            else if (pid == self)
            {
                tree.Add((pid, Environment.CommandLine));
            }

            foreach (var child in rows.Where(r => r.Parent == pid))
            {
                queue.Enqueue(child.Pid);
            }
        }

        table = lines.ToString().TrimEnd();
        return tree;
    }

    private static string? FindTool(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"));
        return folders.Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);
    }

    /// <summary>A program's output and errors, read on threads of their own (the thread pool may be what hangs).</summary>
    private static string Run(string program, IReadOnlyList<string> arguments, TimeSpan limit)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            string output = "", error = "";
            var readers = new[]
            {
                new Thread(() => output = process.StandardOutput.ReadToEnd()) { IsBackground = true },
                new Thread(() => error = process.StandardError.ReadToEnd()) { IsBackground = true },
            };
            foreach (var reader in readers)
            {
                reader.Start();
            }

            if (!process.WaitForExit(limit))
            {
                process.Kill(); // ps and dotnet-stack start nothing, and .NET's tree walk can block on macOS (ProcessTree.Kill)
                return $"{program} didn't finish within {limit.TotalSeconds} seconds.";
            }

            foreach (var reader in readers)
            {
                reader.Join(TimeSpan.FromSeconds(5));
            }

            return (output + error).TrimEnd();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"{program} didn't run: {ex.Message}";
        }
    }

    /// <summary>To the process's standard error, not <see cref="Console.Error"/>, which a test may have replaced.</summary>
    private static void Write(string text)
    {
        lock (s_writing)
        {
            using var error = Console.OpenStandardError();
            var bytes = Encoding.UTF8.GetBytes(text);
            error.Write(bytes, 0, bytes.Length);
            error.Flush();
        }
    }

    private static string Format(TimeSpan time) => $"{(int)time.TotalMinutes}m {time.Seconds:00}s";

    private static string Shorten(string text, int length = 160) => text.Length <= length ? text : text[..length] + "…";
}
