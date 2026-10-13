using Codalus.Debugging;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// A data tip pinned to a source line (M14b): its expression is evaluated again each time the program pauses, and the
/// last value stays shown after the session ends. Kept with the solution, comment included.
/// </summary>
public sealed class PinnedDataTip
{
    internal PinnedDataTip(string path, int line, string expression, string? comment, string? value)
    {
        Path = path;
        Line = line;
        Expression = expression;
        Comment = comment;
        Value = value;
    }

    public string Path { get; }

    /// <summary>The 1-based line the tip is pinned to.</summary>
    public int Line { get; internal set; }

    public string Expression { get; }

    public string? Comment { get; internal set; }

    /// <summary>The last value (null: not evaluated yet).</summary>
    public string? Value { get; internal set; }

    /// <summary>Whether the value changed at the last pause.</summary>
    public bool Changed { get; internal set; }

    /// <summary>Whether the last evaluation failed (out of scope, for example); <see cref="Value"/> holds the message.</summary>
    public bool IsError { get; internal set; }
}

/// <summary>The pinned data tips of the open solution.</summary>
public sealed class PinnedDataTipStore
{
    private readonly Lock _gate = new();
    private readonly List<PinnedDataTip> _tips = [];

    /// <summary>Raised with the file whose tips changed (null: all of them), on any thread.</summary>
    public event EventHandler<string?>? Changed;

    public IReadOnlyList<PinnedDataTip> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _tips];
            }
        }
    }

    public IReadOnlyList<PinnedDataTip> For(string path)
    {
        lock (_gate)
        {
            return [.. _tips.Where(t => SolutionService.PathComparer.Equals(t.Path, path)).OrderBy(t => t.Line)];
        }
    }

    /// <summary>Pins <paramref name="expression"/> to a line (the existing tip when it is already pinned there).</summary>
    public PinnedDataTip Pin(string path, int line, string expression, string? value = null)
    {
        PinnedDataTip tip;
        lock (_gate)
        {
            if (_tips.FirstOrDefault(t => t.Line == line && t.Expression == expression && SolutionService.PathComparer.Equals(t.Path, path)) is { } existing)
            {
                return existing;
            }

            tip = new PinnedDataTip(path, line, expression, null, value);
            _tips.Add(tip);
        }

        Changed?.Invoke(this, path);
        return tip;
    }

    public void Unpin(PinnedDataTip tip)
    {
        lock (_gate)
        {
            if (!_tips.Remove(tip))
            {
                return;
            }
        }

        Changed?.Invoke(this, tip.Path);
    }

    public void SetComment(PinnedDataTip tip, string? comment)
    {
        tip.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        Changed?.Invoke(this, tip.Path);
    }

    /// <summary>Debug → Clear All DataTips.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_tips.Count == 0)
            {
                return;
            }

            _tips.Clear();
        }

        Changed?.Invoke(this, null);
    }

    /// <summary>Restores a solution's tips (their values are evaluated at the next pause).</summary>
    public void Load(IEnumerable<(string Path, int Line, string Expression, string? Comment)> tips)
    {
        lock (_gate)
        {
            _tips.Clear();
            _tips.AddRange(tips.Select(t => new PinnedDataTip(t.Path, t.Line, t.Expression, t.Comment, null)));
        }

        Changed?.Invoke(this, null);
    }

    /// <summary>Moves a file's tips after an edit (old line → new line; dropped when the line is gone).</summary>
    public void Relocate(string path, Func<int, int?> map)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var tip in _tips.Where(t => SolutionService.PathComparer.Equals(t.Path, path)).ToList())
            {
                if (map(tip.Line) is not { } line)
                {
                    _tips.Remove(tip);
                    changed = true;
                }
                else if (line != tip.Line)
                {
                    tip.Line = line;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Evaluates every tip in the current frame; a value that differs from the last one is marked changed.</summary>
    public async Task RefreshAsync(Func<string, CancellationToken, Task<EvaluateResponse>> evaluate, CancellationToken cancellationToken = default)
    {
        var tips = All;
        if (tips.Count == 0)
        {
            return;
        }

        foreach (var tip in tips)
        {
            string value;
            bool error;
            try
            {
                value = (await evaluate(tip.Expression, cancellationToken).ConfigureAwait(false)).Result;
                error = false;
            }
            catch (DapException ex)
            {
                value = ex.Message;
                error = true;
            }
            catch (Exception ex) when (ex is DapConnectionLostException or InvalidOperationException or OperationCanceledException)
            {
                return;
            }

            tip.Changed = !error && !tip.IsError && tip.Value is not null && tip.Value != value;
            tip.IsError = error;
            tip.Value = value;
        }

        Changed?.Invoke(this, null);
    }
}
