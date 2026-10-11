using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaedalusStudio.Diagnostics.Protocol;

namespace DaedalusStudio.Workbench.Diagnostics;

/// <summary>One counter's identity: provider, name and (for meters) its tags.</summary>
public sealed record CounterKey(string Provider, string Name, string? Tags)
{
    public override string ToString() => Tags is null ? $"{Provider}/{Name}" : $"{Provider}/{Name}[{Tags}]";
}

/// <summary>A value at a time (seconds since the session started).</summary>
public readonly record struct CounterPoint(double At, double Value);

/// <summary>A counter's recorded values, with what describes it.</summary>
public sealed class CounterSeries(CounterKey key, string displayName, string? unit, string kind)
{
    private readonly List<CounterPoint> _points = [];

    public CounterKey Key { get; } = key;

    public string DisplayName { get; internal set; } = displayName;

    public string? Unit { get; internal set; } = unit;

    public string Kind { get; } = kind;

    public IReadOnlyList<CounterPoint> Points
    {
        get
        {
            lock (_points)
            {
                return [.. _points];
            }
        }
    }

    public CounterPoint? Last
    {
        get
        {
            lock (_points)
            {
                return _points.Count == 0 ? null : _points[^1];
            }
        }
    }

    internal void Add(CounterPoint point, int max)
    {
        lock (_points)
        {
            _points.Add(point);
            if (_points.Count > max)
            {
                _points.RemoveRange(0, _points.Count - max);
            }
        }
    }
}

/// <summary>
/// A live counters session on a process (the Counters monitor, the Diagnostic Tools window): every counter's values
/// over time (the last <see cref="MaxPoints"/> of each) and the runtime's GC and exception events (the last
/// <see cref="MaxEvents"/>). Events arrive on any thread.
/// </summary>
public sealed class CounterMonitor
{
    public const int MaxPoints = 3600;
    public const int MaxEvents = 2000;

    private readonly DiagnosticsService _service;
    private readonly Dictionary<CounterKey, CounterSeries> _series = [];
    private readonly List<RuntimeEvent> _events = [];
    private readonly TaskCompletionSource<string?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CounterMonitor(DiagnosticsService service, string id, int processId, IReadOnlyList<CounterProvider> providers, double intervalSeconds)
    {
        _service = service;
        Id = id;
        ProcessId = processId;
        Providers = providers;
        IntervalSeconds = intervalSeconds;
    }

    public string Id { get; }

    public int ProcessId { get; }

    public string ProcessName { get; internal set; } = string.Empty;

    public IReadOnlyList<CounterProvider> Providers { get; }

    public double IntervalSeconds { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public bool IsRunning => !_ended.Task.IsCompleted;

    /// <summary>Completes when the session ends, with an error message or null.</summary>
    public Task<string?> Ended => _ended.Task;

    /// <summary>New values arrived (on a background thread).</summary>
    public event EventHandler<CountersUpdate>? Updated;

    /// <summary>GC or exception events arrived (on a background thread).</summary>
    public event EventHandler<IReadOnlyList<RuntimeEvent>>? EventsReceived;

    /// <summary>The session ended: the process exited, it was stopped, or the host stopped (with a message).</summary>
    public event EventHandler<string?>? Stopped;

    public IReadOnlyList<CounterSeries> Series
    {
        get
        {
            lock (_series)
            {
                return [.. _series.Values];
            }
        }
    }

    public IReadOnlyList<RuntimeEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public CounterSeries? Find(string provider, string name) => Series.FirstOrDefault(s => s.Key.Provider == provider && s.Key.Name == name && s.Key.Tags is null);

    internal void OnUpdate(CountersUpdate update)
    {
        lock (_series)
        {
            foreach (var value in update.Values)
            {
                var key = new CounterKey(value.Provider, value.Name, value.Tags);
                if (!_series.TryGetValue(key, out var series))
                {
                    series = new CounterSeries(key, value.DisplayName, value.Unit, value.Kind);
                    _series[key] = series;
                }

                series.DisplayName = value.DisplayName;
                series.Unit = value.Unit;
                if (!double.IsNaN(value.Value))
                {
                    series.Add(new CounterPoint(update.At, value.Value), MaxPoints);
                }
            }
        }

        Updated?.Invoke(this, update);
    }

    internal void OnEvents(IReadOnlyList<RuntimeEvent> events)
    {
        lock (_events)
        {
            _events.AddRange(events);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxEvents);
            }
        }

        EventsReceived?.Invoke(this, events);
    }

    internal void OnEnded(string? error)
    {
        if (_ended.TrySetResult(error))
        {
            Stopped?.Invoke(this, error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => _service.StopCountersAsync(this, cancellationToken);
}

/// <summary>Counters written out (the Counters monitor's Export, and a profiling session's <c>counters.json</c>).</summary>
public static class CounterExport
{
    /// <summary>One row per value: <c>seconds,provider,counter,tags,unit,kind,value</c> (RFC 4180 quoting).</summary>
    public static string ToCsv(IReadOnlyList<CounterSeries> series)
    {
        var builder = new StringBuilder("seconds,provider,counter,tags,unit,kind,value\n");
        var rows = series.SelectMany(s => s.Points.Select(p => (Series: s, Point: p))).OrderBy(r => r.Point.At).ThenBy(r => r.Series.Key.ToString(), StringComparer.Ordinal);
        foreach (var (s, point) in rows)
        {
            builder.Append(point.At.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(Quote(s.Key.Provider)).Append(',')
                .Append(Quote(s.Key.Name)).Append(',')
                .Append(Quote(s.Key.Tags ?? string.Empty)).Append(',')
                .Append(Quote(s.Unit ?? string.Empty)).Append(',')
                .Append(s.Kind).Append(',')
                .Append(point.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    public static string ToJson(CounterMonitor monitor) =>
        ToJson(new CounterFile(monitor.ProcessId, monitor.ProcessName, monitor.StartedAt, monitor.IntervalSeconds, Files(monitor.Series), monitor.Events));

    public static string ToJson(CounterFile file) => JsonSerializer.Serialize(file, CounterFileJsonContext.Default.CounterFile);

    public static CounterFile? ReadJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CounterFileJsonContext.Default.CounterFile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<CounterFileSeries> Files(IReadOnlyList<CounterSeries> series) =>
        [.. series.OrderBy(s => s.Key.ToString(), StringComparer.Ordinal).Select(s => new CounterFileSeries(s.Key.Provider, s.Key.Name, s.DisplayName, s.Unit, s.Kind, s.Key.Tags, [.. s.Points.Select(p => new CounterFilePoint(p.At, p.Value))]))];
}

/// <summary>The JSON export: the process, when it started, and every counter's values.</summary>
public sealed record CounterFile(int ProcessId, string ProcessName, DateTimeOffset StartedAt, double IntervalSeconds, IReadOnlyList<CounterFileSeries> Counters, IReadOnlyList<RuntimeEvent>? Events);

public sealed record CounterFileSeries(string Provider, string Name, string DisplayName, string? Unit, string Kind, string? Tags, IReadOnlyList<CounterFilePoint> Values);

public sealed record CounterFilePoint(double At, double Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(CounterFile))]
internal sealed partial class CounterFileJsonContext : JsonSerializerContext;
