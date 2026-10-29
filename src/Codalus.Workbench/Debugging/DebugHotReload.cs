using System.Text.Json;
using Codalus.Debugging;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Workbench.Debugging;

/// <summary>What applying the edits made of a debugged program: <see cref="HotReloadOutcome.Kind"/> is one of the constants.</summary>
public sealed record HotReloadOutcome(string Kind, IReadOnlyList<HotReloadDiagnostic> Diagnostics, string? Message = null)
{
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
/// Hot Reload while debugging (M55, ADR 0086): while the Codalus debugger runs a program, a Hot Reload session of the
/// language host (Roslyn's Edit and Continue service) follows the program from the build it started from.
/// <see cref="ApplyAsync"/> asks it for the edits since, as deltas, and has every Codalus debugger of the debug
/// session apply them (<c>codalus/applyChanges</c>); the language host then makes the next deltas from them. Rude edits
/// and compile errors come back without anything applied. One apply at a time.
/// </summary>
public sealed class DebugHotReload : IAsyncDisposable
{
    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private readonly DebugService _debugger;
    private readonly Func<LanguageHostClient?> _client;
    private readonly SemaphoreSlim _applying = new(1, 1);
    private readonly Lock _gate = new();
    private Task<string?>? _starting;
    private string? _session;

    public DebugHotReload(DebugService debugger, Func<LanguageHostClient?> client)
    {
        _debugger = debugger;
        _client = client;
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

    private void OnStateChanged()
    {
        if (_debugger.State is not (DebugState.Running or DebugState.Stopped) || _debugger.CodalusAdapters.Count == 0)
        {
            return;
        }

        // Off the debugger's event loop, which handles the adapter's events one at a time.
        lock (_gate)
        {
            _starting ??= Task.Run(StartAsync);
        }
    }

    /// <summary>
    /// Starts the language host's session as soon as the debug session runs: its baseline is the files as built, so
    /// an edit saved before it started couldn't be applied.
    /// </summary>
    private async Task<string?> StartAsync()
    {
        try
        {
            if (_client() is not { } client || _debugger.CodalusAdapters is not [var adapter, ..])
            {
                return null;
            }

            var capabilities = await adapter.Connection.RequestAsync("codalus/hotReloadCapabilities", JsonElement.Parse("{}"), s_json.JsonElement, s_json.HotReloadCapabilitiesResponse).ConfigureAwait(false);
            if (capabilities.Capabilities.Length == 0)
            {
                return null;
            }

            var session = "debug-" + Guid.NewGuid().ToString("N");
            if (!await client.HotReloadStartAsync(session, [], capabilities.Capabilities, CancellationToken.None).ConfigureAwait(false))
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

        if (starting is null || await starting.ConfigureAwait(false) is not { } session || _client() is not { } client)
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

    private async Task EndAsync()
    {
        Task<string?>? starting;
        lock (_gate)
        {
            starting = _starting;
            _starting = null;
            _session = null;
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
        _applying.Dispose();
    }
}
