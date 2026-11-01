using Codalus.Workbench.Updates;

namespace Codalus.Workbench.Tests;

public sealed class UpdateServiceTests
{
    private static readonly UpdateOffer s_next = new("0.3.0", "stable", false, "## 0.3.0\n\nNew things.", new Uri("https://example.com/v0.3.0"));

    [Fact]
    public async Task An_installed_build_downloads_what_it_finds_and_is_then_ready_to_restart()
    {
        var backend = new FakeBackend { Offer = s_next };
        await using var service = Service(InstallKind.SelfUpdating, backend);
        var seen = new List<UpdateStatus>();
        service.StateChanged += (_, state) => seen.Add(state.Status);

        var state = await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.Ready, state.Status);
        Assert.Equal("0.3.0", state.Offer?.Version);
        Assert.Equal(["stable"], backend.CheckedChannels);
        Assert.Equal([UpdateStatus.Checking, UpdateStatus.Downloading, UpdateStatus.Downloading, UpdateStatus.Ready], seen);
        Assert.True(service.ApplyOnExit(restart: true, ["App.sln"]));
        Assert.Equal(("0.3.0", true, "App.sln"), (backend.Applied?.Version, backend.Restarted, string.Join(' ', backend.Arguments)));
    }

    [Theory]
    [InlineData(InstallKind.SystemPackage, UpdateMode.Auto)]
    [InlineData(InstallKind.Portable, UpdateMode.Auto)]
    [InlineData(InstallKind.SelfUpdating, UpdateMode.Notify)]
    public async Task Other_installs_and_notify_mode_only_say_that_a_release_is_available(InstallKind kind, UpdateMode mode)
    {
        var backend = new FakeBackend { Offer = s_next };
        await using var service = Service(kind, backend, mode);

        var state = await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.Available, state.Status);
        Assert.Equal(0, backend.Downloads);
        Assert.False(service.ApplyOnExit(restart: false, []));
    }

    [Fact]
    public async Task Off_means_no_traffic_unless_the_user_asks_and_development_builds_never_check()
    {
        var backend = new FakeBackend { Offer = s_next };
        await using var off = Service(InstallKind.SelfUpdating, backend, UpdateMode.Off);
        await using var development = Service(InstallKind.Development, backend, version: "0.2.0-dev");

        Assert.Equal(UpdateStatus.Idle, (await off.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken)).Status);
        Assert.Empty(backend.CheckedChannels);
        // The user's own check runs, and offers the release rather than downloading it.
        Assert.Equal(UpdateStatus.Available, (await off.CheckAsync(userInitiated: true, TestContext.Current.CancellationToken)).Status);

        var disabled = await development.CheckAsync(userInitiated: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.Disabled, disabled.Status);
        Assert.Single(backend.CheckedChannels); // only the user's check of the installed build
    }

    [Fact]
    public async Task Up_to_date_and_failures_are_states_and_a_failure_is_reported()
    {
        var backend = new FakeBackend();
        await using var service = Service(InstallKind.SelfUpdating, backend);
        var failures = new List<Exception>();
        service.Failed += (_, ex) => failures.Add(ex);

        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken)).Status);

        backend.Fail = new HttpRequestException("feed signature didn't verify");
        var failed = await service.CheckAsync(userInitiated: true, TestContext.Current.CancellationToken);
        Assert.Equal((UpdateStatus.Failed, "feed signature didn't verify"), (failed.Status, failed.Error));
        Assert.Single(failures);
    }

    [Fact]
    public async Task Once_an_update_is_ready_later_checks_leave_it_alone()
    {
        var backend = new FakeBackend { Offer = s_next };
        await using var service = Service(InstallKind.SelfUpdating, backend);
        await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken);

        backend.Offer = s_next with { Version = "0.4.0" };
        var again = await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken);

        Assert.Equal(("0.3.0", 1), (again.Offer?.Version, backend.Downloads));
    }

    [Fact]
    public async Task Switching_from_nightlies_to_stable_asks_before_installing_an_older_version()
    {
        var older = new UpdateOffer("0.2.0", "stable", IsDowngrade: true, null, null);
        var backend = new FakeBackend { Offer = older };
        await using var service = Service(InstallKind.SelfUpdating, backend, version: "0.3.0-nightly.20261011.4");
        var asked = 0;

        var declined = await service.SwitchChannelAsync("stable", _ => { asked++; return Task.FromResult(false); }, TestContext.Current.CancellationToken);
        var accepted = await service.SwitchChannelAsync("stable", _ => { asked++; return Task.FromResult(true); }, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.Idle, declined.Status);
        Assert.Equal((UpdateStatus.Ready, "0.2.0"), (accepted.Status, accepted.Offer?.Version));
        Assert.Equal(2, asked);
        Assert.All(backend.Downgrades, Assert.True);
    }

    [Fact]
    public async Task The_schedule_checks_once_started_and_then_every_four_hours()
    {
        var time = new ManualTime();
        var backend = new FakeBackend();
        await using var service = new UpdateService(new InstallInfo("0.2.0", InstallKind.SelfUpdating), backend, () => UpdateMode.Auto, () => "stable", time);

        service.Start();
        service.Start(); // once only

        Assert.Equal((TimeSpan.Zero, UpdateService.Interval), (time.DueTime, time.Period));
        Assert.Equal(1, time.Timers);
        time.Fire();
        await WaitForAsync(() => backend.CheckedChannels.Count == 1);
    }

    [Theory]
    [InlineData("0.2.0-nightly.20261011.1", "stable", "nightly")]
    [InlineData("0.2.0", "stable", "stable")]
    public void A_build_follows_the_channel_it_came_from(string version, string _, string channel)
    {
        Assert.Equal(channel, new InstallInfo(version, InstallKind.SelfUpdating).DefaultChannel);
    }

    [Theory]
    [InlineData("0.2.0-dev", true, false, InstallKind.Development)]
    [InlineData("0.2.0-dev+abc123", true, false, InstallKind.Development)]
    [InlineData("0.2.0", true, false, InstallKind.SelfUpdating)]
    [InlineData("0.2.0", false, true, InstallKind.SystemPackage)]
    [InlineData("0.2.0-nightly.20261011.1", false, false, InstallKind.Portable)]
    public void The_kind_of_install_decides_what_updating_means(string version, bool velopack, bool system, InstallKind kind)
    {
        Assert.Equal(kind, InstallInfo.KindOf(version, velopack, system));
    }

    [Fact]
    public void An_arch_package_is_the_one_pacman_lists_as_codalus_bin()
    {
        var database = Directory.CreateTempSubdirectory("codalus-pacman-").FullName;
        try
        {
            Assert.False(InstallInfo.IsPacmanPackage(Path.Combine(database, "missing")));
            Directory.CreateDirectory(Path.Combine(database, "codalus-0.10.0-1")); // another package's name
            Assert.False(InstallInfo.IsPacmanPackage(database));

            // pacman's local database: a folder per installed package, <name>-<version>-<release>.
            Directory.CreateDirectory(Path.Combine(database, "codalus-bin-0.10.0-1"));
            Assert.True(InstallInfo.IsPacmanPackage(database));
            Assert.StartsWith("curl -fsSL https://codalus.dev/install-arch.sh", InstallInfo.ArchInstallCommand, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(database, recursive: true);
        }
    }

    [Theory]
    [InlineData("0.2.0", "0.1.9", 1)]
    [InlineData("0.2.0", "0.2.0-nightly.20261011.1", 1)]
    [InlineData("0.2.0-nightly.20261011.2", "0.2.0-nightly.20261011.10", -1)]
    [InlineData("0.2.0-nightly.20261012.1", "0.2.0-nightly.20261011.9", 1)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("0.2.0+abc", "0.2.0", 0)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    public void Versions_compare_by_semver_precedence(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReleaseVersions.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(ReleaseVersions.Compare(b, a)));
    }

    [Theory]
    [InlineData(InstallKind.SelfUpdating, UpdateMode.Auto)]
    [InlineData(InstallKind.SelfUpdating, UpdateMode.Notify)]
    [InlineData(InstallKind.Portable, UpdateMode.Auto)]
    public async Task A_release_whose_runtime_isnt_installed_isnt_downloaded_until_it_is_or_the_user_says_so(InstallKind kind, UpdateMode mode)
    {
        // M49 (ADR 0080): the signed feed names the release's runtime; the shell says whether it's installed.
        var requires = Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse("12.0.0"), Codalus.Rpc.RollForwardPolicy.Major);
        var backend = new FakeBackend { Offer = s_next with { Requires = [requires] } };
        var installed = false;
        var asked = new List<string>();
        await using var service = new UpdateService(new InstallInfo("0.2.0", kind), backend, () => mode, () => "stable")
        {
            RuntimeCheck = offer =>
            {
                asked.Add(offer.Version);
                return new Codalus.Workbench.Setup.ToolStatus("dotnet-runtime", ".NET runtime for Codalus IDE", true, installed ? Codalus.Workbench.Setup.ToolState.Found : Codalus.Workbench.Setup.ToolState.Missing, "helpers")
                {
                    WantedVersion = "12.0.0",
                    WantedChannel = "12.0",
                    Found = "11.0.0 in /usr/local/share/dotnet",
                    Detail = "Codalus IDE's helper processes need Microsoft.NETCore.App 12.0.0 (rollForward Major), which isn't installed.",
                };
            },
        };

        var state = await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.NeedsRuntime, state.Status);
        Assert.Equal(("0.3.0", "12.0", "11.0.0 in /usr/local/share/dotnet"), (state.Offer?.Version, state.Runtime?.WantedChannel, state.Runtime?.Found));
        Assert.Contains("12.0.0", state.Error, StringComparison.Ordinal);
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(["0.3.0"], asked);

        // Installed now: the next check goes on as usual.
        installed = true;
        var after = await service.CheckAsync(userInitiated: true, TestContext.Current.CancellationToken);
        Assert.Equal(kind == InstallKind.SelfUpdating && mode == UpdateMode.Auto ? UpdateStatus.Ready : UpdateStatus.Available, after.Status);

        // A release whose feed names no runtime (from before M49) isn't asked about.
        asked.Clear();
        backend.Offer = s_next;
        await using var older = new UpdateService(new InstallInfo("0.2.0", kind), backend, () => mode, () => "stable") { RuntimeCheck = _ => throw new InvalidOperationException("not asked") };
        Assert.NotEqual(UpdateStatus.NeedsRuntime, (await older.CheckAsync(userInitiated: true, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Update_anyway_downloads_a_release_whose_runtime_isnt_installed()
    {
        var requires = Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse("12.0.0"), Codalus.Rpc.RollForwardPolicy.Major);
        var backend = new FakeBackend { Offer = s_next with { Requires = [requires] } };
        await using var service = new UpdateService(new InstallInfo("0.2.0", InstallKind.SelfUpdating), backend, () => UpdateMode.Auto, () => "stable")
        {
            RuntimeCheck = _ => new Codalus.Workbench.Setup.ToolStatus("dotnet-runtime", "runtime", true, Codalus.Workbench.Setup.ToolState.Missing, "helpers"),
        };
        var state = await service.CheckAsync(userInitiated: false, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.NeedsRuntime, state.Status);
        Assert.Equal(UpdateStatus.Ready, (await service.DownloadAsync(state.Offer!, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, backend.Downloads);
    }

    private static UpdateService Service(InstallKind kind, FakeBackend backend, UpdateMode mode = UpdateMode.Auto, string version = "0.2.0") =>
        new(new InstallInfo(version, kind), backend, () => mode, () => "stable");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public UpdateOffer? Offer { get; set; }

        public Exception? Fail { get; set; }

        public List<string> CheckedChannels { get; } = [];

        public List<bool> Downgrades { get; } = [];

        public int Downloads { get; private set; }

        public UpdateOffer? Applied { get; private set; }

        public bool Restarted { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken)
        {
            CheckedChannels.Add(channel);
            Downgrades.Add(allowDowngrade);
            return Fail is { } fail ? Task.FromException<UpdateOffer?>(fail) : Task.FromResult(Offer);
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            progress.Report(50);
            progress.Report(50); // repeated values aren't reported twice
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(UpdateOffer offer, bool restart, IReadOnlyList<string> arguments)
        {
            Applied = offer;
            Restarted = restart;
            Arguments = arguments;
        }
    }

    /// <summary>Captures the one timer the service creates, so a test fires it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public int Timers { get; private set; }

        public TimeSpan DueTime { get; private set; }

        public TimeSpan Period { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers++;
            (_callback, _state, DueTime, Period) = (callback, state, dueTime, period);
            return new NoTimer();
        }

        public void Fire() => _callback?.Invoke(_state);

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
