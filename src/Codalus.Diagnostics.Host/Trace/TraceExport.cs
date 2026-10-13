using System.Text.Json;
using Codalus.Diagnostics.Host.Cpu;
using Codalus.Diagnostics.Protocol;

namespace Codalus.Diagnostics.Host.Trace;

/// <summary>
/// Exports a profile's samples (ADR 0046) for other tools: Speedscope's file format (a sampled profile per thread, its
/// frames shared) and the Chromium trace event format (Perfetto, chrome://tracing): CPU samples become nested
/// complete events per thread (consecutive samples sharing frames merge), other profiles' samples instant events with
/// their stacks, and the trace's spans, requests and commands complete events on their own track.
/// </summary>
internal static class TraceExport
{
    public static long Write(TraceData data, CpuFilter filter, string format, string path)
    {
        var profile = data.Profile(filter.Profile);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var writer = new Utf8JsonWriter(stream))
        {
            switch (format)
            {
                case ExportFormats.Speedscope:
                    Speedscope(profile, filter, writer, Path.GetFileNameWithoutExtension(path));
                    break;
                case ExportFormats.Chromium:
                    Chromium(data, profile, filter, writer);
                    break;
                default:
                    throw new ArgumentException($"Unknown export format {format}: speedscope or chromium.");
            }
        }

        return new FileInfo(path).Length;
    }

    /// <summary>The functions on a sample's stack, root first.</summary>
    private static List<int> Functions(CpuProfile profile, int stack, List<int> into)
    {
        into.Clear();
        for (var at = stack; at >= 0; at = profile.StackParent(at))
        {
            into.Add(profile.NodeFunction(profile.StackNode(at)));
        }

        into.Reverse();
        return into;
    }

    private static IEnumerable<IGrouping<int, CpuSample>> Threads(CpuProfile profile, CpuFilter filter)
    {
        var threads = filter.Threads is { Count: > 0 } t ? t.ToHashSet() : null;
        return profile.Samples
            .Where(s => CpuViews.Matches(s, filter, threads))
            .GroupBy(s => s.Thread)
            .OrderBy(g => g.Key);
    }

    private static void Speedscope(CpuProfile profile, CpuFilter filter, Utf8JsonWriter writer, string name)
    {
        var (unit, scale) = profile.Unit switch
        {
            "KB" => ("bytes", 1024.0),
            "µs" => ("microseconds", 1.0),
            "samples" => ("milliseconds", 1.0),
            _ => ("none", 1.0),
        };
        var frames = new Dictionary<int, int>();
        var frameNames = new List<string>();
        var stack = new List<int>();
        writer.WriteStartObject();
        writer.WriteString("$schema", "https://www.speedscope.app/file-format-schema.json");
        writer.WriteString("exporter", "Codalus IDE");
        writer.WriteString("name", name);
        writer.WriteNumber("activeProfileIndex", 0);
        writer.WritePropertyName("profiles");
        writer.WriteStartArray();
        foreach (var thread in Threads(profile, filter))
        {
            var samples = thread.OrderBy(s => s.Time).ToList();
            writer.WriteStartObject();
            writer.WriteString("type", "sampled");
            writer.WriteString("name", profile.ThreadNames.GetValueOrDefault(thread.Key) ?? "Thread " + thread.Key);
            writer.WriteString("unit", unit);
            writer.WriteNumber("startValue", 0);
            writer.WriteNumber("endValue", samples.Sum(s => s.Weight * scale));
            writer.WritePropertyName("samples");
            writer.WriteStartArray();
            foreach (var sample in samples)
            {
                writer.WriteStartArray();
                foreach (var function in Functions(profile, sample.Stack, stack))
                {
                    if (!frames.TryGetValue(function, out var frame))
                    {
                        frame = frameNames.Count;
                        frames[function] = frame;
                        frameNames.Add(profile.FunctionName(function));
                    }

                    writer.WriteNumberValue(frame);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("weights");
            writer.WriteStartArray();
            foreach (var sample in samples)
            {
                writer.WriteNumberValue(sample.Weight * scale);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("shared");
        writer.WriteStartObject();
        writer.WritePropertyName("frames");
        writer.WriteStartArray();
        foreach (var frame in frameNames)
        {
            writer.WriteStartObject();
            writer.WriteString("name", frame);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void Chromium(TraceData data, CpuProfile profile, CpuFilter filter, Utf8JsonWriter writer)
    {
        const int Pid = 1;
        const double Gap = 10; // ms without a sample: the thread wasn't running managed code
        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ms");
        writer.WritePropertyName("traceEvents");
        writer.WriteStartArray();

        void Event(string name, string category, string phase, double at, double? duration, int tid, string? detail = null)
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("cat", category);
            writer.WriteString("ph", phase);
            writer.WriteNumber("ts", Math.Round(at * 1000, 3));
            if (duration is { } d)
            {
                writer.WriteNumber("dur", Math.Round(Math.Max(d, 0) * 1000, 3));
            }

            if (phase == "i")
            {
                writer.WriteString("s", "t");
            }

            writer.WriteNumber("pid", Pid);
            writer.WriteNumber("tid", tid);
            if (detail is not null)
            {
                writer.WritePropertyName("args");
                writer.WriteStartObject();
                writer.WriteString("detail", detail);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        void Metadata(int tid, string name)
        {
            writer.WriteStartObject();
            writer.WriteString("name", "thread_name");
            writer.WriteString("ph", "M");
            writer.WriteNumber("pid", Pid);
            writer.WriteNumber("tid", tid);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var cpu = profile.Unit == "samples";
        var stack = new List<int>();
        foreach (var thread in Threads(profile, filter))
        {
            Metadata(thread.Key, profile.ThreadNames.GetValueOrDefault(thread.Key) ?? "Thread " + thread.Key);
            var open = new List<(int Function, double Start)>();
            var last = 0.0;
            void CloseFrom(int depth, double at)
            {
                for (var i = open.Count - 1; i >= depth; i--)
                {
                    Event(profile.FunctionName(open[i].Function), "cpu", "X", open[i].Start, at - open[i].Start, thread.Key);
                }

                open.RemoveRange(depth, open.Count - depth);
            }

            foreach (var sample in thread.OrderBy(s => s.Time))
            {
                var functions = Functions(profile, sample.Stack, stack);
                if (!cpu)
                {
                    Event(profile.FunctionName(functions[^1]), profile.Unit, "i", sample.Time, null, thread.Key, string.Join(" ← ", Enumerable.Reverse(functions).Take(20).Select(f => profile.FunctionName(f))));
                    continue;
                }

                if (open.Count > 0 && sample.Time - last > Gap)
                {
                    CloseFrom(0, last + 1);
                }

                var common = 0;
                while (common < open.Count && common < functions.Count && open[common].Function == functions[common])
                {
                    common++;
                }

                CloseFrom(common, sample.Time);
                for (var i = common; i < functions.Count; i++)
                {
                    open.Add((functions[i], sample.Time));
                }

                last = sample.Time;
            }

            CloseFrom(0, last + 1);
        }

        // Activities on their own tracks.
        var tracks = new (string Name, int Tid, List<ActivityRecord> Items)[] { ("Spans", -1, data.Spans), ("HTTP requests", -2, data.Requests), ("Database commands", -3, data.Commands) };
        foreach (var (name, tid, items) in tracks)
        {
            var shown = items.Where(a => (filter.StartMilliseconds is not { } s || (a.End ?? data.Duration) >= s) && (filter.EndMilliseconds is not { } e || a.Start <= e)).ToList();
            if (shown.Count == 0)
            {
                continue;
            }

            Metadata(tid, name);
            foreach (var item in shown)
            {
                Event(item.Name, name, "X", item.Start, (item.End ?? data.Duration) - item.Start, tid, item.Detail);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
