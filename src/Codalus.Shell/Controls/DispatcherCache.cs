using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace Codalus.Shell.Controls;

/// <summary>
/// A cache of Avalonia objects (geometries, compiled icons), one table per UI dispatcher. Every Avalonia object keeps the
/// dispatcher it was made on, so one table for the process kept the dispatcher of each value's first use: in the headless
/// tests, each test that first drew an icon stayed reachable, with the views and the shell its dispatcher's timers held.
/// A dispatcher's table goes with it; the app has one dispatcher, so one table.
/// </summary>
internal sealed class DispatcherCache<TKey, TValue> where TKey : notnull
{
    private readonly ConditionalWeakTable<Dispatcher, Dictionary<TKey, TValue>> _tables = new();
    private readonly ConditionalWeakTable<Dispatcher, Dictionary<TKey, TValue>>.CreateValueCallback _create;

    public DispatcherCache(IEqualityComparer<TKey>? comparer = null) =>
        _create = _ => new Dictionary<TKey, TValue>(comparer);

    /// <summary>The UI dispatcher's table; lock it to use it (a theme's icons can be read off the UI thread).</summary>
    public Dictionary<TKey, TValue> Current => _tables.GetValue(Dispatcher.UIThread, _create);
}
