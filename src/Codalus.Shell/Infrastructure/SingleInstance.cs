using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// A request from a second <c>codalus</c> process to the running one, for example <c>diff left right</c>. With
/// <see cref="Wait"/> the answer comes when the work is done (<c>merge</c>, <c>--wait</c>: git waits for the tool).
/// </summary>
public sealed record InstanceRequest(string Command, string[] Arguments, bool Wait = false);

/// <summary>
/// Hands command lines to an already running shell of the same user and user-data folder, over a named pipe (a Unix
/// domain socket on Linux and macOS). Used by <c>codalus diff</c>, so diff tools such as Verify's open their
/// comparisons in the running window instead of a new one.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromMilliseconds(500);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private readonly Func<InstanceRequest, Task<bool>> _handler;

    private SingleInstance(string pipeName, Func<InstanceRequest, Task<bool>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
        _ = Task.Run(ListenAsync);
    }

    /// <summary>One pipe per user and user-data folder (separate profiles are separate instances).</summary>
    public static string PipeName(string userDataFolder)
    {
        var key = Environment.UserName + "|" + Path.GetFullPath(userDataFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return "codalus-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Starts answering requests; <paramref name="handler"/> runs on a background thread and says whether the work
    /// succeeded (only waiting requests hear it).
    /// </summary>
    public static SingleInstance Listen(string pipeName, Func<InstanceRequest, Task<bool>> handler) => new(pipeName, handler);

    /// <summary>Sends a request to the running shell. False when none answers (start a new one then).</summary>
    public static bool TrySend(string pipeName, InstanceRequest request) => Send(pipeName, request) is not null;

    /// <summary>
    /// Sends a request; null when no shell answers. A waiting request blocks until the shell is done and returns
    /// whether it succeeded; others return true as soon as the shell has the request.
    /// </summary>
    public static bool? Send(string pipeName, InstanceRequest request)
    {
        if (!MayBeListening(pipeName))
        {
            return null;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(s_connectTimeout);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request, InstanceJsonContext.Default.InstanceRequest));
            return reader.ReadLine() switch
            {
                "ok" or "done" => true,
                "failed" => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a shell may listen on the pipe: connecting where none does retries until the timeout, so every
    /// <c>codalus &lt;solution&gt;</c>, Open with… and <c>codalus file</c> with no IDE running waited half a second before
    /// starting one. Its socket file (Linux, macOS) or its entry among the named pipes (Windows); true when that can't be
    /// told, to connect as before.
    /// </summary>
    internal static bool MayBeListening(string pipeName)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase))
                : File.Exists(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName)); // where .NET binds a pipe's socket
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                // Each client on its own: a waiting request (a merge) must not hold up the next one.
                _ = ServeAsync(pipe);
                pipe = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A second shell already listening on this name; keep trying quietly.
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await using (writer.ConfigureAwait(false))
                {
                    var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (line is null || JsonSerializer.Deserialize(line, InstanceJsonContext.Default.InstanceRequest) is not { } request)
                    {
                        return;
                    }

                    if (!request.Wait)
                    {
                        await writer.WriteLineAsync("ok").ConfigureAwait(false);
                        _ = _handler(request);
                        return;
                    }

                    var succeeded = await _handler(request).ConfigureAwait(false);
                    await writer.WriteLineAsync(succeeded ? "done" : "failed").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, or the shell is closing.
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
