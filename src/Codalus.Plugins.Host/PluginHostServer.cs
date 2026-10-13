using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Codalus.Plugins.Host.Proposed;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;

namespace Codalus.Plugins.Host;

/// <summary>
/// The plugin host's side of the protocol (docs/plugin-protocol.md). Loads plugins, activates them, routes
/// command executions and tree requests to them, and forwards their UI calls to the shell. Outgoing
/// notifications go through one ordered queue so the shell sees them in the order the plugin produced them.
/// </summary>
internal sealed class PluginHostServer : IAsyncDisposable
{
    /// <summary>Reflection-based options for plugin-supplied values (the host runs on the JIT runtime).</summary>
    internal static readonly JsonSerializerOptions PluginValueOptions = new(JsonSerializerDefaults.Web);

    private readonly JsonRpcConnection _connection;
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string PluginId, Func<CommandInvocation, CancellationToken, Task<object?>> Handler)> _commands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ITreeAdapter> _trees = new(StringComparer.Ordinal);
    private readonly Channel<Func<Task>> _outgoing = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _outgoingLoop;

    public PluginHostServer(JsonRpcConnection connection)
    {
        _connection = connection;
        _outgoingLoop = Task.Run(PumpOutgoingAsync);
        Proposed = new ProposedHost(this);
    }

    public Task ExitRequested => _exit.Task;

    internal SettingsCache Settings { get; } = new();

    internal JsonRpcConnection Connection => _connection;

    /// <summary>The proposed APIs (tests, workspace, build, debug) shared by this host's plugins.</summary>
    internal ProposedHost Proposed { get; }

    public void RegisterHandlers()
    {
        var json = PluginJsonContext.Default;
        _connection.OnRequest(PluginMethods.Initialize, json.HostInitializeParams, json.HostInitializeResult, (p, _) =>
        {
            Settings.Replace(p.Settings);
            var version = typeof(PluginHostServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            return Task.FromResult(new HostInitializeResult(PluginMethods.ProtocolVersion, version));
        });
        _connection.OnRequest(PluginMethods.LoadPlugins, json.LoadPluginsParams, json.LoadPluginsResult, (p, _) =>
            Task.FromResult(new LoadPluginsResult([.. p.Plugins.Select(Load)])));
        _connection.OnRequest(PluginMethods.ActivatePlugin, json.ActivatePluginParams, json.ActivatePluginResult, ActivateAsync);
        _connection.OnRequest(PluginMethods.DeactivatePlugin, json.PluginIdParams, json.EmptyResult, async (p, ct) =>
        {
            if (_plugins.TryGetValue(p.Id, out var plugin))
            {
                await DeactivateAsync(plugin, ct).ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        _connection.OnRequest(PluginMethods.ExecuteCommand, json.ExecuteCommandParams, json.ExecuteCommandResult, ExecuteCommandAsync);
        _connection.OnRequest(PluginMethods.GetTreeChildren, json.GetTreeChildrenParams, json.GetTreeChildrenResult, async (p, ct) =>
        {
            if (!_trees.TryGetValue(p.ViewId, out var tree))
            {
                throw new JsonRpcHandlerException(PluginErrorCodes.ViewNotFound, $"No tree data provider is registered for '{p.ViewId}'.");
            }

            return new GetTreeChildrenResult([.. await tree.GetChildrenAsync(p.Handle, ct).ConfigureAwait(false)]);
        });
        _connection.OnNotification(PluginMethods.SettingsDidChange, json.SettingsDidChangeParams, p => Settings.Apply(p.Changes));
        _connection.OnRequest(PluginMethods.AuthGetSessions, json.AuthRequestParams, json.AuthSessionsResult, async (p, ct) =>
            new AuthSessionsResult([.. (await Authentication(p).GetSessionsAsync(p.Scopes, ct).ConfigureAwait(false)).Select(ToDto)]));
        _connection.OnRequest(PluginMethods.AuthCreateSession, json.AuthRequestParams, json.AuthSessionResult, async (p, ct) =>
            new AuthSessionResult(ToDto(await Authentication(p).CreateSessionAsync(p.Scopes ?? [], ct).ConfigureAwait(false))));
        _connection.OnRequest(PluginMethods.AuthRemoveSession, json.AuthRequestParams, json.EmptyResult, async (p, ct) =>
        {
            await Authentication(p).RemoveSessionAsync(p.SessionId ?? string.Empty, ct).ConfigureAwait(false);
            return new EmptyResult();
        });
        _connection.OnRequest(PluginMethods.FsStat, json.FsRequestParams, json.FsStatResult, (p, ct) => FsCallAsync(async () =>
        {
            var stat = await FileSystem(p).StatAsync(p.Path, ct).ConfigureAwait(false);
            return new FsStatResult(stat.Exists, stat.IsDirectory, stat.Size, stat.Modified);
        }));
        _connection.OnRequest(PluginMethods.FsReadDirectory, json.FsRequestParams, json.FsReadDirectoryResult, (p, ct) => FsCallAsync(async () =>
            new FsReadDirectoryResult([.. (await FileSystem(p).ReadDirectoryAsync(p.Path, ct).ConfigureAwait(false)).Select(e => new FsEntryDto(e.Name, e.IsDirectory, e.Size, e.Modified))])));
        _connection.OnRequest(PluginMethods.FsReadFile, json.FsRequestParams, json.FsReadFileResult, (p, ct) => FsCallAsync(async () =>
            new FsReadFileResult(await FileSystem(p).ReadFileAsync(p.Path, ct).ConfigureAwait(false))));
        _connection.OnRequest(PluginMethods.FsWriteFile, json.FsRequestParams, json.EmptyResult, (p, ct) => FsCallAsync(async () =>
        {
            await FileSystem(p).WriteFileAsync(p.Path, p.Content ?? [], ct).ConfigureAwait(false);
            return new EmptyResult();
        }));
        _connection.OnRequest(PluginMethods.FsCreateDirectory, json.FsRequestParams, json.EmptyResult, (p, ct) => FsCallAsync(async () =>
        {
            await FileSystem(p).CreateDirectoryAsync(p.Path, ct).ConfigureAwait(false);
            return new EmptyResult();
        }));
        _connection.OnRequest(PluginMethods.FsDelete, json.FsRequestParams, json.EmptyResult, (p, ct) => FsCallAsync(async () =>
        {
            await FileSystem(p).DeleteAsync(p.Path, p.Recursive, ct).ConfigureAwait(false);
            return new EmptyResult();
        }));
        _connection.OnRequest(PluginMethods.FsRename, json.FsRequestParams, json.EmptyResult, (p, ct) => FsCallAsync(async () =>
        {
            await FileSystem(p).RenameAsync(p.Path, p.Target ?? throw new JsonRpcHandlerException(FsErrors.Failed, "No target."), p.Overwrite, ct).ConfigureAwait(false);
            return new EmptyResult();
        }));
        _connection.OnRequest(PluginMethods.ViewEvent, json.ViewEventParams, json.EmptyResult, async (p, ct) =>
        {
            if (_viewHandlers.TryGetValue(p.ViewId, out var handler))
            {
                await handler(new ViewEvent(p.ElementId, p.Kind, p.Value, p.Row), ct).ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        _connection.OnRequest(PluginMethods.CustomEditorOpen, json.CustomEditorOpenParams, json.EmptyResult, async (p, ct) =>
        {
            if (!_customEditors.TryGetValue(p.ViewType, out var provider))
            {
                throw new JsonRpcHandlerException(PluginErrorCodes.PluginNotActivated, $"No custom editor '{p.ViewType}' is registered.");
            }

            _openCustomEditors[p.ViewId] = provider;
            await provider.OpenAsync(new CustomEditorDocument(p.ViewId, p.Path, p.Text), ct).ConfigureAwait(false);
            return new EmptyResult();
        });
        _connection.OnNotification(PluginMethods.CustomEditorClose, json.ViewIdParams, p =>
        {
            if (_openCustomEditors.TryRemove(p.ViewId, out var provider))
            {
                _ = provider.CloseAsync(p.ViewId, CancellationToken.None);
            }
        });
        _connection.OnNotification(PluginMethods.ProgressCancel, json.ProgressIdParams, p =>
        {
            if (_progress.TryGetValue(p.Id, out var cancel))
            {
                try
                {
                    cancel.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        });
        _connection.OnRequest(PluginMethods.Shutdown, json.EmptyParams, json.EmptyResult, async (_, ct) =>
        {
            foreach (var plugin in _plugins.Values)
            {
                await DeactivateAsync(plugin, ct).ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        _connection.OnNotification(PluginMethods.Exit, json.EmptyParams, _ => _exit.TrySetResult());
        Proposed.RegisterHandlers();
    }

    // ------------------------------------------------------------------ loading and activation

    private PluginLoadResult Load(PluginLoadEntry entry)
    {
        try
        {
            var assemblyPath = Path.GetFullPath(Path.Combine(entry.InstallPath, entry.Assembly));
            if (!File.Exists(assemblyPath))
            {
                return new PluginLoadResult(entry.Id, false, $"Assembly not found: {assemblyPath}");
            }

            var context = new PluginLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = FindEntryType(assembly, entry.EntryType);
            var instance = (ICodalusPlugin)Activator.CreateInstance(type)!;
            _plugins[entry.Id] = new LoadedPlugin(new PluginInfo(entry.Id, entry.Version, entry.InstallPath), instance, context) { EnableProposedApi = entry.EnableProposedApi };
            return new PluginLoadResult(entry.Id, true, null);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or TargetInvocationException or InvalidOperationException
                                       or MissingMethodException or TypeLoadException or ReflectionTypeLoadException or InvalidCastException)
        {
            return new PluginLoadResult(entry.Id, false, ex.Message);
        }
    }

    private static Type FindEntryType(Assembly assembly, string? typeName)
    {
        if (typeName is not null)
        {
            return assembly.GetType(typeName, throwOnError: true)!;
        }

        var candidates = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICodalusPlugin).IsAssignableFrom(t))
            .ToList();
        return candidates.Count == 1
            ? candidates[0]
            : throw new InvalidOperationException($"Expected exactly one public ICodalusPlugin implementation in {assembly.GetName().Name}, found {candidates.Count}. Set entry.type in the manifest.");
    }

    private async Task<ActivatePluginResult> ActivateAsync(ActivatePluginParams request, CancellationToken cancellationToken)
    {
        if (!_plugins.TryGetValue(request.Id, out var plugin))
        {
            throw new JsonRpcHandlerException(PluginErrorCodes.PluginNotFound, $"Plugin '{request.Id}' is not loaded.");
        }

        await plugin.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (plugin.Active)
            {
                return new ActivatePluginResult(true, null);
            }

            plugin.Context = new PluginContext(this, plugin);
            await plugin.Instance.ActivateAsync(plugin.Context, cancellationToken).ConfigureAwait(false);
            plugin.Active = true;
            return new ActivatePluginResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(request.Id, "error", $"Activation failed: {ex}");
            return new ActivatePluginResult(false, ex.Message);
        }
        finally
        {
            plugin.Gate.Release();
        }
    }

    private async Task DeactivateAsync(LoadedPlugin plugin, CancellationToken cancellationToken)
    {
        await plugin.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!plugin.Active)
            {
                return;
            }

            try
            {
                await plugin.Instance.DeactivateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(plugin.Info.Id, "error", $"Deactivation failed: {ex.Message}");
            }

            plugin.Context?.DisposeAll();
            plugin.Active = false;
        }
        finally
        {
            plugin.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ commands and trees

    internal IDisposable RegisterCommand(string pluginId, string commandId, Func<CommandInvocation, CancellationToken, Task<object?>> handler)
    {
        _commands[commandId] = (pluginId, handler);
        Post(PluginMethods.RegisterCommand, new CommandRegistrationParams(pluginId, commandId), PluginJsonContext.Default.CommandRegistrationParams);
        return new Disposable(() =>
        {
            if (_commands.TryRemove(new KeyValuePair<string, (string, Func<CommandInvocation, CancellationToken, Task<object?>>)>(commandId, (pluginId, handler))))
            {
                Post(PluginMethods.UnregisterCommand, new CommandRegistrationParams(pluginId, commandId), PluginJsonContext.Default.CommandRegistrationParams);
            }
        });
    }

    private async Task<ExecuteCommandResult> ExecuteCommandAsync(ExecuteCommandParams request, CancellationToken cancellationToken)
    {
        if (!_commands.TryGetValue(request.Command, out var entry))
        {
            throw new JsonRpcHandlerException(PluginErrorCodes.PluginNotActivated, $"Command '{request.Command}' has no registered handler.");
        }

        object? result;
        try
        {
            result = await entry.Handler(new CommandInvocation(request.Args), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(entry.PluginId, "error", $"Command '{request.Command}' failed: {ex}");
            throw new JsonRpcHandlerException(PluginErrorCodes.CommandFailed, ex.Message);
        }

        return new ExecuteCommandResult(result is null ? null : ToJson(result));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _progress = new(StringComparer.Ordinal);
    private long _nextProgress;

    internal string NewProgressId() => Interlocked.Increment(ref _nextProgress).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A running task's cancellation, for <c>window/progressCancel</c>.</summary>
    internal IDisposable RegisterProgress(string id, CancellationTokenSource cancel)
    {
        _progress[id] = cancel;
        return new Disposable(() => _progress.TryRemove(id, out _));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<ViewEvent, CancellationToken, Task>> _viewHandlers = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ICustomEditorProvider> _customEditors = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ICustomEditorProvider> _openCustomEditors = new(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IFileSystemProvider> _fileSystems = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAuthenticationProvider> _authentication = new(StringComparer.Ordinal);

    internal IDisposable RegisterAuthentication(string pluginId, string id, string label, IAuthenticationProvider provider)
    {
        if (!_authentication.TryAdd(id, provider))
        {
            throw new InvalidOperationException($"The authentication provider '{id}' is already offered.");
        }

        Post(PluginMethods.AuthRegister, new AuthProviderParams(pluginId, id, label), PluginJsonContext.Default.AuthProviderParams);
        return new Disposable(() =>
        {
            if (_authentication.TryRemove(new KeyValuePair<string, IAuthenticationProvider>(id, provider)))
            {
                Post(PluginMethods.AuthUnregister, new AuthProviderParams(pluginId, id, label), PluginJsonContext.Default.AuthProviderParams);
            }
        });
    }

    private IAuthenticationProvider Authentication(AuthRequestParams p) =>
        _authentication.TryGetValue(p.ProviderId, out var provider) ? provider : throw new JsonRpcHandlerException(PluginErrorCodes.PluginNotActivated, $"No authentication provider '{p.ProviderId}'.");

    private static AuthSessionDto ToDto(AuthenticationSession s) => new(s.Id, s.AccessToken, s.Account, [.. s.Scopes]);

    internal IDisposable RegisterFileSystem(string pluginId, string scheme, IFileSystemProvider provider)
    {
        if (!_fileSystems.TryAdd(scheme, provider))
        {
            throw new InvalidOperationException($"The scheme '{scheme}' is already served.");
        }

        Post(PluginMethods.FsRegister, new FsSchemeParams(pluginId, scheme), PluginJsonContext.Default.FsSchemeParams);
        return new Disposable(() =>
        {
            if (_fileSystems.TryRemove(new KeyValuePair<string, IFileSystemProvider>(scheme, provider)))
            {
                Post(PluginMethods.FsUnregister, new FsSchemeParams(pluginId, scheme), PluginJsonContext.Default.FsSchemeParams);
            }
        });
    }

    private IFileSystemProvider FileSystem(FsRequestParams p) =>
        _fileSystems.TryGetValue(p.Scheme, out var provider) ? provider : throw new JsonRpcHandlerException(PluginErrorCodes.PluginNotActivated, $"No file system serves '{p.Scheme}'.");

    /// <summary>A provider's call, with its file system errors as JSON-RPC errors the shell turns back into exceptions.</summary>
    private static async Task<T> FsCallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new JsonRpcHandlerException(FsErrors.NotFound, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new JsonRpcHandlerException(FsErrors.NotFound, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new JsonRpcHandlerException(FsErrors.Denied, ex.Message);
        }
        catch (IOException ex)
        {
            throw new JsonRpcHandlerException(FsErrors.Failed, ex.Message);
        }
    }

    internal IDisposable RegisterViewHandler(string viewId, Func<ViewEvent, CancellationToken, Task> handler)
    {
        _viewHandlers[viewId] = handler;
        return new Disposable(() => _viewHandlers.TryRemove(new KeyValuePair<string, Func<ViewEvent, CancellationToken, Task>>(viewId, handler)));
    }

    internal IDisposable RegisterCustomEditor(string viewType, ICustomEditorProvider provider)
    {
        _customEditors[viewType] = provider;
        return new Disposable(() => _customEditors.TryRemove(new KeyValuePair<string, ICustomEditorProvider>(viewType, provider)));
    }

    internal IDisposable RegisterTree(string viewId, ITreeAdapter adapter)
    {
        _trees[viewId] = adapter;
        return new Disposable(() => _trees.TryRemove(new KeyValuePair<string, ITreeAdapter>(viewId, adapter)));
    }

    internal static JsonElement ToJson(object? value) =>
        value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), PluginValueOptions);

    // ------------------------------------------------------------------ outgoing

    /// <summary>Queues a notification to the shell, preserving order.</summary>
    internal void Post<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        _outgoing.Writer.TryWrite(() => _connection.NotifyAsync(method, parameters, info, CancellationToken.None));

    internal void Log(string? pluginId, string level, string message) =>
        Post(PluginMethods.Log, new LogParams(pluginId, level, message), PluginJsonContext.Default.LogParams);

    /// <summary>Sends a request after everything queued before it has been written.</summary>
    internal async Task<TResult> RequestAsync<TParams, TResult>(
        string method, TParams parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo,
        CancellationToken cancellationToken)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _outgoing.Writer.TryWrite(() =>
        {
            flushed.TrySetResult();
            return Task.CompletedTask;
        });
        await flushed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpOutgoingAsync()
    {
        await foreach (var send in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await send().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionLostException)
            {
                // The shell went away; the host is about to exit.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Proposed.Dispose();
        _outgoing.Writer.TryComplete();
        await _outgoingLoop.ConfigureAwait(false);
        foreach (var plugin in _plugins.Values)
        {
            plugin.Gate.Dispose();
        }
    }

    internal sealed class LoadedPlugin(PluginInfo info, ICodalusPlugin instance, PluginLoadContext loadContext)
    {
        public PluginInfo Info { get; } = info;

        public ICodalusPlugin Instance { get; } = instance;

        public PluginLoadContext LoadContext { get; } = loadContext;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Active { get; set; }

        /// <summary>The manifest's <c>enableProposedApi</c>, allowed by the shell.</summary>
        public bool EnableProposedApi { get; init; }

        public PluginContext? Context { get; set; }
    }

    internal sealed class Disposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
