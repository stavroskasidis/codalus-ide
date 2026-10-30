using System.Text.Json;
using Codalus.Debugging;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// What applying the edits made of a debugged program: <see cref="HotReloadOutcome.Kind"/> is one of the constants.
/// <see cref="Browser"/> says what a Blazor Web App's pages made of them (null for other programs), and
/// <see cref="HandlerErrors"/> what the program's <c>[MetadataUpdateHandler]</c>s threw (ADR 0090).
/// </summary>
public sealed record HotReloadOutcome(string Kind, IReadOnlyList<HotReloadDiagnostic> Diagnostics, string? Message = null)
{
    public HotReloadBrowserOutcome? Browser { get; init; }

    public IReadOnlyList<string> HandlerErrors { get; init; } = [];

    /// <summary>Nothing changed since the last edit applied.</summary>
    public const string None = "none";

    /// <summary>Every Codalus debugger of the session took the edits.</summary>
    public const string Applied = "applied";

    /// <summary>Rude edits (<see cref="Diagnostics"/>): the program has to restart to run them.</summary>
    public const string Rude = "rude";

    /// <summary>The code doesn't compile (<see cref="Diagnostics"/>): nothing was applied.</summary>
    public const string Errors = "errors";

    /// <summary>A debugger refused the edits (<see cref="Message"/>: the runtime's reason).</summary>
    public const string Failed = "failed";

    /// <summary>No session takes edits (netcoredbg, no language service, the session ended).</summary>
    public const string Unavailable = "unavailable";

    public static HotReloadOutcome Of(string kind, string? message = null) => new(kind, [], message);
}

/// <summary>
/// What a Blazor Web App's pages made of an edit to code that runs in the browser (ADR 0090): <see cref="Unreachable"/>
/// when no channel reaches it (an older app or SDK); else how many pages were open, the errors of those that couldn't
/// apply it, and how many hadn't answered yet (their answers come later, <see cref="BrowserRefreshServer.LateAnswer"/>).
/// </summary>
public sealed record HotReloadBrowserOutcome(bool Unreachable, int Pages, IReadOnlyList<string> Errors, int Unanswered = 0);

/// <summary>
/// Hot Reload while debugging (M55, ADR 0086): while the Codalus debugger runs a program, a Hot Reload session of the
/// language host (Roslyn's Edit and Continue service) follows the program from the build it started from.
/// <see cref="ApplyAsync"/> asks it for the edits since, as deltas, and has every Codalus debugger of the debug
/// session apply them (<c>codalus/applyChanges</c>); the language host then makes the next deltas from them. Rude edits
/// and compile errors come back without anything applied. One apply at a time. Then the program's update handlers run
/// (<see cref="HotReloadAgents"/>), and a Blazor Web App's pages get the edits to code that runs in the browser
/// (<see cref="UseBrowser"/>, ADR 0090).
/// </summary>
public sealed class DebugHotReload : IAsyncDisposable
{
    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    /// <summary>How long an apply waits for the program's handlers, and for each page, to say they ran.</summary>
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(10);

    private readonly DebugService _debugger;
    private readonly Func<LanguageHostClient?> _client;
    private readonly HotReloadAgents _agents = new();
    private BrowserHotReload? _browser;

    /// <summary>Whether a debug session started with <see cref="_browser"/>: only its end disposes it (a late end of the one before doesn't).</summary>
    private bool _browserInUse;
    private readonly SemaphoreSlim _applying = new(1, 1);
    private readonly Lock _gate = new();
    private Task<string?>? _starting;
    private CancellationTokenSource? _startCancel;
    private string? _session;

    public DebugHotReload(DebugService debugger, Func<LanguageHostClient?> client)
    {
        _debugger = debugger;
        _client = client;
        debugger.HotReloadAgents = _agents;
        debugger.StateChanged += (_, _) => OnStateChanged();
        debugger.Ended += (_, _) => _ = EndAsync();
    }

    /// <summary>Whether the debug session takes edits: a Codalus debugger runs it and the language host follows it.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>The session started taking edits, or stopped (on any thread).</summary>
    public event EventHandler? ActiveChanged;

    /// <summary>The outcome of each apply that did something (on any thread).</summary>
    public event EventHandler<HotReloadOutcome>? Applied;

    /// <summary>
    /// The next debug session is a Blazor Web App's (ADR 0090): where its WebAssembly edits go, and what its browser's
    /// runtime can apply. Set before the session starts; the session's end disposes its server.
    /// </summary>
    public void UseBrowser(BrowserHotReload browser)
    {
        BrowserHotReload? previous;
        lock (_gate)
        {
            previous = _browser;
            _browser = browser;
            _browserInUse = false;
        }

        if (previous?.Server is { } server && !ReferenceEquals(server, browser.Server))
        {
            _ = server.DisposeAsync().AsTask();
        }
    }

    /// <summary>The session's browser channel (tests, the status bar), while one is used.</summary>
    public BrowserHotReload? Browser
    {
        get
        {
            lock (_gate)
            {
                return _browser;
            }
        }
    }

    private void OnStateChanged()
    {
        if (_debugger.State is not (DebugState.Running or DebugState.Stopped) || _debugger.CodalusAdapters.Count == 0)
        {
            return;
        }

        // Off the debugger's event loop, which handles the adapter's events one at a time.
        lock (_gate)
        {
            if (_starting is null)
            {
                _startCancel = new CancellationTokenSource();
                var cancel = _startCancel.Token;
                _starting = Task.Run(() => StartAsync(cancel));
                _browserInUse = _browser is not null;
            }
        }
    }

    /// <summary>
    /// Starts the language host's session as soon as the debug session runs: its baseline is the files as built, so
    /// an edit saved before it started couldn't be applied. The language host waits for the solution to load first.
    /// </summary>
    private async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_client() is not { } client || _debugger.CodalusAdapters is not [var adapter, ..])
            {
                return null;
            }

            var capabilities = (await adapter.Connection.RequestAsync("codalus/hotReloadCapabilities", JsonElement.Parse("{}"), s_json.JsonElement, s_json.HotReloadCapabilitiesResponse, cancellationToken).ConfigureAwait(false)).Capabilities;
            if (capabilities.Length == 0)
            {
                return null;
            }

            // A Blazor Web App's edits apply in the browser too: only what both runtimes can apply.
            if (Browser?.Capabilities is { } browser)
            {
                capabilities = [.. capabilities.Intersect(browser, StringComparer.Ordinal)];
            }

            var session = "debug-" + Guid.NewGuid().ToString("N");
            if (!await client.HotReloadStartAsync(session, [], capabilities, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            lock (_gate)
            {
                _session = session;
            }

            ActiveChanged?.Invoke(this, EventArgs.Empty);
            return session;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or IOException or InvalidOperationException or OperationCanceledException or Rpc.JsonRpcRemoteException)
        {
            return null;
        }
    }

    /// <summary>Applies the edits made since the last one to the program (Apply Code Changes, Continue, a save).</summary>
    public async Task<HotReloadOutcome> ApplyAsync(CancellationToken cancellationToken)
    {
        Task<string?>? starting;
        lock (_gate)
        {
            starting = _starting;
        }

        // A session still starting (the language host waits for the solution to load) has nothing to apply yet, and
        // Continue doesn't wait for it.
        if (starting is not { IsCompleted: true } || await starting.ConfigureAwait(false) is not { } session || _client() is not { } client)
        {
            return HotReloadOutcome.Of(HotReloadOutcome.Unavailable);
        }

        await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var update = await client.HotReloadUpdateAsync(session, cancellationToken).ConfigureAwait(false);
            HotReloadOutcome outcome;
            if (update.Status == HotReloadUpdateResult.Blocked)
            {
                outcome = new HotReloadOutcome(HotReloadOutcome.Errors, update.Errors);
            }
            else if (update.RudeEdits.Length > 0 || update.ProjectsToRestart.Length > 0)
            {
                await client.HotReloadDiscardAsync(session, cancellationToken).ConfigureAwait(false);
                outcome = new HotReloadOutcome(HotReloadOutcome.Rude, update.RudeEdits);
            }
            else if (update.Status == HotReloadUpdateResult.None || update.Updates.Length == 0)
            {
                return HotReloadOutcome.Of(HotReloadOutcome.None);
            }
            else
            {
                outcome = await ApplyToAdaptersAsync(update, cancellationToken).ConfigureAwait(false);
                await (outcome.Kind is HotReloadOutcome.Applied or HotReloadOutcome.None
                    ? client.HotReloadCommitAsync(session, cancellationToken)
                    : client.HotReloadDiscardAsync(session, cancellationToken)).ConfigureAwait(false);
                if (outcome.Kind == HotReloadOutcome.None)
                {
                    return outcome;
                }

                if (outcome.Kind == HotReloadOutcome.Applied)
                {
                    outcome = await ApplyToApplicationAsync(update.Updates, outcome, cancellationToken).ConfigureAwait(false);
                }
            }

            Applied?.Invoke(this, outcome);
            return outcome;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or IOException or InvalidOperationException or Rpc.JsonRpcRemoteException)
        {
            var failed = HotReloadOutcome.Of(HotReloadOutcome.Failed, ex.Message);
            Applied?.Invoke(this, failed);
            return failed;
        }
        finally
        {
            _applying.Release();
        }
    }

    /// <summary>Every Codalus debugger of the session takes the deltas; a module a program doesn't have is skipped.</summary>
    private async Task<HotReloadOutcome> ApplyToAdaptersAsync(HotReloadUpdateResult update, CancellationToken cancellationToken)
    {
        var arguments = new ApplyChangesArguments([.. update.Updates.Select(u => new ModuleDelta(u.ModuleId, u.MetadataDelta, u.IlDelta, u.PdbDelta))]);
        var applied = false;
        foreach (var adapter in _debugger.CodalusAdapters)
        {
            var response = await adapter.Connection.RequestAsync("codalus/applyChanges", arguments, s_json.ApplyChangesArguments, s_json.ApplyChangesResponse, cancellationToken).ConfigureAwait(false);
            if (response.Results.FirstOrDefault(r => r.Status == ModuleDeltaResult.Failed) is { } failed)
            {
                return HotReloadOutcome.Of(HotReloadOutcome.Failed, failed.Message);
            }

            applied |= response.Results.Any(r => r.Status is ModuleDeltaResult.Applied or ModuleDeltaResult.Pending);
        }

        // No Codalus debugger in the session took them: the next update is made from this one all the same.
        return applied ? HotReloadOutcome.Of(HotReloadOutcome.Applied) : HotReloadOutcome.Of(HotReloadOutcome.None);
    }

    /// <summary>
    /// After the debugger applied an edit (ADR 0090): the program's <c>[MetadataUpdateHandler]</c>s run, through its
    /// agent, so its UI shows the new code (Blazor re-renders its components); a Blazor Web App's pages then get the
    /// deltas of the code that runs in the browser and refresh what the server rendered. A program stopped at a
    /// breakpoint runs its handlers when it goes on: the apply doesn't wait for them then.
    /// </summary>
    private async Task<HotReloadOutcome> ApplyToApplicationAsync(IReadOnlyList<HotReloadModuleUpdate> updates, HotReloadOutcome outcome, CancellationToken cancellationToken)
    {
        var stopped = _debugger.State == DebugState.Stopped;
        var handlerErrors = await _agents.NotifyAsync(updates, stopped ? TimeSpan.Zero : s_answerWait, cancellationToken).ConfigureAwait(false);
        outcome = outcome with { HandlerErrors = handlerErrors };
        if (Browser is not { } browser)
        {
            return outcome;
        }

        var inBrowser = updates.Where(u => browser.Projects.Contains(u.Project, SolutionService.PathComparer)).ToList();
        if (browser.Server is not { } server)
        {
            return inBrowser.Count == 0 ? outcome : outcome with { Browser = new HotReloadBrowserOutcome(Unreachable: true, 0, []) };
        }

        // Stopped, a page paused at a breakpoint answers once it goes on: it isn't waited for. A page that doesn't answer in
        // time isn't taken for a failure either: its answer comes later.
        var result = inBrowser.Count == 0 ? null : await server.ApplyAsync(inBrowser, stopped ? TimeSpan.Zero : s_answerWait, cancellationToken).ConfigureAwait(false);
        if (!stopped)
        {
            await server.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        // An edit to the server only (the pages refreshed what it renders) says nothing of the browser.
        return result is null ? outcome : outcome with { Browser = new HotReloadBrowserOutcome(Unreachable: false, result.Pages, result.Errors, result.Unanswered) };
    }

    private async Task EndAsync()
    {
        Task<string?>? starting;
        CancellationTokenSource? cancel;
        BrowserHotReload? browser;
        lock (_gate)
        {
            starting = _starting;
            cancel = _startCancel;
            browser = _browserInUse ? _browser : null;
            _starting = null;
            _startCancel = null;
            _session = null;
            if (_browserInUse)
            {
                _browser = null;
                _browserInUse = false;
            }
        }

        if (browser?.Server is { } server)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (cancel is not null)
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            cancel.Dispose();
        }

        if (starting is not null && await starting.ConfigureAwait(false) is { } session && _client() is { } client)
        {
            try
            {
                await client.HotReloadEndAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or Rpc.JsonRpcRemoteException)
            {
            }

            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await EndAsync().ConfigureAwait(false);
        await _agents.DisposeAsync().ConfigureAwait(false);
        _applying.Dispose();
    }
}
