using System.IO.Pipes;
using System.Text;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Build;

/// <summary>
/// The IDE's end of a Hot Reload run in a console of its own (a console window or the integrated terminal): there the
/// app gets the console <c>dotnet watch</c> runs in, and the IDE can't read its output, so the startup hook in
/// <c>dotnet-watch</c> (<c>WatchRelay</c>, <see cref="PipeVariable"/>) sends this pipe a copy of its messages, which
/// feed the run's <see cref="HotReloadSession"/> as its output would.
/// </summary>
public sealed class HotReloadRelay : IDisposable
{
    public const string PipeVariable = "CODALUS_WATCH_RELAY_PIPE";

    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly string _hook;
    private int _disposed;

    private HotReloadRelay(string hook)
    {
        _hook = hook;
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>The pipe's name, unique to the run.</summary>
    public string PipeName { get; } = "codalus-watch-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A relay, or null when the startup hook isn't there (the run then writes to the Output window).</summary>
    public static HotReloadRelay? Create() => HeldLaunch.ResolveHook() is { } hook ? new HotReloadRelay(hook) : null;

    /// <summary>
    /// The environment of <c>dotnet watch</c> (<paramref name="planned"/>'s): the startup hook added to
    /// <c>DOTNET_STARTUP_HOOKS</c>, and the pipe.
    /// </summary>
    public Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> planned)
    {
        var environment = new Dictionary<string, string>(planned, StringComparer.Ordinal);
        var existing = planned.TryGetValue(HeldLaunch.StartupHooksVariable, out var fromPlan) ? fromPlan : System.Environment.GetEnvironmentVariable(HeldLaunch.StartupHooksVariable);
        environment[HeldLaunch.StartupHooksVariable] = string.IsNullOrEmpty(existing) ? _hook : _hook + Path.PathSeparator + existing;
        environment[PipeVariable] = PipeName;
        return environment;
    }

    /// <summary>Reads the lines <c>dotnet-watch</c> sends, on a background thread, until it ends or this is disposed.</summary>
    public void Start(Action<string> onLine) => _ = Task.Run(() => ReadAsync(onLine));

    private async Task ReadAsync(Action<string> onLine)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                onLine(line);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The run ended.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop.Cancel();
        _pipe.Dispose();
        _stop.Dispose();
    }
}
