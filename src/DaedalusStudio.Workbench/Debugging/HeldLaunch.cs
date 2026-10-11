using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Debugging;

/// <summary>
/// A program started in the integrated terminal and held before <c>Main</c> by the startup hook
/// (<c>DaedalusStudio.Debugging.StartupHook.dll</c>, M14c, ADR 0039) until the debugger has attached: netcoredbg can't
/// launch into a terminal, so the IDE launches and the debugger attaches. <see cref="Release"/> lets it run.
/// </summary>
public sealed class HeldLaunch
{
    public const string HookFileName = "DaedalusStudio.Debugging.StartupHook.dll";
    public const string WaitVariable = "DAEDALUS_DEBUG_WAIT";
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";
    public const string EnvironmentVariable = "DAEDALUS_STARTUP_HOOK";

    private readonly string _folder;

    private HeldLaunch(string folder, int processId)
    {
        _folder = folder;
        ProcessId = processId;
    }

    /// <summary>The program's process id, as the startup hook wrote it.</summary>
    public int ProcessId { get; }

    /// <summary>
    /// The startup hook: <c>DAEDALUS_STARTUP_HOOK</c>, then <c>debughost/</c> next to the shell, then next to the
    /// running assembly (tests).
    /// </summary>
    public static string? ResolveHook()
    {
        var candidates = new List<string>();
        if (System.Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "debughost", HookFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, HookFileName));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The environment that makes the program wait in the hook: <c>DOTNET_STARTUP_HOOKS</c> extended, and the folder to signal through.</summary>
    public static Dictionary<string, string?> Environment(IReadOnlyDictionary<string, string> planned, string hook, string folder)
    {
        var environment = planned.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
        var existing = planned.TryGetValue(StartupHooksVariable, out var fromPlan) ? fromPlan : System.Environment.GetEnvironmentVariable(StartupHooksVariable);
        environment[StartupHooksVariable] = string.IsNullOrEmpty(existing) ? hook : hook + Path.PathSeparator + existing;
        environment[WaitVariable] = folder;
        return environment;
    }

    /// <summary>
    /// Starts <paramref name="plan"/> in a terminal through <paramref name="terminal"/> and waits until the hook holds
    /// it (its process id written). Throws when the program ends first or doesn't get there within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<HeldLaunch> StartAsync(ITerminalLauncher terminal, string hook, LaunchPlan plan, string title, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "daedalus-debug-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        try
        {
            var arguments = plan.ArgumentList ?? CommandLine.Split(plan.Arguments);
            var started = await terminal.LaunchAsync(new TerminalLaunch(title, plan.FileName, arguments, plan.WorkingDirectory, Environment(plan.Environment, hook, folder)), cancellationToken).ConfigureAwait(false);
            var pidFile = Path.Combine(folder, "pid");
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile, cancellationToken).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    return new HeldLaunch(folder, pid);
                }

                if (started is { } id && HasExited(id))
                {
                    throw new InvalidOperationException($"{Path.GetFileName(plan.FileName)} ended before the debugger could attach (see the terminal).");
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException($"{Path.GetFileName(plan.FileName)} didn't reach the debugger's startup hook in {timeout.TotalSeconds:0} seconds. A program that isn't .NET, or that disables startup hooks, can't be debugged in the terminal.");
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    /// <summary>Lets the program run on (the hook removes the folder once it saw the signal).</summary>
    public void Release()
    {
        try
        {
            File.WriteAllText(Path.Combine(_folder, "go"), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is gone: the hook stopped waiting already.
        }
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
