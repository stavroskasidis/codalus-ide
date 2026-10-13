using System.Collections.Concurrent;
using System.Text.Json;
using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Languages;

/// <summary>An open document handed to a language service. <see cref="Text"/> reads the editor's current text.</summary>
public sealed record LanguageDocument(string Path, string LanguageId, Func<(string Text, int Version)> Text);

/// <summary>
/// Connects editors and the project system to language services (ADR 0005). It starts a service's host on demand
/// (the first file of its language opens, or the first design-time build of its projects arrives), forwards the
/// build host's compiler inputs as project data, keeps open documents in sync, and collects diagnostics. UI-agnostic:
/// events arrive on any thread.
/// </summary>
public sealed class LanguageService : IAsyncDisposable
{
    private readonly LanguageServiceRegistry _registry;
    private readonly SolutionService _solutions;
    private readonly SupervisionPolicy? _policy;
    private readonly ConcurrentDictionary<string, Lazy<Task<LanguageHostClient?>>> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LanguageDocument> _documents = new(SolutionService.PathComparer);
    private readonly ConcurrentDictionary<string, ProjectData> _projects = new(SolutionService.PathComparer);
    private readonly ConcurrentDictionary<string, WorkspaceStatusParams> _status = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<(string Server, string Path), CancellationTokenSource> _pulls = new();
    private readonly LanguageRegistry? _languages;
    private JsonElement? _settings;

    /// <param name="languages">The language of a file (ADR 0016); without it, files map to services by extension.</param>
    public LanguageService(LanguageServiceRegistry registry, SolutionService solutions, SupervisionPolicy? policy = null, LanguageRegistry? languages = null)
    {
        _registry = registry;
        _solutions = solutions;
        _policy = policy;
        _languages = languages;
        _solutions.CompilerInputsChanged += OnCompilerInputs;
        _solutions.SolutionChanged += OnSolutionChanged;
    }

    public DiagnosticsStore Diagnostics { get; } = new();

    public LanguageServiceRegistry Registry => _registry;

    /// <summary>A language service reported its loading state (status bar, benchmarks).</summary>
    public event EventHandler<(string LanguageId, WorkspaceStatusParams Status)>? StatusChanged;

    public event EventHandler<string>? Log;

    /// <summary>
    /// Where language hosts run: null here, or a remote window's server (M21, ADR 0050), which returns null for a host
    /// that can't run there.
    /// </summary>
    public Func<Rpc.ProcessLaunchInfo, Rpc.ProcessLaunchInfo?>? Placement { get; set; }

    /// <summary>A language host crashed: (language, whether it restarts).</summary>
    public event EventHandler<(string LanguageId, bool WillRestart)>? HostCrashed;

    public event EventHandler<string>? HostRestarted;

    public WorkspaceStatusParams? Status(string languageId) => _status.GetValueOrDefault(languageId);

    /// <summary>Each started service's process and what its connection waits for (diagnostics).</summary>
    public IReadOnlyList<string> Describe() =>
        [.. _clients.Where(c => c.Value.IsValueCreated).Select(c => c.Value.Value.IsCompletedSuccessfully && c.Value.Value.Result is { } client
            ? $"{c.Key}: process {client.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {(client.IsRunning ? "running" : "not running")}; {client.Describe()}"
            : $"{c.Key}: {(c.Value.Value.IsCompleted ? "not started" : "starting")}")];

    /// <summary>The process id of a running host (tests, diagnostics).</summary>
    public int? HostProcessId(string languageId) =>
        _clients.TryGetValue(languageId, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result?.ProcessId : null;

    public string? LanguageIdOf(string path) =>
        _languages?.ForFile(path, () => LanguageRegistry.ReadFirstLine(path))?.Id ?? _registry.ForFile(path)?.LanguageId;

    /// <summary>A project's evaluated property and where it is set (MSBuild hover in the markup host).</summary>
    public Func<string, string, CancellationToken, Task<(string? Value, string? Origin)?>>? PropertyEvaluator { get; set; }

    /// <summary>Applies an edit a server asks for (<c>workspace/applyEdit</c>), e.g. after a linter's quick fix runs.</summary>
    public Func<WorkspaceEdit, Task<bool>>? ApplyEditHandler { get; set; }

    /// <summary>Settings for <c>workspace/configuration</c> of servers that declare a settings section.</summary>
    public Func<string?, JsonElement?>? SettingsProvider { get; set; }

    /// <summary>The service (server) id of a document language, or null when no server serves it.</summary>
    private string? ServerOf(string languageId) => (_registry.ForLanguage(languageId) ?? _registry.Get(languageId))?.LanguageId;

    /// <summary>The compiler inputs of a project from its last design-time build, or null.</summary>
    public ProjectData? Project(string path) => _projects.GetValueOrDefault(path);

    /// <summary>The client of a language's service, starting it if needed; null when it can't be started.</summary>
    public Task<LanguageHostClient?> ClientAsync(string languageId) =>
        ServerOf(languageId) is { } server
            ? _clients.GetOrAdd(server, id => new Lazy<Task<LanguageHostClient?>>(() => StartAsync(id))).Value
            : Task.FromResult<LanguageHostClient?>(null);

    /// <summary>
    /// A service that couldn't start (its launch returned null, e.g. a server not installed yet) is tried again, and the
    /// open documents of its languages are opened in it. Returns whether it runs now.
    /// </summary>
    public async Task<bool> RetryAsync(string languageId)
    {
        if (ServerOf(languageId) is not { } server)
        {
            return false;
        }

        if (_clients.TryGetValue(server, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompleted && (!lazy.Value.IsCompletedSuccessfully || lazy.Value.Result is null))
        {
            _clients.TryRemove(server, out _);
        }

        if (await ClientAsync(languageId).ConfigureAwait(false) is not { } client)
        {
            return false;
        }

        var descriptor = _registry.Get(server);
        foreach (var document in _documents.Values.Where(d => descriptor is { IsLinter: true } ? Takes(descriptor, d) : ServerOf(d.LanguageId) == server))
        {
            await client.DidOpenAsync(State(document), _lifetime.Token).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Whether a linter takes a document: one of its languages, and a file its configuration covers.</summary>
    private static bool Takes(LanguageServiceDescriptor linter, LanguageDocument document) =>
        linter.Serves(document.LanguageId) && (linter.Accepts?.Invoke(document.Path) ?? true);

    /// <summary>The running linters (ADR 0027) of an open document, for its quick fixes.</summary>
    public IReadOnlyList<LanguageHostClient> RunningLinters(string path) =>
        _documents.TryGetValue(path, out var document)
            ? [.. _registry.LintersFor(document.LanguageId).Where(l => Takes(l, document)).Select(l => RunningClient(l.LanguageId)).OfType<LanguageHostClient>()]
            : [];

    /// <summary>The running client of a language's service, without starting it.</summary>
    public LanguageHostClient? RunningClient(string languageId) =>
        ServerOf(languageId) is { } server && _clients.TryGetValue(server, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    /// <summary>Server settings for every language service (e.g. <c>{ "analysisScope": "solution" }</c>).</summary>
    public async Task ConfigureAsync(JsonElement settings)
    {
        _settings = settings;
        foreach (var lazy in _clients.Values.Where(l => l.IsValueCreated))
        {
            if (await lazy.Value.ConfigureAwait(false) is { } client)
            {
                await client.ConfigureAsync(settings, _lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<LanguageHostClient?> StartAsync(string languageId)
    {
        if (_registry.Get(languageId) is not { } descriptor || descriptor.Launch() is not { } launch)
        {
            Log?.Invoke(this, $"[{languageId}] No language service host was found.");
            return null;
        }

        if (Placement is { } place)
        {
            // A remote window (M21): the hosts that read the workspace run on the server; the others don't run.
            if (place(launch) is not { } placed)
            {
                Log?.Invoke(this, $"[{languageId}] {descriptor.DisplayName} doesn't run in a remote window yet.");
                return null;
            }

            launch = placed;
        }

        var client = new LanguageHostClient(descriptor.DisplayName + " language service", launch, policy: _policy)
        {
            ProjectsProvider = () => [.. _projects.Values.Where(p => p.Language == descriptor.ProjectLanguage)],
            OpenDocumentsProvider = () => [.. _documents.Values.Where(d => descriptor.IsLinter ? Takes(descriptor, d) : descriptor.Serves(d.LanguageId)).Select(State)],
            InitializationOptions = descriptor.InitializationOptions,
            ScopedConfigurationProvider = descriptor.ScopedConfiguration,
            AcceptedRequests = descriptor.AcceptedRequests,
            ApplyEditHandler = edit => ApplyEditHandler?.Invoke(edit) ?? Task.FromResult(false),
            RootUri = _solutions.Solution?.Directory is { } solution ? DocumentUris.FromPath(solution) : null,
            ConfigurationProvider = section => SettingsProvider?.Invoke(section ?? descriptor.SettingsSection),
            PropertyEvaluator = (project, name, ct) => PropertyEvaluator?.Invoke(project, name, ct) ?? Task.FromResult<(string?, string?)?>(null),
        };
        client.Diagnostics += (_, p) =>
        {
            // The HTML of a Razor file (ADR 0027) has blanks where its C# was: its diagnostics would be false alarms.
            if (DocumentUris.ToPath(p.Uri) is { } path && !path.EndsWith(RazorHtmlProjection.VirtualSuffix, StringComparison.Ordinal))
            {
                Diagnostics.Set(path, languageId, p.Version, p.Diagnostics);
            }
        };
        client.DiagnosticsRefreshRequested += (_, _) =>
        {
            foreach (var document in _documents.Values.Where(d => descriptor.IsLinter ? Takes(descriptor, d) : ServerOf(d.LanguageId) == languageId))
            {
                SchedulePull(languageId, client, document.Path);
            }
        };
        client.WorkspaceStatus += (_, status) =>
        {
            _status[languageId] = status;
            StatusChanged?.Invoke(this, (languageId, status));
        };
        client.Log += (_, line) => Log?.Invoke(this, $"[{languageId}] {line}");
        client.Crashed += (_, e) =>
        {
            if (!e.WillRestart)
            {
                Diagnostics.Clear(languageId);
            }

            HostCrashed?.Invoke(this, (languageId, e.WillRestart));
        };
        client.Restarted += (_, _) => HostRestarted?.Invoke(this, languageId);

        // Each stage is logged when the start takes long, so a client that never becomes usable says where it stopped.
        var stage = "starting the host";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var watchdog = new Timer(_ => Log?.Invoke(this, $"[{languageId}] Still {stage} after {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0} s."), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        try
        {
            await client.StartAsync(_lifetime.Token).ConfigureAwait(false);
            if (_settings is { } settings)
            {
                stage = "sending the settings";
                await client.ConfigureAsync(settings, _lifetime.Token).ConfigureAwait(false);
            }

            if (descriptor.ProjectLanguage is { } language && _projects.Values.Where(p => p.Language == language).ToList() is { Count: > 0 } projects)
            {
                stage = "sending the projects";
                await client.UpdateProjectsAsync(projects, _lifetime.Token).ConfigureAwait(false);
            }

            stage = "started";
            return client;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonRpcConnectionLostException)
        {
            Log?.Invoke(this, $"[{languageId}] The language service could not start: {ex.Message}");
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private static OpenDocumentState State(LanguageDocument document)
    {
        var (text, version) = document.Text();
        return new OpenDocumentState(DocumentUris.FromPath(document.Path), document.LanguageId, version, text);
    }

    // ------------------------------------------------------------------ documents

    /// <summary>An editor opened a document of a registered language. Returns false when no service owns it.</summary>
    public async Task<bool> OpenAsync(LanguageDocument document)
    {
        _documents[document.Path] = document;
        foreach (var linter in _registry.LintersFor(document.LanguageId).Where(l => Takes(l, document)))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await ClientAsync(linter.LanguageId).ConfigureAwait(false) is { } lint && _documents.ContainsKey(document.Path))
                    {
                        await lint.DidOpenAsync(State(document), _lifetime.Token).ConfigureAwait(false);
                        SchedulePull(linter.LanguageId, lint, document.Path);
                    }
                }
                catch (Exception ex) when (ex is JsonRpcConnectionLostException or OperationCanceledException or InvalidOperationException or IOException)
                {
                    Log?.Invoke(this, $"[{linter.LanguageId}] {document.Path}: {ex.Message}");
                }
            });
        }

        if (await ClientAsync(document.LanguageId).ConfigureAwait(false) is not { } client)
        {
            return false;
        }

        await client.DidOpenAsync(State(document), _lifetime.Token).ConfigureAwait(false);
        if (ServerOf(document.LanguageId) is { } server)
        {
            SchedulePull(server, client, document.Path);
        }

        return true;
    }

    /// <summary>The running clients a document is open in: its language's service and its linters, with their ids.</summary>
    private IEnumerable<(string Server, LanguageHostClient Client)> ClientsOf(LanguageDocument document)
    {
        if (ServerOf(document.LanguageId) is { } server && RunningClient(document.LanguageId) is { } client)
        {
            yield return (server, client);
        }

        foreach (var linter in _registry.LintersFor(document.LanguageId).Where(l => Takes(l, document)))
        {
            if (RunningClient(linter.LanguageId) is { } lint)
            {
                yield return (linter.LanguageId, lint);
            }
        }
    }

    /// <summary>
    /// Pull diagnostics (LSP 3.17, ESLint's server): a server that doesn't publish is asked for a document's diagnostics
    /// a moment after it opened or changed (the next change cancels the request).
    /// </summary>
    private void SchedulePull(string server, LanguageHostClient client, string path)
    {
        if (client.Capabilities?.DiagnosticProvider != true)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (_pulls.TryGetValue((server, path), out var previous))
        {
            previous.Cancel();
        }

        _pulls[(server, path)] = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token).ConfigureAwait(false);
                if (!_documents.TryGetValue(path, out var document))
                {
                    return;
                }

                var version = document.Text().Version;
                if (await client.PullDiagnosticsAsync(DocumentUris.FromPath(path), cancellation.Token).ConfigureAwait(false) is { } items
                    && !cancellation.IsCancellationRequested && _documents.ContainsKey(path))
                {
                    Diagnostics.Set(path, server, version, items);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or JsonRpcConnectionLostException or JsonRpcRemoteException or InvalidOperationException)
            {
            }
            finally
            {
                _pulls.TryRemove(KeyValuePair.Create((server, path), cancellation));
                cancellation.Dispose();
            }
        });
    }

    /// <summary>An open document was edited or saved (for consumers that follow source, like test discovery).</summary>
    public event EventHandler<string>? DocumentChanged;

    public async Task ChangeAsync(string path, int version, TextDocumentContentChangeEvent[] changes)
    {
        DocumentChanged?.Invoke(this, path);
        if (!_documents.TryGetValue(path, out var document))
        {
            return;
        }

        foreach (var (server, client) in ClientsOf(document).ToList())
        {
            // Servers that sync whole documents (LSP's TextDocumentSyncKind.Full) get the text, not the edits.
            await (client.Capabilities?.TextDocumentSync switch
            {
                TextDocumentSyncKind.None => Task.CompletedTask,
                TextDocumentSyncKind.Full => client.DidChangeAsync(DocumentUris.FromPath(path), version, [new TextDocumentContentChangeEvent(null, document.Text().Text)], _lifetime.Token),
                _ => client.DidChangeAsync(DocumentUris.FromPath(path), version, changes, _lifetime.Token),
            }).ConfigureAwait(false);
            SchedulePull(server, client, path);
        }
    }

    public async Task SavedAsync(string path)
    {
        DocumentChanged?.Invoke(this, path);
        if (_documents.TryGetValue(path, out var document))
        {
            foreach (var (server, client) in ClientsOf(document).ToList())
            {
                await client.DidSaveAsync(DocumentUris.FromPath(path), _lifetime.Token).ConfigureAwait(false);
                SchedulePull(server, client, path);
            }
        }
    }

    public async Task CloseAsync(string path)
    {
        if (_documents.TryGetValue(path, out var document))
        {
            var clients = ClientsOf(document).ToList();
            _documents.TryRemove(path, out _);
            foreach (var (server, client) in clients)
            {
                await client.DidCloseAsync(DocumentUris.FromPath(path), _lifetime.Token).ConfigureAwait(false);
                if (client.Capabilities?.DiagnosticProvider == true)
                {
                    // Pulled diagnostics belong to the open document.
                    Diagnostics.Set(path, server, null, []);
                }
            }
        }
    }

    public bool IsOpen(string path) => _documents.ContainsKey(path);

    // ------------------------------------------------------------------ projects

    private void OnCompilerInputs(object? sender, CompilerInputsInfo inputs)
    {
        if (_registry.ForProjectLanguage(inputs.Language) is not { } descriptor)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (inputs.State != CompilerInputStates.Ok)
                {
                    if (_projects.TryRemove(inputs.Path, out _) && RunningClient(descriptor.LanguageId) is { } running)
                    {
                        await running.RemoveProjectsAsync([inputs.Path], _lifetime.Token).ConfigureAwait(false);
                    }

                    return;
                }

                var data = new ProjectData(inputs.Path, Path.GetFileNameWithoutExtension(inputs.Path), inputs.Language, inputs.TargetFramework, inputs.Arguments, inputs.OutputPath, inputs.OutputRefPath, inputs.ProjectReferences);
                var isNew = !_clients.ContainsKey(descriptor.LanguageId);
                _projects[inputs.Path] = data;

                // A new client sends every known project once it has started.
                if (await ClientAsync(descriptor.LanguageId).ConfigureAwait(false) is { } client && !isNew)
                {
                    await client.UpdateProjectsAsync([data], _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or OperationCanceledException or InvalidOperationException)
            {
            }
        });
    }

    private void OnSolutionChanged(object? sender, EventArgs e)
    {
        var current = _solutions.Solution?.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer) ?? [];
        var removed = _projects.Keys.Where(p => !current.Contains(p)).ToList();
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var path in removed)
        {
            _projects.TryRemove(path, out _);
        }

        _ = Task.Run(async () =>
        {
            foreach (var lazy in _clients.Values.Where(l => l.IsValueCreated))
            {
                try
                {
                    if (await lazy.Value.ConfigureAwait(false) is { } client)
                    {
                        await client.RemoveProjectsAsync(removed, _lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is JsonRpcConnectionLostException or OperationCanceledException or InvalidOperationException)
                {
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _solutions.CompilerInputsChanged -= OnCompilerInputs;
        _solutions.SolutionChanged -= OnSolutionChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var lazy in _clients.Values.Where(l => l.IsValueCreated))
        {
            try
            {
                if (await lazy.Value.ConfigureAwait(false) is { } client)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
    }
}
