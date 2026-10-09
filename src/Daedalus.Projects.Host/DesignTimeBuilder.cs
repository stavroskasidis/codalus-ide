using System.Collections.Concurrent;
using Daedalus.Projects.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace Daedalus.Projects.Host;

/// <summary>
/// Design-time builds (ADR 0005): runs <c>Compile</c> and <c>CoreCompile</c> with <c>SkipCompilerExecution</c> and
/// <c>ProvideCommandLineArgs</c>, the way Roslyn's own project loading does, and returns the compiler command line.
/// Referenced projects are not built (<c>BuildingInsideVisualStudio</c> makes <c>ResolveProjectReferences</c> ask
/// them for their target path only). One batch at a time, through a <see cref="BuildManager"/> of its own.
/// </summary>
internal sealed class DesignTimeBuilder(ProjectCollection collection) : IDisposable
{
    private static readonly string[] s_targets = ["Compile", "CoreCompile"];

    private readonly BuildManager _manager = new("daedalus-designtime");
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>One project to build: its configuration and, for multi-targeted projects, the framework.</summary>
    public sealed record Request(string Path, string Configuration, string Platform, string? TargetFramework);

    /// <summary>
    /// Builds <paramref name="requests"/>, calling <paramref name="completed"/> as each one finishes. A newer batch
    /// waits for this one; cancelling the token cancels the submissions in flight.
    /// </summary>
    public async Task BuildAsync(
        IReadOnlyList<Request> requests,
        IReadOnlyDictionary<string, string> solutionProperties,
        Func<CompilerInputsInfo, Task> completed,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var errors = new ErrorCollector();
        try
        {
            _manager.BeginBuild(new BuildParameters(collection)
            {
                Loggers = [errors],
                MaxNodeCount = 1,
                EnableNodeReuse = false,
                DisableInProcNode = false,
            });
            try
            {
                await using var registration = cancellationToken.Register(_manager.CancelAllSubmissions).ConfigureAwait(false);
                var pending = requests.Select(r => BuildOneAsync(r, solutionProperties, errors, completed, cancellationToken)).ToList();
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            finally
            {
                _manager.EndBuild();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task BuildOneAsync(
        Request request,
        IReadOnlyDictionary<string, string> solutionProperties,
        ErrorCollector errors,
        Func<CompilerInputsInfo, Task> completed,
        CancellationToken cancellationToken)
    {
        var language = LanguageOf(request.Path);
        var framework = request.TargetFramework ?? string.Empty;
        CompilerInputsInfo inputs;
        try
        {
            var global = new Dictionary<string, string>(solutionProperties, StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = request.Configuration,
                ["Platform"] = request.Platform,
                ["DesignTimeBuild"] = "true",
                ["SkipCompilerExecution"] = "true",
                ["ProvideCommandLineArgs"] = "true",
                ["BuildingInsideVisualStudio"] = "true",
                ["BuildProjectReferences"] = "false",
                ["ShouldUnsetParentConfigurationAndPlatform"] = "false",
                ["AlwaysCompileMarkupFilesInSeparateDomain"] = "false",
            };
            if (request.TargetFramework is { } tfm)
            {
                global["TargetFramework"] = tfm;
            }

            var data = new BuildRequestData(request.Path, global, null, s_targets, null, BuildRequestDataFlags.ProvideProjectStateAfterBuild);
            var result = await ExecuteAsync(_manager.PendBuildRequest(data)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var state = result.ProjectStateAfterBuild;
            var arguments = state?.GetItems(language switch { "VB" => "VbcCommandLineArgs", "F#" => "FscCommandLineArgs", _ => "CscCommandLineArgs" })
                .Select(i => i.EvaluatedInclude).ToArray() ?? [];
            if (state is not null && framework.Length == 0)
            {
                framework = state.GetPropertyValue("TargetFramework");
            }

            var ok = result.OverallResult == BuildResultCode.Success && arguments.Length > 0;
            var directory = Path.GetDirectoryName(request.Path)!;
            inputs = new CompilerInputsInfo(
                request.Path,
                framework,
                language,
                ok ? CompilerInputStates.Ok : CompilerInputStates.Failed,
                ok ? null : errors.For(request.Path) ?? result.Exception?.Message ?? "The design-time build produced no compiler command line.",
                arguments,
                [.. state?.GetItems("ProjectReference").Select(i => Path.GetFullPath(Path.Combine(directory, i.EvaluatedInclude.Replace('\\', Path.DirectorySeparatorChar)))) ?? []],
                FullOrNull(directory, state?.GetPropertyValue("TargetPath")),
                FullOrNull(directory, state?.GetPropertyValue("TargetRefPath")));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            inputs = new CompilerInputsInfo(request.Path, framework, language, CompilerInputStates.Failed, ex.Message, [], [], null, null);
        }

        await completed(inputs).ConfigureAwait(false);
    }

    private static Task<BuildResult> ExecuteAsync(BuildSubmission submission)
    {
        var done = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        submission.ExecuteAsync(s => done.TrySetResult(s.BuildResult!), null);
        return done.Task;
    }

    private static string? FullOrNull(string directory, string? path) =>
        string.IsNullOrEmpty(path) ? null : Path.GetFullPath(Path.Combine(directory, path));

    public static string LanguageOf(string projectPath) => Path.GetExtension(projectPath).ToLowerInvariant() switch
    {
        ".vbproj" => "VB",
        ".fsproj" => "F#",
        _ => "C#",
    };

    public void Dispose()
    {
        _manager.Dispose();
        _gate.Dispose();
    }

    /// <summary>Keeps the first few errors of each project, so a failed design-time build can say why.</summary>
    private sealed class ErrorCollector : ILogger
    {
        private readonly ConcurrentDictionary<string, List<string>> _errors = new(StringComparer.OrdinalIgnoreCase);

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;

        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource) => eventSource.ErrorRaised += (_, e) =>
        {
            var list = _errors.GetOrAdd(e.ProjectFile ?? string.Empty, _ => []);
            lock (list)
            {
                if (list.Count < 3)
                {
                    list.Add(string.IsNullOrEmpty(e.Code) ? e.Message ?? string.Empty : $"{e.Code}: {e.Message}");
                }
            }
        };

        public string? For(string projectPath)
        {
            if (!_errors.TryGetValue(projectPath, out var list))
            {
                return null;
            }

            lock (list)
            {
                return list.Count == 0 ? null : string.Join(Environment.NewLine, list);
            }
        }

        public void Shutdown()
        {
        }
    }
}
