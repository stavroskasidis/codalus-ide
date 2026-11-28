using System.Diagnostics;
using System.Globalization;

namespace Codalus.Debugging.Tests;

/// <summary>
/// A program that ends between its held start and the debugger's attach is said to have ended before the attach. An
/// x64 .NET Core 2.1 program under Rosetta 2, which no startup hook holds, ended right there in the Mac's full suite
/// (2026-11-24, docs/ci-flakes.md): dbgshim answered for a process that was gone, and the session said it couldn't
/// load the debugging API of the program's runtime. Here the program is held by the hook and ended by the test while
/// the debugger waits (<c>CODALUS_DEBUG_HELD_ATTACH_DELAY_MS</c>).
/// </summary>
public sealed class HeldStartEndTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_program_that_ends_while_the_debugger_attaches_ended_before_the_attach()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The held start is for Linux and macOS (dbgshim's own search is used on Windows).");

        // The adapter's own temporary folder: the only wait folder in it is this launch's.
        // (A short name: the launcher's pipe is a socket there, and a socket's path has 104 characters at most.)
        var temp = Directory.CreateTempSubdirectory("he").FullName;
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["CODALUS_DEBUG_HELD_START"] = "1",
                ["CODALUS_DEBUG_HELD_ATTACH_DELAY_MS"] = "5000",
                ["TMPDIR"] = temp,
            };
            await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter() with { Environment = environment }, Ct);
            var launch = adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll], fixture.Folder, Type: "coreclr"), DapJsonContext.Default.NetFxDebugArguments, Ct);

            // The hook's word that the program waits in it; then the program's end, before the debugger attaches.
            var started = Stopwatch.StartNew();
            string? reported = null;
            while (reported is null)
            {
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), $"The program didn't reach the startup hook. {temp}: {string.Join(", ", Directory.GetFileSystemEntries(temp, "*", SearchOption.AllDirectories))}");
                Assert.False(launch.IsCompleted, $"The launch ended before the program reported: {launch.Exception?.GetBaseException().Message}");
                reported = Directory.GetFiles(temp, "pid", SearchOption.AllDirectories).FirstOrDefault();
                await Task.Delay(20, Ct);
            }

            using (var program = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(reported, Ct), CultureInfo.InvariantCulture)))
            {
                program.Kill();
            }

            var error = await Assert.ThrowsAsync<DapException>(() => launch.WaitAsync(TimeSpan.FromSeconds(60), Ct));
            Assert.Contains("ended", error.Message, StringComparison.Ordinal);
            Assert.Contains("before the debugger could attach to it", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
