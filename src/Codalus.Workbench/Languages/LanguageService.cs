using System.Collections.Concurrent;
using System.Text.Json;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Languages;

/// <summary>
/// An open document handed to a language service. <see cref="Text"/> reads the editor's current text. <see cref="Snapshot"/>,
/// when given, takes the current version's immutable snapshot cheaply, its text made later by the function it returns
/// (M57): a <c>didOpen</c> sends the snapshot taken when it was queued, so a request queued behind it never reads a later
/// edit's text.
/// </summary>
public sealed record LanguageDocument(string Path, string LanguageId, Func<(string Text, int Version)> Text, Func<(Func<string> Text, int Version)>? Snapshot = null);

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
    private readonly SemaphoreSlim _documentNotifications = new(1, 1);
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
        _solutions.SolutionChanged += OnLoadingProgress;
        _solutions.ProgressChanged += OnLoadingProgress;
        _solutions.DesignTimeProgressChanged += OnLoadingProgress;
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

    /// <summary>
    /// A running language host crashed: (language, whether it restarts). Since M49 a host that exits during its first
    /// start isn't a crash (<see cref="StartFailed"/>), and one that exits during a restart is reported there too.
    /// </summary>
    public event EventHandler<(string LanguageId, bool WillRestart)>? HostCrashed;

    /// <summary>
    /// A language host couldn't start (M49): its .NET runtime is missing (<see cref="HostRuntimeMissingException"/>),
    /// or it exited during startup (<see cref="HostExitedException"/>, with what it wrote; a plain
    /// <see cref="JsonRpcConnectionLostException"/> when it ended cleanly and said nothing). It isn't started again.
    /// </summary>
    public event EventHandler<(string LanguageId, Exception Error)>? StartFailed;

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

    /// <summary>
    /// Moves the language hosts (M21): sets <see cref="Placement"/> and stops the running services, whose files are the
    /// old place's (a window that had a local solution open, then connected to a server, kept asking the local C# host
    /// about the server's files). The next request starts each one again where it now belongs, with the open documents.
    /// </summary>
    public async Task ReplaceHostsAsync(Func<Rpc.ProcessLaunchInfo, Rpc.ProcessLaunchInfo?>? placement)
    {
        Placement = placement;
        foreach (var (server, lazy) in _clients.ToArray())
        {
            if (!_clients.TryRemove(KeyValuePair.Create(server, lazy)))
            {
                continue;
            }

            _status.TryRemove(server, out _);
            Diagnostics.Clear(server);
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                if (await lazy.Value.ConfigureAwait(false) is { } client)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonRpcConnectionLostException)
            {
            }
        }
    }

    /// <summary>The client of a language's service, starting it if needed; null when it can't be started.</summary>
    public Task<LanguageHostClient?> ClientAsync(string languageId) =>
        ServerOf(languageId) is { } server
            ? _clients.GetOrAdd(server, id => new Lazy<Task<LanguageHostClient?>>(() => StartAsync(id))).Value
            : Task.FromResult<LanguageHostClient?>(null);

    /// <summary>
    /// A service that couldn't start (its launch returned null, e.g. a server not installed yet) is tried again, and the
    /// open documents of its languages are opened in it. Returns whether it runs now.
    /// </summary>
    public Task<bool> RetryAsync(string languageId) => StartAgainAsync(languageId, restart: false);

    /// <summary>
    /// Every service that couldn't start is tried again (M49: once the .NET runtime its host needs is installed), with the
    /// open documents of its languages.
    /// </summary>
    public async Task RetryFailedAsync()
    {
        var failed = _clients.Where(c => c.Value.IsValueCreated && c.Value.Value.IsCompleted && (!c.Value.Value.IsCompletedSuccessfully || c.Value.Value.Result is null))
            .Select(c => c.Key).ToList();
        foreach (var server in failed)
        {
            await RetryAsync(server).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops a running service and starts it again with the open documents: a server that loads the workspace itself
    /// (FsAutoComplete, M36) does it again for a newly opened solution. Returns whether it runs now.
    /// </summary>
    public Task<bool> RestartAsync(string languageId) => StartAgainAsync(languageId, restart: true);

    private async Task<bool> StartAgainAsync(string languageId, bool restart)
    {
        if (ServerOf(languageId) is not { } server)
        {
            return false;
        }

        if (_clients.TryGetValue(server, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompleted && (restart || !lazy.Value.IsCompletedSuccessfully || lazy.Value.Result is null)
            && _clients.TryRemove(KeyValuePair.Create(server, lazy)))
        {
            _status.TryRemove(server, out _);
            Diagnostics.Clear(server);
            if (lazy.Value.IsCompletedSuccessfully && lazy.Value.Result is { } running)
            {
                try
                {
                    await running.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonRpcConnectionLostException)
                {
                }
            }
        }

        if (await ClientAsync(languageId).ConfigureAwait(false) is not { } client)
        {
            return false;
        }

        var descriptor = _registry.Get(server);
        foreach (var document in _documents.Values.Where(d => descriptor is { IsLinter: true } ? Takes(descriptor, d) : ServerOf(d.LanguageId) == server))
        {
            await Open(client, document).ConfigureAwait(false);
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

    /// <summary>Every running language service (one per server; linters included), without starting any: Code Search asks each for workspace symbols.</summary>
    public IReadOnlyList<(string Server, LanguageHostClient Client)> RunningClients() =>
        [.. _clients.Where(c => c.Value.IsValueCreated && c.Value.Value.IsCompletedSuccessfully && c.Value.Value.Result is not null).Select(c => (c.Key, c.Value.Value.Result!))];

    /// <summary>The running client of a language's service, without starting it.</summary>
    public LanguageHostClient? RunningClient(string languageId) =>
        ServerOf(languageId) is { } server && _clients.TryGetValue(server, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    /// <summary>Whether a language's service was started and couldn't (until it's tried again).</summary>
    public bool HasFailed(string languageId) =>
        ServerOf(languageId) is { } server && _clients.TryGetValue(server, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompleted
        && (!lazy.Value.IsCompletedSuccessfully || lazy.Value.Result is null);

    /// <summary>Server settings for every language service (e.g. <c>{ "analysisScope": "solution" }</c>).</summary>
    public async Task ConfigureAsync(JsonElement settings)
    {
        _settings = settings;
        foreach (var (server, lazy) in _clients.Where(c => c.Value.IsValueCreated).ToArray())
        {
            if (await lazy.Value.ConfigureAwait(false) is { } client)
            {
                await client.ConfigureAsync(_registry.Get(server)?.Settings?.Invoke() ?? settings, _lifetime.Token).ConfigureAwait(false);
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
            ProjectsProvider = () => [.. _projects.Values.Where(p => descriptor.TakesProjects(p.Language))],
            LoadingProvider = () => descriptor.ProjectLanguage is null ? null : IsLoading,
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

            switch (e)
            {
                case { DuringStartup: true, IsRestart: false }:
                    break; // the first start failed: StartAsync below reports it, once
                case { DuringStartup: true, WillRestart: false }:
                    StartFailed?.Invoke(this, (languageId, e.Error ?? new JsonRpcConnectionLostException($"The {descriptor.DisplayName} language service exited during startup.", null)));
                    break;
                default:
                    HostCrashed?.Invoke(this, (languageId, e.WillRestart));
                    break;
            }
        };
        client.Restarted += (_, _) => HostRestarted?.Invoke(this, languageId);

        // Each stage is logged when the start takes long, so a client that never becomes usable says where it stopped.
        var stage = "starting the host";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var watchdog = new Timer(_ => Log?.Invoke(this, $"[{languageId}] Still {stage} after {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0} s."), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        try
        {
            await client.StartAsync(_lifetime.Token).ConfigureAwait(false);
            if ((descriptor.Settings?.Invoke() ?? _settings) is { } settings)
            {
                stage = "sending the settings";
                await client.ConfigureAsync(settings, _lifetime.Token).ConfigureAwait(false);
            }

            if (descriptor.ProjectLanguage is not null)
            {
                var projects = _projects.Values.Where(p => descriptor.TakesProjects(p.Language)).ToList();
                var loading = IsLoading;
                if (projects.Count > 0 || loading)
                {
                    stage = "sending the projects";
                    await client.UpdateProjectsAsync(projects, _lifetime.Token, loading).ConfigureAwait(false);
                }
            }

            stage = "started";
            return client;
        }
        catch (OperationCanceledException)
        {
            // Disposed while it started: a cancelled start leaves its host running (SupervisedProcess), and no one else
            // holds the client to end it. Each C# host left so cost ~150 MB, and a full run of the shell tests, whose
            // windows close as soon as they pass, ran out of memory with dozens of them (docs/ci-flakes.md).
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonRpcConnectionLostException)
        {
            Log?.Invoke(this, $"[{languageId}] The language service could not start: {ex.Message}");
            await client.DisposeAsync().ConfigureAwait(false);
            // Every loss of the host while it starts (before its handshake ended) is its first start failing, which the
            // crash handler leaves to this: one that exited cleanly or said nothing (no HostExitedException) too.
            if (ex is HostRuntimeMissingException or HostExitedException { DuringStartup: true }
                || (ex is JsonRpcConnectionLostException and not HostExitedException && stage == "starting the host"))
            {
                StartFailed?.Invoke(this, (languageId, ex));
            }

            return null;
        }
    }

    private static OpenDocumentState State(LanguageDocument document)
    {
        var (text, version) = document.Text();
        return new OpenDocumentState(DocumentUris.FromPath(document.Path), document.LanguageId, version, text);
    }

    /// <summary>
    /// Queues a document's <c>didOpen</c>, its text made and encoded when it is written, off the caller's thread (M57: the
    /// UI thread built and encoded the whole text of each file it opened). It carries the snapshot taken now, when the
    /// document gives one, so a request queued behind it reads this text and not a later edit's.
    /// </summary>
    private Task Open(LanguageHostClient client, LanguageDocument document) =>
        client.DidOpenAsync(DocumentUris.FromPath(document.Path), document.LanguageId, document.Text, _lifetime.Token, document.Snapshot?.Invoke());

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
                    if (await ClientAsync(linter.LanguageId).ConfigureAwait(false) is { } lint && await SendOpenAsync(lint, document).ConfigureAwait(false))
                    {
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

        if (await SendOpenAsync(client, document).ConfigureAwait(false) && ServerOf(document.LanguageId) is { } server)
        {
            SchedulePull(server, client, document.Path);
        }

        return true;
    }

    /// <summary>
    /// Sends a document's <c>didOpen</c> unless it was closed (or opened again) while its service started: its text
    /// is read now, and a closed document's may be gone already (its editor let go of the text model), so a late open
    /// would leave the service with an empty file that no edit or close would ever correct (M47: Code Search's preview,
    /// moving on while the C# host started, emptied Shapes.cs there, so neither the index nor workspace symbols had its
    /// types). Opens and closes go out one at a time, so a close that comes while the open is sent follows it.
    /// </summary>
    private async Task<bool> SendOpenAsync(LanguageHostClient client, LanguageDocument document)
    {
        Task opened;
        await _documentNotifications.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_documents.TryGetValue(document.Path, out var current) || !ReferenceEquals(current, document))
            {
                return false;
            }

            // Queued in order here, its text read when it is written (M57); a close queued after it follows it.
            opened = Open(client, document);
        }
        finally
        {
            _documentNotifications.Release();
        }

        await opened.ConfigureAwait(false);
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

    /// <summary>
    /// An open document was edited to <paramref name="version"/>. <paramref name="text"/> makes that version's whole text
    /// (the editor passes its snapshot of this change, M57), read on the writer loop for a server that syncs whole
    /// documents; without it the text is read now, from the document.
    /// </summary>
    public async Task ChangeAsync(string path, int version, TextDocumentContentChangeEvent[] changes, Func<string>? text = null)
    {
        DocumentChanged?.Invoke(this, path);
        if (!_documents.TryGetValue(path, out var document))
        {
            return;
        }

        // Every client's change is queued before anything is awaited (M57): two changes in a row reach a linter in their
        // order too (each awaited its write before the next client's was queued, so a later change could overtake).
        var uri = DocumentUris.FromPath(path);
        var queued = new List<Task>();
        foreach (var (server, client) in ClientsOf(document).ToList())
        {
            // Servers that sync whole documents (LSP's TextDocumentSyncKind.Full) get the text, not the edits: this
            // change's own text, made when written, off the caller's thread, and replaced by a newer one while queued.
            // Never the document's latest text: that could carry a later edit past a request queued before it (M57
            // review: a completion's position read against the next key's text).
            queued.Add(client.Capabilities?.TextDocumentSync switch
            {
                TextDocumentSyncKind.None => Task.CompletedTask,
                TextDocumentSyncKind.Full => client.DidChangeTextAsync(uri, version, text ??= Now(document.Text().Text), _lifetime.Token),
                _ => client.DidChangeAsync(uri, version, changes, _lifetime.Token),
            });
            SchedulePull(server, client, path);
        }

        await Task.WhenAll(queued).ConfigureAwait(false);

        static Func<string> Now(string text) => () => text;
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
            await _documentNotifications.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
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
            finally
            {
                _documentNotifications.Release();
            }
        }
    }

    public bool IsOpen(string path) => _documents.ContainsKey(path);

    // ------------------------------------------------------------------ projects

    /// <summary>How long design-time build results gather before they go to the language host together (M34b).</summary>
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(150);

    private readonly ConcurrentDictionary<string, PendingInputs> _pendingInputs = new(SolutionService.PathComparer);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _flushScheduled;
    private bool? _sentLoading;

    /// <summary>Changes when another solution opens or the solution closes: inputs gathered before belong to the one before.</summary>
    private int _solutionEpoch;
    private string? _solutionPath;

    /// <summary>The solution changed: the next flush drops the projects it no longer has.</summary>
    private int _removeProjects;

    /// <summary>A design-time build's result waiting for the next flush, and the solution it came from.</summary>
    private readonly record struct PendingInputs(CompilerInputsInfo Inputs, int Epoch);

    /// <summary>Flushes and removals run while holding it, one at a time (tests hold it to delay them, as a busy machine does).</summary>
    internal SemaphoreSlim FlushGate => _flushGate;

    /// <summary>
    /// Whether the solution is still loading: projects are being evaluated, design-time builds are running, or their
    /// results haven't reached the language hosts yet. Hosts hold back semantic diagnostics that missing projects would
    /// make false until it ends (M34b).
    /// </summary>
    public bool IsLoading =>
        _solutions.Solution is { Trusted: true }
        && (_solutions.Progress is not { Completed: true } || _solutions.DesignTimeProgress is not { Completed: true } || !_pendingInputs.IsEmpty);

    private void OnCompilerInputs(object? sender, CompilerInputsInfo inputs)
    {
        if (_registry.ForProjectLanguage(inputs.Language) is null)
        {
            return;
        }

        _pendingInputs[inputs.Path] = new PendingInputs(inputs, Volatile.Read(ref _solutionEpoch));
        ScheduleFlush();
    }

    private void OnLoadingProgress<T>(object? sender, T e)
    {
        if (IsLoading != _sentLoading)
        {
            ScheduleFlush();
        }
    }

    /// <summary>
    /// Design-time builds finish a project at a time; the language host gets them in batches (one update of many
    /// projects costs about what one of one project does: before M34b, a 96-project solution took a minute to load
    /// there).
    /// </summary>
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BatchDelay, _lifetime.Token).ConfigureAwait(false);
                await _flushGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    Volatile.Write(ref _flushScheduled, 0);
                    await FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    _flushGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>
    /// Sends what gathered. Flushes run one at a time and the pending set keeps each project's newest inputs, so a host
    /// that starts slowly never gets a design-time build from before a restore after the one from after it.
    /// </summary>
    /// <remarks>
    /// The projects a solution change removed go out here too, before the updates and in the same order as them: a
    /// removal sent on its own could pass an update of the same project still on its way, and the host kept it. Inputs
    /// gathered for a solution that has closed since are dropped: flushed after the next solution opened, they loaded the
    /// closed folder's projects next to its projects for good (the smoke test's EF step after the folder step, on macOS:
    /// the C# host "ready" with 2 or 3 projects instead of 1).
    /// </remarks>
    private async Task FlushAsync()
    {
        // Removals first, then the batch: a solution change from here on leaves its removals to the next flush.
        var gone = new List<ProjectData>();
        if (Interlocked.Exchange(ref _removeProjects, 0) == 1)
        {
            var current = _solutions.Solution?.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer) ?? [];
            foreach (var path in _projects.Keys.Where(p => !current.Contains(p)).ToList())
            {
                if (_projects.TryRemove(path, out var project))
                {
                    gone.Add(project);
                }
            }
        }

        var epoch = Volatile.Read(ref _solutionEpoch);
        var batch = new List<CompilerInputsInfo>();
        foreach (var path in _pendingInputs.Keys)
        {
            if (_pendingInputs.TryRemove(path, out var pending) && pending.Epoch == epoch)
            {
                batch.Add(pending.Inputs);
            }
        }

        var loading = IsLoading;
        _sentLoading = loading;
        foreach (var descriptor in _registry.All.Where(d => d.ProjectLanguage is not null))
        {
            var updated = new List<ProjectData>();
            var removed = gone.Where(p => descriptor.TakesProjects(p.Language)).Select(p => p.Path).ToList();
            foreach (var inputs in batch.Where(i => descriptor.TakesProjects(i.Language)))
            {
                if (inputs.State != CompilerInputStates.Ok)
                {
                    if (_projects.TryRemove(inputs.Path, out _))
                    {
                        removed.Add(inputs.Path);
                    }

                    continue;
                }

                var data = new ProjectData(inputs.Path, Path.GetFileNameWithoutExtension(inputs.Path), inputs.Language, inputs.TargetFramework, inputs.Arguments, inputs.OutputPath, inputs.OutputRefPath, inputs.ProjectReferences);
                _projects[inputs.Path] = data;
                updated.Add(data);
            }

            try
            {
                // A new client sends every known project (and the loading state) once it has started.
                var isNew = !_clients.ContainsKey(descriptor.LanguageId);
                if (isNew && updated.Count == 0)
                {
                    continue;
                }

                if (await ClientAsync(descriptor.LanguageId).ConfigureAwait(false) is not { } client || isNew)
                {
                    continue;
                }

                if (removed.Count > 0)
                {
                    await client.RemoveProjectsAsync(removed, _lifetime.Token).ConfigureAwait(false);
                }

                await client.UpdateProjectsAsync(updated, _lifetime.Token, loading).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// The solution opened, closed or changed: the next flush removes the projects it no longer has, in order with the
    /// updates, and drops what was gathered for another solution.
    /// </summary>
    private void OnSolutionChanged(object? sender, EventArgs e)
    {
        var path = _solutions.Solution?.Path;
        if (!string.Equals(Interlocked.Exchange(ref _solutionPath, path), path, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _solutionEpoch);
        }

        Volatile.Write(ref _removeProjects, 1);
        ScheduleFlush();
    }

    public async ValueTask DisposeAsync()
    {
        _solutions.CompilerInputsChanged -= OnCompilerInputs;
        _solutions.SolutionChanged -= OnSolutionChanged;
        _solutions.SolutionChanged -= OnLoadingProgress;
        _solutions.ProgressChanged -= OnLoadingProgress;
        _solutions.DesignTimeProgressChanged -= OnLoadingProgress;
        await _lifetime.CancelAsync().ConfigureAwait(false);

        // The servers shut down together (M56): one after another, each up to its shutdown's time, a few slow ones
        // outlasted the time the IDE's exit gives them all.
        await Task.WhenAll(_clients.Values.Where(l => l.IsValueCreated).Select(DisposeClientAsync)).ConfigureAwait(false);
        _lifetime.Dispose();

        static async Task DisposeClientAsync(Lazy<Task<LanguageHostClient?>> lazy)
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
    }
}
