namespace Codalus.Debugging.Tests;

/// <summary>What a session that stopped making progress leaves to diagnose it, without an adapter.</summary>
public sealed class AdapterDiagnosticsTests
{
    /// <summary>
    /// A relative <c>CODALUS_NETCOREDBG_LOG</c> (the smoke test's <c>artifacts/smoke-test/netcoredbg</c>) reaches
    /// netcoredbg as an absolute path: netcoredbg starts in its own folder, where the relative one named nothing, and
    /// Linux arm64's failed debug.compound (run 41374588224) kept no log.
    /// </summary>
    [Fact]
    public void A_relative_log_folder_reaches_netcoredbg_as_an_absolute_path()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-netcoredbg-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, folder);
            var adapter = Path.Combine(Path.GetTempPath(), "netcoredbg", OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");

            var start = DebugAdapterStartInfo.Local(adapter, relative);

            var log = Assert.Single(start.Arguments, a => a.StartsWith("--engineLogging=", StringComparison.Ordinal))["--engineLogging=".Length..];
            Assert.True(Path.IsPathRooted(log), log);
            Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(log));
            Assert.True(Directory.Exists(folder));
            Assert.Equal(Path.GetDirectoryName(adapter), start.WorkingDirectory);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void No_log_folder_starts_netcoredbg_without_a_log()
    {
        var start = DebugAdapterStartInfo.Local("/opt/netcoredbg/netcoredbg", null);

        Assert.Equal(["--interpreter=vscode"], start.Arguments);
    }

    /// <summary>
    /// On Linux a stuck session says where each thread of netcoredbg and of the program waits in the kernel, with the
    /// threads' names; elsewhere nothing (run 41374588224's program sat in state S with no clue why).
    /// </summary>
    [Fact]
    public void Thread_waits_name_each_kernel_wait_with_its_threads_on_Linux()
    {
        using var parked = new ManualResetEventSlim();
        var thread = new Thread(() => parked.Wait()) { Name = "ParkedForTest", IsBackground = true };
        thread.Start();
        try
        {
            var waits = DebugAdapter.ThreadWaits(Environment.ProcessId);

            if (!OperatingSystem.IsLinux())
            {
                Assert.Null(waits);
                return;
            }

            Assert.NotNull(waits);
            Assert.Matches(@"^\d+ threads: \S+ ×\d+", waits);
            Assert.Contains("ParkedForTest", waits, StringComparison.Ordinal);
        }
        finally
        {
            parked.Set();
            thread.Join();
        }
    }

    [Fact]
    public void Thread_waits_of_a_process_that_is_gone_are_null()
    {
        Assert.Null(DebugAdapter.ThreadWaits(int.MaxValue));
    }
}
