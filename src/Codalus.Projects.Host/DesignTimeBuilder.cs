using System.Collections.Concurrent;
using Codalus.Projects.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace Codalus.Projects.Host;

/// <summary>
/// Design-time builds (ADR 0005): runs <c>Compile</c> and <c>CoreCompile</c> with <c>SkipCompilerExecution</c> and
/// <c>ProvideCommandLineArgs</c>, the way Roslyn's own project loading does, and returns the compiler command line.
/// Referenced projects are not built (<c>BuildingInsideVisualStudio</c> makes <c>ResolveProjectReferences</c> ask
/// them for their target path only). One batch at a time, through a <see cref="BuildManager"/> of its own;
/// <paramref name="gate"/> is shared with real builds, so the two never run at the same time.
/// </summary>
internal sealed class DesignTimeBuilder(ProjectCollection collection, SemaphoreSlim gate) : IDisposable
{
    private static readonly string[] s_targets = ["Compile", "CoreCompile"];

    private readonly BuildManager _manager = new("codalus-designtime");
    private readonly SemaphoreSlim _gate = gate;

    /// <summary>One project to build: its configuration and, for multi-targeted projects, the framework.</summary>
    public sealed record Request(string Path, string Configuration, string Platform, string? TargetFramework)
    {
        /// <summary>A WinUI 3 project (M44): off Windows, the design-time pass of its XAML compiler is left out.</summary>
        public bool WinUI { get; init; }

        /// <summary>The framework the result names when the project has no <c>TargetFramework</c> (a non-SDK-style project's <c>net472</c>, M43a).</summary>
        public string? FrameworkLabel { get; init; }

        /// <summary>
        /// Loads the project without the imports that are missing (M43a): a non-SDK-style project that imports Visual
        /// Studio's own targets still gets IntelliSense, though it can't build. It builds in this process (see <see cref="InProcess"/>).
        /// </summary>
        public bool IgnoreMissingImports { get; init; }
    }

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
                MaxNodeCount = NodeCount(requests.Count),
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
                // The in-process node clears MSBuild's string cache here: not while this process evaluates (StringCacheGate).
                StringCacheGate.Exclusive(_manager.EndBuild);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// How many MSBuild nodes a batch uses (M34b). The in-process node runs one project at a time; a batch of several
    /// projects adds out-of-process worker nodes (half the processors at most), which start in about a second, so small
    /// batches (an edit) stay in process. <c>CODALUS_DESIGNTIME_NODES</c> sets the most; MSBuild before 17 stays in
    /// process (its worker nodes never connect to this host, M33).
    /// </summary>
    internal static int NodeCount(int requests)
    {
        if (SolutionBuilder.InProcess || requests < ParallelThreshold)
        {
            return 1;
        }

        var most = int.TryParse(Environment.GetEnvironmentVariable("CODALUS_DESIGNTIME_NODES"), out var configured) && configured > 0
            ? configured
            : Math.Max(1, Environment.ProcessorCount / 2);
        return Math.Clamp(requests / 2, 1, most);
    }

    /// <summary>Batches of fewer projects build in process only.</summary>
    internal const int ParallelThreshold = 6;

    /// <summary>
    /// The only state a design-time build sends back: from a worker node, the whole project instance would be
    /// serialized for nothing.
    /// </summary>
    private static readonly RequestedProjectState s_state = new()
    {
        PropertyFilters = ["TargetFramework", "TargetPath", "TargetRefPath"],
        ItemFilters = new Dictionary<string, List<string>>
        {
            ["CscCommandLineArgs"] = [],
            ["VbcCommandLineArgs"] = [],
            ["FscCommandLineArgs"] = [],
            ["ProjectReference"] = [],
            ["Page"] = [],
            ["ApplicationDefinition"] = [],
        },
    };

    /// <summary>
    /// Keeps a build of a project instance loaded here in this process: a batch's worker node would load the project again
    /// from its file, without the instance's load settings (the missing imports it ignores), and fail.
    /// </summary>
    internal static HostServices InProcess(string path)
    {
        var host = new HostServices();
        host.SetNodeAffinity(path, NodeAffinity.InProc);
        return host;
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

            if (request.WinUI && WindowsDesktop.WinUIDesignTimeTargets() is { } skipXamlCompiler)
            {
                // WinUI's XAML compiler is a Windows program (M44): off Windows its design-time pass would fail the build.
                // A self-contained Windows App SDK needs a Windows runtime identifier, which only Windows infers; a
                // design-time build writes nothing, so it doesn't need one.
                global[WindowsDesktop.CustomAfterDirectoryBuildTargets] = skipXamlCompiler;
                global["WindowsAppSDKSelfContained"] = "false";
            }

            BuildRequestData data;
            if (request.IgnoreMissingImports)
            {
                ProjectInstance instance;
                using (StringCacheGate.Enter())
                {
                    instance = ProjectInstance.FromFile(request.Path, new Microsoft.Build.Definition.ProjectOptions
                    {
                        GlobalProperties = global,
                        ProjectCollection = collection,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports | ProjectLoadSettings.IgnoreEmptyImports,
                    });
                }

                data = new BuildRequestData(instance, s_targets, InProcess(request.Path), BuildRequestDataFlags.ProvideProjectStateAfterBuild);
            }
            else
            {
                data = new BuildRequestData(request.Path, global, null, s_targets, null, BuildRequestDataFlags.ProvideSubsetOfStateAfterBuild, s_state);
            }

            var result = await ExecuteAsync(_manager.PendBuildRequest(data)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var state = result.ProjectStateAfterBuild;
            var arguments = state?.GetItems(language switch { "VB" => "VbcCommandLineArgs", "F#" => "FscCommandLineArgs", _ => "CscCommandLineArgs" })
                .Select(i => i.EvaluatedInclude).ToArray() ?? [];
            if (state is not null && language != "F#" && arguments.Length > 0)
            {
                var projectDirectory = Path.GetDirectoryName(request.Path)!;
                arguments = WindowsDesktop.WithWpfXaml(arguments, state.GetItems("Page").Concat(state.GetItems("ApplicationDefinition"))
                    .Select(i => Path.GetFullPath(Path.Combine(projectDirectory, i.EvaluatedInclude.Replace('\\', Path.DirectorySeparatorChar)))));
            }

            if (state is not null && framework.Length == 0)
            {
                framework = state.GetPropertyValue("TargetFramework");
            }

            if (framework.Length == 0 && request.FrameworkLabel is { } label)
            {
                framework = label;
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

    public void Dispose() => _manager.Dispose();

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
