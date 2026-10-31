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

    /// <summary>
    /// A start waits in its launcher (for the environment, M56) while its owner shuts down: the process the launcher then
    /// returns is stopped, and the start ends as disposed. It ran on, unowned: the disposal had found nothing to stop, and
    /// the start released a lock the disposal had disposed.
    /// </summary>
    [Fact]
    public async Task A_process_started_once_its_supervisor_was_disposed_is_stopped()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var launcher = new GatedLauncher(entered, release);
        var process = new SupervisedProcess("test host", new ProcessLaunchInfo("anything", []) { Launcher = launcher }, new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null });
        var start = process.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        await process.DisposeAsync();
        release.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
        Assert.NotNull(launcher.Started);
        Assert.True(launcher.Started.Killed);
        Assert.True(launcher.Started.Disposed);
        Assert.False(process.IsRunning);
    }

    /// <summary>Returns a process once released, from the thread pool (where the supervisor runs its launcher).</summary>
    private sealed class GatedLauncher(ManualResetEventSlim entered, ManualResetEventSlim release) : IProcessLauncher
    {
        public StoppableProcess? Started { get; private set; }

        public Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30), cancellationToken);
            Started = new StoppableProcess();
            return Task.FromResult<IHostProcess>(Started);
        }
    }

    /// <summary>A process that says whether it was killed and disposed.</summary>
    private sealed class StoppableProcess : IHostProcess
    {
        private readonly TaskCompletionSource<int?> _exited = new();

        public Stream Output => Stream.Null;

        public Stream Input => Stream.Null;

        public int? Id => 42;

        public Task<int?> Exited => _exited.Task;

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public event Action<string>? ErrorLine
        {
            add { }
            remove { }
        }

        public void BeginErrorRead()
        {
        }

        public void Kill()
        {
            Killed = true;
            _exited.TrySetResult(-1);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
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
