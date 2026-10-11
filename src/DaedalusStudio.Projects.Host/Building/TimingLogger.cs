using System.Collections.Concurrent;
using DaedalusStudio.Projects.Protocol;
using Microsoft.Build.Framework;

namespace DaedalusStudio.Projects.Host.Building;

/// <summary>
/// Build timings (ADR 0034): records each project build's span and node, its targets' and tasks' durations, and the
/// projects it waited for (a project build started while another was in progress is that one's dependency when it is
/// a project reference's build), then works out the critical path: the chain of project builds that ends last and
/// whose links each started after the previous one finished.
/// </summary>
internal sealed class TimingLogger : ILogger
{
    private readonly ConcurrentDictionary<int, ProjectRecord> _projects = new();
    private readonly ConcurrentDictionary<(int Project, int Target), (string Name, DateTime Start)> _targets = new();
    private readonly ConcurrentDictionary<(int Project, int Task), (string Name, DateTime Start)> _tasks = new();
    private readonly ConcurrentBag<TargetTiming> _targetTimings = [];
    private readonly ConcurrentDictionary<string, (long Duration, int Count)> _taskTimings = new(StringComparer.Ordinal);
    private DateTime _start = DateTime.UtcNow;
    private DateTime _end;

    private sealed class ProjectRecord(string file, string? framework, int node, DateTime start, int parent)
    {
        public string File { get; } = file;

        public string? Framework { get; } = framework;

        public int Node { get; } = node;

        public DateTime Start { get; } = start;

        public int Parent { get; } = parent;

        public DateTime End { get; set; }

        public bool Succeeded { get; set; }

        public string Targets { get; set; } = string.Empty;
    }

    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        eventSource.BuildStarted += (_, e) => _start = e.Timestamp.ToUniversalTime();
        eventSource.BuildFinished += (_, e) => _end = e.Timestamp.ToUniversalTime();
        eventSource.ProjectStarted += (_, e) =>
        {
            var context = e.BuildEventContext;
            if (context is null || e.ProjectFile is null)
            {
                return;
            }

            var framework = e.GlobalProperties?.TryGetValue("TargetFramework", out var tfm) == true && tfm.Length > 0 ? tfm : null;
            _projects[context.ProjectContextId] = new ProjectRecord(e.ProjectFile, framework, context.NodeId, e.Timestamp.ToUniversalTime(), e.ParentProjectBuildEventContext?.ProjectContextId ?? -1)
            {
                Targets = e.TargetNames ?? string.Empty,
            };
        };
        eventSource.ProjectFinished += (_, e) =>
        {
            if (e.BuildEventContext is { } context && _projects.TryGetValue(context.ProjectContextId, out var record))
            {
                record.End = e.Timestamp.ToUniversalTime();
                record.Succeeded = e.Succeeded;
            }
        };
        eventSource.TargetStarted += (_, e) =>
        {
            if (e.BuildEventContext is { } context)
            {
                _targets[(context.ProjectContextId, context.TargetId)] = (e.TargetName, e.Timestamp);
            }
        };
        eventSource.TargetFinished += (_, e) =>
        {
            if (e.BuildEventContext is { } context && _targets.TryRemove((context.ProjectContextId, context.TargetId), out var started))
            {
                _targetTimings.Add(new TargetTiming(e.ProjectFile ?? string.Empty, started.Name, (long)(e.Timestamp - started.Start).TotalMilliseconds));
            }
        };
        eventSource.TaskStarted += (_, e) =>
        {
            if (e.BuildEventContext is { } context)
            {
                _tasks[(context.ProjectContextId, context.TaskId)] = (e.TaskName, e.Timestamp);
            }
        };
        eventSource.TaskFinished += (_, e) =>
        {
            if (e.BuildEventContext is { } context && _tasks.TryRemove((context.ProjectContextId, context.TaskId), out var started))
            {
                var duration = (long)(e.Timestamp - started.Start).TotalMilliseconds;
                _taskTimings.AddOrUpdate(started.Name, (duration, 1), (_, total) => (total.Duration + duration, total.Count + 1));
            }
        };
    }

    public void Shutdown()
    {
    }

    /// <summary>The timings of the build this logger watched.</summary>
    public BuildTimingsResult Result(string? binaryLog)
    {
        var end = _end == default ? _projects.Values.Select(p => p.End).DefaultIfEmpty(_start).Max() : _end;
        long Offset(DateTime time) => Math.Max(0, (long)(time - _start).TotalMilliseconds);

        // Solution metaprojects and the outer builds of multi-targeted projects only dispatch: keep the builds that do work.
        var records = _projects
            .Where(p => !p.Value.File.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && !p.Value.File.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                        && !p.Value.File.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value);
        string Id(int context) => records[context].File + (records[context].Framework is { } f ? "|" + f : string.Empty) + "#" + context.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // A project build's dependencies: the builds it started (its references), found through their parent context.
        var children = records.GroupBy(r => r.Value.Parent).ToDictionary(g => g.Key, g => g.Select(r => r.Key).ToList());
        var projects = records
            .Select(r => new ProjectTiming(
                Id(r.Key),
                r.Value.File,
                Path.GetFileNameWithoutExtension(r.Value.File),
                r.Value.Framework,
                r.Value.Node,
                Offset(r.Value.Start),
                Math.Max(0, (long)((r.Value.End == default ? end : r.Value.End) - r.Value.Start).TotalMilliseconds),
                r.Value.Succeeded,
                [.. r.Value.Targets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                [.. (children.GetValueOrDefault(r.Key) ?? []).Where(c => c != r.Key).Select(Id)]))
            .OrderBy(p => p.Start)
            .ToArray();

        return new BuildTimingsResult(
            Offset(end),
            projects,
            [.. _targetTimings.OrderByDescending(t => t.Duration).Take(50)],
            [.. _taskTimings.Select(t => new TaskTiming(t.Key, t.Value.Duration, t.Value.Count)).OrderByDescending(t => t.Duration).Take(50)],
            CriticalPath(projects),
            binaryLog);
    }

    /// <summary>
    /// The critical path, over each project's real build (per project and target framework, its longest build: the
    /// others are the short calls the project reference protocol makes, or results MSBuild had cached): from the build
    /// that finished last, follow the referenced project whose build finished last, back to a project that references
    /// none. Returned first to last.
    /// </summary>
    internal static string[] CriticalPath(IReadOnlyList<ProjectTiming> projects)
    {
        static string Key(ProjectTiming p) => p.Project + "|" + p.TargetFramework;
        var byId = projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var work = projects.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Duration).First(), StringComparer.OrdinalIgnoreCase);
        var dependsOn = projects.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(
            g => g.Key,
            g => g.SelectMany(p => p.DependsOn).Select(byId.GetValueOrDefault).OfType<ProjectTiming>().Select(Key).Where(k => !k.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var path = new List<string>();
        var current = work.Values.OrderByDescending(p => p.Start + p.Duration).FirstOrDefault();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && seen.Add(Key(current)))
        {
            path.Add(current.Id);
            current = dependsOn[Key(current)].Select(k => work[k]).Where(p => !seen.Contains(Key(p))).OrderByDescending(p => p.Start + p.Duration).FirstOrDefault();
        }

        path.Reverse();
        return [.. path];
    }
}
