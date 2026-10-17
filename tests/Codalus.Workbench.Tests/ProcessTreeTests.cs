using System.Diagnostics;
using System.Reflection;
using Codalus.Platform;

namespace Codalus.Workbench.Tests;

/// <summary>
/// <see cref="ProcessTree.Children"/>: a process this one starts is listed as its child on every OS; on Windows a listed
/// process created before its parent is an earlier holder's child of the parent's reused id, and left out
/// (<see cref="ProcessTree.StartedSinceParent"/>), while a child created at or after it stays.
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
            child.Kill(entireProcessTree: true);
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
}
