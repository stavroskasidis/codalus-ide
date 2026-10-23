using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Codalus.Platform;

namespace Codalus.Workbench.Tests;

/// <summary>
/// <see cref="ProcessTree.Children"/>: a process this one starts is listed as its child on every OS; on Windows a listed
/// process created before its parent is an earlier holder's child of the parent's reused id, and left out
/// (<see cref="ProcessTree.StartedSinceParent"/>), while a child created at or after it stays.
/// <see cref="ProcessTree.Kill"/> ends a process and the processes it started, and is the only way the product does.
/// </summary>
public sealed class ProcessTreeTests
{
    private static string Metadata(string key) =>
        typeof(ProcessTreeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>The scripted adapter's program (<c>--program</c>): it runs until it is killed.</summary>
    private static string Program => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    [Fact]
    public void A_started_process_is_listed_among_its_parents_children()
    {
        // On Windows this goes through the creation-time check with a real child: it is never left out.
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add(Program);
        start.ArgumentList.Add("--program");
        using var child = Process.Start(start)!;
        try
        {
            Assert.Contains(child.Id, ProcessTree.Children(Environment.ProcessId));
            Assert.DoesNotContain(Environment.ProcessId, ProcessTree.Children(child.Id));
        }
        finally
        {
            ProcessTree.Kill(child);
            child.WaitForExit();
        }
    }

    [Fact]
    public void A_listed_process_created_before_its_parent_is_left_out_and_later_ones_stay()
    {
        // Process 5 is an earlier holder's child of the parent's id (created before the parent); 6 was created in the same
        // clock tick as the parent, 7 after it; 8's creation time can't be read (it ended, or may not be queried).
        long? Created(int id) => id switch { 5 => 1_000, 6 => 2_000, 7 => 2_001, _ => null };

        Assert.Equal([6, 7, 8], ProcessTree.StartedSinceParent(2_000, [5, 6, 7, 8], Created));
        Assert.Equal([7, 6, 8], ProcessTree.StartedSinceParent(2_000, [5, 7, 5, 6, 8], Created));

        // A parent whose creation time can't be read keeps every listed child: only a process known to be older is left out.
        Assert.Equal([5, 6, 7, 8], ProcessTree.StartedSinceParent(null, [5, 6, 7, 8], Created));
        Assert.Empty(ProcessTree.StartedSinceParent(2_000, [], Created));
    }

    [Fact]
    public async Task Kill_ends_a_process_and_the_processes_it_started()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c start /b ping -n 60 127.0.0.1 >nul & ping -n 60 127.0.0.1 >nul")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 60 & sleep 60 & wait" } };
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        using var shell = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            IReadOnlyList<int> children;
            while ((children = ProcessTree.Children(shell.Id)).Count < 2)
            {
                Assert.True(DateTime.UtcNow < deadline, $"The shell started {children.Count} of its 2 processes.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            ProcessTree.Kill(shell);

            Assert.True(shell.WaitForExit(TimeSpan.FromSeconds(30)), "The shell is still running.");
            foreach (var child in children)
            {
                while (Running(child))
                {
                    Assert.True(DateTime.UtcNow < deadline, $"Process {child}, which the shell started, is still running.");
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
            }
        }
        finally
        {
            if (!shell.HasExited)
            {
                shell.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Kill_ends_a_child_started_as_its_parent_is_stopped()
    {
        // Killed the moment it prints, the shell is often in the middle of forking sleep: on macOS the child shows only
        // once the fork is done, after the first listing. A sleep that survives keeps standard output open (as a host's
        // child keeps the host's connection open), so the output's end is the check.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c echo started& ping -n 60 127.0.0.1 >nul")
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "echo started; sleep 60" } };
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            using var shell = Process.Start(start)!;
            try
            {
                Assert.Equal("started", (await shell.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))?.Trim());
                ProcessTree.Kill(shell);
                var rest = shell.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                Assert.True(await Task.WhenAny(rest, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)) == rest, $"Attempt {attempt + 1}: the shell's child still holds its output.");
            }
            finally
            {
                if (!shell.HasExited)
                {
                    shell.Kill(entireProcessTree: true);
                }
            }
        }
    }

    [Fact]
    public void The_product_ends_process_trees_through_ProcessTree_Kill()
    {
        // .NET's tree walk reads every process's task information on macOS, which blocks for minutes while ReportCrash
        // holds a crashed process's: every test that ended a tree hung with it on CI.
        var source = Path.Combine(Metadata("CodalusRepoRoot"), "src");
        var uses = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && Path.GetFileName(file) != "ProcessTree.cs")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\.Kill\(\s*(entireProcessTree:\s*)?true\s*\)"))
            .Select(file => Path.GetRelativePath(source, file))
            .ToList();
        Assert.True(uses.Count == 0, "Process.Kill(entireProcessTree: true) in place of ProcessTree.Kill: " + string.Join(", ", uses));
    }

    /// <summary>Whether the process runs: on Linux a zombie (one no init reaps, in a container) has ended.</summary>
    private static bool Running(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{processId}/stat");
                return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z') is false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
