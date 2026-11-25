using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Codalus.Rpc;

// codalus-rpcbench [rounds]: JSON-RPC round trips over an in-memory pipe, an echo of each payload, at 1 KB (a completion
// request's size), 1 MB (a large file's semantic tokens) and 15 MB (a didOpen of a large file), ASCII and markup text
// (XML/Razor: the characters the default encoder escaped). One JSON line per size with the median and best time per
// round trip and the bytes allocated per round trip. Only the connection's public raw API is used, so the same source
// builds against an older Codalus.Rpc for a same-runner comparison (benchmarks/README.md).
var rounds = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 7;
var toServer = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
var toClient = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
await using var client = new JsonRpcConnection(toClient.Reader.AsStream(), toServer.Writer.AsStream());
await using var server = new JsonRpcConnection(toServer.Reader.AsStream(), toClient.Writer.AsStream());
server.OnRequest("echo", (parameters, _) => Task.FromResult<JsonElement?>(parameters.Clone()));
client.Start();
server.Start();

foreach (var (name, size, markup) in new[] { ("1kb", 1_024, false), ("1kb-markup", 1_024, true), ("1mb", 1 << 20, false), ("1mb-markup", 1 << 20, true), ("15mb", 15 << 20, false) })
{
    var text = Text(size, markup);
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text }, BenchJson.Default.DictionaryStringString));
    var parameters = document.RootElement.Clone();
    var count = size <= 1_024 ? 2_000 : size <= 1 << 20 ? 40 : 4;
    for (var i = 0; i < Math.Max(1, count / 4); i++)
    {
        await client.InvokeRawAsync("echo", parameters, CancellationToken.None); // warm-up
    }

    var times = new List<double>();
    long allocated = 0;
    for (var round = 0; round < rounds; round++)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            var echoed = await client.InvokeRawAsync("echo", parameters, CancellationToken.None);
            if (i == 0 && echoed.GetProperty("text").GetString()!.Length != text.Length)
            {
                throw new InvalidOperationException("The echo differs.");
            }
        }

        times.Add(Stopwatch.GetElapsedTime(started).TotalMicroseconds / count);
        allocated += (GC.GetTotalAllocatedBytes(precise: true) - before) / count;
    }

    times.Sort();
    Console.WriteLine(JsonSerializer.Serialize(new BenchResult(name, size, count, times[times.Count / 2], times[0], allocated / rounds), BenchJson.Default.BenchResult));
}

static string Text(int size, bool markup)
{
    const string ascii = "public static int Compute(int value) { return value * 2; } ";
    const string xml = "<Grid x:Name='Root' Margin=\"4\"><TextBlock Text=\"a & b + c\"/></Grid> ";
    var unit = markup ? xml : ascii;
    var builder = new StringBuilder(size + unit.Length);
    while (builder.Length < size)
    {
        builder.Append(unit);
    }

    return builder.ToString(0, size);
}

internal sealed record BenchResult(string Payload, int Bytes, int RoundTrips, double MedianMicroseconds, double BestMicroseconds, long AllocatedBytesPerRoundTrip);

[System.Text.Json.Serialization.JsonSerializable(typeof(BenchResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class BenchJson : System.Text.Json.Serialization.JsonSerializerContext;
