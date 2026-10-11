using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace DaedalusStudio.Workbench.Aspire;

/// <summary>A URL of an Aspire resource (an endpoint).</summary>
public sealed record AspireUrl(string Name, string Url, bool IsInternal);

/// <summary>A command a resource offers (<c>resource-start</c>, <c>resource-stop</c>, <c>resource-restart</c>…).</summary>
public sealed record AspireCommand(string Name, string DisplayName, bool IsEnabled);

/// <summary>
/// An Aspire resource as the AppHost's resource service describes it: a project, container, executable or parameter,
/// with its state (<c>Running</c>, <c>Starting</c>, <c>Finished</c>, <c>NotStarted</c>…), endpoints and environment.
/// </summary>
public sealed record AspireResource(
    string Name,
    string Type,
    string DisplayName,
    string? State,
    string? StateStyle,
    IReadOnlyList<AspireUrl> Urls,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    IReadOnlyList<KeyValuePair<string, string>> Properties,
    IReadOnlyList<AspireCommand> Commands,
    bool IsHidden);

/// <summary>A change of the resource tree: <see cref="Initial"/> (the whole list), or one resource upserted or deleted.</summary>
public sealed record AspireResourceChange(IReadOnlyList<AspireResource>? Initial, AspireResource? Upsert, string? Deleted);

/// <summary>A line of a resource's console output.</summary>
public sealed record AspireLogLine(int Number, string Text, bool IsError);

/// <summary>
/// The client side of the AppHost's resource service (<c>aspire.v1.DashboardService</c>, the gRPC service the Aspire
/// dashboard reads, M17b, ADR 0043): the resource tree as it changes, a resource's console logs, and its commands.
/// gRPC is spoken directly over <see cref="HttpClient"/> (HTTP/2, cleartext for an <c>http</c> endpoint) with our own
/// protocol buffers reader, so the Native AOT shell needs neither Grpc.Net.Client nor generated code; unknown fields
/// are skipped, so newer AppHosts still read.
/// </summary>
public sealed class AspireResourceClient(Uri endpoint, string? apiKey) : IDisposable
{
    private const string Service = "/aspire.v1.DashboardService/";
    private const string ApiKeyHeader = "x-resource-service-api-key";

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // The resource service of a local AppHost uses the development certificate (or none).
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || endpoint.IsLoopback },
        EnableMultipleHttp2Connections = true,
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public Uri Endpoint { get; } = endpoint;

    /// <summary>The resource tree: the initial list, then each change, until cancelled or the AppHost ends.</summary>
    public async IAsyncEnumerable<AspireResourceChange> WatchResourcesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in StreamAsync("WatchResources", [], cancellationToken).ConfigureAwait(false))
        {
            foreach (var change in AspireProto.ReadUpdate(message))
            {
                yield return change;
            }
        }
    }

    /// <summary>A resource's console output: what it has written, then new lines as they come.</summary>
    public async IAsyncEnumerable<AspireLogLine> WatchLogsAsync(string resource, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ProtoWriter().String(1, resource).ToArray();
        await foreach (var message in StreamAsync("WatchResourceConsoleLogs", request, cancellationToken).ConfigureAwait(false))
        {
            foreach (var line in AspireProto.ReadLogs(message))
            {
                yield return line;
            }
        }
    }

    /// <summary>Runs a resource command (<c>resource-start</c>…); null when it succeeded, else the error.</summary>
    public async Task<string?> ExecuteAsync(string command, AspireResource resource, CancellationToken cancellationToken)
    {
        var request = new ProtoWriter().String(1, command).String(2, resource.Name).String(3, resource.Type).ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2)); // a command that never finishes doesn't hold its caller
        await foreach (var message in StreamAsync("ExecuteResourceCommand", request, timeout.Token).ConfigureAwait(false))
        {
            return AspireProto.ReadCommandResponse(message);
        }

        return "The resource service didn't answer.";
    }

    /// <summary>One gRPC call: the request message framed, then the response messages as they arrive.</summary>
    private async IAsyncEnumerable<byte[]> StreamAsync(string method, byte[] request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var frame = new byte[5 + request.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)request.Length);
        request.CopyTo(frame, 5);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, Service + method))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(frame),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        message.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));
        if (apiKey is not null)
        {
            message.Headers.Add(ApiKeyHeader, apiKey);
        }

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The resource service answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        Check(response.Headers); // a trailers-only error
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var header = new byte[5];
            while (await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                if (header[0] != 0)
                {
                    throw new InvalidDataException("The resource service sent a compressed message.");
                }

                var body = new byte[length];
                if (!await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                yield return body;
            }
        }

        Check(response.TrailingHeaders);
    }

    private static void Check(HttpHeaders headers)
    {
        if (headers.TryGetValues("grpc-status", out var status) && status.FirstOrDefault() is { } code && code != "0")
        {
            var text = headers.TryGetValues("grpc-message", out var messages) ? Uri.UnescapeDataString(messages.FirstOrDefault() ?? string.Empty) : string.Empty;
            throw new HttpRequestException($"The resource service failed (gRPC status {code}): {text}");
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 && buffer.Length > 0 ? false : throw new IOException("The resource service's stream ended inside a message.");
            }

            read += n;
        }

        return true;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The resource service's messages, read from the wire (field numbers from <c>dashboard_service.proto</c>, MIT).</summary>
internal static class AspireProto
{
    public static IReadOnlyList<AspireResourceChange> ReadUpdate(ReadOnlySpan<byte> data)
    {
        var changes = new List<AspireResourceChange>();
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                // InitialResourceData { repeated Resource resources = 1; }
                var initial = new ProtoReader(reader.ReadBytes());
                var resources = new List<AspireResource>();
                while (initial.Next(out var f, out var w))
                {
                    if (f == 1 && w == 2)
                    {
                        resources.Add(ReadResource(initial.ReadBytes()));
                    }
                    else
                    {
                        initial.Skip(w);
                    }
                }

                changes.Add(new AspireResourceChange(resources, null, null));
            }
            else if (field == 2 && wire == 2)
            {
                // WatchResourcesChanges { repeated WatchResourcesChange value = 1; }
                var list = new ProtoReader(reader.ReadBytes());
                while (list.Next(out var f, out var w))
                {
                    if (f != 1 || w != 2)
                    {
                        list.Skip(w);
                        continue;
                    }

                    var change = new ProtoReader(list.ReadBytes());
                    while (change.Next(out var cf, out var cw))
                    {
                        if (cf == 1 && cw == 2)
                        {
                            // ResourceDeletion { resource_name = 1; }
                            var deletion = new ProtoReader(change.ReadBytes());
                            string? name = null;
                            while (deletion.Next(out var df, out var dw))
                            {
                                if (df == 1 && dw == 2)
                                {
                                    name = deletion.ReadString();
                                }
                                else
                                {
                                    deletion.Skip(dw);
                                }
                            }

                            changes.Add(new AspireResourceChange(null, null, name));
                        }
                        else if (cf == 2 && cw == 2)
                        {
                            changes.Add(new AspireResourceChange(null, ReadResource(change.ReadBytes()), null));
                        }
                        else
                        {
                            change.Skip(cw);
                        }
                    }
                }
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return changes;
    }

    public static AspireResource ReadResource(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        string name = string.Empty, type = string.Empty, display = string.Empty;
        string? state = null, style = null;
        var hidden = false;
        var urls = new List<AspireUrl>();
        var environment = new List<KeyValuePair<string, string>>();
        var properties = new List<KeyValuePair<string, string>>();
        var commands = new List<AspireCommand>();
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    name = reader.ReadString();
                    break;
                case 2 when wire == 2:
                    type = reader.ReadString();
                    break;
                case 3 when wire == 2:
                    display = reader.ReadString();
                    break;
                case 5 when wire == 2:
                    state = reader.ReadString();
                    break;
                case 7 when wire == 2:
                    var (variable, value) = Pair(reader.ReadBytes(), 1, 2);
                    environment.Add(new(variable, value));
                    break;
                case 11 when wire == 2:
                    commands.Add(ReadCommand(reader.ReadBytes()));
                    break;
                case 12 when wire == 2:
                    properties.Add(ReadProperty(reader.ReadBytes()));
                    break;
                case 13 when wire == 2:
                    urls.Add(ReadUrl(reader.ReadBytes()));
                    break;
                case 14 when wire == 2:
                    style = reader.ReadString();
                    break;
                case 21 when wire == 0:
                    hidden = reader.ReadBool();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new AspireResource(name, type, display.Length > 0 ? display : name, state is { Length: > 0 } ? state : null, style is { Length: > 0 } ? style : null, urls, environment, properties, commands, hidden);
    }

    private static (string, string) Pair(ReadOnlySpan<byte> data, int first, int second)
    {
        var reader = new ProtoReader(data);
        string a = string.Empty, b = string.Empty;
        while (reader.Next(out var field, out var wire))
        {
            if (field == first && wire == 2)
            {
                a = reader.ReadString();
            }
            else if (field == second && wire == 2)
            {
                b = reader.ReadString();
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return (a, b);
    }

    private static AspireCommand ReadCommand(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        string name = string.Empty, display = string.Empty;
        var state = 0UL;
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    name = reader.ReadString();
                    break;
                case 2 when wire == 2:
                    display = reader.ReadString();
                    break;
                case 9 when wire == 0:
                    state = reader.ReadVarint(); // 0 enabled, 1 disabled, 2 hidden
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new AspireCommand(name, display.Length > 0 ? display : name, state == 0);
    }

    private static KeyValuePair<string, string> ReadProperty(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        string name = string.Empty;
        string? value = null;
        var sensitive = false;
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    name = reader.ReadString();
                    break;
                case 3 when wire == 2:
                    value = ProtoValue.Read(reader.ReadBytes());
                    break;
                case 4 when wire == 0:
                    sensitive = reader.ReadBool();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new(name, sensitive ? "••••" : value ?? string.Empty);
    }

    private static AspireUrl ReadUrl(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        string name = string.Empty, url = string.Empty;
        var isInternal = false;
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    name = reader.ReadString();
                    break;
                case 2 when wire == 2:
                    url = reader.ReadString();
                    break;
                case 3 when wire == 0:
                    isInternal = reader.ReadBool();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new AspireUrl(name, url, isInternal);
    }

    public static IReadOnlyList<AspireLogLine> ReadLogs(ReadOnlySpan<byte> data)
    {
        var lines = new List<AspireLogLine>();
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field != 1 || wire != 2)
            {
                reader.Skip(wire);
                continue;
            }

            var line = new ProtoReader(reader.ReadBytes());
            string text = string.Empty;
            var error = false;
            var number = 0;
            while (line.Next(out var f, out var w))
            {
                switch (f)
                {
                    case 1 when w == 2:
                        text = line.ReadString();
                        break;
                    case 2 when w == 0:
                        error = line.ReadBool();
                        break;
                    case 3 when w == 0:
                        number = (int)line.ReadVarint();
                        break;
                    default:
                        line.Skip(w);
                        break;
                }
            }

            lines.Add(new AspireLogLine(number, text, error));
        }

        return lines;
    }

    /// <summary>ResourceCommandResponse { kind = 1 (1 succeeded, 2 failed, 3 cancelled); error_message = 2; }: null when it succeeded.</summary>
    public static string? ReadCommandResponse(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        ulong kind = 0;
        string? error = null;
        while (reader.Next(out var field, out var wire))
        {
            if (field == 1 && wire == 0)
            {
                kind = reader.ReadVarint();
            }
            else if (field == 2 && wire == 2)
            {
                error = reader.ReadString();
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return kind == 1 ? null : error ?? (kind == 3 ? "The command was cancelled." : "The command failed.");
    }
}
