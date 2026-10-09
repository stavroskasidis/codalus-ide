using System.Globalization;
using DaedalusStudio.Plugins.Proposed;
using DaedalusStudio.Plugins.Protocol;

namespace DaedalusStudio.Plugins.Host.Proposed;

/// <summary>
/// Test controllers of the plugins in this host (ADR 0010). Item and run changes are collected and sent in batches
/// (every <see cref="FlushInterval"/>, and whenever a run completes), items before run states so the shell always knows the
/// items a result refers to.
/// </summary>
internal sealed class HostTestRegistry : IDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly PluginHostServer _server;
    private readonly Lock _gate = new();
    private readonly Lock _flushGate = new();
    private readonly Dictionary<string, HostTestController> _controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostTestRun> _runs = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private readonly AsyncLocal<string?> _currentRequest = new();
    private readonly Dictionary<string, List<string>> _requestRuns = new(StringComparer.Ordinal);
    private int _scheduled;

    public HostTestRegistry(PluginHostServer server)
    {
        _server = server;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The shell's run request whose handler is running on this async flow.</summary>
    internal string? CurrentRequest => _currentRequest.Value;

    public HostTestController Create(string pluginId, string id, string label)
    {
        var controller = new HostTestController(this, pluginId, pluginId + ":" + id, label);
        lock (_gate)
        {
            if (_controllers.ContainsKey(controller.WireId))
            {
                throw new InvalidOperationException($"A test controller '{id}' already exists.");
            }

            _controllers[controller.WireId] = controller;
        }

        _server.Post(ProposedMethods.TestsCreateController, new TestControllerParams(pluginId, controller.WireId, label), PluginJsonContext.Default.TestControllerParams);
        return controller;
    }

    internal void Removed(HostTestController controller)
    {
        lock (_gate)
        {
            _controllers.Remove(controller.WireId);
        }

        Flush();
        _server.Post(ProposedMethods.TestsDisposeController, new TestControllerIdParams(controller.WireId), PluginJsonContext.Default.TestControllerIdParams);
    }

    internal void RunStarted(HostTestRun run, string? profileKind)
    {
        lock (_gate)
        {
            _runs[run.Id] = run;
            if (run.RequestId is { } request && _requestRuns.TryGetValue(request, out var runs))
            {
                runs.Add(run.Id);
            }
        }

        Flush(); // the items the run reports on go first
        _server.Post(ProposedMethods.TestsRunStarted, new TestRunStartedParams(run.Controller.WireId, run.Id, run.Name, run.RequestId, profileKind), PluginJsonContext.Default.TestRunStartedParams);
    }

    internal void RunEnded(HostTestRun run)
    {
        Flush();
        lock (_gate)
        {
            _runs.Remove(run.Id);
        }

        _server.Post(ProposedMethods.TestsRunEnded, new TestRunIdParams(run.Id), PluginJsonContext.Default.TestRunIdParams);
    }

    internal void Post<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => _server.Post(method, parameters, info);

    /// <summary>Schedules a flush within <see cref="FlushInterval"/>.</summary>
    internal void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            _timer.Change(FlushInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Sends pending item changes, then pending run updates.</summary>
    internal void Flush()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        List<HostTestController> controllers;
        List<HostTestRun> runs;
        lock (_gate)
        {
            controllers = [.. _controllers.Values];
            runs = [.. _runs.Values];
        }

        // One lock for the whole flush keeps batches from different threads in order.
        lock (_flushGate)
        {
            foreach (var controller in controllers)
            {
                controller.FlushItems();
            }

            foreach (var run in runs)
            {
                run.FlushUpdates();
            }
        }
    }

    // ------------------------------------------------------------------ shell → host

    /// <summary>Runs a profile's handler; returns the ids of the runs it created.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(TestRunParams request, CancellationToken cancellationToken)
    {
        var controller = Controller(request.ControllerId);
        var profile = controller.Profile(request.ProfileId) ?? throw new InvalidOperationException($"Unknown run profile '{request.ProfileId}'.");
        var include = request.Include?.Select(controller.Find).OfType<TestItem>().ToList();
        var exclude = request.Exclude.Select(controller.Find).OfType<TestItem>().ToList();
        lock (_gate)
        {
            _requestRuns[request.RequestId] = [];
        }

        _currentRequest.Value = request.RequestId;
        try
        {
            await profile.Handler(new TestRunRequest(include, exclude, profile), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentRequest.Value = null;
            Flush();
        }

        lock (_gate)
        {
            _requestRuns.Remove(request.RequestId, out var runs);
            return runs ?? [];
        }
    }

    public async Task RefreshAsync(string controllerId, CancellationToken cancellationToken)
    {
        if (Controller(controllerId).RefreshHandler is { } refresh)
        {
            await refresh(cancellationToken).ConfigureAwait(false);
        }

        Flush();
    }

    private HostTestController Controller(string id)
    {
        lock (_gate)
        {
            return _controllers.GetValueOrDefault(id) ?? throw new InvalidOperationException($"Unknown test controller '{id}'.");
        }
    }

    public void Dispose() => _timer.Dispose();

    // ------------------------------------------------------------------ conversions

    internal static RangeDto? ToDto(TestRange? range) =>
        range is null ? null : new RangeDto(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter);

    internal static TestRange ToRange(RangeDto range) => new(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter);

    internal static string KindName(TestRunProfileKind kind) => kind switch
    {
        TestRunProfileKind.Debug => "debug",
        TestRunProfileKind.Coverage => "coverage",
        _ => "run",
    };
}

/// <summary>One plugin's test controller: mirrors its item tree to the shell.</summary>
internal sealed class HostTestController : ITestController
{
    private readonly HostTestRegistry _registry;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TestItem> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestItem> _dirty = new(StringComparer.Ordinal);
    private readonly List<string> _removed = [];
    private readonly List<HostTestRunProfile> _profiles = [];
    private Func<CancellationToken, Task>? _refresh;
    private int _nextProfile;
    private bool _disposed;

    public HostTestController(HostTestRegistry registry, string pluginId, string wireId, string label)
    {
        _registry = registry;
        PluginId = pluginId;
        WireId = wireId;
        Id = wireId[(pluginId.Length + 1)..];
        Label = label;
        Items = TestItemCollection.CreateRoot();
        Items.Changed += OnCollectionChanged;
    }

    public string PluginId { get; }

    /// <summary>The id on the wire: <c>pluginId:controllerId</c>.</summary>
    public string WireId { get; }

    public string Id { get; }

    public string Label { get; }

    public TestItemCollection Items { get; }

    public Func<CancellationToken, Task>? RefreshHandler
    {
        get => _refresh;
        set
        {
            _refresh = value;
            SendProfiles();
        }
    }

    public TestItem? Find(string id)
    {
        lock (_gate)
        {
            return _index.GetValueOrDefault(id);
        }
    }

    internal HostTestRunProfile? Profile(string id)
    {
        lock (_gate)
        {
            return _profiles.FirstOrDefault(p => p.Id == id);
        }
    }

    public ITestRunProfile CreateRunProfile(string label, TestRunProfileKind kind, Func<TestRunRequest, CancellationToken, Task> handler, bool isDefault = true)
    {
        HostTestRunProfile profile;
        lock (_gate)
        {
            profile = new HostTestRunProfile(this, "p" + (++_nextProfile).ToString(CultureInfo.InvariantCulture), label, kind, isDefault, handler);
            _profiles.Add(profile);
        }

        SendProfiles();
        return profile;
    }

    internal void RemoveProfile(HostTestRunProfile profile)
    {
        lock (_gate)
        {
            _profiles.Remove(profile);
        }

        SendProfiles();
    }

    private void SendProfiles()
    {
        if (_disposed)
        {
            return;
        }

        TestProfileDto[] profiles;
        lock (_gate)
        {
            profiles = [.. _profiles.Select(p => new TestProfileDto(p.Id, p.Label, HostTestRegistry.KindName(p.Kind), p.IsDefault))];
        }

        _registry.Post(ProposedMethods.TestsUpdateProfiles, new TestProfilesParams(WireId, profiles, _refresh is not null), PluginJsonContext.Default.TestProfilesParams);
    }

    public ITestRun CreateRun(TestRunRequest request, string? name = null)
    {
        var run = new HostTestRun(_registry, this, Guid.NewGuid().ToString("N"), name ?? request.Profile?.Label ?? "Test run", _registry.CurrentRequest);
        _registry.RunStarted(run, request.Profile is null ? null : HostTestRegistry.KindName(request.Profile.Kind));
        return run;
    }

    // ------------------------------------------------------------------ item tracking

    private void OnCollectionChanged(object? sender, TestItemCollectionChangedEventArgs e)
    {
        lock (_gate)
        {
            foreach (var item in e.Removed)
            {
                Detach(item);
                _removed.Add(item.Id);
            }

            foreach (var item in e.Added)
            {
                Attach(item);
            }
        }

        _registry.Schedule();
    }

    private void OnItemChanged(object? sender, EventArgs e)
    {
        if (sender is TestItem item)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(item.Id, out var known) && ReferenceEquals(known, item))
                {
                    _dirty[item.Id] = item;
                }
            }

            _registry.Schedule();
        }
    }

    private void Attach(TestItem item)
    {
        _index[item.Id] = item;
        _dirty[item.Id] = item;
        item.Changed += OnItemChanged;
        item.Children.Changed += OnCollectionChanged;
        foreach (var child in item.Children)
        {
            Attach(child);
        }
    }

    private void Detach(TestItem item)
    {
        item.Changed -= OnItemChanged;
        item.Children.Changed -= OnCollectionChanged;
        if (_index.TryGetValue(item.Id, out var known) && ReferenceEquals(known, item))
        {
            _index.Remove(item.Id);
            _dirty.Remove(item.Id);
        }

        foreach (var child in item.Children)
        {
            Detach(child);
        }
    }

    internal void FlushItems()
    {
        TestItemDto[] items;
        string[] removed;
        lock (_gate)
        {
            if (_dirty.Count == 0 && _removed.Count == 0)
            {
                return;
            }

            // Parents before children: the shell may not know a new item's parent yet.
            items = [.. _dirty.Values.OrderBy(Depth).Select(ToDto)];
            removed = [.. _removed];
            _dirty.Clear();
            _removed.Clear();
        }

        _registry.Post(ProposedMethods.TestsUpdateItems, new TestItemsUpdateParams(WireId, items, removed), PluginJsonContext.Default.TestItemsUpdateParams);
    }

    private static int Depth(TestItem item)
    {
        var depth = 0;
        for (var parent = item.Parent; parent is not null; parent = parent.Parent)
        {
            depth++;
        }

        return depth;
    }

    private static TestItemDto ToDto(TestItem item) => new(
        item.Id,
        item.Parent?.Id,
        item.Label,
        item.Description,
        item.Uri,
        HostTestRegistry.ToDto(item.Range),
        item.Traits.Count == 0 ? null : [.. item.Traits.Select(t => new TestTraitDto(t.Name, t.Value))],
        item.SortText,
        item.Error);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Items.Changed -= OnCollectionChanged;
        lock (_gate)
        {
            foreach (var item in Items)
            {
                Detach(item);
            }
        }

        _registry.Removed(this);
    }
}

internal sealed class HostTestRunProfile(HostTestController controller, string id, string label, TestRunProfileKind kind, bool isDefault, Func<TestRunRequest, CancellationToken, Task> handler) : ITestRunProfile
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public TestRunProfileKind Kind { get; } = kind;

    public bool IsDefault { get; } = isDefault;

    public Func<TestRunRequest, CancellationToken, Task> Handler { get; } = handler;

    public void Dispose() => controller.RemoveProfile(this);
}

/// <summary>One run: buffers state changes and output until the next flush.</summary>
internal sealed class HostTestRun(HostTestRegistry registry, HostTestController controller, string id, string name, string? requestId) : ITestRun
{
    private readonly Lock _gate = new();
    private readonly List<TestStateDto> _states = [];
    private readonly List<TestOutputDto> _output = [];
    private readonly List<TestAttachmentDto> _attachments = [];
    private bool _ended;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public string? RequestId { get; } = requestId;

    public HostTestController Controller { get; } = controller;

    public void Enqueued(TestItem item) => State(item, TestStates.Enqueued, null, null);

    public void Started(TestItem item) => State(item, TestStates.Started, null, null);

    public void Skipped(TestItem item, string? reason = null) =>
        State(item, TestStates.Skipped, null, reason is null ? null : [new TestMessage(reason)]);

    public void Passed(TestItem item, TimeSpan? duration = null) => State(item, TestStates.Passed, duration, null);

    public void Failed(TestItem item, IReadOnlyList<TestMessage> messages, TimeSpan? duration = null) => State(item, TestStates.Failed, duration, messages);

    public void Errored(TestItem item, IReadOnlyList<TestMessage> messages, TimeSpan? duration = null) => State(item, TestStates.Errored, duration, messages);

    public void AppendOutput(string text, TestItem? item = null)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _output.Add(new TestOutputDto(item?.Id, text));
        }

        registry.Schedule();
    }

    public void AddAttachment(TestAttachment attachment)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _attachments.Add(new TestAttachmentDto(attachment.Uri, attachment.DisplayName));
        }

        registry.Schedule();
    }

    public void AddCoverage(CoverageReport coverage)
    {
        registry.Flush();
        registry.Post(
            ProposedMethods.TestsRunCoverage,
            new TestRunCoverageParams(
                Id,
                [.. coverage.Files.Select(f => new FileCoverageDto(f.Uri, [.. f.Lines.Select(l => new LineCoverageDto(l.Line, l.Hits, l.BranchesCovered, l.BranchesTotal))]))],
                [.. coverage.Methods.Select(m => new MethodCoverageDto(m.Assembly, m.Namespace, m.Class, m.Method, m.Uri, m.Line, m.LinesCovered, m.LinesTotal, m.BranchesCovered, m.BranchesTotal))]),
            PluginJsonContext.Default.TestRunCoverageParams);
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }
        }

        registry.Flush();
        lock (_gate)
        {
            _ended = true;
        }

        registry.RunEnded(this);
    }

    private void State(TestItem item, string state, TimeSpan? duration, IReadOnlyList<TestMessage>? messages)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _states.Add(new TestStateDto(
                item.Id,
                state,
                duration?.TotalMilliseconds,
                messages is null ? null : [.. messages.Select(m => new TestMessageDto(m.Message, m.Expected, m.Actual, m.StackTrace, m.Uri, HostTestRegistry.ToDto(m.Range)))]));
        }

        registry.Schedule();
    }

    internal void FlushUpdates()
    {
        TestRunUpdateParams update;
        lock (_gate)
        {
            if (_states.Count == 0 && _output.Count == 0 && _attachments.Count == 0)
            {
                return;
            }

            update = new TestRunUpdateParams(Id, [.. _states], [.. _output], [.. _attachments]);
            _states.Clear();
            _output.Clear();
            _attachments.Clear();
        }

        registry.Post(ProposedMethods.TestsRunUpdate, update, PluginJsonContext.Default.TestRunUpdateParams);
    }
}
