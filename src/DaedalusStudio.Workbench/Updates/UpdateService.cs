namespace DaedalusStudio.Workbench.Updates;

/// <summary>How the running IDE was installed (M32, ADR 0029): what it can do about a newer release.</summary>
public enum InstallKind
{
    /// <summary>A development build (<c>X.Y.Z-dev</c>, <c>dotnet run</c>): it never checks.</summary>
    Development,

    /// <summary>Installed by Velopack (the Setup.exe, the .pkg, the AppImage, winget): it updates itself.</summary>
    SelfUpdating,

    /// <summary>A .deb or .rpm: the system's package manager updates it; the IDE only says a release is out.</summary>
    SystemPackage,

    /// <summary>The zip, the .app zip or the tar.gz: the IDE says a release is out, with a link to it.</summary>
    Portable,
}

/// <summary>The <c>update.mode</c> setting.</summary>
public enum UpdateMode
{
    /// <summary>Check, download in the background, and offer a restart (the default).</summary>
    Auto,

    /// <summary>Check and say so; download only when asked.</summary>
    Notify,

    /// <summary>No update traffic at all (a check the user asks for still runs).</summary>
    Off,
}

/// <summary>What the IDE knows about itself.</summary>
public sealed record InstallInfo(string Version, InstallKind Kind)
{
    public const string Stable = "stable";
    public const string Nightly = "nightly";

    /// <summary>
    /// The channel this build came from: an install's own (Velopack knows it), else nightlies for a nightly build and stable
    /// releases for everything else.
    /// </summary>
    public string DefaultChannel { get; init; } = ChannelOf(Version);

    public static string ChannelOf(string version) => version.Contains("-nightly.", StringComparison.Ordinal) ? Nightly : Stable;

    public static InstallKind KindOf(string version, bool velopackInstalled, bool systemPackage) =>
        version.EndsWith("-dev", StringComparison.Ordinal) || version.Contains("-dev+", StringComparison.Ordinal) ? InstallKind.Development
        : velopackInstalled ? InstallKind.SelfUpdating
        : systemPackage ? InstallKind.SystemPackage
        : InstallKind.Portable;
}

/// <summary>A release on the channel: newer than this build, or (when switching to stable) the newest one at all.</summary>
public sealed record UpdateOffer(string Version, string Channel, bool IsDowngrade, string? Notes, Uri? ReleasePage);

/// <summary>Finds, downloads and applies releases (the shell's Velopack client; fakes in tests).</summary>
public interface IUpdateBackend
{
    /// <summary>The newest release on <paramref name="channel"/> newer than this build (any version when <paramref name="allowDowngrade"/>), or null.</summary>
    Task<UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken);

    /// <summary>Downloads the offer in the background (self-updating installs only).</summary>
    Task DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken);

    /// <summary>Applies the downloaded offer once the IDE has exited, and starts it again with <paramref name="arguments"/> when asked.</summary>
    void ApplyAfterExit(UpdateOffer offer, bool restart, IReadOnlyList<string> arguments);
}

public enum UpdateStatus
{
    Idle,
    Disabled,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Failed,
}

public sealed record UpdateState(UpdateStatus Status, UpdateOffer? Offer = null, int Progress = 0, string? Error = null);

/// <summary>
/// Self-update (M32, D16): checks after startup and every four hours, downloads in the background and has the update
/// applied when the IDE next restarts. What it does depends on the kind of install and the <c>update.mode</c> setting;
/// the shell shows the state (a "Restart to update" status bar item, notifications).
/// </summary>
public sealed class UpdateService : IAsyncDisposable
{
    public const string ModeSetting = "update.mode";
    public const string ChannelSetting = "update.channel";

    /// <summary>How often a running IDE checks again.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private readonly IUpdateBackend _backend;
    private readonly Func<UpdateMode> _mode;
    private readonly Func<string> _channel;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ITimer? _timer;
    private UpdateState _state;

    /// <param name="channel">The <c>update.channel</c> setting, or <see cref="InstallInfo.DefaultChannel"/> when it isn't set.</param>
    public UpdateService(InstallInfo install, IUpdateBackend backend, Func<UpdateMode> mode, Func<string> channel, TimeProvider? time = null)
    {
        Install = install;
        _backend = backend;
        _mode = mode;
        _channel = channel;
        _time = time ?? TimeProvider.System;
        _state = new UpdateState(install.Kind == InstallKind.Development ? UpdateStatus.Disabled : UpdateStatus.Idle);
    }

    public InstallInfo Install { get; }

    public UpdateState State => _state;

    /// <summary>Raised on any thread: marshal to the UI (<c>Ui.Post</c>).</summary>
    public event EventHandler<UpdateState>? StateChanged;

    /// <summary>A failure while checking or downloading, for the log (a check the user didn't ask for fails quietly).</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Starts the schedule: a check now (the shell calls this once it is interactive), then every <see cref="Interval"/>.</summary>
    public void Start()
    {
        if (Install.Kind == InstallKind.Development || _timer is not null)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _ = CheckAsync(userInitiated: false, _lifetime.Token), null, TimeSpan.Zero, Interval);
    }

    /// <summary>
    /// Checks the channel. A self-updating install in <see cref="UpdateMode.Auto"/> downloads what it finds; anything else
    /// reports it as <see cref="UpdateStatus.Available"/>. With <see cref="UpdateMode.Off"/> only a check the user asks for runs.
    /// </summary>
    public async Task<UpdateState> CheckAsync(bool userInitiated, CancellationToken cancellationToken)
    {
        if (Install.Kind == InstallKind.Development)
        {
            return Set(new UpdateState(UpdateStatus.Disabled, Error: "Development builds don't update themselves."));
        }

        if (!userInitiated && _mode() == UpdateMode.Off)
        {
            return _state;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (!await _gate.WaitAsync(0, linked.Token).ConfigureAwait(false))
        {
            return _state; // a check or download is under way
        }

        try
        {
            if (_state.Status is UpdateStatus.Ready or UpdateStatus.Downloading)
            {
                return _state; // one update at a time; it is applied at the next restart
            }

            Set(new UpdateState(UpdateStatus.Checking));
            var offer = await _backend.CheckAsync(_channel(), allowDowngrade: false, linked.Token).ConfigureAwait(false);
            if (offer is null)
            {
                return Set(new UpdateState(UpdateStatus.UpToDate));
            }

            return Install.Kind == InstallKind.SelfUpdating && _mode() == UpdateMode.Auto
                ? await DownloadCoreAsync(offer, linked.Token).ConfigureAwait(false)
                : Set(new UpdateState(UpdateStatus.Available, offer));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(this, ex);
            return Set(new UpdateState(UpdateStatus.Failed, Error: ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Downloads an offer the user chose to take (<see cref="UpdateMode.Notify"/>, or a channel switch).</summary>
    public async Task<UpdateState> DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        if (Install.Kind != InstallKind.SelfUpdating)
        {
            throw new InvalidOperationException("Only an installed build can download its updates.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(offer, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(this, ex);
            return Set(new UpdateState(UpdateStatus.Failed, offer, Error: ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// After <c>update.channel</c> changed: the newest release of the new channel. Moving from nightlies to stable releases
    /// usually means an older version: <paramref name="confirmDowngrade"/> asks first (D16).
    /// </summary>
    public async Task<UpdateState> SwitchChannelAsync(string channel, Func<UpdateOffer, Task<bool>> confirmDowngrade, CancellationToken cancellationToken)
    {
        if (Install.Kind == InstallKind.Development)
        {
            return _state;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        UpdateOffer? offer;
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Set(new UpdateState(UpdateStatus.Checking));
            offer = await _backend.CheckAsync(channel, allowDowngrade: true, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(this, ex);
            return Set(new UpdateState(UpdateStatus.Failed, Error: ex.Message));
        }
        finally
        {
            _gate.Release();
        }

        if (offer is null || offer.Version == Install.Version)
        {
            return Set(new UpdateState(UpdateStatus.UpToDate));
        }

        if (offer.IsDowngrade && !await confirmDowngrade(offer).ConfigureAwait(false))
        {
            return Set(new UpdateState(UpdateStatus.Idle));
        }

        return Install.Kind == InstallKind.SelfUpdating
            ? await DownloadAsync(offer, linked.Token).ConfigureAwait(false)
            : Set(new UpdateState(UpdateStatus.Available, offer));
    }

    /// <summary>
    /// Has a downloaded update applied once the IDE exits (and the IDE started again with <paramref name="arguments"/> when
    /// <paramref name="restart"/>). False when there is nothing to apply.
    /// </summary>
    public bool ApplyOnExit(bool restart, IReadOnlyList<string> arguments)
    {
        if (_state is not { Status: UpdateStatus.Ready, Offer: { } offer })
        {
            return false;
        }

        _backend.ApplyAfterExit(offer, restart, arguments);
        return true;
    }

    private async Task<UpdateState> DownloadCoreAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        Set(new UpdateState(UpdateStatus.Downloading, offer));
        var progress = new Progress(this, offer);
        await _backend.DownloadAsync(offer, progress, cancellationToken).ConfigureAwait(false);
        return Set(new UpdateState(UpdateStatus.Ready, offer, 100));
    }

    private UpdateState Set(UpdateState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
        return state;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
        _gate.Dispose();
    }

    /// <summary>Reports download progress as state changes (whole percents only, so the status bar isn't flooded).</summary>
    private sealed class Progress(UpdateService service, UpdateOffer offer) : IProgress<int>
    {
        private int _last = -1;

        public void Report(int value)
        {
            if (Interlocked.Exchange(ref _last, value) != value && service._state.Status == UpdateStatus.Downloading)
            {
                service.Set(new UpdateState(UpdateStatus.Downloading, offer, value));
            }
        }
    }
}
