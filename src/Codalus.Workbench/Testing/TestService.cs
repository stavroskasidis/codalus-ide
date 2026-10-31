using System.Globalization;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Output;
using Codalus.Workbench.Plugins;

namespace Codalus.Workbench.Testing;

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

    /// <summary>The items that name a document, by its URI (ignoring case, as editors compare them).</summary>
    internal Dictionary<string, HashSet<TestNode>> ByDocument { get; } = new(StringComparer.OrdinalIgnoreCase);
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

    // The documents' versions (DocumentVersion): each change gets a stamp, given to the documents of the items it touched
    // and of their ancestors (a class's glyph and CodeLens sum up its tests). _allDocuments is the stamp of the last
    // change to every document (a controller went away).
    private readonly Dictionary<string, long> _documentVersions = new(StringComparer.OrdinalIgnoreCase);
    private long _stamp;
    private long _allDocuments;
    private long _order;
    private readonly Timer _notifyTimer;
    private CancellationTokenSource? _current;
    private (TestRunKind Kind, IReadOnlyList<string>? Include)? _last;
    private int _pendingNotify;
    private int _activeRequests;
    private volatile bool _disposed;

    public TestService() => _notifyTimer = new Timer(_ => RaiseChanged());

    /// <summary>Items or results changed (coalesced).</summary>
    public event EventHandler? Changed;

    /// <summary>A run request started or finished (<see cref="IsRunning"/> changed).</summary>
    public event EventHandler? RunningChanged;

    /// <summary>A run reported coverage (ADR 0011).</summary>
    public event EventHandler<TestRunCoverageParams>? CoverageReported;

    /// <summary>A run finished (results are in <see cref="TestRunRecord.Results"/>).</summary>
    public event EventHandler<TestRunRecord>? RunFinished;

    /// <summary>A controller started a run (raised on its thread, before the run's output).</summary>
    public event EventHandler<TestRunRecord>? RunBegan;

    /// <summary>
    /// Output a run reported, as it comes, on the controller's thread and in order: the Output window's Tests channel
    /// follows it (M56: the whole run's output, read twice at its end on the UI thread, froze the window).
    /// </summary>
    public event EventHandler<TestRunOutput>? RunOutput;

    /// <summary>
    /// The most of a test's output its result keeps: the end of it (M56: a test's whole output stayed in memory, and in the
    /// Test Explorer's details).
    /// </summary>
    public const int MaxTestOutputLength = 1024 * 1024;

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
    public TestNode? FindAt(string uri, int line)
    {
        lock (_gate)
        {
            return Declared(uri)
                .Where(n => n.Range!.StartLine <= line && line <= n.Range.EndLine)
                .OrderBy(n => n.Range!.EndLine - n.Range.StartLine)
                .FirstOrDefault();
        }
    }

    /// <summary>Items declared in a document, for editor glyphs, as a walk of the tree meets them.</summary>
    public IReadOnlyList<TestNode> InDocument(string uri)
    {
        lock (_gate)
        {
            return Declared(uri);
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> on the items declared in a document (as <see cref="InDocument(string)"/> lists them)
    /// with the model locked: what it reads of them (ranges, outcomes, the tests under a class) can't change meanwhile.
    /// </summary>
    public T InDocument<T>(string uri, Func<IReadOnlyList<TestNode>, T> read)
    {
        lock (_gate)
        {
            return read(Declared(uri));
        }
    }

    /// <summary>
    /// Changes whenever the items declared in a document, or their outcomes, may have: editors refresh their glyphs and
    /// CodeLens when it did, instead of looking through every test of the solution at each change (M56).
    /// </summary>
    public long DocumentVersion(string uri)
    {
        lock (_gate)
        {
            return Math.Max(_allDocuments, _documentVersions.GetValueOrDefault(uri));
        }
    }

    // Under _gate: the items with a range in the document, from the controllers' indexes, in tree order (parents first,
    // children in order): editors keep the first of the items that start on a line and are as small.
    private List<TestNode> Declared(string uri)
    {
        var declared = new List<TestNode>();
        foreach (var controller in _controllers.Values)
        {
            if (controller.ByDocument.TryGetValue(uri, out var items))
            {
                // Controller by controller, as Read lists their trees.
                var nodes = items.Where(n => n.Range is not null).ToArray();
                Array.Sort(nodes.Select(PathOf).ToArray(), nodes, Comparer<long[]>.Create(ComparePaths));
                declared.AddRange(nodes);
            }
        }

        return declared;
    }

    /// <summary>The <see cref="TestNode.Order"/> of the node and of its ancestors, root first.</summary>
    private static long[] PathOf(TestNode node)
    {
        var depth = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            depth++;
        }

        var path = new long[depth];
        for (var n = node; n is not null; n = n.Parent)
        {
            path[--depth] = n.Order;
        }

        return path;
    }

    private static int ComparePaths(long[] a, long[] b)
    {
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    // ------------------------------------------------------------------ ITestSink

    public void ControllerCreated(TestControllerParams controller, ITestControllerConnection connection)
    {
        lock (_gate)
        {
            Forget(controller.ControllerId); // created again: the old one's items and runs go first
            _controllers[controller.ControllerId] = new TestControllerInfo(controller.ControllerId, controller.PluginId, controller.Label, connection);
        }

        ScheduleChanged();
    }

    public void ControllerDisposed(string controllerId)
    {
        lock (_gate)
        {
            Forget(controllerId);
        }

        ScheduleChanged();
    }

    // Under _gate: a controller's items leave every document, and its runs end.
    private void Forget(string controllerId)
    {
        if (_controllers.Remove(controllerId))
        {
            _documentVersions.Clear();
            _allDocuments = ++_stamp;
        }

        foreach (var run in _runs.Values.Where(r => r.ControllerId == controllerId).ToList())
        {
            _runs.Remove(run.RunId);
            RunEndedSignal(run.RunId).TrySetResult();
        }
    }

    public void ItemsUpdated(TestItemsUpdateParams update)
    {
        lock (_gate)
        {
            if (!_controllers.TryGetValue(update.ControllerId, out var controller))
            {
                return;
            }

            var stamp = ++_stamp;
            var touched = new HashSet<TestNode>();
            foreach (var id in update.Removed)
            {
                if (controller.Index.TryGetValue(id, out var removed))
                {
                    Detach(controller, removed, stamp, touched);
                }
            }

            foreach (var item in update.Items)
            {
                Upsert(controller, item, stamp, touched);
            }
        }

        ScheduleChanged();
    }

    private void Upsert(TestControllerInfo controller, TestItemDto item, long stamp, HashSet<TestNode> touched)
    {
        if (!controller.Index.TryGetValue(item.Id, out var node))
        {
            node = new TestNode(controller.Id, item.Id);
            controller.Index[item.Id] = node;
        }
        else
        {
            Stamp(node.Uri, stamp); // the document it leaves, or whose glyph changes
        }

        node.Label = item.Label;
        node.Description = item.Description;
        SetUri(controller, node, item.Uri);
        node.Range = item.Range;
        node.Traits = item.Traits ?? [];
        node.SortText = item.SortText;
        node.Error = item.Error;

        var parent = item.ParentId is { } parentId ? controller.Index.GetValueOrDefault(parentId) : null;
        for (var above = parent; above is not null; above = above.Parent)
        {
            if (above == node)
            {
                // The item itself, or an item under it (a plugin that reuses an id): a cycle that walks up the tree would
                // never leave. The item becomes a root instead.
                parent = null;
                break;
            }
        }

        if (node.Parent != parent || (parent is null && !controller.Roots.Contains(node)))
        {
            if (node.Parent is { } old)
            {
                Touch(old, stamp, touched);
            }

            Unlink(controller, node);
            node.Parent = parent;
            node.Order = ++_order;
            (parent?.MutableChildren ?? controller.Roots).Add(node);
        }

        Touch(node, stamp, touched);
    }

    // Under _gate: keeps the controller's index of items by document.
    private static void SetUri(TestControllerInfo controller, TestNode node, string? uri)
    {
        if (string.Equals(node.Uri, uri, StringComparison.Ordinal))
        {
            return;
        }

        Unindex(controller, node);
        node.Uri = uri;
        if (uri is not null)
        {
            if (!controller.ByDocument.TryGetValue(uri, out var items))
            {
                controller.ByDocument[uri] = items = [];
            }

            items.Add(node);
        }
    }

    private static void Unindex(TestControllerInfo controller, TestNode node)
    {
        if (node.Uri is { } uri && controller.ByDocument.TryGetValue(uri, out var items) && items.Remove(node) && items.Count == 0)
        {
            controller.ByDocument.Remove(uri);
        }
    }

    // Under _gate: the change's stamp goes to the node's document and its ancestors'; an ancestor already touched by the
    // change (a sibling's) ends the climb.
    private void Touch(TestNode node, long stamp, HashSet<TestNode> touched)
    {
        Stamp(node.Uri, stamp);
        for (var ancestor = node.Parent; ancestor is not null && touched.Add(ancestor); ancestor = ancestor.Parent)
        {
            Stamp(ancestor.Uri, stamp);
        }
    }

    private void Stamp(string? uri, long stamp)
    {
        if (uri is not null)
        {
            _documentVersions[uri] = stamp;
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

    private void Detach(TestControllerInfo controller, TestNode node, long stamp, HashSet<TestNode> touched)
    {
        if (node.Parent is { } parent)
        {
            Touch(parent, stamp, touched);
        }

        Unlink(controller, node);
        node.Parent = null;
        foreach (var descendant in node.SelfAndDescendants())
        {
            Stamp(descendant.Uri, stamp);
            Unindex(controller, descendant);
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
        TestRunRecord record;
        lock (_gate)
        {
            _runs[run.RunId] = record = new TestRunRecord(run.RunId, run.ControllerId, run.Name, run.RequestId, ParseKind(run.ProfileKind)) { StartedAlone = _runs.Count == 0 };
        }

        RunBegan?.Invoke(this, record);
    }

    public void RunUpdated(TestRunUpdateParams update)
    {
        TestRunOutput? reported = null;
        lock (_gate)
        {
            if (!_runs.TryGetValue(update.RunId, out var run) || !_controllers.TryGetValue(run.ControllerId, out var controller))
            {
                return;
            }

            Dictionary<string, TestNode>? late = null;
            foreach (var output in update.Output)
            {
                run.AppendOutput(output.Text);
                if (output.ItemId is { } itemId)
                {
                    var key = TestNode.KeyOf(controller.Id, itemId);
                    if (run.Results.TryGetValue(key, out var finished) && controller.Index.TryGetValue(itemId, out var finishedNode))
                    {
                        // Output reported after the test's result belongs to that result: it goes on from what the result
                        // kept and how much went before, and the result's text is made once per update, not per chunk.
                        if (!_resultOutput.TryGetValue((run.RunId, key), out var kept))
                        {
                            _resultOutput[(run.RunId, key)] = kept = new PendingOutput();
                            kept.Append(finished.Output); // all of it: a result whose output was cut kept its own, with the count
                        }

                        kept.Append(output.Text);
                        (late ??= new Dictionary<string, TestNode>(StringComparer.Ordinal))[key] = finishedNode;
                    }
                    else
                    {
                        // Gathered in a builder: a string concatenated per chunk copied all of it each time, under this lock.
                        if (!_pendingOutput.TryGetValue(key, out var pending))
                        {
                            _pendingOutput[key] = pending = new PendingOutput();
                        }

                        pending.Append(output.Text);
                    }
                }
            }

            foreach (var (key, node) in late ?? [])
            {
                var finished = run.Results[key];
                var updated = finished with { Output = _resultOutput[(run.RunId, key)].ToString() };
                run.Results[key] = updated;
                node.Result = updated;
                ReplaceHistory(key, finished, updated);
            }

            if (update.Output.Length > 0)
            {
                reported = new TestRunOutput(run, update.Output.Length == 1 ? update.Output[0].Text : string.Concat(update.Output.Select(o => o.Text)));
            }

            foreach (var attachment in update.Attachments)
            {
                run.AddAttachment(attachment);
            }

            var stamp = ++_stamp;
            var touched = new HashSet<TestNode>();
            foreach (var state in update.States)
            {
                if (!controller.Index.TryGetValue(state.ItemId, out var node))
                {
                    continue;
                }

                Touch(node, stamp, touched);
                var outcome = TestOutcomes.Parse(state.State);
                if (!outcome.IsFinal())
                {
                    node.Pending = outcome;
                    continue;
                }

                var key = node.Key;
                _pendingOutput.Remove(key, out var testOutput);
                _resultOutput.Remove((run.RunId, key));
                if (testOutput is { IsCut: true })
                {
                    _resultOutput[(run.RunId, key)] = testOutput; // output after the result goes on from it, its count kept
                }

                var result = new TestResult(
                    outcome,
                    state.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                    state.Messages ?? [],
                    testOutput?.ToString() ?? string.Empty,
                    DateTimeOffset.Now,
                    run.RunId);
                node.Pending = TestOutcome.None;
                node.Result = result;
                run.Results[key] = result;
                AddHistory(key, result);
            }
        }

        if (reported is not null)
        {
            RunOutput?.Invoke(this, reported);
        }

        ScheduleChanged();
    }

    private readonly Dictionary<string, PendingOutput> _pendingOutput = new(StringComparer.Ordinal);

    // The output of the results that more may follow while their run lasts, by run and test: only those that were cut or
    // got output after them, so the line saying how much went before counts all of it.
    private readonly Dictionary<(string RunId, string Key), PendingOutput> _resultOutput = [];

    private static string NotKept(long characters) =>
        Localization.Localizer.Format("msg.testing.earlierOutputNotKept", "[{0} earlier characters of output not kept]", characters.ToString("N0", CultureInfo.CurrentCulture)) + "\n";

    /// <summary>
    /// A test's output while it runs: its last <see cref="MaxTestOutputLength"/> characters, from a line's start where one
    /// is near, and how many went before.
    /// </summary>
    private sealed class PendingOutput
    {
        private readonly System.Text.StringBuilder _text = new();
        private long _dropped;

        /// <summary>Whether a result keeps only part of it.</summary>
        public bool IsCut => _dropped > 0 || _text.Length > MaxTestOutputLength;

        public void Append(string text)
        {
            _text.Append(text);

            // Dropped in large steps: a little at each chunk moved the whole megabyte each time.
            if (_text.Length > 2 * MaxTestOutputLength)
            {
                var drop = OutputChannel.LineStart(_text, _text.Length - MaxTestOutputLength);
                _text.Remove(0, drop);
                _dropped += drop;
            }
        }

        public override string ToString()
        {
            var drop = _text.Length > MaxTestOutputLength ? OutputChannel.LineStart(_text, _text.Length - MaxTestOutputLength) : 0;
            return drop + _dropped == 0 ? _text.ToString() : NotKept(drop + _dropped) + _text.ToString(drop, _text.Length - drop);
        }
    }

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
            foreach (var kept in _resultOutput.Keys.Where(k => k.RunId == runId).ToList())
            {
                _resultOutput.Remove(kept);
            }

            if (_controllers.TryGetValue(run.ControllerId, out var controller))
            {
                // Tests still queued or running when the run ended did not run.
                var stamp = ++_stamp;
                var touched = new HashSet<TestNode>();
                foreach (var node in controller.Index.Values.Where(n => n.Pending != TestOutcome.None))
                {
                    node.Pending = TestOutcome.None;
                    Touch(node, stamp, touched);
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
        await ((Task)ended).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    /// <summary>
    /// A run outside the controllers (a remote test environment, M21b): the service is running until the returned
    /// handle is disposed, and <see cref="Cancel"/> cancels its token.
    /// </summary>
    public (IDisposable Handle, CancellationToken Token) BeginExternalRun()
    {
        var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _current = cancellation;
        }

        Interlocked.Increment(ref _activeRequests);
        RunningChanged?.Invoke(this, EventArgs.Empty);
        return (new ExternalRun(() =>
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
        }), cancellation.Token);
    }

    private sealed class ExternalRun(Action end) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                end();
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
    /// Applies results from a file (TRX) to the tests they name (<see cref="TestNames.Matches"/>, the first in tree order),
    /// looked up by name; results for unknown tests are returned. The import becomes a finished run named
    /// <paramref name="name"/>.
    /// </summary>
    public IReadOnlyList<ImportedResult> ApplyImported(string name, IReadOnlyList<ImportedResult> results)
    {
        var unmatched = new List<ImportedResult>();
        TestRunRecord run;
        lock (_gate)
        {
            run = new TestRunRecord("import:" + Guid.NewGuid().ToString("N"), string.Empty, name, null, TestRunKind.Run) { Ended = DateTimeOffset.Now };
            var index = TestNames.Index(_controllers.Values.SelectMany(c => c.Roots).SelectMany(r => r.Leaves()));
            var stamp = ++_stamp;
            var touched = new HashSet<TestNode>();
            foreach (var imported in results)
            {
                var node = index.Matches(imported.FullyQualifiedName, imported.DisplayName).FirstOrDefault();
                if (node is null)
                {
                    unmatched.Add(imported);
                    continue;
                }

                Touch(node, stamp, touched);
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
        if (!_disposed && Interlocked.Exchange(ref _pendingNotify, 1) == 0)
        {
            try
            {
                _notifyTimer.Change(NotifyInterval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // disposed meanwhile: nobody listens any more
            }
        }
    }

    private void RaiseChanged()
    {
        Interlocked.Exchange(ref _pendingNotify, 0);
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops the coalesced notifications: a pending one would reach editors that outlived the window.</summary>
    public void Dispose()
    {
        _disposed = true;
        _notifyTimer.Dispose();
    }

    internal static string Format(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
            : duration.TotalMilliseconds < 1 ? "< 1 ms" : duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
}

/// <summary>A result read from a file: the test's fully qualified name (and display name) and its result.</summary>
public sealed record ImportedResult(string FullyQualifiedName, string DisplayName, TestResult Result);
