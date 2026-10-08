using System.Collections.Concurrent;
using System.Text.Json;
using Daedalus.Workbench.Context;

namespace Daedalus.Workbench.Commands;

/// <summary>Where a command, menu item, key binding or setting came from.</summary>
public sealed record ContributionSource(string Id, bool IsBuiltIn)
{
    public static ContributionSource Shell { get; } = new("daedalus.shell", IsBuiltIn: true);

    public static ContributionSource Plugin(string pluginId, bool builtIn) => new(pluginId, builtIn);
}

/// <summary>Metadata of a command. It can exist before a handler is registered (declarative plugin contributions).</summary>
public sealed record CommandDescriptor(string Id, string Title, ContributionSource Source)
{
    public string? Category { get; init; }

    public WhenClause Enablement { get; init; } = WhenClause.Always;

    /// <summary>Hidden commands are executable but not listed in the command palette.</summary>
    public bool ShowInPalette { get; init; } = true;

    public string DisplayTitle => Category is { Length: > 0 } c ? $"{c}: {Title}" : Title;
}

/// <summary>Arguments and result of a command are JSON values so that they can cross process boundaries.</summary>
public delegate Task<JsonElement?> CommandHandler(IReadOnlyList<JsonElement> args, CancellationToken cancellationToken);

public sealed class CommandExecutedEventArgs(string commandId, TimeSpan duration, Exception? error) : EventArgs
{
    public string CommandId { get; } = commandId;

    public TimeSpan Duration { get; } = duration;

    public Exception? Error { get; } = error;
}

/// <summary>
/// All commands of the workbench. Commands are invoked by id from menus, key bindings, the palette, status
/// items and plugins. Handlers run on the caller's context; UI handlers marshal themselves.
/// </summary>
public sealed class CommandRegistry(ContextKeyService context)
{
    private const int MaxRecent = 30;
    private readonly ConcurrentDictionary<string, CommandDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CommandHandler> _handlers = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly Lock _recentGate = new();

    public event EventHandler? DescriptorsChanged;

    public event EventHandler<CommandExecutedEventArgs>? Executed;

    /// <summary>Called when a command without a handler is invoked (e.g. to activate the contributing plugin).</summary>
    public Func<string, CancellationToken, Task>? MissingHandler { get; set; }

    public IReadOnlyCollection<CommandDescriptor> Descriptors => [.. _descriptors.Values];

    public IReadOnlyList<string> RecentCommandIds
    {
        get
        {
            lock (_recentGate)
            {
                return [.. _recent];
            }
        }
    }

    public CommandDescriptor? Find(string id) => _descriptors.GetValueOrDefault(id);

    public void Declare(CommandDescriptor descriptor)
    {
        _descriptors[descriptor.Id] = descriptor;
        DescriptorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Declares (if needed) and binds a handler. Disposing removes the handler, not the declaration.</summary>
    public IDisposable Register(CommandDescriptor descriptor, CommandHandler handler)
    {
        Declare(descriptor);
        return RegisterHandler(descriptor.Id, handler);
    }

    /// <summary>Convenience for synchronous UI commands without arguments or results.</summary>
    public IDisposable Register(CommandDescriptor descriptor, Action handler) =>
        Register(descriptor, (_, _) =>
        {
            handler();
            return Task.FromResult<JsonElement?>(null);
        });

    public IDisposable RegisterHandler(string id, CommandHandler handler)
    {
        _handlers[id] = handler;
        return new Registration(() => _handlers.TryRemove(new KeyValuePair<string, CommandHandler>(id, handler)));
    }

    public void Remove(string id)
    {
        _handlers.TryRemove(id, out _);
        if (_descriptors.TryRemove(id, out _))
        {
            DescriptorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsEnabled(string id) => _descriptors.TryGetValue(id, out var d) ? d.Enablement.Evaluate(context) : _handlers.ContainsKey(id);

    public Task<JsonElement?> ExecuteAsync(string id, CancellationToken cancellationToken) => ExecuteAsync(id, [], cancellationToken);

    public async Task<JsonElement?> ExecuteAsync(string id, IReadOnlyList<JsonElement> args, CancellationToken cancellationToken)
    {
        if (_descriptors.TryGetValue(id, out var descriptor) && !descriptor.Enablement.Evaluate(context))
        {
            throw new CommandNotEnabledException(id);
        }

        if (!_handlers.ContainsKey(id) && MissingHandler is { } missing && _descriptors.ContainsKey(id))
        {
            await missing(id, cancellationToken).ConfigureAwait(false);
        }

        if (!_handlers.TryGetValue(id, out var handler))
        {
            throw new CommandNotFoundException(id);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            return await handler(args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex;
            throw;
        }
        finally
        {
            Executed?.Invoke(this, new CommandExecutedEventArgs(id, System.Diagnostics.Stopwatch.GetElapsedTime(started), error));
        }
    }

    public void SetRecent(IEnumerable<string> ids)
    {
        lock (_recentGate)
        {
            _recent.Clear();
            foreach (var id in ids.Take(MaxRecent))
            {
                _recent.AddLast(id);
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="id"/> to the front of the most-recently-used list. Only user choices in the command
    /// palette count, not commands run by key bindings, menus or plugins.
    /// </summary>
    public void MarkRecent(string id)
    {
        if (_descriptors.TryGetValue(id, out var d) && !d.ShowInPalette)
        {
            return;
        }

        lock (_recentGate)
        {
            _recent.Remove(id);
            _recent.AddFirst(id);
            while (_recent.Count > MaxRecent)
            {
                _recent.RemoveLast();
            }
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class CommandNotFoundException(string id) : Exception($"Command '{id}' not found.")
{
    public string CommandId { get; } = id;
}

public sealed class CommandNotEnabledException(string id) : Exception($"Command '{id}' is not enabled in the current context.")
{
    public string CommandId { get; } = id;
}
