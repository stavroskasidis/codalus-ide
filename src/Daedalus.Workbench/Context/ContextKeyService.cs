using System.Collections.Concurrent;

namespace Daedalus.Workbench.Context;

public interface IContextKeyReader
{
    object? GetValue(string key);
}

/// <summary>
/// Global context keys (e.g. <c>focusedView</c>, <c>theme</c>, <c>isMac</c>, <c>inCommandPalette</c>) that
/// when-clauses are evaluated against. Thread-safe; <see cref="Changed"/> fires on the setting thread.
/// </summary>
public sealed class ContextKeyService : IContextKeyReader
{
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);

    public event EventHandler<string>? Changed;

    public object? GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, object? value)
    {
        if (value is null)
        {
            if (_values.TryRemove(key, out _))
            {
                Changed?.Invoke(this, key);
            }

            return;
        }

        if (!_values.TryGetValue(key, out var existing) || !Equals(existing, value))
        {
            _values[key] = value;
            Changed?.Invoke(this, key);
        }
    }

    /// <summary>A reader that overlays <paramref name="overrides"/> on top of the global keys.</summary>
    public IContextKeyReader With(IReadOnlyDictionary<string, object?> overrides) => new Overlay(this, overrides);

    private sealed class Overlay(IContextKeyReader inner, IReadOnlyDictionary<string, object?> overrides) : IContextKeyReader
    {
        public object? GetValue(string key) => overrides.TryGetValue(key, out var value) ? value : inner.GetValue(key);
    }
}
