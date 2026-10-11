using System.Collections.ObjectModel;
using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.ViewModels;

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
/// refreshed at every stop. The node with the active thread is marked.
/// </summary>
public sealed class ParallelStacksViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.parallelStacks";
    private const int Levels = 64;

    private readonly DebugService _debugger;
    private int _generation;

    public ParallelStacksViewModel(DebugService debugger)
    {
        _debugger = debugger;
        debugger.Stopped += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                _generation++;
                Roots.Clear();
                OnPropertyChanged(nameof(IsEmpty));
            }
        });
    }

    public ObservableCollection<ParallelStackNodeViewModel> Roots { get; } = [];

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
        Roots.Clear();
        var tree = ParallelStacks.Build(stacks.ToDictionary(s => s.Key, s => (IReadOnlyList<string>)[.. s.Value.Select(f => f.Name)]));
        foreach (var node in tree)
        {
            Roots.Add(new ParallelStackNodeViewModel(node, names, _debugger.SelectedThreadId));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed record ParallelWatchRow(int ThreadId, string Thread, string Frame, string Value)
{
    public string AccessibleName => $"Thread {Thread}, {Frame}: {Value}";
}

/// <summary>
/// Parallel Watch (M14b): one expression evaluated on every thread, in each thread's innermost frame that has source
/// (its own code), at every stop.
/// </summary>
public sealed class ParallelWatchViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.parallelWatch";

    private readonly DebugService _debugger;
    private string _expression = string.Empty;
    private int _generation;

    public ParallelWatchViewModel(DebugService debugger)
    {
        _debugger = debugger;
        debugger.Stopped += (_, _) => Ui.Post(() => _ = RefreshAsync());
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
