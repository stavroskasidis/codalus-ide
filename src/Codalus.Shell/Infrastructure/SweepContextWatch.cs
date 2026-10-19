using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Codalus.Workbench.Context;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The command sweep's view of the context (<c>shell.commandSweep</c>): it waits for the context to settle before each
/// entry point, and says which keys changed when a command it chose isn't enabled any more. Work a command starts
/// reaches the context keys a step later, through a host's notification and a posted UI job (an Exclude From Project
/// reaches the Solution Explorer's selection through a refresh posted at Background priority): an entry point that
/// runs before then runs in the context the previous one left behind, not the scenario's.
/// </summary>
internal sealed class SweepContextWatch
{
    private readonly ContextKeyService _context;
    private HashSet<string> _keys = new(StringComparer.Ordinal);
    private long _changes;

    public SweepContextWatch(ContextKeyService context)
    {
        _context = context;
        _context.Changed += OnChanged;
    }

    /// <summary>The changes to the watched keys so far.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>Watches these keys from now on (those a command's enablement reads, and the focus).</summary>
    public void Watch(IEnumerable<string> keys) => Volatile.Write(ref _keys, new HashSet<string>(keys, StringComparer.Ordinal));

    /// <summary>
    /// Waits until the UI thread has run every job queued above idle priority, <paramref name="busy"/> says nothing
    /// is in progress (a project evaluation), and no watched key changed meanwhile, at most <paramref name="limit"/>.
    /// False when it didn't settle in time.
    /// </summary>
    public async Task<bool> SettleAsync(Func<bool> busy, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var changes = Changes;
            try
            {
                // ContextIdle runs after every Background job queued before it (the Solution Explorer's refresh is one).
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).GetTask()
                    .WaitAsync(Max(limit - clock.Elapsed, TimeSpan.FromMilliseconds(1))).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return false;
            }

            if (Changes == changes && !busy())
            {
                return true;
            }

            if (clock.Elapsed >= limit)
            {
                return false;
            }

            await Task.Delay(20).ConfigureAwait(true);
        }
    }

    /// <summary>The keys <paramref name="clause"/> reads in <paramref name="context"/>, with their values, in the order it reads them.</summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> Read(WhenClause clause, IContextKeyReader context)
    {
        var recorder = new Recorder(context);
        clause.Evaluate(recorder);
        return recorder.Values;
    }

    /// <summary>
    /// What changed in the keys <paramref name="clause"/> reads between <paramref name="expected"/> (read when the
    /// sweep chose to run the command) and <paramref name="context"/> now: "explorerItemInProject was True, is False".
    /// </summary>
    public static string Differences(WhenClause clause, IReadOnlyList<KeyValuePair<string, object?>> expected, IContextKeyReader context)
    {
        var now = Read(clause, context);
        var then = expected.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var differences = new List<string>();
        foreach (var key in expected.Select(p => p.Key).Concat(now.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
        {
            var value = context.GetValue(key);
            if (!then.TryGetValue(key, out var was))
            {
                differences.Add($"{key} is {Show(value)} (not read before)");
            }
            else if (!Equals(was, value))
            {
                differences.Add($"{key} was {Show(was)}, is {Show(value)}");
            }
        }

        return differences.Count > 0
            ? "context: " + string.Join(", ", differences)
            : "context: no key it reads changed (" + string.Join(", ", now.Select(p => $"{p.Key} = {Show(p.Value)}")) + ")";
    }

    /// <summary>Stops watching (the end of the sweep).</summary>
    public void Stop() => _context.Changed -= OnChanged;

    private void OnChanged(object? sender, string key)
    {
        if (Volatile.Read(ref _keys).Contains(key))
        {
            Interlocked.Increment(ref _changes);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Show(object? value) => value switch
    {
        null => "unset",
        string s => $"'{s}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "unset",
    };

    private sealed class Recorder(IContextKeyReader inner) : IContextKeyReader
    {
        public List<KeyValuePair<string, object?>> Values { get; } = [];

        public object? GetValue(string key)
        {
            var value = inner.GetValue(key);
            if (!Values.Exists(p => p.Key == key))
            {
                Values.Add(new(key, value));
            }

            return value;
        }
    }
}
