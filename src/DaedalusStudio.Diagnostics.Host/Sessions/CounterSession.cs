using System.Diagnostics.Tracing;
using System.Globalization;
using DaedalusStudio.Diagnostics.Protocol;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DaedalusStudio.Diagnostics.Host.Sessions;

/// <summary>
/// A live counters session (the Counters monitor and the Diagnostic Tools window): EventCounters of the named
/// EventSources (<c>EventCounterIntervalSec</c>), <c>System.Diagnostics.Metrics</c> meters through
/// <c>MetricsEventSource</c>, and optionally GC and exception events, read as they arrive (TraceEvent's
/// <see cref="EventPipeEventSource"/>) and published in batches a few times a second. It ends when the process exits.
/// </summary>
internal sealed class CounterSession : IAsyncDisposable
{
    private const string MetricsProvider = "System.Diagnostics.Metrics";
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromMilliseconds(250);

    private readonly CountersStartParams _parameters;
    private readonly Func<CountersUpdate, Task> _publish;
    private readonly Func<RuntimeEventsUpdate, Task> _publishEvents;
    private readonly Func<string?, Task> _ended;
    private readonly string _metricsSession = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, HashSet<string>?> _eventCounterFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private List<CounterValue> _values = [];
    private List<RuntimeEvent> _events = [];
    private readonly Dictionary<int, (double Start, int Generation, string Reason)> _gcs = [];
    private EventPipeSession? _session;
    private Task _processing = Task.CompletedTask;
    private Task _flushing = Task.CompletedTask;
    private double _lastAt;

    public CounterSession(CountersStartParams parameters, Func<CountersUpdate, Task> publish, Func<RuntimeEventsUpdate, Task> publishEvents, Func<string?, Task> ended)
    {
        _parameters = parameters;
        _publish = publish;
        _publishEvents = publishEvents;
        _ended = ended;
    }

    public string Id => _parameters.Session;

    internal IReadOnlyList<EventPipeProvider> Providers()
    {
        var interval = Math.Clamp(_parameters.IntervalSeconds, 0.1, 3600).ToString("0.###", CultureInfo.InvariantCulture);
        var providers = new List<EventPipeProvider>();
        var meters = new List<string>();
        foreach (var provider in _parameters.Providers)
        {
            if (provider.Kind == CounterProviderKinds.Meter)
            {
                meters.AddRange(provider.Counters is { Count: > 0 } instruments ? instruments.Select(i => provider.Name + "\\" + i) : [provider.Name]);
            }
            else
            {
                _eventCounterFilters[provider.Name] = provider.Counters is { Count: > 0 } names ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase) : null;
                providers.Add(new EventPipeProvider(provider.Name, EventLevel.Informational, 0, new Dictionary<string, string> { ["EventCounterIntervalSec"] = interval }));
            }
        }

        if (meters.Count > 0)
        {
            providers.Add(new EventPipeProvider(MetricsProvider, EventLevel.Informational, 0x2 /* TimeSeriesValues */, new Dictionary<string, string>
            {
                ["SessionId"] = _metricsSession,
                ["Metrics"] = string.Join(',', meters),
                ["RefreshInterval"] = interval,
                ["MaxTimeSeries"] = "1000",
                ["MaxHistograms"] = "20",
                ["ClientId"] = _metricsSession,
            }));
        }

        if (_parameters.RuntimeEvents)
        {
            providers.Add(new EventPipeProvider(DiagnosticsPort.RuntimeProvider, EventLevel.Informational, DiagnosticsPort.RuntimeEventKeywords));
        }

        return providers;
    }

    public async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        var name = DiagnosticsPort.NameOf(_parameters.ProcessId);
        _session = await DiagnosticsPort.StartSessionAsync(_parameters.ProcessId, Providers(), rundown: false, stacks: false, bufferMegabytes: 64, cancellationToken).ConfigureAwait(false);
        var session = _session;
        _processing = Task.Run(() => ProcessAsync(session), CancellationToken.None);
        _flushing = Task.Run(FlushLoopAsync, CancellationToken.None);
        return name;
    }

    private async Task ProcessAsync(EventPipeSession session)
    {
        string? error = null;
        try
        {
            using var source = new EventPipeEventSource(session.EventStream);
            source.Dynamic.All += OnEvent;
            if (_parameters.RuntimeEvents)
            {
                source.Clr.GCStart += OnGcStart;
                source.Clr.GCStop += OnGcStop;
                source.Clr.ExceptionStart += OnException;
            }

            source.Process();
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidOperationException or ObjectDisposedException or FormatException)
        {
            // A process that exits (or is killed) cuts the stream short: that is the session's normal end. The stream can
            // end a moment before the process is gone (Linux), so it is given a little time.
            if (!_stop.IsCancellationRequested && ex is not EndOfStreamException && await StillAliveAsync().ConfigureAwait(false))
            {
                error = ex.Message;
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flushing.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await FlushAsync().ConfigureAwait(false);
        await _ended(error).ConfigureAwait(false);
    }

    private async Task<bool> StillAliveAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            if (!DiagnosticsPort.IsAlive(_parameters.ProcessId))
            {
                return false;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return true;
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.EventName == "EventCounters" && _eventCounterFilters.TryGetValue(data.ProviderName, out var filter))
        {
            OnEventCounter(data, filter);
        }
        else if (data.ProviderName == MetricsProvider)
        {
            OnMetric(data);
        }
    }

    private void OnEventCounter(TraceEvent data, HashSet<string>? filter)
    {
        if (data.PayloadValue(0) is not IDictionary<string, object> outer || !outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> fields)
        {
            return;
        }

        var name = fields.TryGetValue("Name", out var n) ? n?.ToString() : null;
        if (string.IsNullOrEmpty(name) || (filter is not null && !filter.Contains(name)))
        {
            return;
        }

        var sum = fields.TryGetValue("CounterType", out var type) && type?.ToString() == "Sum";
        var value = sum ? Number(fields, "Increment") : Number(fields, "Mean");
        var display = fields.TryGetValue("DisplayName", out var d) && d?.ToString() is { Length: > 0 } text ? text : name;
        var unit = fields.TryGetValue("DisplayUnits", out var u) && u?.ToString() is { Length: > 0 } units ? units : null;
        Add(data, new CounterValue(data.ProviderName, name, display, unit, value, sum ? CounterValueKinds.Rate : CounterValueKinds.Gauge));
    }

    private static double Number(IDictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out var value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;

    private void OnMetric(TraceEvent data)
    {
        if (data.PayloadByName("sessionId") as string != _metricsSession)
        {
            return;
        }

        var meter = data.PayloadByName("meterName") as string ?? string.Empty;
        var instrument = data.PayloadByName("instrumentName") as string ?? string.Empty;
        var unit = data.PayloadByName("unit") as string is { Length: > 0 } u ? u : null;
        var tags = data.PayloadByName("tags") as string is { Length: > 0 } t ? t : null;
        switch (data.EventName)
        {
            case "CounterRateValuePublished":
            case "UpDownCounterRateValuePublished":
                if (Parse(data.PayloadByName("rate")) is { } rate)
                {
                    Add(data, new CounterValue(meter, instrument, instrument, unit, rate, CounterValueKinds.Rate, tags));
                }

                break;
            case "GaugeValuePublished":
                if (Parse(data.PayloadByName("lastValue")) is { } last)
                {
                    Add(data, new CounterValue(meter, instrument, instrument, unit, last, CounterValueKinds.Gauge, tags));
                }

                break;
            case "HistogramValuePublished":
                foreach (var (quantile, value) in Quantiles(data.PayloadByName("quantiles") as string))
                {
                    var percentile = "P" + (quantile * 100).ToString("0.#", CultureInfo.InvariantCulture);
                    Add(data, new CounterValue(meter, instrument + " " + percentile, instrument + " " + percentile, unit, value, CounterValueKinds.Percentile, tags));
                }

                break;
        }
    }

    private static double? Parse(object? value) =>
        value is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
        : value is double d ? d
        : null;

    /// <summary><c>0.5=1.2;0.95=3;0.99=5</c>.</summary>
    internal static IEnumerable<(double Quantile, double Value)> Quantiles(string? text)
    {
        foreach (var part in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0
                && double.TryParse(part[..equals], NumberStyles.Float, CultureInfo.InvariantCulture, out var quantile)
                && double.TryParse(part[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return (quantile, value);
            }
        }
    }

    private void OnGcStart(GCStartTraceData data)
    {
        lock (_gate)
        {
            _gcs[data.Count] = (data.TimeStampRelativeMSec, data.Depth, data.Reason.ToString());
        }
    }

    private void OnGcStop(GCEndTraceData data)
    {
        lock (_gate)
        {
            if (!_gcs.Remove(data.Count, out var start))
            {
                return;
            }

            var duration = data.TimeStampRelativeMSec - start.Start;
            var text = string.Create(CultureInfo.InvariantCulture, $"GC #{data.Count}: gen {start.Generation}, {start.Reason}, {duration:0.##} ms");
            _events.Add(new RuntimeEvent(start.Start / 1000, RuntimeEventKinds.GarbageCollection, text, start.Generation, duration));
            _gcs.Remove(data.Count - 1000); // never-finished collections don't pile up
        }
    }

    private void OnException(ExceptionTraceData data)
    {
        var text = string.IsNullOrEmpty(data.ExceptionMessage) ? data.ExceptionType : $"{data.ExceptionType}: {data.ExceptionMessage}";
        lock (_gate)
        {
            _events.Add(new RuntimeEvent(data.TimeStampRelativeMSec / 1000, RuntimeEventKinds.Exception, text, Type: data.ExceptionType));
        }
    }

    private void Add(TraceEvent data, CounterValue value)
    {
        lock (_gate)
        {
            _lastAt = Math.Max(_lastAt, data.TimeStampRelativeMSec / 1000);
            _values.Add(value);
        }
    }

    private async Task FlushLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(s_flushInterval, token).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task FlushAsync()
    {
        List<CounterValue> values;
        List<RuntimeEvent> events;
        double at;
        lock (_gate)
        {
            (values, _values) = (_values, []);
            (events, _events) = (_events, []);
            at = _lastAt;
        }

        try
        {
            if (values.Count > 0)
            {
                await _publish(new CountersUpdate(Id, at, values)).ConfigureAwait(false);
            }

            if (events.Count > 0)
            {
                await _publishEvents(new RuntimeEventsUpdate(Id, events)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or DaedalusStudio.Rpc.JsonRpcConnectionLostException)
        {
            // The shell has gone.
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_session is { } session)
        {
            try
            {
                await session.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerNotAvailableException or IOException or EndOfStreamException or ServerErrorException or UnsupportedCommandException or TimeoutException)
            {
                // The process has exited: its stream has ended too.
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _processing.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _session?.Dispose();
        if (_processing.IsCompleted)
        {
            _stop.Dispose();
        }
    }
}
