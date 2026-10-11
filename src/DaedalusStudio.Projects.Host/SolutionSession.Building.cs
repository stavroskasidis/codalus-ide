using System.Diagnostics;
using System.Globalization;
using DaedalusStudio.Projects.Host.Building;
using DaedalusStudio.Projects.Host.ProjectManagement;
using DaedalusStudio.Projects.Protocol;

namespace DaedalusStudio.Projects.Host;

/// <summary>Build depth (ADR 0034): the fast up-to-date check, batch builds, timings, build events and binary logs.</summary>
internal sealed partial class SolutionSession
{
    private readonly BinaryLogs _binaryLogs = new();
    private BuildTimingsResult? _lastTimings;

    /// <summary>The projects a solution build builds: loaded, and set to build in the active configuration.</summary>
    private List<string> BuiltProjects() =>
        [.. ProjectPaths.Where(p => _evaluated.TryGetValue(p, out var project) && project.Info.State == ProjectStates.Loaded && BuildsInActiveConfiguration(p))];

    private bool BuildsInActiveConfiguration(string projectPath) =>
        _model?.SolutionProjects.FirstOrDefault(p => s_pathComparer.Equals(Full(System.IO.Path.GetDirectoryName(Path)!, p.FilePath), projectPath)) is not { } project
        || project.GetProjectConfiguration(Active.Configuration, Active.Platform).Build;

    /// <summary>
    /// The global properties each project builds with: a project build passes its own to the projects it references; a
    /// solution build gives each project its configuration from the solution.
    /// </summary>
    private Func<string, IReadOnlyDictionary<string, string>> BuildGlobals(bool perProject, Dictionary<string, string> properties) =>
        perProject
            ? project =>
            {
                var own = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
                (own["Configuration"], own["Platform"]) = ProjectConfiguration(project);
                return own;
            }
            : _ => properties;

    /// <summary>
    /// Builds <paramref name="path"/>; for Build, first asks the fast up-to-date check about <paramref name="scope"/> (with
    /// the options' <see cref="BuildOptions.UpToDateCheck"/>) and skips MSBuild when every project is up to date. After a
    /// build with options, the check records (or, after a failure or a clean, forgets) the state of the projects it built.
    /// </summary>
    private async Task<BuildResultInfo> BuildWithCheckAsync(
        string path,
        string target,
        bool restore,
        Dictionary<string, string> properties,
        IReadOnlyList<string> scope,
        Func<string, IReadOnlyDictionary<string, string>> globals,
        BuildOptions? options,
        IBuildEvents events,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var startedUtc = DateTime.UtcNow;
        // The state is recorded whenever a build runs with options (so turning the check on later finds it); it is only
        // used to skip MSBuild with UpToDateCheck.
        using var check = options is not null && target is BuildTargets.Build or BuildTargets.Rebuild or BuildTargets.Clean && scope.Count > 0
            ? new UpToDateCheck(globals)
            : null;
        if (check is not null && options!.UpToDateCheck && target == BuildTargets.Build)
        {
            var verdicts = await Task.Run(() => scope.Select(check.Check).ToList(), cancellationToken).ConfigureAwait(false);
            if (verdicts.FirstOrDefault(v => !v.UpToDate) is { } stale)
            {
                await events.BuildOutputAsync($"{System.IO.Path.GetFileNameWithoutExtension(stale.Project)} is not up to date: {stale.Reason}.").ConfigureAwait(false);
            }
            else
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var projects = scope.Count == 1 ? System.IO.Path.GetFileNameWithoutExtension(scope[0]) + " is" : $"All {scope.Count.ToString(CultureInfo.InvariantCulture)} projects are";
                await events.BuildOutputAsync($"{projects} up to date, so MSBuild didn't run (fast up-to-date check). Elapsed {elapsed:hh\\:mm\\:ss\\.ff}.").ConfigureAwait(false);
                return new BuildResultInfo(true, false, 0, 0, (long)elapsed.TotalMilliseconds, UpToDate: true);
            }
        }

        var timings = new TimingLogger();
        var result = await _builder.BuildAsync(path, target, restore, properties, events, cancellationToken, options, timings).ConfigureAwait(false);
        _lastTimings = timings.Result(result.BinaryLog);
        if (check is not null)
        {
            await Task.Run(() =>
            {
                // Evaluated afresh: the build's restore may have changed what the projects import.
                using var after = new UpToDateCheck(globals);
                foreach (var project in after.Closure(scope))
                {
                    try
                    {
                        if (result.Succeeded && target != BuildTargets.Clean)
                        {
                            after.Record(project, startedUtc);
                        }
                        else
                        {
                            after.Forget(project);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Build.Exceptions.InvalidProjectFileException)
                    {
                        // The next build runs MSBuild; that's always correct.
                    }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Batch Build: each project × configuration × platform in turn, in one exclusive build. Each entry builds its project
    /// (and what it references) with that configuration; the fast up-to-date check applies to each.
    /// </summary>
    public async Task<BatchBuildResult> BatchBuildAsync(BatchBuildParams parameters, IBuildEvents events, CancellationToken cancellationToken)
    {
        RequireTrusted("Builds");
        if (parameters.Entries.Length == 0)
        {
            return new BatchBuildResult(true, false, 0, 0, 0, []);
        }

        var options = parameters.Options is null ? null : parameters.Options with { BinaryLog = null };
        return await ExclusiveBuildAsync(async token =>
        {
            var started = Stopwatch.GetTimestamp();
            var results = new List<BatchBuildEntryResult>();
            var restored = new HashSet<string>(s_pathComparer);
            var canceled = false;
            for (var i = 0; i < parameters.Entries.Length; i++)
            {
                if (token.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var entry = parameters.Entries[i];
                var project = System.IO.Path.GetFullPath(entry.Project);
                var properties = SolutionProperties();
                properties["Configuration"] = entry.Configuration;
                properties["Platform"] = entry.Platform.Replace(" ", string.Empty, StringComparison.Ordinal);
                foreach (var (name, value) in options?.Properties ?? [])
                {
                    properties[name] = value;
                }

                await events.BuildOutputAsync($"------ {(i + 1).ToString(CultureInfo.InvariantCulture)} of {parameters.Entries.Length.ToString(CultureInfo.InvariantCulture)}: {System.IO.Path.GetFileNameWithoutExtension(project)}, {entry.Configuration}|{entry.Platform} ------").ConfigureAwait(false);
                var result = await BuildWithCheckAsync(project, parameters.Target, restored.Add(project), properties, [project], _ => properties, options, events, token).ConfigureAwait(false);
                results.Add(new BatchBuildEntryResult(entry.Project, entry.Configuration, entry.Platform, result.Succeeded, result.UpToDate, result.Errors, result.Warnings, result.ElapsedMilliseconds));
                if (result.Canceled)
                {
                    canceled = true;
                    break;
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var succeeded = results.Count(r => r.Succeeded);
            var upToDate = results.Count(r => r.Skipped);
            var failed = results.Count - succeeded;
            var skipped = parameters.Entries.Length - results.Count;
            await events.BuildOutputAsync(
                $"========== Batch {parameters.Target}: {(succeeded - upToDate).ToString(CultureInfo.InvariantCulture)} succeeded, {failed.ToString(CultureInfo.InvariantCulture)} failed, "
                + $"{upToDate.ToString(CultureInfo.InvariantCulture)} up-to-date, {skipped.ToString(CultureInfo.InvariantCulture)} skipped. Elapsed {elapsed:hh\\:mm\\:ss\\.ff} ==========").ConfigureAwait(false);
            return new BatchBuildResult(!canceled && failed == 0, canceled, results.Sum(r => r.Errors), results.Sum(r => r.Warnings), (long)elapsed.TotalMilliseconds, [.. results]);
        }, new BatchBuildResult(false, true, 0, 0, 0, []), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The fast up-to-date check alone, for a project or the solution (what Build would skip).</summary>
    public Task<UpToDateResult> CheckUpToDateAsync(UpToDateParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("The up-to-date check");
        var properties = SolutionProperties();
        IReadOnlyList<string> scope;
        bool perProject;
        if (parameters.Project is { } project)
        {
            var path = System.IO.Path.GetFullPath(project);
            (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(path);
            scope = [path];
            perProject = false;
        }
        else if (IsSingleProject)
        {
            (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(Path);
            scope = [Path];
            perProject = false;
        }
        else
        {
            scope = BuiltProjects();
            perProject = true;
        }

        return Task.Run(() =>
        {
            using var check = new UpToDateCheck(BuildGlobals(perProject, properties));
            var verdicts = scope.Select(check.Check).Select(v => new UpToDateProject(v.Project, v.UpToDate, v.Reason)).ToArray();
            return new UpToDateResult(verdicts.All(v => v.UpToDate), verdicts);
        }, cancellationToken);
    }

    /// <summary>The last build's timings.</summary>
    public BuildTimingsResult GetTimings() =>
        _lastTimings ?? throw new InvalidOperationException("No build has run in this session yet (an up-to-date build doesn't run MSBuild).");

    // ------------------------------------------------------------------ build events

    public BuildEventsResult GetBuildEvents(BuildEventsParams parameters) => BuildEvents.Get(System.IO.Path.GetFullPath(parameters.Project));

    public async Task<BuildEventsResult> SetBuildEventsAsync(SetBuildEventsParams parameters)
    {
        RequireTrusted("Build events");
        var path = System.IO.Path.GetFullPath(parameters.Project);
        if (BuildEvents.Set(path, parameters.PreBuild, parameters.PostBuild, parameters.RunPostBuildEvent))
        {
            await ReevaluateAsync([path]).ConfigureAwait(false);
        }

        return BuildEvents.Get(path);
    }

    // ------------------------------------------------------------------ binary logs

    public Task<BinaryLogInfo> OpenBinaryLogAsync(OpenBinaryLogParams parameters, CancellationToken cancellationToken) =>
        Task.Run(() => _binaryLogs.Open(parameters.Path), cancellationToken);

    public BinaryLogChildrenResult BinaryLogChildren(BinaryLogChildrenParams parameters) => _binaryLogs.Children(parameters);

    public Task<SearchBinaryLogResult> SearchBinaryLogAsync(SearchBinaryLogParams parameters, CancellationToken cancellationToken) =>
        Task.Run(() => _binaryLogs.Search(parameters, cancellationToken), cancellationToken);

    public void CloseBinaryLog(CloseBinaryLogParams parameters) => _binaryLogs.Close(parameters.Id);
}
