using System.Diagnostics;
using System.Globalization;
using System.Text;
using Codalus.Platform;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// What a failure of the smoke adds so that it explains itself when it doesn't reproduce: a build wait that timed out
/// says what the build host logged last and which of the IDE's processes still ran; an error toast hidden before a
/// picture says the exception behind it.
/// </summary>
internal sealed partial class SmokeTest
{
    /// <summary>The heading of <see cref="BuildWaitDiagnostics"/>: a failure that has it already isn't given it twice.</summary>
    private const string BuildWaitHeading = "--- The Build Host log (the end) ---";

    /// <summary>
    /// For a build wait that timed out (or a step that failed while a build ran): the build host's last log lines and the
    /// IDE's child processes with their command lines. The Mac's web.scaffolding (2026-11-18) waited 420 s for the
    /// Client project's restore and build with Grpc.Tools, its output stopped after "All projects are up-to-date for
    /// restore", and nothing said what the build host was doing or what it had started.
    /// </summary>
    private string BuildWaitDiagnostics()
    {
        var log = shell.Workbench.Output.Channels.FirstOrDefault(c => c.Id == "codalus.buildhost")?.GetText() ?? "(no Build Host channel)";
        return $"\n{BuildWaitHeading}\n{Tail(log, 3000)}\n--- The IDE's child processes ---\n{ChildProcesses()}";
    }

    /// <summary>
    /// The processes this one started, and theirs, indented under their parent, each with how long it has run and its
    /// command line (<see cref="ProcessTree.Children"/>; the command lines from <c>/proc</c> on Linux, one <c>ps</c> on
    /// macOS, the image name on Windows, where reading another process's command line needs its memory).
    /// </summary>
    internal static string ChildProcesses()
    {
        var tree = new List<(int Id, int Depth)>();
        void Walk(int parent, int depth)
        {
            foreach (var child in ProcessTree.Children(parent))
            {
                if (tree.Count >= 200 || tree.Exists(p => p.Id == child))
                {
                    return;
                }

                tree.Add((child, depth));
                Walk(child, depth + 1);
            }
        }

        Walk(Environment.ProcessId, 0);
        if (tree.Count == 0)
        {
            return "(none)";
        }

        var described = Describe([.. tree.Select(p => p.Id)]);
        var text = new StringBuilder();
        foreach (var (id, depth) in tree)
        {
            var line = $"{new string(' ', 2 * depth)}{id.ToString(CultureInfo.InvariantCulture)} {described.GetValueOrDefault(id, "(ended)")}";
            text.AppendLine(line.Length > 400 ? line[..400] + "…" : line);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Each process's running time and command line, by id; a process that ended meanwhile is left out.</summary>
    private static Dictionary<int, string> Describe(IReadOnlyList<int> ids)
    {
        var described = new Dictionary<int, string>();
        if (OperatingSystem.IsMacOS())
        {
            // One ps for all of them (-ww: the whole command line), bounded: ps blocks while ReportCrash holds a process.
            var output = RunTool("ps", TimeSpan.FromSeconds(15), "-ww", "-o", "pid=,etime=,command=", "-p", string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = row.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    var rest = parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    described[id] = rest.Length == 2 ? $"(running {rest[0]}) {rest[1]}" : parts[1];
                }
            }

            return described;
        }

        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                var running = $"(running {DateTime.Now - process.StartTime:hh\\:mm\\:ss})";
                described[id] = OperatingSystem.IsLinux() && File.Exists($"/proc/{id}/cmdline")
                    ? $"{running} {File.ReadAllText($"/proc/{id}/cmdline").Replace('\0', ' ').TrimEnd()}"
                    : $"{running} {process.ProcessName}";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                // It ended, or isn't ours to read.
            }
        }

        return described;
    }

    /// <summary>
    /// Runs a tool that starts nothing (ps, dotnet-stack) and returns what it printed, or why it couldn't run. It's ended
    /// alone after <paramref name="limit"/>: ending a process tree lists the system's processes, which is what blocks when
    /// ps does (the macOS runner, blazor.wasmDebug).
    /// </summary>
    private static string RunTool(string program, TimeSpan limit, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(limit))
            {
                process.Kill();
                return "(timed out)";
            }

            return output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"({program}: {ex.Message})";
        }
    }

    /// <summary>What <c>App.OnUnhandledException</c> logs before each exception, and starts its error toast with.</summary>
    private const string UnhandledEntry = "[unhandled] ";

    internal const string UnhandledToast = "An unexpected error occurred: ";

    /// <summary>
    /// The exception behind an error toast, from the IDE's log: <c>App.OnUnhandledException</c> logs
    /// <c>[unhandled] {exception}</c> with its stack and shows only its message, and the smoke doesn't save that log. The
    /// latest entry whose text has the toast's message; for "An unexpected error occurred" with none that has it, the
    /// latest entry, said so; null when there's none to show. An entry ends at the next line that starts another entry
    /// (<c>[</c>), at most 6000 characters. debug.remote's picture (the Mac's smoke, 2026-11-18) hid "An unexpected error
    /// occurred: Collection was modified; enumeration operation may not execute", and no stack was kept.
    /// </summary>
    internal static string? UnhandledBehind(string log, string toastMessage)
    {
        var message = toastMessage.StartsWith(UnhandledToast, StringComparison.Ordinal) ? toastMessage[UnhandledToast.Length..] : toastMessage;
        string? latest = null;
        for (var at = log.LastIndexOf(UnhandledEntry, StringComparison.Ordinal); at >= 0; at = at == 0 ? -1 : log.LastIndexOf(UnhandledEntry, at - 1, StringComparison.Ordinal))
        {
            if (at > 0 && log[at - 1] != '\n')
            {
                continue; // inside a line: not an entry's start
            }

            var entry = Entry(log, at);
            if (message.Length > 0 && entry.Contains(message.Trim(), StringComparison.Ordinal))
            {
                return entry;
            }

            latest ??= entry;
        }

        return latest is not null && toastMessage.StartsWith(UnhandledToast, StringComparison.Ordinal)
            ? "(the latest unhandled exception logged; its message isn't the toast's)\n" + latest
            : null;

        static string Entry(string log, int start)
        {
            var end = log.Length;
            for (var line = log.IndexOf('\n', start); line >= 0 && line + 1 < log.Length; line = log.IndexOf('\n', line + 1))
            {
                if (log[line + 1] == '[')
                {
                    end = line;
                    break;
                }
            }

            var entry = log[start..end].TrimEnd();
            return entry.Length > 6000 ? entry[..6000] + "…" : entry;
        }
    }
}
