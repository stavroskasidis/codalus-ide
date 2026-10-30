using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.ViewModels;

/// <summary>A node of Parallel Stacks: the frames a group of threads shares, and where they branch.</summary>
public sealed class ParallelStackNodeViewModel(ParallelStackNode node, IReadOnlyDictionary<int, string> names, int? current)
{
    public ParallelStackNode Node { get; } = node;

    public string Header => Node.Threads.Count == 1
        ? $"Thread {Label(Node.Threads[0])}"
        : $"{Node.Threads.Count} threads: {string.Join(", ", Node.Threads.Take(8).Select(Label))}{(Node.Threads.Count > 8 ? ", …" : string.Empty)}";

    public bool HasCurrentThread => current is { } id && Node.Threads.Contains(id);

    public IReadOnlyList<string> Frames => Node.Frames;

    public IReadOnlyList<ParallelStackNodeViewModel> Children { get; } = [.. node.Children.Select(c => new ParallelStackNodeViewModel(c, names, current))];

    public string AccessibleName => $"{Header}: {string.Join(", ", Node.Frames)}";

    private string Label(int id) => names.TryGetValue(id, out var name) && name.Length > 0 ? $"{id} ({name})" : id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Parallel Stacks (M14b): every thread's call stack, merged — the callers threads share once, then their branches —
/// refreshed at every stop while the window is shown, and once when it's shown (M56: a request per thread). The node
/// with the active thread is marked.
/// </summary>
public sealed class ParallelStacksViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.parallelStacks";
    private const int Levels = 64;

    private readonly DebugService _debugger;
    private readonly ShownRefresh _refresh;
    private int _generation;

    public ParallelStacksViewModel(DebugService debugger)
    {
        _debugger = debugger;
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(_refresh.Request);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                _generation++;
                Roots.Reset([]);
                OnPropertyChanged(nameof(IsEmpty));
            }
        });
    }

    public ResettableCollection<ParallelStackNodeViewModel> Roots { get; } = [];

    void IShownAware.SetViewShown(bool shown) => _refresh.SetShown(shown);

    public bool IsEmpty => Roots.Count == 0;

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        IReadOnlyDictionary<int, IReadOnlyList<StackFrame>> stacks;
        try
        {
            stacks = await _debugger.AllStacksAsync(Levels, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            stacks = new Dictionary<int, IReadOnlyList<StackFrame>>();
        }

        if (generation != _generation)
        {
            return;
        }

        var names = _debugger.Threads.ToDictionary(t => t.Id, t => t.Name);
        var tree = ParallelStacks.Build(stacks.ToDictionary(s => s.Key, s => (IReadOnlyList<string>)[.. s.Value.Select(f => f.Name)]));
        Roots.Reset([.. tree.Select(node => new ParallelStackNodeViewModel(node, names, _debugger.SelectedThreadId))]);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed record ParallelWatchRow(int ThreadId, string Thread, string Frame, string Value)
{
    public string AccessibleName => $"Thread {Thread}, {Frame}: {Value}";
}

/// <summary>
/// Parallel Watch (M14b): one expression evaluated on every thread, in each thread's innermost frame that has source
/// (its own code), at every stop while the window is shown, and once when it's shown (M56).
/// </summary>
public sealed class ParallelWatchViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.parallelWatch";

    private readonly DebugService _debugger;
    private readonly ShownRefresh _refresh;
    private string _expression = string.Empty;
    private int _generation;

    public ParallelWatchViewModel(DebugService debugger)
    {
        _debugger = debugger;
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(_refresh.Request);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                _generation++;
                Rows.Clear();
            }
        });
    }

    public string Expression
    {
        get => _expression;
        set => SetField(ref _expression, value);
    }

    public ObservableCollection<ParallelWatchRow> Rows { get; } = [];

    void IShownAware.SetViewShown(bool shown) => _refresh.SetShown(shown);

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        var expression = Expression.Trim();
        Rows.Clear();
        if (expression.Length == 0 || _debugger.State != DebugState.Stopped)
        {
            return;
        }

        var stacks = await _debugger.AllStacksAsync(32, CancellationToken.None).ConfigureAwait(true);
        if (generation != _generation)
        {
            return; // a newer refresh (the next stop, another expression) reads its own
        }
        var names = _debugger.Threads.ToDictionary(t => t.Id, t => t.Name);
        foreach (var (thread, frames) in stacks.OrderBy(s => s.Key))
        {
            var frame = frames.FirstOrDefault(f => f.Source?.Path is not null) ?? frames[0];
            string value;
            try
            {
                value = (await _debugger.EvaluateInFrameAsync(expression, frame.Id, CancellationToken.None).ConfigureAwait(true)).Result;
            }
            catch (Exception ex) when (ex is DapException or InvalidOperationException or TimeoutException)
            {
                value = $"<{ex.Message}>";
            }

            if (generation != _generation)
            {
                return;
            }

            Rows.Add(new ParallelWatchRow(thread, names.TryGetValue(thread, out var name) && name.Length > 0 ? $"{thread} ({name})" : thread.ToString(CultureInfo.InvariantCulture), frame.Name, value));
        }
    }
}
