using Codalus.Rpc;

namespace Codalus.Shell.Tests;

/// <summary>0.9: what opening a large solution changes about the C# host.</summary>
public class LargeSolutionPerformanceTests
{
    [Fact]
    public void A_large_solution_runs_the_CSharp_host_with_the_server_garbage_collector()
    {
        var launch = new ProcessLaunchInfo("codalus-lshost", []) { Environment = new Dictionary<string, string> { ["KEEP"] = "1" } };

        Assert.Same(launch, ShellHost.WithServerGc(launch, 0));
        Assert.Same(launch, ShellHost.WithServerGc(launch, ShellHost.ServerGcProjects - 1));
        Assert.Null(ShellHost.WithServerGc(null, 100));

        var large = ShellHost.WithServerGc(launch, ShellHost.ServerGcProjects)!;
        Assert.Equal("1", large.Environment!["DOTNET_gcServer"]);
        Assert.Equal("1", large.Environment["KEEP"]);
        Assert.Equal(launch.FileName, large.FileName);
        Assert.False(launch.Environment.ContainsKey("DOTNET_gcServer"));
    }
}
