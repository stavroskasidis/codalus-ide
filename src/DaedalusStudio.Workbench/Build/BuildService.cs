using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Build;

/// <summary>A build that started: Build, Rebuild or Clean of the solution (<see cref="Project"/> null) or a project.</summary>
public sealed record BuildRequest(string Target, string? Project);

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

    /// <summary>
    /// Builds the solution or a project. Fails if a build is already running. Never throws for build failures or
    /// cancellation: the result says what happened, and host errors become a failed result with the message in the
    /// Build channel.
    /// </summary>
    public async Task<BuildResultInfo> BuildAsync(string target, string? project, CancellationToken cancellationToken)
    {
        var request = new BuildRequest(target, project);
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
            result = await _solutions.BuildAsync(target, project, cancellation.Token).ConfigureAwait(false);
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
