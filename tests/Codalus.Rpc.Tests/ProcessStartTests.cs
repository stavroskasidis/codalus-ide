namespace Codalus.Rpc.Tests;

/// <summary>The tests that hold this process's environment (<see cref="ProcessEnvironment"/>) run alone.</summary>
[CollectionDefinition(nameof(ProcessEnvironmentTests), DisableParallelization = true)]
public sealed class ProcessEnvironmentTests;

/// <summary>
/// Where and when a host starts (M56): never on the caller's thread, which is often the UI thread (a restored tab, a
/// solution's open, a plugin's activation), and here only once this process's environment is complete.
/// </summary>
[Collection(nameof(ProcessEnvironmentTests))]
public sealed class ProcessStartTests
{
    /// <summary>
    /// The launcher works synchronously (the runtime check's file probes, Process.Start), so a start on the caller's thread
    /// held it for as long: here the launcher blocks until the test lets it go, which the caller couldn't do if it ran there.
    /// </summary>
    [Fact]
    public async Task A_start_runs_its_launcher_off_the_callers_thread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var launcher = new BlockingLauncher(entered, release);
        await using var process = new SupervisedProcess("test host", new ProcessLaunchInfo("anything", []) { Launcher = launcher }, new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null });
        var caller = Environment.CurrentManagedThreadId;

        var start = process.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(start.IsCompleted);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.NotEqual(caller, launcher.Thread);
        release.Set();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.Equal("released", error.Message);
    }

    /// <summary>
    /// Started from the Finder or a desktop launcher, the login shell's PATH and DOTNET_ROOT come in while the window shows: a
    /// process started meanwhile waits for them. A program that doesn't exist fails as soon as its start is tried.
    /// </summary>
    [Fact]
    public async Task A_local_start_waits_until_the_environment_is_complete()
    {
        var environment = new TaskCompletionSource();
        ProcessEnvironment.WaitFor(environment.Task);
        try
        {
            var launch = new ProcessLaunchInfo(Path.Combine(Path.GetTempPath(), "codalus-no-such-program-" + Guid.NewGuid().ToString("N")), []) { CheckRuntime = false };
            var start = LocalProcessLauncher.Instance.StartAsync(launch, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(start.IsCompleted);

            environment.SetResult();
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => start);
        }
        finally
        {
            ProcessEnvironment.WaitFor(Task.CompletedTask);
        }
    }

    /// <summary>A start that waits for the environment ends when it's cancelled meanwhile.</summary>
    [Fact]
    public async Task A_start_waiting_for_the_environment_can_be_cancelled()
    {
        ProcessEnvironment.WaitFor(new TaskCompletionSource().Task);
        try
        {
            using var cancel = new CancellationTokenSource();
            var start = LocalProcessLauncher.Instance.StartAsync(new ProcessLaunchInfo("anything", []) { CheckRuntime = false }, cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        }
        finally
        {
            ProcessEnvironment.WaitFor(Task.CompletedTask);
        }
    }

    /// <summary>Blocks in its start until released (at most 5 s, so a start on the caller's thread fails the test, not hangs it).</summary>
    private sealed class BlockingLauncher(ManualResetEventSlim entered, ManualResetEventSlim release) : IProcessLauncher
    {
        public int Thread { get; private set; }

        public Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
        {
            Thread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5), cancellationToken);
            throw new InvalidOperationException("released");
        }
    }
}
