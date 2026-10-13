namespace Codalus.Plugins.Proposed;

/// <summary>Creates test controllers (ADR 0010), modelled on VS Code's testing API.</summary>
public interface ITests
{
    /// <summary>A controller whose items appear in the Test Explorer. <paramref name="id"/> is unique per plugin.</summary>
    ITestController CreateController(string id, string label);
}

/// <summary>
/// Owns a tree of <see cref="TestItem"/>s and the ways to run them. Changes to items are sent to the shell in batches.
/// Item ids must be unique within the controller.
/// </summary>
public interface ITestController : IDisposable
{
    /// <summary>The id given to <see cref="ITests.CreateController"/>.</summary>
    string Id { get; }

    /// <summary>The name shown in the Test Explorer.</summary>
    string Label { get; }

    /// <summary>The root items (typically one per project).</summary>
    TestItemCollection Items { get; }

    /// <summary>
    /// Called when the user asks to reload the tests (the Test Explorer's Refresh). The returned task ends the
    /// refresh indicator.
    /// </summary>
    Func<CancellationToken, Task>? RefreshHandler { get; set; }

    /// <summary>
    /// A way to run tests: Run, Debug or Coverage. The shell calls <paramref name="handler"/> with the user's
    /// selection; cancelling its token means the user pressed Cancel. The default profile of each kind is used by
    /// toolbar and editor commands.
    /// </summary>
    ITestRunProfile CreateRunProfile(string label, TestRunProfileKind kind, Func<TestRunRequest, CancellationToken, Task> handler, bool isDefault = true);

    /// <summary>Starts reporting a run. Call <see cref="ITestRun.Complete"/> when it is over.</summary>
    ITestRun CreateRun(TestRunRequest request, string? name = null);

    /// <summary>The item with <paramref name="id"/> anywhere in the tree, or null.</summary>
    TestItem? Find(string id);
}

/// <summary>What a run profile does.</summary>
public enum TestRunProfileKind
{
    /// <summary>Runs the tests.</summary>
    Run,

    /// <summary>Runs the tests under the debugger.</summary>
    Debug,

    /// <summary>Runs the tests and collects code coverage.</summary>
    Coverage,
}

/// <summary>A way to run tests, shown in the Test Explorer and the editor. Dispose to remove it.</summary>
public interface ITestRunProfile : IDisposable
{
    /// <summary>The name shown in menus.</summary>
    string Label { get; }

    /// <summary>Run, Debug or Coverage.</summary>
    TestRunProfileKind Kind { get; }

    /// <summary>Whether toolbar and editor commands of this kind use it.</summary>
    bool IsDefault { get; }
}

/// <summary>
/// What to run. <see cref="Include"/> null means every test of the controller; tests under an included item are
/// included unless they (or an ancestor) are in <see cref="Exclude"/>.
/// </summary>
public sealed record TestRunRequest(IReadOnlyList<TestItem>? Include, IReadOnlyList<TestItem> Exclude, ITestRunProfile? Profile);

/// <summary>Reports the progress and results of one run. Methods may be called from any thread.</summary>
public interface ITestRun
{
    /// <summary>The run's name, shown in the results.</summary>
    string Name { get; }

    /// <summary>The test is waiting to run.</summary>
    void Enqueued(TestItem item);

    /// <summary>The test is running.</summary>
    void Started(TestItem item);

    /// <summary>The test was skipped, optionally with the framework's reason.</summary>
    void Skipped(TestItem item, string? reason = null);

    /// <summary>The test passed.</summary>
    void Passed(TestItem item, TimeSpan? duration = null);

    /// <summary>An assertion failed.</summary>
    void Failed(TestItem item, IReadOnlyList<TestMessage> messages, TimeSpan? duration = null);

    /// <summary>The test could not run, or threw something other than an assertion failure.</summary>
    void Errored(TestItem item, IReadOnlyList<TestMessage> messages, TimeSpan? duration = null);

    /// <summary>Output of the run; with <paramref name="item"/>, output of that test.</summary>
    void AppendOutput(string text, TestItem? item = null);

    /// <summary>A file the run produced (a TRX file, a coverage report…).</summary>
    void AddAttachment(TestAttachment attachment);

    /// <summary>Coverage collected by the run (ADR 0011). Several calls are merged.</summary>
    void AddCoverage(CoverageReport coverage);

    /// <summary>The run is over; tests still enqueued or started are shown as not run.</summary>
    void Complete();
}

/// <summary>A failure or error message. <see cref="Uri"/> and <see cref="Range"/> locate it in source.</summary>
public sealed record TestMessage(string Message)
{
    /// <summary>The expected value of a failed assertion, when the framework reports it.</summary>
    public string? Expected { get; init; }

    /// <summary>The actual value of a failed assertion.</summary>
    public string? Actual { get; init; }

    /// <summary>The stack trace as the runner printed it; the shell makes <c>in file:line N</c> frames clickable.</summary>
    public string? StackTrace { get; init; }

    /// <summary>The document of the failure (a <c>file:</c> URI).</summary>
    public string? Uri { get; init; }

    /// <summary>The failure's location in <see cref="Uri"/>.</summary>
    public TestRange? Range { get; init; }
}

/// <summary>A zero-based range in a document (LSP style).</summary>
public sealed record TestRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

/// <summary>A trait (xUnit), category (NUnit, MSTest) or property; <see cref="Value"/> is null for plain categories.</summary>
public sealed record TestTrait(string Name, string? Value);

/// <summary>A file a run produced. <see cref="Uri"/> is a <c>file:</c> URI.</summary>
public sealed record TestAttachment(string Uri, string? DisplayName = null);

/// <summary>Coverage of one line (zero-based). <see cref="BranchesTotal"/> is 0 for lines without branches.</summary>
public sealed record LineCoverage(int Line, int Hits, int BranchesCovered = 0, int BranchesTotal = 0);

/// <summary>Coverage of one document.</summary>
public sealed record FileCoverage(string Uri, IReadOnlyList<LineCoverage> Lines);

/// <summary>Coverage of one method, for the Coverage Results tree (assembly, namespace, class, method).</summary>
public sealed record MethodCoverage(
    string Assembly,
    string? Namespace,
    string Class,
    string Method,
    string? Uri,
    int Line,
    int LinesCovered,
    int LinesTotal,
    int BranchesCovered,
    int BranchesTotal);

/// <summary>Coverage reported by a run: per document line, and per method for the results tree.</summary>
/// <param name="Files">Line coverage per document.</param>
/// <param name="Methods">Coverage per method.</param>
public sealed record CoverageReport(IReadOnlyList<FileCoverage> Files, IReadOnlyList<MethodCoverage> Methods);

/// <summary>
/// A test, or a group of tests (project, namespace, class, theory). Property changes and changes to
/// <see cref="Children"/> are sent to the shell once the item belongs to a controller.
/// </summary>
public sealed class TestItem
{
    private readonly Lock _gate = new();
    private string _label;
    private string? _description;
    private string? _uri;
    private TestRange? _range;
    private IReadOnlyList<TestTrait> _traits = [];
    private string? _sortText;
    private string? _error;

    /// <summary>A new item; it is sent to the shell once added to a controller's tree.</summary>
    /// <param name="id">Unique within the controller.</param>
    /// <param name="label">The text shown in the Test Explorer.</param>
    public TestItem(string id, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
        _label = label;
        Children = new TestItemCollection(this);
    }

    /// <summary>Raised after a property changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Unique within the controller.</summary>
    public string Id { get; }

    /// <summary>The item this one is a child of, or null for roots and detached items.</summary>
    public TestItem? Parent { get; internal set; }

    /// <summary>The item's children.</summary>
    public TestItemCollection Children { get; }

    /// <summary>The text shown in the Test Explorer.</summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Muted text after the label.</summary>
    public string? Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    /// <summary>The document that declares the test (a <c>file:</c> URI).</summary>
    public string? Uri
    {
        get => _uri;
        set => Set(ref _uri, value);
    }

    /// <summary>The declaration's range in <see cref="Uri"/>; its first line gets the editor glyph.</summary>
    public TestRange? Range
    {
        get => _range;
        set => Set(ref _range, value);
    }

    /// <summary>Traits and categories, for grouping and <c>trait:</c> filters.</summary>
    public IReadOnlyList<TestTrait> Traits
    {
        get => _traits;
        set => Set(ref _traits, value);
    }

    /// <summary>Sorts the item among its siblings instead of the label.</summary>
    public string? SortText
    {
        get => _sortText;
        set => Set(ref _sortText, value);
    }

    /// <summary>A problem loading this item (for example a discovery failure), shown on it.</summary>
    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    private void Set<T>(ref T field, T value)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public override string ToString() => Id;
}

/// <summary>Arguments of <see cref="TestItemCollection.Changed"/>.</summary>
public sealed class TestItemCollectionChangedEventArgs(IReadOnlyList<TestItem> added, IReadOnlyList<TestItem> removed) : EventArgs
{
    /// <summary>Items added (or replacing an item with the same id).</summary>
    public IReadOnlyList<TestItem> Added { get; } = added;

    /// <summary>Items removed (or replaced).</summary>
    public IReadOnlyList<TestItem> Removed { get; } = removed;
}

/// <summary>The children of a <see cref="TestItem"/> (or a controller's roots), keyed by id. Thread-safe.</summary>
public sealed class TestItemCollection : IReadOnlyCollection<TestItem>
{
    private readonly Lock _gate = new();
    private readonly TestItem? _owner;
    private readonly Dictionary<string, TestItem> _items = new(StringComparer.Ordinal);
    private readonly List<TestItem> _order = [];

    internal TestItemCollection(TestItem? owner) => _owner = owner;

    /// <summary>A collection without an owner item (a controller's roots).</summary>
    public static TestItemCollection CreateRoot() => new(null);

    /// <summary>Raised after items were added, replaced or removed.</summary>
    public event EventHandler<TestItemCollectionChangedEventArgs>? Changed;

    /// <inheritdoc/>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    /// <summary>The direct child with <paramref name="id"/>, or null.</summary>
    public TestItem? Get(string id)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(id);
        }
    }

    /// <summary>Adds <paramref name="item"/>, replacing an existing item with the same id.</summary>
    public void Add(TestItem item)
    {
        TestItem? replaced;
        lock (_gate)
        {
            replaced = _items.GetValueOrDefault(item.Id);
            if (ReferenceEquals(replaced, item))
            {
                return;
            }

            if (replaced is not null)
            {
                _order[_order.IndexOf(replaced)] = item;
                replaced.Parent = null;
            }
            else
            {
                _order.Add(item);
            }

            _items[item.Id] = item;
            item.Parent = _owner;
        }

        Changed?.Invoke(this, new TestItemCollectionChangedEventArgs([item], replaced is null ? [] : [replaced]));
    }

    /// <summary>Removes the child with <paramref name="id"/> (and its subtree). Returns false when there was none.</summary>
    public bool Remove(string id)
    {
        TestItem? removed;
        lock (_gate)
        {
            if (!_items.Remove(id, out removed))
            {
                return false;
            }

            _order.Remove(removed);
            removed.Parent = null;
        }

        Changed?.Invoke(this, new TestItemCollectionChangedEventArgs([], [removed]));
        return true;
    }

    /// <summary>
    /// Makes the collection hold exactly <paramref name="items"/>. Items whose id is already present and that are
    /// the same instance stay; others are added or removed.
    /// </summary>
    public void Replace(IEnumerable<TestItem> items)
    {
        var next = items.ToList();
        List<TestItem> added = [];
        List<TestItem> removed = [];
        lock (_gate)
        {
            var keep = next.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var old in _order.Where(o => !keep.Contains(o.Id) || !next.Contains(o)))
            {
                removed.Add(old);
                old.Parent = null;
            }

            added.AddRange(next.Where(i => !(_items.TryGetValue(i.Id, out var existing) && ReferenceEquals(existing, i))));
            _items.Clear();
            _order.Clear();
            foreach (var item in next)
            {
                _items[item.Id] = item;
                _order.Add(item);
                item.Parent = _owner;
            }
        }

        if (added.Count > 0 || removed.Count > 0)
        {
            Changed?.Invoke(this, new TestItemCollectionChangedEventArgs(added, removed));
        }
    }

    /// <summary>A snapshot of the items, in insertion order.</summary>
    public IEnumerator<TestItem> GetEnumerator()
    {
        List<TestItem> snapshot;
        lock (_gate)
        {
            snapshot = [.. _order];
        }

        return snapshot.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
