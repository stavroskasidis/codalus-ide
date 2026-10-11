using System.Diagnostics;
using System.Globalization;
using DaedalusStudio.Projects.Host.Building;
using DaedalusStudio.Projects.Protocol;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace DaedalusStudio.Projects.Host;

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
    /// <summary>
    /// MSBuild before 17 (the .NET 5 SDK and older) never connects to the worker nodes it starts from a host on a
    /// newer .NET: the build waits forever (M33, ADR 0030). Its builds run in the build host's own node instead, as its
    /// design-time builds do. <c>DAEDALUS_BUILD_IN_PROCESS=1</c> does the same for any SDK.
    /// </summary>
    internal static bool InProcess { get; } = MSBuildMajorVersion() < 17 || Environment.GetEnvironmentVariable("DAEDALUS_BUILD_IN_PROCESS") == "1";

    private readonly BuildManager _manager = new("daedalus-build");

    /// <summary>One build at a time; the session also holds it to keep design-time builds out of the way.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Builds <paramref name="path"/> (a solution or a project) with <paramref name="globalProperties"/>.
    /// The caller holds <see cref="Gate"/>. <paramref name="options"/> (ADR 0034) set the Output window's verbosity, a
    /// log file, a binary log and the most nodes; <paramref name="timings"/> records the build's timings.
    /// </summary>
    public async Task<BuildResultInfo> BuildAsync(
        string path,
        string target,
        bool restore,
        IReadOnlyDictionary<string, string> globalProperties,
        IBuildEvents events,
        CancellationToken cancellationToken,
        BuildOptions? options = null,
        TimingLogger? timings = null)
    {
        var started = Stopwatch.GetTimestamp();
        var logger = new BuildLogger(events) { Verbosity = ParseVerbosity(options?.Verbosity, LoggerVerbosity.Minimal) };
        var loggers = new List<ILogger> { logger };
        if (options?.LogFile is { Length: > 0 } logFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
            loggers.Add(new FileLogger
            {
                Parameters = $"LogFile={logFile};Encoding=UTF-8",
                Verbosity = ParseVerbosity(options.LogFileVerbosity, LoggerVerbosity.Normal),
            });
        }

        string? binaryLog = null;
        if (options?.BinaryLog is { Length: > 0 } binlog)
        {
            binaryLog = Path.GetFullPath(binlog);
            Directory.CreateDirectory(Path.GetDirectoryName(binaryLog)!);
            loggers.Add(new BinaryLogger { Parameters = binaryLog, CollectProjectImports = BinaryLogger.ProjectImportsCollectionMode.Embed });
        }

        // The timing logger hears only the events it needs, so a build with it forwards nothing else from the nodes.
        var forwarding = new List<ForwardingLoggerRecord>();
        if (timings is not null)
        {
            var forwarder = typeof(ConfigurableForwardingLogger);
            forwarding.Add(new ForwardingLoggerRecord(timings, new LoggerDescription(
                forwarder.FullName,
                forwarder.Assembly.FullName,
                null,
                "BUILDSTARTEDEVENT;BUILDFINISHEDEVENT;PROJECTSTARTEDEVENT;PROJECTFINISHEDEVENT;TARGETSTARTEDEVENT;TARGETFINISHEDEVENT;TASKSTARTEDEVENT;TASKFINISHEDEVENT",
                LoggerVerbosity.Quiet)));
        }

        var maxNodes = options?.MaxParallelProjects is > 0 and var max ? Math.Min(max, Environment.ProcessorCount * 2) : Environment.ProcessorCount;
        var msbuildTarget = target switch
        {
            BuildTargets.Rebuild => "Rebuild",
            BuildTargets.Clean => "Clean",
            BuildTargets.Pack => "Pack",
            _ => "Build",
        };
        await events.BuildOutputAsync($"{msbuildTarget} started: {Path.GetFileName(path)} ({Describe(globalProperties)})").ConfigureAwait(false);

        using var collection = new ProjectCollection();
        var canceled = false;
        var succeeded = false;
        _manager.BeginBuild(new BuildParameters(collection)
        {
            Loggers = loggers,
            ForwardingLoggers = forwarding,
            MaxNodeCount = InProcess ? 1 : maxNodes,
            EnableNodeReuse = false,
            DisableInProcNode = !InProcess,
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
        return new BuildResultInfo(succeeded && !canceled, canceled, logger.Errors, logger.Warnings, (long)elapsed.TotalMilliseconds, BinaryLog: binaryLog);
    }

    /// <summary>The loaded MSBuild's version (its assembly version stays 15.1 across releases; the file version doesn't).</summary>
    private static int MSBuildMajorVersion()
    {
        var location = typeof(BuildManager).Assembly.Location;
        return location.Length > 0 && FileVersionInfo.GetVersionInfo(location) is { FileMajorPart: > 0 } version ? version.FileMajorPart : 17;
    }

    /// <summary>An MSBuild verbosity from its name (<see cref="BuildVerbosities"/>), or <paramref name="fallback"/>.</summary>
    internal static LoggerVerbosity ParseVerbosity(string? name, LoggerVerbosity fallback) => name?.ToLowerInvariant() switch
    {
        BuildVerbosities.Quiet => LoggerVerbosity.Quiet,
        BuildVerbosities.Minimal => LoggerVerbosity.Minimal,
        BuildVerbosities.Normal => LoggerVerbosity.Normal,
        BuildVerbosities.Detailed => LoggerVerbosity.Detailed,
        BuildVerbosities.Diagnostic => LoggerVerbosity.Diagnostic,
        _ => fallback,
    };

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
            eventSource.ErrorRaised += (_, e) => _ = ReportAsync(BuildSeverities.Error, e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile, e.EndLineNumber, e.EndColumnNumber, HelpLink(e));
            eventSource.WarningRaised += (_, e) => _ = ReportAsync(BuildSeverities.Warning, e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile, e.EndLineNumber, e.EndColumnNumber, HelpLink(e));
            var lowest = Verbosity switch
            {
                LoggerVerbosity.Quiet => (MessageImportance?)null,
                LoggerVerbosity.Minimal => MessageImportance.High,
                LoggerVerbosity.Normal => MessageImportance.Normal,
                _ => MessageImportance.Low,
            };
            eventSource.MessageRaised += (_, e) =>
            {
                // Command lines only from normal verbosity, as MSBuild's console logger shows them.
                if (lowest is { } threshold && e.Importance <= threshold && !string.IsNullOrWhiteSpace(e.Message)
                    && (e is not TaskCommandLineEventArgs || Verbosity >= LoggerVerbosity.Normal))
                {
                    Enqueue(() => events.BuildOutputAsync(e.Message!.TrimEnd()));
                }
            };
            if (Verbosity >= LoggerVerbosity.Normal)
            {
                eventSource.ProjectStarted += (_, e) =>
                {
                    if (e.ProjectFile is { } file && e.ParentProjectBuildEventContext?.ProjectContextId is null or < 0)
                    {
                        Enqueue(() => events.BuildOutputAsync($"Project \"{file}\" ({e.TargetNames}):"));
                    }
                };
                eventSource.TargetStarted += (_, e) =>
                {
                    if (Verbosity >= LoggerVerbosity.Detailed)
                    {
                        Enqueue(() => events.BuildOutputAsync($"  Target \"{e.TargetName}\" in \"{e.ProjectFile}\":"));
                    }
                };
            }
        }

        // HelpLink came with MSBuild 16.8: the MSBuild of older SDKs (M33) doesn't have it, and a logger that throws
        // stops the build ("an unexpected logger failure"). The property is read in methods of their own, so a missing
        // one fails at that call, where it is caught.
        private static string? HelpLink(BuildErrorEventArgs e)
        {
            try
            {
                return ErrorHelpLink(e);
            }
            catch (MissingMemberException)
            {
                return null;
            }
        }

        private static string? HelpLink(BuildWarningEventArgs e)
        {
            try
            {
                return WarningHelpLink(e);
            }
            catch (MissingMemberException)
            {
                return null;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string? ErrorHelpLink(BuildErrorEventArgs e) => e.HelpLink;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string? WarningHelpLink(BuildWarningEventArgs e) => e.HelpLink;

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
