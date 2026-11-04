using System.Diagnostics;
using Codalus.Debugging.Host.CoreClr;

namespace Codalus.Debugging.Tests;

/// <summary>
/// What the launch's timeout says about its program (<see cref="DbgShimDebugger.StartState"/>): the diagnostic for the
/// Windows CI launches whose runtime didn't start in 30 seconds (run 41442485548, docs/ci-flakes.md).
/// </summary>
public sealed class LaunchStartStateTests
{
    [Fact]
    public void A_running_program_says_how_long_it_ran_on_how_much_processor_time_and_whether_its_runtime_loaded()
    {
        var self = Environment.ProcessId;

        var loaded = DbgShimDebugger.StartState(self, _ => "/dotnet/shared/Microsoft.NETCore.App/11.0.0/libcoreclr.so");
        Assert.Matches(@"^The program still runs \(\d+\.\d s old, \d+\.\d s of processor time, \d+ threads\) and has loaded /dotnet/shared/Microsoft\.NETCore\.App/11\.0\.0/libcoreclr\.so\.$", loaded);

        var notLoaded = DbgShimDebugger.StartState(self, _ => null);
        Assert.EndsWith("and hasn't loaded a .NET runtime.", notLoaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_ended_says_so()
    {
        using var program = Process.Start(new ProcessStartInfo(DebuggeeFixture.DotnetHost, "--version") { RedirectStandardOutput = true })!;
        await program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await program.WaitForExitAsync(TestContext.Current.CancellationToken);

        // "(exit code 0)" while a handle keeps the process object (Windows), without it when the id is gone.
        Assert.StartsWith("The program ended", DbgShimDebugger.StartState(program.Id, _ => throw new InvalidOperationException("not asked")), StringComparison.Ordinal);
    }
}
