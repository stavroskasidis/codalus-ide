using System.Text.Json;

namespace Codalus.Debugging;

/// <summary>
/// Source paths between this machine and a debug target on another one (M14c): <c>/src/App/Program.cs</c> in a
/// container is <c>D:\work\App\Program.cs</c> here. Mappings are folder prefixes, given (the <c>debug.sourceFileMap</c>
/// setting) or inferred: a remote file that maps to nothing is looked up among <see cref="LocalFiles"/> by its
/// trailing path segments, and a match on its folder and name adds the prefix pair, so breakpoints in the other files of
/// that folder map too. Remote paths are compared case-sensitively (Linux), local ones as the OS does.
/// </summary>
public sealed class SourceMap
{
    private static readonly StringComparison s_local = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Lock _gate = new();
    private readonly List<(string Remote, string Local)> _entries = [];

    public SourceMap(IEnumerable<KeyValuePair<string, string>>? entries = null, IReadOnlyList<string>? localFiles = null)
    {
        foreach (var (remote, local) in entries ?? [])
        {
            Add(remote, local);
        }

        LocalFiles = localFiles ?? [];
    }

    /// <summary>Files of the solution, for inferring mappings.</summary>
    public IReadOnlyList<string> LocalFiles { get; set; }

    /// <summary>The folder pairs, given and inferred (remote first).</summary>
    public IReadOnlyList<(string Remote, string Local)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Adds a folder pair; longer remote prefixes win.</summary>
    public void Add(string remote, string local)
    {
        var r = TrimSeparator(remote.Replace('\\', '/'));
        var l = TrimSeparator(local);
        if (r.Length == 0 || l.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Any(e => e.Remote == r))
            {
                return;
            }

            _entries.Add((r, l));
            _entries.Sort((a, b) => b.Remote.Length.CompareTo(a.Remote.Length));
        }
    }

    /// <summary>The target's path of a file here (unchanged when no mapping covers it).</summary>
    public string ToRemote(string localPath)
    {
        lock (_gate)
        {
            foreach (var (remote, local) in _entries.OrderByDescending(e => e.Local.Length))
            {
                if (StartsWithFolder(localPath, local, s_local))
                {
                    var rest = localPath[local.Length..].TrimStart('\\', '/').Replace('\\', '/');
                    return rest.Length == 0 ? remote : remote + "/" + rest;
                }
            }
        }

        return localPath;
    }

    /// <summary>This machine's path of a file on the target: mapped, as it is when it exists here, or inferred.</summary>
    public string ToLocal(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/');
        lock (_gate)
        {
            foreach (var (remote, local) in _entries)
            {
                if (StartsWithFolder(normalized, remote, StringComparison.Ordinal))
                {
                    var rest = normalized[remote.Length..].TrimStart('/');
                    return rest.Length == 0 ? local : Path.Combine(local, rest.Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }

        if (File.Exists(remotePath))
        {
            return remotePath;
        }

        return Infer(normalized) ?? remotePath;
    }

    /// <summary>The local file sharing the most trailing segments (two at least: its folder and name) with <paramref name="remote"/>.</summary>
    private string? Infer(string remote)
    {
        var remoteParts = remote.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (remoteParts.Length < 2)
        {
            return null;
        }

        string? best = null;
        var bestCount = 1;
        var ambiguous = false;
        foreach (var file in LocalFiles)
        {
            var localParts = file.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var count = 0;
            while (count < remoteParts.Length && count < localParts.Length
                   && string.Equals(remoteParts[^(count + 1)], localParts[^(count + 1)], s_local))
            {
                count++;
            }

            if (count > bestCount)
            {
                (best, bestCount, ambiguous) = (file, count, false);
            }
            else if (count == bestCount && best is not null)
            {
                ambiguous = true;
            }
        }

        if (best is null || ambiguous)
        {
            return null;
        }

        // The folders above the matched segments: /src/App ↔ D:\work\App when Program.cs and App matched.
        var remotePrefix = "/" + string.Join('/', remoteParts[..^bestCount]);
        if (remote.Length > 1 && remote[1] == ':')
        {
            remotePrefix = string.Join('/', remoteParts[..^bestCount]); // a Windows path on the target
        }

        var localPrefix = best;
        for (var i = 0; i < bestCount; i++)
        {
            localPrefix = Path.GetDirectoryName(localPrefix) ?? localPrefix;
        }

        if (remoteParts.Length > bestCount)
        {
            Add(remotePrefix, localPrefix);
        }

        return best;
    }

    private static bool StartsWithFolder(string path, string folder, StringComparison comparison) =>
        path.StartsWith(folder, comparison) && (path.Length == folder.Length || path[folder.Length] is '/' or '\\');

    private static string TrimSeparator(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;
}

/// <summary>
/// An adapter on another machine seen through a <see cref="SourceMap"/> (M14c): breakpoints go out with the target's
/// paths, and frames, breakpoints and output come back with this machine's.
/// </summary>
public sealed class PathMappingDebugAdapter : IDebugAdapter
{
    public PathMappingDebugAdapter(DebugAdapter inner, SourceMap map)
    {
        Inner = inner;
        Map = map;
        inner.EventReceived += OnEventAsync;
        inner.Log += (_, line) => Log?.Invoke(this, line);
    }

    /// <summary>The adapter itself (for <c>launch</c> and <c>attach</c>, whose arguments carry the target's paths).</summary>
    public DebugAdapter Inner { get; }

    public SourceMap Map { get; }

    public Capabilities Capabilities => Inner.Capabilities;

    public Task Initialized => Inner.Initialized;

    public Task Closed => Inner.Closed;

    public event Func<DapEvent, Task>? EventReceived;

    public event EventHandler<string>? Log;

    public string Describe() => Inner.Describe();

    private static DapJsonContext Json => DapJsonContext.Default;

    private Task OnEventAsync(DapEvent e)
    {
        var body = e.Name switch
        {
            DapEvents.Breakpoint when DebugAdapter.Read(e.Body, Json.BreakpointEvent) is { Breakpoint.Source.Path: not null } changed =>
                JsonSerializer.SerializeToElement(changed with { Breakpoint = Local(changed.Breakpoint) }, Json.BreakpointEvent),
            DapEvents.Output when DebugAdapter.Read(e.Body, Json.OutputEvent) is { Source.Path: { } path } output =>
                JsonSerializer.SerializeToElement(output with { Source = output.Source with { Path = Map.ToLocal(path) } }, Json.OutputEvent),
            _ => e.Body,
        };
        return EventReceived?.Invoke(new DapEvent(e.Name, body)) ?? Task.CompletedTask;
    }

    private Breakpoint Local(Breakpoint breakpoint) =>
        breakpoint.Source is { Path: { } path } source ? breakpoint with { Source = source with { Path = Map.ToLocal(path) } } : breakpoint;

    public Task ConfigurationDoneAsync(CancellationToken cancellationToken) => Inner.ConfigurationDoneAsync(cancellationToken);

    public async Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken) =>
        [.. (await Inner.SetBreakpointsAsync(Map.ToRemote(path), breakpoints, cancellationToken).ConfigureAwait(false)).Select(Local)];

    public Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken) => Inner.SetFunctionBreakpointsAsync(breakpoints, cancellationToken);

    public Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null) => Inner.SetExceptionBreakpointsAsync(filters, cancellationToken, options);

    public Task ContinueAsync(int threadId, CancellationToken cancellationToken) => Inner.ContinueAsync(threadId, cancellationToken);

    public Task NextAsync(int threadId, CancellationToken cancellationToken) => Inner.NextAsync(threadId, cancellationToken);

    public Task StepInAsync(int threadId, CancellationToken cancellationToken) => Inner.StepInAsync(threadId, cancellationToken);

    public Task StepOutAsync(int threadId, CancellationToken cancellationToken) => Inner.StepOutAsync(threadId, cancellationToken);

    public Task PauseAsync(int threadId, CancellationToken cancellationToken) => Inner.PauseAsync(threadId, cancellationToken);

    public Task<DapThread[]> ThreadsAsync(CancellationToken cancellationToken) => Inner.ThreadsAsync(cancellationToken);

    public async Task<StackTraceResponse> StackTraceAsync(int threadId, int levels, CancellationToken cancellationToken)
    {
        var response = await Inner.StackTraceAsync(threadId, levels, cancellationToken).ConfigureAwait(false);
        return response with
        {
            StackFrames = [.. response.StackFrames.Select(f => f.Source is { Path: { } path } source ? f with { Source = source with { Path = Map.ToLocal(path) } } : f)],
        };
    }

    public Task<Scope[]> ScopesAsync(int frameId, CancellationToken cancellationToken) => Inner.ScopesAsync(frameId, cancellationToken);

    public Task<Variable[]> VariablesAsync(int variablesReference, CancellationToken cancellationToken) => Inner.VariablesAsync(variablesReference, cancellationToken);

    public Task<Variable[]> VariablesAsync(int variablesReference, VariablesPage page, CancellationToken cancellationToken) => Inner.VariablesAsync(variablesReference, page, cancellationToken);

    public Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken) => Inner.EvaluateAsync(expression, frameId, context, cancellationToken);

    public Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken) => Inner.SetVariableAsync(variablesReference, name, value, cancellationToken);

    public Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken cancellationToken) => Inner.SetExpressionAsync(expression, value, frameId, cancellationToken);

    public Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken) => Inner.ExceptionInfoAsync(threadId, cancellationToken);

    public Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken) => Inner.DisconnectAsync(terminateDebuggee, cancellationToken);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
