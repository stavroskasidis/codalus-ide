using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Shell.Infrastructure;

/// <summary>A request from a second <c>daedalus</c> process to the running one, for example <c>diff left right</c>.</summary>
public sealed record InstanceRequest(string Command, string[] Arguments);

/// <summary>
/// Hands command lines to an already running shell of the same user and user-data folder, over a named pipe (a Unix
/// domain socket on Linux and macOS). Used by <c>daedalus diff</c>, so diff tools such as Verify's open their
/// comparisons in the running window instead of a new one.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromMilliseconds(500);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private readonly Action<InstanceRequest> _handler;

    private SingleInstance(string pipeName, Action<InstanceRequest> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
        _ = Task.Run(ListenAsync);
    }

    /// <summary>One pipe per user and user-data folder (separate profiles are separate instances).</summary>
    public static string PipeName(string userDataFolder)
    {
        var key = Environment.UserName + "|" + Path.GetFullPath(userDataFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return "daedalus-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    }

    /// <summary>Starts answering requests; <paramref name="handler"/> runs on a background thread.</summary>
    public static SingleInstance Listen(string pipeName, Action<InstanceRequest> handler) => new(pipeName, handler);

    /// <summary>Sends a request to the running shell. False when none answers (start a new one then).</summary>
    public static bool TrySend(string pipeName, InstanceRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(s_connectTimeout);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request, InstanceJsonContext.Default.InstanceRequest));
            return reader.ReadLine() == "ok";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is not null && JsonSerializer.Deserialize(line, InstanceJsonContext.Default.InstanceRequest) is { } request)
                {
                    await writer.WriteLineAsync("ok").ConfigureAwait(false);
                    _handler(request);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken client or a second shell already listening on this name; keep serving (or give up quietly).
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstanceRequest))]
internal sealed partial class InstanceJsonContext : JsonSerializerContext;
