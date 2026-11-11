using System.Diagnostics;

namespace Codalus.Rpc.Tests;

/// <summary>
/// A host that exits as its supervisor begins reading it (<c>PipeStartGuard</c>). On Unix the first read of a
/// redirected stream makes the pipe's socket, and a dispose at that moment left the read spinning forever (.NET 11.0's
/// <c>SafePipeHandle.CreatePipeSocket</c>; Linux CI, PR #321's run 42964724644): about 1 round in 130 without the guard.
/// </summary>
public sealed class PipeStartGuardTests
{
    [Fact]
    public async Task A_host_disposed_as_its_output_is_first_read_ends_the_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' redirected streams aren't pipe streams.");
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var round = 0; round < 3000; round++)
        {
            var start = new ProcessStartInfo("true") { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
            var host = new LocalHostProcess(Process.Start(start)!);
            var output = host.Output;
            var buffer = new byte[4096];
            var read = Task.Run(async () =>
            {
                try
                {
                    _ = await output.ReadAsync(buffer, cancellationToken);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or IOException)
                {
                    // Disposed first: as expected.
                }
            }, cancellationToken);
            // As a supervisor ends a host: the connection disposes its stream, then the process is disposed.
            var dispose = Task.Run(async () =>
            {
                await output.DisposeAsync();
                await host.DisposeAsync();
            }, cancellationToken);
            var both = Task.WhenAll(read, dispose);
            await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.True(both.IsCompleted, $"Round {round}: the read {(read.IsCompleted ? "ended" : "never ended")}, the dispose {(dispose.IsCompleted ? "ended" : "never ended")}.");
        }
    }
}
