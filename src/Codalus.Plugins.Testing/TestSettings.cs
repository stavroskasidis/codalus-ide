using System.Collections.Concurrent;
using System.Text.Json;

namespace Codalus.Plugins.Testing;

/// <summary>Settings in a test: unset keys give the plugin its fallback, as the IDE does with a default.</summary>
public sealed class TestSettings : ISettings
{
    private readonly ConcurrentDictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Sets <paramref name="key"/> (null removes it) and raises <see cref="Changed"/>.</summary>
    public void Set(string key, object? value)
    {
        if (value is null)
        {
            _values.TryRemove(key, out _);
        }
        else
        {
            _values[key] = TestCommands.ToJson(value);
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs([key]));
    }

    /// <inheritdoc/>
    public T GetValue<T>(string key, T fallback)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        try
        {
            return value.Deserialize<T>(TestPluginContext.JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback; // a value of another type, as the IDE treats an invalid setting
        }
    }
}

/// <summary>What a plugin logged.</summary>
public sealed class TestLog : IPluginLog
{
    private readonly ConcurrentQueue<(PluginLogLevel Level, string Message)> _entries = new();

    /// <summary>The entries, in order.</summary>
    public IReadOnlyList<(PluginLogLevel Level, string Message)> Entries => [.. _entries];

    /// <inheritdoc/>
    public void Write(PluginLogLevel level, string message) => _entries.Enqueue((level, message));
}

/// <summary>A plugin's secrets, in memory.</summary>
public sealed class TestSecrets : ISecrets
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>What is stored, by key.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <inheritdoc/>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    /// <inheritdoc/>
    public Task StoreAsync(string key, string value, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
