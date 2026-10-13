using System.Globalization;
using Codalus.Diagnostics.Host.Cpu;
using Codalus.Diagnostics.Protocol;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Codalus.Diagnostics.Host.Trace;

/// <summary>A task or async method from the TPL's <c>TraceOperation</c> events.</summary>
internal sealed record TaskRecord(int Id, string Name, double Start, int Thread, CallStackIndex Stack)
{
    public double? End { get; init; }

    public string? Status { get; init; }
}

/// <summary>An HTTP request, a database command or a span: start and end (null: unfinished), what and how it went.</summary>
internal sealed record ActivityRecord(double Start, double? End, string Name, string? Detail, string? Status, string? Source, string? Id, string? Parent)
{
    public bool Failed { get; init; }

    public string? Kind { get; init; }

    public int Depth { get; set; }
}

/// <summary>
/// Everything one pass over a trace finds (ADR 0046): the stack profiles by kind (<see cref="ProfileKinds"/>), the
/// TPL's tasks, the thread pool's events, lock contentions, exceptions, HTTP requests, database commands, spans, the
/// I/O counters recorded next to the trace, and a compact index of every event for the events viewer. Built once and
/// then only read; the views below answer a page at a time.
/// </summary>
internal sealed class TraceData(NettraceReader? reader)
{
    private readonly Dictionary<(string Provider, string Name), int> _keys = [];
    private readonly List<(string Provider, string Name)> _keyNames = [];
    private readonly List<int> _keyCounts = [];
    private readonly List<EventEntry> _events = [];

    /// <summary>An event in the index: when, which provider and name (<see cref="_keyNames"/>), thread and its index in the trace.</summary>
    private readonly record struct EventEntry(double Time, int Key, int Thread, int Index);

    public Dictionary<string, CpuProfile> Profiles { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, CpuViews> Views { get; } = new(StringComparer.Ordinal);

    public List<TaskRecord> Tasks { get; } = [];

    public List<ActivityRecord> Requests { get; } = [];

    public List<ActivityRecord> Commands { get; } = [];

    public List<ActivityRecord> Spans { get; } = [];

    public List<ThreadPoolEvent> Pool { get; } = [];

    public List<(int Thread, double Start, double End)> Contentions { get; } = [];

    public List<ExceptionEvent> Exceptions { get; } = [];

    /// <summary>Each exception's leaf stack in the exceptions profile, with its index in <see cref="Exceptions"/>.</summary>
    public List<(int Stack, int Exception)> ExceptionStacks { get; } = [];

    public Dictionary<int, string> ThreadNames { get; set; } = [];

    public double Duration { get; set; }

    public IoRecording? Io { get; set; }

    public int EventCount => _events.Count;

    public CpuProfile Profile(string? kind) =>
        Profiles.TryGetValue(kind ?? ProfileKinds.Cpu, out var profile) ? profile : throw new ArgumentException($"The trace has no {kind} profile.");

    public CpuViews ViewsOf(string? kind)
    {
        kind ??= ProfileKinds.Cpu;
        lock (Views)
        {
            if (!Views.TryGetValue(kind, out var views))
            {
                views = new CpuViews(Profile(kind));
                Views[kind] = views;
            }

            return views;
        }
    }

    public void Index(TraceEvent e, string provider, string name)
    {
        if (!_keys.TryGetValue((provider, name), out var key))
        {
            key = _keyNames.Count;
            _keys[(provider, name)] = key;
            _keyNames.Add((provider, name));
            _keyCounts.Add(0);
        }

        _keyCounts[key]++;
        _events.Add(new EventEntry(e.TimeStampRelativeMSec, key, e.ThreadID, (int)e.EventIndex));
    }

    /// <summary>Spans' depth in their traces; called once everything is in.</summary>
    public void Seal()
    {
        var byId = new Dictionary<string, ActivityRecord>(StringComparer.Ordinal);
        foreach (var span in Spans)
        {
            if (span.Id is { } id)
            {
                byId[id] = span;
            }
        }

        foreach (var span in Spans)
        {
            var depth = 0;
            for (var parent = span.Parent; parent is not null && depth < 50 && byId.TryGetValue(parent, out var up); parent = up.Parent)
            {
                depth++;
            }

            span.Depth = depth;
        }
    }

    public IReadOnlyList<ProfileSummary> ProfileSummaries() =>
        [.. Profiles.Where(p => p.Value.Samples.Count > 0).Select(p => new ProfileSummary(p.Key, p.Value.Unit, p.Value.Samples.Sum(s => (long)s.Weight), p.Value.Samples.Count))];

    private static bool Overlaps(double start, double end, CpuFilter filter) =>
        (filter.StartMilliseconds is not { } from || end >= from) && (filter.EndMilliseconds is not { } to || start <= to);

    // ------------------------------------------------------------------ .NET Async

    public AsyncTasksResult AsyncTasks(CpuFilter filter, string? search, int offset, int count)
    {
        var rows = Tasks
            .Where(t => Overlaps(t.Start, t.End ?? Duration, filter) && (string.IsNullOrEmpty(search) || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => (t.End ?? Duration) - t.Start)
            .ThenBy(t => t.Start)
            .ToList();
        var page = rows.Skip(offset).Take(Math.Clamp(count, 0, 1000)).Select(t =>
        {
            var stack = reader?.Frames(t.Stack, 16) ?? [];
            return new AsyncTask(t.Id, t.Name, MethodOf(t.Name, stack), t.Start, (t.End ?? Duration) - t.Start, t.Thread, t.End is null ? null : t.Status ?? "Completed", stack);
        }).ToList();
        return new AsyncTasksResult(rows.Count, Duration, page, Math.Max(0, rows.Count - offset - page.Count));
    }

    /// <summary>
    /// The method an async operation runs: <c>Async: &lt;FetchAsync&gt;d__0</c> is the state machine of FetchAsync; its
    /// <c>MoveNext</c> frame on the stack names the type (<c>Worker+&lt;FetchAsync&gt;d__0.MoveNext()</c> → <c>Worker.FetchAsync</c>).
    /// </summary>
    internal static string? MethodOf(string operation, IReadOnlyList<string> stack)
    {
        var open = operation.IndexOf('<', StringComparison.Ordinal);
        var close = open < 0 ? -1 : operation.IndexOf(">d__", open, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        var inner = operation[(open + 1)..close];
        var machine = "<" + inner + ">d__";
        foreach (var frame in stack)
        {
            var at = frame.IndexOf(machine, StringComparison.Ordinal);
            if (at > 0 && frame[at - 1] == '+')
            {
                var type = frame[..(at - 1)];
                var space = type.LastIndexOf(' ');
                return (space >= 0 ? type[(space + 1)..] : type) + "." + inner;
            }
        }

        return inner;
    }

    // ------------------------------------------------------------------ threads

    public ThreadsResult Threads(int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 2000);
        var duration = Math.Max(Duration, 1);
        var size = duration / buckets;
        var running = new Dictionary<int, double[]>();
        var waiting = new Dictionary<int, double[]>();
        var blocked = new Dictionary<int, double[]>();
        double[] Row(Dictionary<int, double[]> rows, int thread) =>
            rows.TryGetValue(thread, out var row) ? row : rows[thread] = new double[buckets];

        if (Profiles.TryGetValue(ProfileKinds.Cpu, out var cpu))
        {
            foreach (var sample in cpu.Samples)
            {
                var bucket = Math.Clamp((int)(sample.Time / size), 0, buckets - 1);
                var row = Row(sample.External ? waiting : running, sample.Thread);
                Row(sample.External ? running : waiting, sample.Thread); // every thread has a row of each
                row[bucket] += 1;
            }
        }

        foreach (var (thread, start, end) in Contentions)
        {
            var row = Row(blocked, thread);
            Row(running, thread);
            Row(waiting, thread);
            for (var bucket = Math.Clamp((int)(start / size), 0, buckets - 1); bucket < buckets && bucket * size < end; bucket++)
            {
                var from = Math.Max(start, bucket * size);
                var to = Math.Min(end, (bucket + 1) * size);
                row[bucket] += Math.Max(0, to - from);
            }
        }

        var threads = new List<ThreadTimeline>();
        foreach (var thread in running.Keys)
        {
            var r = running[thread];
            var w = waiting[thread];
            var c = blocked.GetValueOrDefault(thread) ?? new double[buckets];
            var states = new char[buckets];
            for (var i = 0; i < buckets; i++)
            {
                // A thread blocked on a lock is also "waiting" to the sampler: a third of the bucket blocked shows as blocked.
                states[i] = c[i] > 0 && (c[i] >= size / 3 || (c[i] >= r[i] && c[i] >= w[i])) ? 'C'
                    : r[i] > 0 && r[i] >= w[i] ? 'R'
                    : w[i] > 0 ? 'W'
                    : c[i] > 0 ? 'C'
                    : ' ';
            }

            var name = cpu?.ThreadNames.GetValueOrDefault(thread) is { } named && !named.StartsWith("Thread ", StringComparison.Ordinal) ? named
                : ThreadNames.GetValueOrDefault(thread) ?? "Thread " + thread.ToString(CultureInfo.InvariantCulture);
            threads.Add(new ThreadTimeline(thread, name, new string(states), r.Sum(), w.Sum(), c.Sum()));
        }

        return new ThreadsResult(
            duration,
            size,
            [.. threads.OrderByDescending(t => t.Running + t.Contention).ThenBy(t => t.Id)],
            [.. Pool.Where(p => p.Kind == "adjustment").Concat(Pool.Where(p => p.Kind != "adjustment").Take(2000)).OrderBy(p => p.At)]);
    }

    // ------------------------------------------------------------------ exceptions

    public ExceptionsResult ExceptionGroups(CpuFilter filter)
    {
        var profile = Profile(ProfileKinds.Exceptions);
        var groups = new Dictionary<(int Type, int Site), (int Count, int First, int SiteStack)>();
        var total = 0;
        foreach (var (stack, index) in ExceptionStacks)
        {
            var exception = Exceptions[index];
            if (!Overlaps(exception.At, exception.At, filter) || (filter.Threads is { Count: > 0 } threads && !threads.Contains(exception.Thread)))
            {
                continue;
            }

            total++;
            var type = profile.NodeFunction(profile.StackNode(stack));
            var siteStack = profile.StackParent(stack);
            var site = siteStack < 0 ? -1 : profile.NodeFunction(profile.StackNode(siteStack));
            (int Count, int First, int SiteStack) entry = groups.TryGetValue((type, site), out var known) ? known : (0, index, siteStack);
            groups[(type, site)] = (entry.Count + 1, entry.First, entry.SiteStack);
        }

        var rows = groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => profile.FunctionName(g.Key.Type), StringComparer.Ordinal)
            .Select(g =>
            {
                string? file = null;
                int? line = null;
                if (g.Value.SiteStack >= 0 && profile.StackLine(g.Value.SiteStack) is >= 0 and var at)
                {
                    (file, var number) = profile.LineAt(at);
                    line = number;
                }

                return new ExceptionGroup(
                    profile.FunctionName(g.Key.Type),
                    g.Key.Site < 0 ? "(unknown)" : profile.FunctionName(g.Key.Site),
                    g.Value.Count,
                    Exceptions[g.Value.First].Message,
                    file,
                    line);
            })
            .ToList();
        var recent = Exceptions.Where(e => Overlaps(e.At, e.At, filter)).TakeLast(500).ToList();
        return new ExceptionsResult(total == 0 ? recent.Count : total, rows, recent);
    }

    // ------------------------------------------------------------------ activities

    private static bool IsDatabaseSpan(ActivityRecord span) =>
        span.Detail is { } tags && (tags.Contains("db.system", StringComparison.Ordinal) || tags.Contains("db.statement", StringComparison.Ordinal) || tags.Contains("db.query.text", StringComparison.Ordinal));

    /// <summary>A tag's value in DiagnosticSource's <c>k:v, k:v</c> rendering.</summary>
    internal static string? Tag(string? tags, string name)
    {
        if (tags is null)
        {
            return null;
        }

        var at = tags.IndexOf(name + ":", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + name.Length + 1;
        var end = tags.IndexOf(", ", start, StringComparison.Ordinal);
        return end < 0 ? tags[start..] : tags[start..end];
    }

    public ActivitiesResult Activities(string kind, CpuFilter filter, bool byTime, int offset, int count)
    {
        IEnumerable<ActivityRecord> items = kind switch
        {
            ActivityKinds.Http => Requests,
            ActivityKinds.Database => Commands.Concat(Spans.Where(IsDatabaseSpan).Select(s => s with { Name = Tag(s.Detail, "db.query.text") ?? Tag(s.Detail, "db.statement") ?? s.Name })),
            ActivityKinds.Span => Spans,
            _ => throw new ArgumentException($"No such activity kind: {kind}."),
        };
        var rows = items.Where(a => Overlaps(a.Start, a.End ?? Duration, filter)).ToList();
        rows = byTime
            ? [.. rows.OrderBy(a => a.Start)]
            : [.. rows.OrderByDescending(a => (a.End ?? Duration) - a.Start).ThenBy(a => a.Start)];
        var page = rows.Skip(offset).Take(Math.Clamp(count, 0, 2000))
            .Select(a => new ActivityItem(a.Start, (a.End ?? Duration) - a.Start, a.Name, a.Detail, a.End is null ? "unfinished" : a.Status, a.Source, a.Id, a.Parent, a.Depth, a.Failed))
            .ToList();
        return new ActivitiesResult(rows.Count, Duration, page, Math.Max(0, rows.Count - offset - page.Count));
    }

    // ------------------------------------------------------------------ file I/O

    public FileIoResult FileIo()
    {
        if (Io is not { } io)
        {
            return new FileIoResult(false, IoSampler.PlatformName, IoSampler.SourceDescription, [], 0, 0, null, null);
        }

        var samples = io.Samples;
        return new FileIoResult(
            true,
            io.Platform,
            io.Source,
            samples,
            samples.Sum(s => s.ReadBytes),
            samples.Sum(s => s.WriteBytes),
            samples.Any(s => s.ReadOperations is not null) ? samples.Sum(s => s.ReadOperations ?? 0) : null,
            samples.Any(s => s.WriteOperations is not null) ? samples.Sum(s => s.WriteOperations ?? 0) : null);
    }

    // ------------------------------------------------------------------ the events viewer

    public EventStatsResult EventStats() =>
        new(_events.Count, [.. _keyNames.Select((k, i) => new EventCount(k.Provider, k.Name, _keyCounts[i])).OrderByDescending(c => c.Count).ThenBy(c => c.Provider, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)]);

    public EventsResult Events(EventFilter filter, int offset, int count, CancellationToken cancellationToken)
    {
        bool KeyMatches(int key)
        {
            var (provider, name) = _keyNames[key];
            return (filter.Providers is not { Count: > 0 } providers || providers.Contains(provider, StringComparer.OrdinalIgnoreCase))
                && (filter.Names is not { Count: > 0 } names || names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase) || (provider + "/" + name).Equals(n, StringComparison.OrdinalIgnoreCase)));
        }

        var keys = new bool[_keyNames.Count];
        for (var key = 0; key < keys.Length; key++)
        {
            keys[key] = KeyMatches(key);
        }

        var threads = filter.Threads is { Count: > 0 } t ? t.ToHashSet() : null;
        var text = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim();
        count = Math.Clamp(count, 0, 2000);
        var page = new List<TraceEventRow>();
        var matched = 0;
        for (var i = 0; i < _events.Count; i++)
        {
            var entry = _events[i];
            if (!keys[entry.Key]
                || (filter.StartMilliseconds is { } start && entry.Time < start)
                || (filter.EndMilliseconds is { } end && entry.Time > end)
                || (threads is not null && !threads.Contains(entry.Thread)))
            {
                continue;
            }

            string? payload = null;
            if (text is not null)
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var (provider, name) = _keyNames[entry.Key];
                payload = reader?.Payload(entry.Index) ?? string.Empty;
                if (!payload.Contains(text, StringComparison.OrdinalIgnoreCase) && !(provider + "/" + name).Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (matched >= offset && page.Count < count)
            {
                var (provider, name) = _keyNames[entry.Key];
                page.Add(new TraceEventRow(entry.Index, entry.Time, provider, name, entry.Thread, payload ?? reader?.Payload(entry.Index) ?? string.Empty));
            }

            matched++;
        }

        return new EventsResult(matched, page, Math.Max(0, matched - offset - page.Count));
    }
}
