using System.Diagnostics;
using Codalus.Projects.Host.Packages;

namespace Codalus.Projects.Tests;

public sealed class ChildProcessesTests
{
    /// <summary>
    /// The build host exits right after it kills its children: one it killed but hadn't reaped yet was left a zombie for
    /// the process that adopted it, for good where that process doesn't reap, and a zombie still counts as running
    /// (<c>EntityFrameworkIntegrationTests.Closing_the_solution_ends_a_running_dotnet_ef</c>, Linux, a full validation run
    /// under load). Killing them now waits for each to end, which reaps it.
    /// </summary>
    [Fact]
    public void Killed_children_have_ended_and_are_reaped_when_the_kill_returns()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", ["-n", "300", "127.0.0.1"])
            : new ProcessStartInfo("sleep", ["300"]);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.CreateNoWindow = true;
        var children = Enumerable.Range(0, 8).Select(_ => Process.Start(psi)!).ToList();
        try
        {
            var ids = children.Select(c => c.Id).ToList();
            ChildProcesses.KillAndReap(children, TimeSpan.FromSeconds(10));
            var left = ids.Where(Exists).ToList();
            Assert.True(left.Count == 0, $"Still there when the kill returned: {string.Join(", ", left.Select(Describe))}");
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }

                child.Dispose();
            }
        }
    }

    /// <summary>Whether the process still exists: on Linux a zombie does, as it does for another process's <see cref="Process.HasExited"/>.</summary>
    private static bool Exists(int id)
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.Exists($"/proc/{id}");
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Describe(int id)
    {
        try
        {
            return OperatingSystem.IsLinux()
                ? $"{id} in state {File.ReadAllText($"/proc/{id}/stat").Split(") ")[^1].Split(' ')[0]}"
                : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return $"{id} (gone)";
        }
    }
}
