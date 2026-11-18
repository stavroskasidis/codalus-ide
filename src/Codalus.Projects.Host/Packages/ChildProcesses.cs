using System.Diagnostics;
#if CODALUS_PROCESS_TREE_INTERNAL
using Codalus.Platform.Internal;
#else
using Codalus.Platform;
#endif

namespace Codalus.Projects.Host.Packages;

/// <summary>Ending the host's children as it exits (<see cref="DotnetProcess.KillAll"/>).</summary>
internal static class ChildProcesses
{
    /// <summary>
    /// Ends each process and what it started, then waits for each to end, up to <paramref name="limit"/> in all, which on
    /// Linux and macOS also reaps it.
    /// </summary>
    /// <remarks>
    /// The host exits right after this. A child it killed and hadn't reaped yet (.NET reaps a child when its SIGCHLD
    /// handling gets to it) was left a zombie for the process that adopts it, for good where that process doesn't reap, as
    /// in a container whose first process isn't an init; and a zombie still counts as a running process
    /// (<c>EntityFrameworkIntegrationTests.Closing_the_solution_ends_a_running_dotnet_ef</c>, Linux, a full validation run
    /// under load, 2026-11-10).
    /// </remarks>
    public static void KillAndReap(IReadOnlyList<Process> processes, TimeSpan limit)
    {
        var killed = new List<Process>();
        foreach (var process in processes)
        {
            try
            {
                ProcessTree.Kill(process);
                killed.Add(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
            {
                // Already gone.
            }
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency);
        foreach (var process in killed)
        {
            try
            {
                var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                process.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
}
