using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Build;

/// <summary>
/// A build that started: Build, Rebuild or Clean of the solution (<see cref="Project"/> null) or a project, or a batch
/// build (<see cref="Batch"/>: how many entries).
/// </summary>
public sealed record BuildRequest(string Target, string? Project)
{
    public int Batch { get; init; }
}

/// <summary>A build that finished.</summary>
public sealed record BuildCompleted(BuildRequest Request, BuildResultInfo Result);

/// <summary>
/// Builds through the build host (ADR 0006): one at a time, output in the Build channel, errors and warnings kept
/// for the Error List until the next build starts. Events may be raised on any thread.
/// </summary>
public sealed class BuildService
{
    public const string OutputChannelId = "daedalus.build";

    private readonly SolutionService _solutions;
    private readonly OutputService _output;
    private readonly Lock _gate = new();
    private readonly List<BuildDiagnosticInfo> _diagnostics = [];
    private CancellationTokenSource? _current;

    public BuildService(SolutionService solutions, OutputService output)
    {
        _solutions = solutions;
        _output = output;
        solutions.BuildOutput += (_, line) => Channel.AppendLine(line);
        solutions.BuildDiagnostic += (_, diagnostic) =>
        {
            lock (_gate)
            {
                _diagnostics.Add(diagnostic);
            }

            DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        };
        solutions.SolutionChanged += (_, _) =>
        {
            if (!solutions.IsOpen)
            {
                ClearDiagnostics();
            }
        };
    }

    public OutputChannel Channel => _output.GetOrCreate(OutputChannelId, "Build");

    /// <summary>The request of the build in progress, or null.</summary>
    public BuildRequest? Current { get; private set; }

    public bool IsBuilding => Current is not null;

    public BuildCompleted? LastBuild { get; private set; }

    /// <summary>The errors and warnings of the current (or last) build.</summary>
    public IReadOnlyList<BuildDiagnosticInfo> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return [.. _diagnostics];
            }
        }
    }

    public event EventHandler<BuildRequest>? Started;

    public event EventHandler<BuildCompleted>? Finished;

    public event EventHandler? DiagnosticsChanged;

    /// <summary>How builds run (ADR 0034); the shell sets it from the build settings. Null: the host's defaults.</summary>
    public Func<BuildOptions?> Options { get; set; } = () => null;

    /// <summary>The last batch build's result, entry by entry.</summary>
    public BatchBuildResult? LastBatch { get; private set; }

    /// <summary>
    /// Builds the solution or a project. Fails if a build is already running. Never throws for build failures or
    /// cancellation: the result says what happened, and host errors become a failed result with the message in the
    /// Build channel.
    /// </summary>
    public Task<BuildResultInfo> BuildAsync(string target, string? project, CancellationToken cancellationToken) =>
        RunAsync(new BuildRequest(target, project), token => _solutions.BuildAsync(target, project, token, Options()), cancellationToken);

    /// <summary>Batch Build: each project × configuration × platform in turn (ADR 0034). Never throws for build failures.</summary>
    public async Task<BatchBuildResult> BatchBuildAsync(string target, IReadOnlyList<BatchBuildEntry> entries, CancellationToken cancellationToken)
    {
        BatchBuildResult? batch = null;
        await RunAsync(new BuildRequest(target, null) { Batch = entries.Count }, async token =>
        {
            batch = await _solutions.BatchBuildAsync(new BatchBuildParams(target, [.. entries], Options()), token).ConfigureAwait(false);
            return new BuildResultInfo(batch.Succeeded, batch.Canceled, batch.Errors, batch.Warnings, batch.ElapsedMilliseconds);
        }, cancellationToken).ConfigureAwait(false);
        LastBatch = batch ?? new BatchBuildResult(false, false, 0, 0, 0, []);
        return LastBatch;
    }

    private async Task<BuildResultInfo> RunAsync(BuildRequest request, Func<CancellationToken, Task<BuildResultInfo>> build, CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (Current is not null)
            {
                cancellation.Dispose();
                throw new InvalidOperationException("A build is already running.");
            }

            Current = request;
            _current = cancellation;
        }

        ClearDiagnostics();
        Channel.Clear();
        Started?.Invoke(this, request);
        BuildResultInfo result;
        try
        {
            result = await build(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Channel.AppendLine($"The build could not run: {ex.Message}");
            result = new BuildResultInfo(false, cancellation.IsCancellationRequested, 0, 0, 0);
        }
        finally
        {
            lock (_gate)
            {
                Current = null;
                _current = null;
            }

            cancellation.Dispose();
        }

        var completed = new BuildCompleted(request, result);
        LastBuild = completed;
        Finished?.Invoke(this, completed);
        return result;
    }

    /// <summary>Stops the build in progress (its result reports the cancellation).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            try
            {
                _current?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void ClearDiagnostics()
    {
        lock (_gate)
        {
            if (_diagnostics.Count == 0)
            {
                return;
            }

            _diagnostics.Clear();
        }

        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }
}
