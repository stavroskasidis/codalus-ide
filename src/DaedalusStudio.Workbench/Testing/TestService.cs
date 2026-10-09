using System.Globalization;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Workbench.Plugins;

namespace DaedalusStudio.Workbench.Testing;

/// <summary>A test controller as the shell knows it.</summary>
public sealed class TestControllerInfo
{
    internal TestControllerInfo(string id, string pluginId, string label, ITestControllerConnection connection)
    {
        Id = id;
        PluginId = pluginId;
        Label = label;
        Connection = connection;
    }

    public string Id { get; }

    public string PluginId { get; }

    public string Label { get; }

    internal ITestControllerConnection Connection { get; }

    public IReadOnlyList<TestProfile> Profiles { get; internal set; } = [];

    public bool CanRefresh { get; internal set; }

    internal List<TestNode> Roots { get; } = [];

    internal Dictionary<string, TestNode> Index { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The Test Explorer's model (ADR 0010): the items of every test controller, run profiles, runs and results (the
/// last result per test plus a history of <see cref="HistoryLength"/> runs). Receives the controllers' messages
/// (<see cref="ITestSink"/>) and sends them run and refresh requests. Thread-safe; events are raised on any thread,
/// coalesced per <see cref="NotifyInterval"/> for item and result changes.
/// </summary>
public sealed class TestService : ITestSink, IDisposable
{
    public static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, TestControllerInfo> _controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestRunRecord> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TestResult>> _history = new(StringComparer.Ordinal);
    private readonly List<TestRunRecord> _finishedRuns = [];
    private readonly Dictionary<string, TaskCompletionSource> _runEnded = new(StringComparer.Ordinal);
    private readonly Timer _notifyTimer;
    private CancellationTokenSource? _current;
    private (TestRunKind Kind, IReadOnlyList<string>? Include)? _last;
    private int _pendingNotify;
    private int _activeRequests;

    public TestService() => _notifyTimer = new Timer(_ => RaiseChanged());

    /// <summary>Items or results changed (coalesced).</summary>
    public event EventHandler? Changed;

    /// <summary>A run request started or finished (<see cref="IsRunning"/> changed).</summary>
    public event EventHandler? RunningChanged;

    /// <summary>A run reported coverage (ADR 0011).</summary>
    public event EventHandler<TestRunCoverageParams>? CoverageReported;

    /// <summary>A run finished (results are in <see cref="TestRunRecord.Results"/>).</summary>
    public event EventHandler<TestRunRecord>? RunFinished;

    /// <summary>The latest coverage (ADR 0011), fed by <see cref="RunCoverage"/>.</summary>
    public CoverageStore Coverage { get; } = new();

    /// <summary>Results kept per test (current plus earlier runs).</summary>
    public int HistoryLength { get; set; } = 10;

    public bool IsRunning => Volatile.Read(ref _activeRequests) > 0;

    public IReadOnlyList<TestControllerInfo> Controllers
    {
        get
        {
            lock (_gate)
            {
                return [.. _controllers.Values];
            }
        }
    }

    /// <summary>Finished runs, newest last (the last <see cref="HistoryLength"/>).</summary>
    public IReadOnlyList<TestRunRecord> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _finishedRuns];
            }
        }
    }

    /// <summary>Runs under <paramref name="read"/> with the model locked: walk <see cref="TestNode"/> trees only inside it.</summary>
    public T Read<T>(Func<IReadOnlyList<TestNode>, T> read)
    {
        lock (_gate)
        {
            return read([.. _controllers.Values.SelectMany(c => c.Roots)]);
        }
    }

    public TestNode? Find(string key)
    {
        lock (_gate)
        {
            foreach (var controller in _controllers.Values)
            {
                if (key.StartsWith(controller.Id + "\u001f", StringComparison.Ordinal)
                    && controller.Index.TryGetValue(key[(controller.Id.Length + 1)..], out var node))
                {
                    return node;
                }
            }

            return null;
        }
    }

    /// <summary>Earlier results of a test, newest first.</summary>
    public IReadOnlyList<TestResult> History(string key)
    {
        lock (_gate)
        {
            return _history.TryGetValue(key, out var list) ? [.. Enumerable.Reverse(list)] : [];
        }
    }

    /// <summary>The innermost test item declared at <paramref name="line"/> (zero-based) of a document.</summary>
    public TestNode? FindAt(string uri, int line) => Read(roots =>
        roots.SelectMany(r => r.SelfAndDescendants())
            .Where(n => n.Uri is not null && string.Equals(n.Uri, uri, StringComparison.OrdinalIgnoreCase) && n.Range is { } range && range.StartLine <= line && line <= range.EndLine)
            .OrderBy(n => n.Range!.EndLine - n.Range.StartLine)
            .FirstOrDefault());

    /// <summary>Items declared in a document, for editor glyphs.</summary>
    public IReadOnlyList<TestNode> InDocument(string uri) => Read(roots =>
        (IReadOnlyList<TestNode>)[.. roots.SelectMany(r => r.SelfAndDescendants())
            .Where(n => n.Uri is not null && n.Range is not null && string.Equals(n.Uri, uri, StringComparison.OrdinalIgnoreCase))]);

    // ------------------------------------------------------------------ ITestSink

    public void ControllerCreated(TestControllerParams controller, ITestControllerConnection connection)
    {
        lock (_gate)
        {
            _controllers[controller.ControllerId] = new TestControllerInfo(controller.ControllerId, controller.PluginId, controller.Label, connection);
        }

        ScheduleChanged();
    }

    public void ControllerDisposed(string controllerId)
    {
        lock (_gate)
        {
            _controllers.Remove(controllerId);
            foreach (var run in _runs.Values.Where(r => r.ControllerId == controllerId).ToList())
            {
                _runs.Remove(run.RunId);
                RunEndedSignal(run.RunId).TrySetResult();
            }
        }

        ScheduleChanged();
    }

    public void ItemsUpdated(TestItemsUpdateParams update)
    {
        lock (_gate)
        {
            if (!_controllers.TryGetValue(update.ControllerId, out var controller))
            {
                return;
            }

            foreach (var id in update.Removed)
            {
                if (controller.Index.TryGetValue(id, out var removed))
                {
                    Detach(controller, removed);
                }
            }

            foreach (var item in update.Items)
            {
                Upsert(controller, item);
            }
        }

        ScheduleChanged();
    }

    private static void Upsert(TestControllerInfo controller, TestItemDto item)
    {
        if (!controller.Index.TryGetValue(item.Id, out var node))
        {
            node = new TestNode(controller.Id, item.Id);
            controller.Index[item.Id] = node;
        }

        node.Label = item.Label;
        node.Description = item.Description;
        node.Uri = item.Uri;
        node.Range = item.Range;
        node.Traits = item.Traits ?? [];
        node.SortText = item.SortText;
        node.Error = item.Error;

        var parent = item.ParentId is { } parentId ? controller.Index.GetValueOrDefault(parentId) : null;
        if (node.Parent != parent || (parent is null && !controller.Roots.Contains(node)))
        {
            Unlink(controller, node);
            node.Parent = parent;
            (parent?.MutableChildren ?? controller.Roots).Add(node);
        }
    }

    private static void Unlink(TestControllerInfo controller, TestNode node)
    {
        if (node.Parent is { } parent)
        {
            parent.MutableChildren.Remove(node);
        }
        else
        {
            controller.Roots.Remove(node);
        }
    }

    private static void Detach(TestControllerInfo controller, TestNode node)
    {
        Unlink(controller, node);
        node.Parent = null;
        foreach (var descendant in node.SelfAndDescendants())
        {
            if (controller.Index.TryGetValue(descendant.Id, out var known) && ReferenceEquals(known, descendant))
            {
                controller.Index.Remove(descendant.Id);
            }
        }
    }

    public void ProfilesUpdated(TestProfilesParams profiles)
    {
        lock (_gate)
        {
            if (_controllers.TryGetValue(profiles.ControllerId, out var controller))
            {
                controller.Profiles = [.. profiles.Profiles.Select(p => new TestProfile(profiles.ControllerId, p.Id, p.Label, ParseKind(p.Kind), p.IsDefault))];
                controller.CanRefresh = profiles.CanRefresh;
            }
        }

        ScheduleChanged();
    }

    private static TestRunKind ParseKind(string? kind) => kind switch
    {
        "debug" => TestRunKind.Debug,
        "coverage" => TestRunKind.Coverage,
        _ => TestRunKind.Run,
    };

    public void RunStarted(TestRunStartedParams run)
    {
        lock (_gate)
        {
            _runs[run.RunId] = new TestRunRecord(run.RunId, run.ControllerId, run.Name, run.RequestId, ParseKind(run.ProfileKind));
        }
    }

    public void RunUpdated(TestRunUpdateParams update)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(update.RunId, out var run) || !_controllers.TryGetValue(run.ControllerId, out var controller))
            {
                return;
            }

            foreach (var output in update.Output)
            {
                run.AppendOutput(output.Text);
                if (output.ItemId is { } itemId)
                {
                    var key = TestNode.KeyOf(controller.Id, itemId);
                    if (run.Results.TryGetValue(key, out var finished) && controller.Index.TryGetValue(itemId, out var finishedNode))
                    {
                        // Output reported after the test's result belongs to that result.
                        var updated = finished with { Output = finished.Output + output.Text };
                        run.Results[key] = updated;
                        finishedNode.Result = updated;
                        ReplaceHistory(key, finished, updated);
                    }
                    else
                    {
                        _pendingOutput[key] = _pendingOutput.GetValueOrDefault(key) + output.Text;
                    }
                }
            }

            foreach (var attachment in update.Attachments)
            {
                run.AddAttachment(attachment);
            }

            foreach (var state in update.States)
            {
                if (!controller.Index.TryGetValue(state.ItemId, out var node))
                {
                    continue;
                }

                var outcome = TestOutcomes.Parse(state.State);
                if (!outcome.IsFinal())
                {
                    node.Pending = outcome;
                    continue;
                }

                var key = node.Key;
                _pendingOutput.Remove(key, out var testOutput);
                var result = new TestResult(
                    outcome,
                    state.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                    state.Messages ?? [],
                    testOutput ?? string.Empty,
                    DateTimeOffset.Now,
                    run.RunId);
                node.Pending = TestOutcome.None;
                node.Result = result;
                run.Results[key] = result;
                AddHistory(key, result);
            }
        }

        ScheduleChanged();
    }

    private readonly Dictionary<string, string> _pendingOutput = new(StringComparer.Ordinal);

    private void ReplaceHistory(string key, TestResult old, TestResult updated)
    {
        if (_history.TryGetValue(key, out var list) && list.IndexOf(old) is var index and >= 0)
        {
            list[index] = updated;
        }
    }

    private void AddHistory(string key, TestResult result)
    {
        if (!_history.TryGetValue(key, out var list))
        {
            _history[key] = list = [];
        }

        list.Add(result);
        if (list.Count > Math.Max(1, HistoryLength))
        {
            list.RemoveRange(0, list.Count - Math.Max(1, HistoryLength));
        }
    }

    public void RunCoverage(TestRunCoverageParams coverage)
    {
        Coverage.Add(coverage);
        CoverageReported?.Invoke(this, coverage);
    }

    public void RunEnded(string runId)
    {
        TestRunRecord? run;
        lock (_gate)
        {
            if (!_runs.Remove(runId, out run))
            {
                return;
            }

            run.Ended = DateTimeOffset.Now;
            if (_controllers.TryGetValue(run.ControllerId, out var controller))
            {
                // Tests still queued or running when the run ended did not run.
                foreach (var node in controller.Index.Values.Where(n => n.Pending != TestOutcome.None))
                {
                    node.Pending = TestOutcome.None;
                }
            }

            _finishedRuns.Add(run);
            if (_finishedRuns.Count > Math.Max(1, HistoryLength))
            {
                _finishedRuns.RemoveAt(0);
            }
        }

        ScheduleChanged();
        RunFinished?.Invoke(this, run);
        lock (_gate)
        {
            RunEndedSignal(runId).TrySetResult();
        }
    }

    // Must be called under _gate. Created by whichever comes first: the run's end, or a caller waiting for it.
    private TaskCompletionSource RunEndedSignal(string runId)
    {
        if (!_runEnded.TryGetValue(runId, out var signal))
        {
            _runEnded[runId] = signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return signal;
    }

    /// <summary>Completes when the run has ended (its results applied).</summary>
    private async Task WaitForRunAsync(string runId)
    {
        Task ended;
        lock (_gate)
        {
            ended = RunEndedSignal(runId).Task;
        }

        // A plugin that never ends its run (it crashed) is not waited for forever.
        await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        lock (_gate)
        {
            _runEnded.Remove(runId);
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>The default profile of <paramref name="kind"/> for each controller that has one.</summary>
    public IReadOnlyList<TestProfile> DefaultProfiles(TestRunKind kind)
    {
        lock (_gate)
        {
            return [.. _controllers.Values.Select(c => c.Profiles.FirstOrDefault(p => p.Kind == kind && p.IsDefault) ?? c.Profiles.FirstOrDefault(p => p.Kind == kind)).OfType<TestProfile>()];
        }
    }

    /// <summary>
    /// Runs <paramref name="include"/> (node keys; null = everything) with each controller's default profile of
    /// <paramref name="kind"/>. Returns false when no controller can run that kind. One request at a time per call;
    /// <see cref="Cancel"/> cancels it.
    /// </summary>
    public async Task<bool> RunAsync(TestRunKind kind, IReadOnlyList<string>? include, CancellationToken cancellationToken = default)
    {
        var profiles = DefaultProfiles(kind);
        var requests = new List<(TestControllerInfo Controller, TestRunParams Params)>();
        lock (_gate)
        {
            foreach (var profile in profiles)
            {
                var controller = _controllers[profile.ControllerId];
                string[]? ids = null;
                if (include is not null)
                {
                    ids = [.. include.Where(k => k.StartsWith(controller.Id + "\u001f", StringComparison.Ordinal)).Select(k => k[(controller.Id.Length + 1)..]).Where(controller.Index.ContainsKey)];
                    if (ids.Length == 0)
                    {
                        continue;
                    }
                }

                requests.Add((controller, new TestRunParams(controller.Id, profile.Id, Guid.NewGuid().ToString("N"), ids, [])));
            }
        }

        if (requests.Count == 0)
        {
            return false;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _current = cancellation;
            _last = (kind, include);
        }

        Interlocked.Increment(ref _activeRequests);
        RunningChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await Task.WhenAll(requests.Select(async r =>
            {
                try
                {
                    var runs = await r.Controller.Connection.RunAsync(r.Params, cancellation.Token).ConfigureAwait(false);
                    await Task.WhenAll(runs.Select(WaitForRunAsync)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            })).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_current == cancellation)
                {
                    _current = null;
                }
            }

            cancellation.Dispose();
            Interlocked.Decrement(ref _activeRequests);
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>Runs the tests whose last result failed.</summary>
    public Task<bool> RunFailedAsync(TestRunKind kind = TestRunKind.Run) =>
        RunAsync(kind, Read(roots => (IReadOnlyList<string>)[.. roots.SelectMany(r => r.Leaves()).Where(n => n.Result?.Outcome.IsFailure() == true).Select(n => n.Key)]) is { Count: > 0 } failed ? failed : []);

    /// <summary>Runs the tests that have no result yet.</summary>
    public Task<bool> RunNotRunAsync(TestRunKind kind = TestRunKind.Run) =>
        RunAsync(kind, Read(roots => (IReadOnlyList<string>)[.. roots.SelectMany(r => r.Leaves()).Where(n => n.Result is null).Select(n => n.Key)]));

    /// <summary>Repeats the last request. Returns false when there was none.</summary>
    public Task<bool> RepeatLastAsync()
    {
        (TestRunKind Kind, IReadOnlyList<string>? Include)? last;
        lock (_gate)
        {
            last = _last;
        }

        return last is { } l ? RunAsync(l.Kind, l.Include) : Task.FromResult(false);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            try
            {
                _current?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Asks every controller that can refresh to reload its tests.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var controllers = Controllers.Where(c => c.CanRefresh).ToList();
        await Task.WhenAll(controllers.Select(c => c.Connection.RefreshAsync(cancellationToken))).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ imported results

    /// <summary>
    /// Applies results from a file (TRX) to the tests they match by <paramref name="match"/>; results for unknown tests
    /// are returned. The import becomes a finished run named <paramref name="name"/>.
    /// </summary>
    public IReadOnlyList<ImportedResult> ApplyImported(string name, IReadOnlyList<ImportedResult> results, Func<TestNode, ImportedResult, bool> match)
    {
        var unmatched = new List<ImportedResult>();
        TestRunRecord run;
        lock (_gate)
        {
            run = new TestRunRecord("import:" + Guid.NewGuid().ToString("N"), string.Empty, name, null, TestRunKind.Run) { Ended = DateTimeOffset.Now };
            var leaves = _controllers.Values.SelectMany(c => c.Roots).SelectMany(r => r.Leaves()).ToList();
            foreach (var imported in results)
            {
                var node = leaves.FirstOrDefault(n => match(n, imported));
                if (node is null)
                {
                    unmatched.Add(imported);
                    continue;
                }

                var result = imported.Result with { RunId = run.RunId };
                node.Result = result;
                run.Results[node.Key] = result;
                AddHistory(node.Key, result);
            }

            _finishedRuns.Add(run);
        }

        ScheduleChanged();
        RunFinished?.Invoke(this, run);
        return unmatched;
    }

    // ------------------------------------------------------------------ notifications

    private void ScheduleChanged()
    {
        if (Interlocked.Exchange(ref _pendingNotify, 1) == 0)
        {
            _notifyTimer.Change(NotifyInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged()
    {
        Interlocked.Exchange(ref _pendingNotify, 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _notifyTimer.Dispose();

    internal static string Format(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
            : duration.TotalMilliseconds < 1 ? "< 1 ms" : duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
}

/// <summary>A result read from a file: the test's fully qualified name (and display name) and its result.</summary>
public sealed record ImportedResult(string FullyQualifiedName, string DisplayName, TestResult Result);
