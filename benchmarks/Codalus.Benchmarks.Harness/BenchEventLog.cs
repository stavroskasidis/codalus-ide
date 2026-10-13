using System.Text.Json;

namespace Codalus.Benchmarks.Harness;

/// <summary>One line of a prototype's bench-event file (see Codalus.Diagnostics.BenchEvents).</summary>
internal sealed record BenchEvent(string Name, long WallMicros, long MonoMicros, IReadOnlyDictionary<string, long> Fields)
{
    /// <summary>String fields (M34c: <c>designtime_done</c>'s <c>cache_miss_reasons</c>).</summary>
    public IReadOnlyDictionary<string, string> Text { get; init; } = new Dictionary<string, string>();

    public long Field(string key) => Fields.TryGetValue(key, out var value) ? value : 0;
}

internal static class BenchEventLog
{
    public static List<BenchEvent> Read(string path)
    {
        var events = new List<BenchEvent>();
        if (!File.Exists(path))
        {
            return events;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var fields = new Dictionary<string, long>(StringComparer.Ordinal);
                var text = new Dictionary<string, string>(StringComparer.Ordinal);
                string? name = null;
                long wall = 0, mono = 0;
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "ev":
                            name = property.Value.GetString();
                            break;
                        case "wall_us":
                            wall = property.Value.GetInt64();
                            break;
                        case "mono_us":
                            mono = property.Value.GetInt64();
                            break;
                        default:
                            if (property.Value.ValueKind == JsonValueKind.Number)
                            {
                                fields[property.Name] = property.Value.GetInt64();
                            }
                            else if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                text[property.Name] = property.Value.GetString()!;
                            }

                            break;
                    }
                }

                if (name is not null)
                {
                    events.Add(new BenchEvent(name, wall, mono, fields) { Text = text });
                }
            }
            catch (JsonException)
            {
                // A partially written last line.
            }
        }

        return events;
    }

    /// <summary>Polls the file until <paramref name="name"/> appears; returns it or null on timeout or process exit.</summary>
    public static async Task<BenchEvent?> WaitForAsync(string path, string name, TimeSpan timeout, System.Diagnostics.Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Read(path).FirstOrDefault(e => e.Name == name) is { } found)
            {
                return found;
            }

            if (process.HasExited)
            {
                return Read(path).FirstOrDefault(e => e.Name == name);
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public static long UnixMicrosNow() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
}
