using System.Collections.Concurrent;
using System.Text.Json;

namespace Codalus.Plugins.Testing;

/// <summary>
/// Commands in a test: the plugin's own (run them with <see cref="ExecuteAsync"/>), and the IDE's, which a test answers
/// with <see cref="OnShellCommand"/>. Every call is recorded in <see cref="Executed"/>.
/// </summary>
public sealed class TestCommands : ICommands
{
    private readonly ConcurrentDictionary<string, Func<CommandInvocation, CancellationToken, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<IReadOnlyList<JsonElement>, object?>> _shell = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Command, IReadOnlyList<JsonElement> Args)> _executed = new();

    /// <summary>The ids of the commands the plugin registered and hasn't disposed.</summary>
    public IReadOnlyCollection<string> Registered => [.. _handlers.Keys];

    /// <summary>Every command executed, the plugin's and the IDE's, with its arguments, in order.</summary>
    public IReadOnlyList<(string Command, IReadOnlyList<JsonElement> Args)> Executed => [.. _executed];

    /// <inheritdoc/>
    public IDisposable Register(string commandId, Func<CommandInvocation, CancellationToken, Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(commandId, handler))
        {
            throw new InvalidOperationException($"The command '{commandId}' is registered already.");
        }

        return new Registration(() => _handlers.TryRemove(commandId, out _));
    }

    /// <inheritdoc/>
    public IDisposable Register(string commandId, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(commandId, (_, _) =>
        {
            handler();
            return Task.FromResult<object?>(null);
        });
    }

    /// <summary>Answers the IDE's command <paramref name="commandId"/> when the plugin executes it.</summary>
    public void OnShellCommand(string commandId, Func<IReadOnlyList<JsonElement>, object?> handler) => _shell[commandId] = handler;

    /// <summary>
    /// Runs a command: the plugin's, as the IDE would (arguments and result through JSON, as over the protocol), or an
    /// IDE command given to <see cref="OnShellCommand"/>. Throws <see cref="InvalidOperationException"/> for any other.
    /// </summary>
    public async Task<JsonElement?> ExecuteAsync(string commandId, CancellationToken cancellationToken, params object?[] args)
    {
        var json = (IReadOnlyList<JsonElement>)[.. (args ?? []).Select(ToJson)];
        _executed.Enqueue((commandId, json));
        if (_handlers.TryGetValue(commandId, out var handler))
        {
            var result = await handler(new CommandInvocation(json), cancellationToken).ConfigureAwait(false);
            return result is null ? null : ToJson(result);
        }

        if (_shell.TryGetValue(commandId, out var shell))
        {
            return shell(json) is { } value ? ToJson(value) : null;
        }

        throw new InvalidOperationException($"No command '{commandId}': the plugin didn't register it, and the test didn't answer it with OnShellCommand.");
    }

    /// <summary>Runs the plugin's command and reads its result as <typeparamref name="T"/>.</summary>
    public async Task<T?> ExecuteAsync<T>(string commandId, params object?[] args) =>
        await ExecuteAsync(commandId, CancellationToken.None, args).ConfigureAwait(false) is { } result ? result.Deserialize<T>(TestPluginContext.JsonOptions) : default;

    internal static JsonElement ToJson(object? value) =>
        value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), TestPluginContext.JsonOptions);
}
