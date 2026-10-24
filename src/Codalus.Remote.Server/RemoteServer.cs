using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Codalus.Platform;
using Codalus.Platform.Pty;
using Codalus.Remote.Protocol;
using Codalus.Rpc;
using Codalus.Rpc.Multiplexing;

namespace Codalus.Remote.Server;

/// <summary>
/// The server side of a remote session (M21, ADR 0050): processes with their stdio on channels (the build and language
/// hosts, netcoredbg), pseudo-terminals, the file system, listening ports and TCP tunnels to them. Everything it
/// started ends with the session: when stdin ends, or on <c>shutdown</c>.
/// </summary>
public sealed partial class RemoteServer(ChannelMultiplexer multiplexer, JsonRpcConnection connection) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, Process> _processes = new();
    private readonly ConcurrentDictionary<uint, PseudoTerminal> _terminals = new();
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly RemoteJsonContext Json = RemoteJsonContext.Default;

    public static string Version { get; } =
        (typeof(RemoteServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        connection.OnRequest(RemoteMethods.Hello, Json.HelloParams, Json.HelloResult, (p, _) => Task.FromResult(Hello(p)));
        connection.OnRequest(RemoteMethods.Shutdown, async (_, _) =>
        {
            await StopAllAsync().ConfigureAwait(false);
            return null;
        });
        connection.OnNotification(RemoteMethods.Exit, Json.EmptyResult, _ => _exit.TrySetResult());
        connection.OnRequest(RemoteMethods.Spawn, Json.SpawnParams, Json.SpawnResult, (p, _) => Task.FromResult(Spawn(p)));
        connection.OnRequest(RemoteMethods.Kill, Json.ChannelParams, Json.EmptyResult, (p, _) => Task.FromResult(Kill(p)));
        connection.OnRequest(RemoteMethods.PtyOpen, Json.PtyOpenParams, Json.SpawnResult, (p, _) => Task.FromResult(OpenTerminal(p)));
        connection.OnRequest(RemoteMethods.PtyResize, Json.PtyResizeParams, Json.EmptyResult, (p, _) => Task.FromResult(Resize(p)));
        RegisterFileSystem();
        RegisterPorts();
    }

    // ------------------------------------------------------------------ hello

    private static HelloResult Hello(HelloParams p)
    {
        if (p.ProtocolVersion != RemoteMethods.ProtocolVersion)
        {
            throw new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, $"This server speaks protocol {RemoteMethods.ProtocolVersion}, not {p.ProtocolVersion}.");
        }

        var dotnet = Dotnet.Value;
        return new HelloResult(
            Version,
            RemoteMethods.ProtocolVersion,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.ProcessId,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.UserName,
            dotnet,
            dotnet is null ? null : DotnetVersion(dotnet),
            LoginShell(),
            AppContext.BaseDirectory);
    }

    /// <summary>
    /// The <c>dotnet</c> that runs hosts: the one running this server (a framework-dependent build), then
    /// <c>DOTNET_ROOT</c>, <c>~/.dotnet</c>, the PATH, and the usual install folders.
    /// </summary>
    internal static readonly Lazy<string?> Dotnet = new(() =>
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.ProcessPath is { } self && Path.GetFileName(self).Equals(exe, StringComparison.OrdinalIgnoreCase))
        {
            return self;
        }

        List<string> candidates = [];
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        {
            candidates.Add(Path.Combine(root, exe));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", exe));
        if (PseudoTerminal.ResolveExecutable("dotnet") is { } onPath)
        {
            candidates.Add(onPath);
        }

        candidates.AddRange(OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", exe)]
            : ["/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/local/share/dotnet/dotnet", "/opt/homebrew/opt/dotnet/libexec/dotnet"]);
        return candidates.FirstOrDefault(File.Exists);
    });

    private static string? DotnetVersion(string dotnet)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(dotnet, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.WaitForExit(15000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string LoginShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        }

        if (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell && File.Exists(shell))
        {
            return shell;
        }

        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    // ------------------------------------------------------------------ processes

    private SpawnResult Spawn(SpawnParams p)
    {
        var (file, prefix) = Resolve(p.FileName);
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in prefix.Concat(p.Arguments))
        {
            start.ArgumentList.Add(argument);
        }

        if (p.WorkingDirectory is { Length: > 0 } directory && Directory.Exists(directory))
        {
            start.WorkingDirectory = directory;
        }

        foreach (var (key, value) in p.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        var channel = multiplexer.Open(p.Channel);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = channel.CloseAsync();
            throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, $"Couldn't start {p.FileName} on {Environment.MachineName}: {ex.Message}");
        }

        _processes[p.Channel] = process;
        var stdout = Task.Run(async () =>
        {
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(channel.Stream, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }

            await channel.CloseOutputAsync().ConfigureAwait(false);
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await channel.Stream.CopyToAsync(process.StandardInput.BaseStream, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
        });
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                _ = Notify(RemoteMethods.Stderr, new StderrParams(p.Channel, line), Json.StderrParams);
            }
        };
        process.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await ((Task)stdout).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            int? code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = null;
            }

            _processes.TryRemove(p.Channel, out _);
            process.Dispose();
            await Notify(RemoteMethods.Exited, new ExitedParams(p.Channel, code), Json.ExitedParams).ConfigureAwait(false);
        });
        return new SpawnResult(process.Id);
    }

    /// <summary><c>dotnet</c> is this server's <see cref="Dotnet"/>; a <c>.dll</c> runs on it.</summary>
    private static (string File, IReadOnlyList<string> Prefix) Resolve(string fileName)
    {
        if (fileName == "dotnet")
        {
            return (Dotnet.Value ?? throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, $".NET isn't installed on {Environment.MachineName}: 'dotnet' wasn't found (install the .NET SDK there)."), []);
        }

        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return (Resolve("dotnet").File, [fileName]);
        }

        return (fileName, []);
    }

    private EmptyResult Kill(ChannelParams p)
    {
        if (_processes.TryGetValue(p.Channel, out var process))
        {
            try
            {
                ProcessTree.Kill(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        if (_terminals.TryGetValue(p.Channel, out var terminal))
        {
            terminal.Kill();
        }

        return new EmptyResult();
    }

    // ------------------------------------------------------------------ terminals

    private SpawnResult OpenTerminal(PtyOpenParams p)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        environment["TERM_PROGRAM"] = "codalus";
        foreach (var (key, value) in p.Environment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        var (file, arguments) = p.FileName is { Length: > 0 } program
            ? (program == "dotnet" ? Resolve("dotnet").File : program, p.Arguments)
            : (LoginShell(), OperatingSystem.IsWindows() ? p.Arguments : ["-l", .. p.Arguments]);
        var directory = p.WorkingDirectory is { Length: > 0 } folder && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var channel = multiplexer.Open(p.Channel);
        PseudoTerminal terminal;
        try
        {
            terminal = PseudoTerminal.Start(new PtyStartInfo(file, arguments, directory, environment, Math.Max(2, p.Columns), Math.Max(2, p.Rows)));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _ = channel.CloseAsync();
            throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, $"Couldn't start a terminal ({file}): {ex.Message}");
        }

        _terminals[p.Channel] = terminal;

        // Output arrives on the PTY's reader thread: queued, so the channel gets it in order without blocking that thread.
        var output = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        terminal.Output += data => output.Writer.TryWrite(data.ToArray());
        var exitHandled = 0;
        void OnExited(int code)
        {
            if (Interlocked.Exchange(ref exitHandled, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                _terminals.TryRemove(p.Channel, out _);
                try
                {
                    // The code goes before the output ends: the client asks for it once its reading ends.
                    await Notify(RemoteMethods.Exited, new ExitedParams(p.Channel, code), Json.ExitedParams).ConfigureAwait(false);
                }
                finally
                {
                    output.Writer.TryComplete();
                }
            });
        }

        terminal.Exited += OnExited;

        // A quick program can exit before the handler is attached (its output is kept for the first handler, its exit isn't).
        if (terminal.ExitCode is { } exitCode)
        {
            OnExited(exitCode);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in output.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    await channel.Stream.WriteAsync(chunk, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }

            await channel.CloseOutputAsync().ConfigureAwait(false);
        });
        _ = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (await channel.Stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false) is var read and > 0)
                {
                    if (terminal.ExitCode is null)
                    {
                        terminal.Write(buffer.AsSpan(0, read));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }

            // The client closed the terminal.
            if (terminal.ExitCode is null)
            {
                terminal.Kill();
            }
        });
        return new SpawnResult(terminal.ProcessId);
    }

    private EmptyResult Resize(PtyResizeParams p)
    {
        if (_terminals.TryGetValue(p.Channel, out var terminal) && terminal.ExitCode is null)
        {
            terminal.Resize(Math.Max(2, p.Columns), Math.Max(2, p.Rows));
        }

        return new EmptyResult();
    }

    // ------------------------------------------------------------------ helpers

    private async Task Notify<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            await connection.NotifyAsync(method, parameters, info, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionLostException or OperationCanceledException)
        {
        }
    }

    private Task StopAllAsync()
    {
        foreach (var process in _processes.Values)
        {
            try
            {
                ProcessTree.Kill(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        foreach (var terminal in _terminals.Values)
        {
            terminal.Kill();
        }

        StopWatchers();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var terminal in _terminals.Values)
        {
            terminal.Dispose();
        }

        _lifetime.Dispose();
    }
}
