using System.Diagnostics;
using System.Globalization;
using Daedalus.Projects.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace Daedalus.Projects.Host;

/// <summary>What a build reports while it runs.</summary>
internal interface IBuildEvents
{
    Task BuildOutputAsync(string line);

    Task BuildDiagnosticAsync(BuildDiagnosticInfo diagnostic);
}

/// <summary>
/// Real builds (ADR 0006): Build, Rebuild or Clean of the solution file or of one project, through a
/// <see cref="BuildManager"/> of its own with MSBuild's out-of-process worker nodes (no in-process node, so task
/// assemblies never load into the host, and no node reuse, so nodes exit with the build). A restore runs first in
/// the same session, the way <c>msbuild -restore</c> does it. Each build uses a fresh <see cref="ProjectCollection"/>.
/// </summary>
internal sealed class SolutionBuilder : IDisposable
{
    private readonly BuildManager _manager = new("daedalus-build");

    /// <summary>One build at a time; the session also holds it to keep design-time builds out of the way.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Builds <paramref name="path"/> (a solution or a project) with <paramref name="globalProperties"/>.
    /// The caller holds <see cref="Gate"/>.
    /// </summary>
    public async Task<BuildResultInfo> BuildAsync(
        string path,
        string target,
        bool restore,
        IReadOnlyDictionary<string, string> globalProperties,
        IBuildEvents events,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var logger = new BuildLogger(events);
        var msbuildTarget = target switch
        {
            BuildTargets.Rebuild => "Rebuild",
            BuildTargets.Clean => "Clean",
            _ => "Build",
        };
        await events.BuildOutputAsync($"{msbuildTarget} started: {Path.GetFileName(path)} ({Describe(globalProperties)})").ConfigureAwait(false);

        using var collection = new ProjectCollection();
        var canceled = false;
        var succeeded = false;
        _manager.BeginBuild(new BuildParameters(collection)
        {
            Loggers = [logger],
            MaxNodeCount = Environment.ProcessorCount,
            EnableNodeReuse = false,
            DisableInProcNode = true,
            ShutdownInProcNodeOnBuildFinish = true,
        });
        try
        {
            await using var registration = cancellationToken.Register(_manager.CancelAllSubmissions).ConfigureAwait(false);
            var restored = true;
            if (restore && target != BuildTargets.Clean)
            {
                var restoreProperties = new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase)
                {
                    ["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D"),
                    ["MSBuildIsRestoring"] = "true",
                    ["ExcludeRestorePackageImports"] = "true",
                };
                var restoreResult = await ExecuteAsync(new BuildRequestData(
                    path,
                    restoreProperties,
                    null,
                    ["Restore"],
                    null,
                    BuildRequestDataFlags.ClearCachesAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets
                    | BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports | BuildRequestDataFlags.FailOnUnresolvedSdk)).ConfigureAwait(false);
                restored = restoreResult.OverallResult == BuildResultCode.Success;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
            }
            else if (restored)
            {
                var result = await ExecuteAsync(new BuildRequestData(path, new Dictionary<string, string>(globalProperties), null, [msbuildTarget], null)).ConfigureAwait(false);
                succeeded = result.OverallResult == BuildResultCode.Success;
                canceled = cancellationToken.IsCancellationRequested;
                if (result.Exception is { } exception && !canceled && logger.Errors == 0)
                {
                    await logger.ReportAsync(BuildSeverities.Error, null, exception.Message, path, 0, 0, path).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _manager.EndBuild();
        }

        await logger.DrainAsync().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var outcome = canceled ? "canceled" : succeeded ? "succeeded" : "failed";
        await events.BuildOutputAsync($"{msbuildTarget} {outcome}: {Count(logger.Errors, "error")}, {Count(logger.Warnings, "warning")}. Elapsed {elapsed:hh\\:mm\\:ss\\.ff}.").ConfigureAwait(false);
        return new BuildResultInfo(succeeded && !canceled, canceled, logger.Errors, logger.Warnings, (long)elapsed.TotalMilliseconds);
    }

    private Task<BuildResult> ExecuteAsync(BuildRequestData data)
    {
        var done = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.PendBuildRequest(data).ExecuteAsync(s => done.TrySetResult(s.BuildResult!), null);
        return done.Task;
    }

    private static string Describe(IReadOnlyDictionary<string, string> properties) =>
        properties.TryGetValue("Configuration", out var configuration) && properties.TryGetValue("Platform", out var platform)
            ? $"{configuration}|{platform}"
            : "default configuration";

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";

    public void Dispose()
    {
        _manager.Dispose();
        Gate.Dispose();
    }

    /// <summary>
    /// Turns MSBuild events into output lines and structured diagnostics (minimal verbosity: errors, warnings and
    /// high-importance messages such as <c>App -> bin/App.dll</c>). Events arrive on MSBuild's logging thread; they are
    /// forwarded in order through one chain of tasks.
    /// </summary>
    private sealed class BuildLogger(IBuildEvents events) : ILogger
    {
        private readonly Lock _gate = new();
        private Task _chain = Task.CompletedTask;
        private int _errors;
        private int _warnings;

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;

        public string? Parameters { get; set; }

        public int Errors => Volatile.Read(ref _errors);

        public int Warnings => Volatile.Read(ref _warnings);

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (_, e) => _ = ReportAsync(BuildSeverities.Error, e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile, e.EndLineNumber, e.EndColumnNumber, e.HelpLink);
            eventSource.WarningRaised += (_, e) => _ = ReportAsync(BuildSeverities.Warning, e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile, e.EndLineNumber, e.EndColumnNumber, e.HelpLink);
            eventSource.MessageRaised += (_, e) =>
            {
                if (e.Importance == MessageImportance.High && !string.IsNullOrWhiteSpace(e.Message))
                {
                    Enqueue(() => events.BuildOutputAsync(e.Message!.TrimEnd()));
                }
            };
        }

        public Task ReportAsync(string severity, string? code, string? message, string? file, int line, int column, string? project, int endLine = 0, int endColumn = 0, string? helpLink = null)
        {
            Interlocked.Increment(ref severity == BuildSeverities.Error ? ref _errors : ref _warnings);
            var full = FullPath(file, project);
            var diagnostic = new BuildDiagnosticInfo(severity, NullIfEmpty(code), message ?? string.Empty, full, line, column, endLine, endColumn, NullIfEmpty(project), NullIfEmpty(helpLink));
            return Enqueue(async () =>
            {
                await events.BuildOutputAsync(Format(diagnostic)).ConfigureAwait(false);
                await events.BuildDiagnosticAsync(diagnostic).ConfigureAwait(false);
            });
        }

        /// <summary>The MSBuild form: <c>file(line,col): error CODE: message [project]</c>.</summary>
        public static string Format(BuildDiagnosticInfo d)
        {
            var location = d.File is null ? "" : d.Line > 0 ? $"{d.File}({d.Line},{Math.Max(1, d.Column)}): " : $"{d.File}: ";
            var code = d.Code is null ? "" : $" {d.Code}";
            var project = d.Project is null || string.Equals(d.Project, d.File, StringComparison.OrdinalIgnoreCase) ? "" : $" [{d.Project}]";
            return $"{location}{d.Severity}{code}: {d.Message}{project}";
        }

        private static string? FullPath(string? file, string? project)
        {
            if (string.IsNullOrEmpty(file))
            {
                return null;
            }

            try
            {
                return Path.IsPathRooted(file) || project is null
                    ? Path.GetFullPath(file)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, file));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return file;
            }
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        private Task Enqueue(Func<Task> send)
        {
            lock (_gate)
            {
                _chain = _chain.ContinueWith(_ => send(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                return _chain;
            }
        }

        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _chain;
            }
        }

        public void Shutdown()
        {
        }
    }
}
