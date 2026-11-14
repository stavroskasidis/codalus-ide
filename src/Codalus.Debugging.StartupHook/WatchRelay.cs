using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;

/// <summary>
/// Hot Reload in a console of its own (a console window or the integrated terminal): <c>dotnet watch</c> runs there, so
/// the app it starts gets that console, and the IDE no longer reads <c>dotnet watch</c>'s output. Loaded into
/// <c>dotnet-watch</c> with <c>CODALUS_WATCH_RELAY_PIPE</c> set, this copies what it writes to standard error (all of
/// its own messages: "Waiting for changes", "Hot reload succeeded", compile errors, restarts) to the IDE's pipe, while
/// the console still shows them. The app inherits the console's own handles, so its output isn't copied.
/// The <c>dotnet</c> command before it (which starts <c>dotnet-watch</c>) leaves the variable for it; <c>dotnet-watch</c>
/// clears it and takes this hook out of <c>DOTNET_STARTUP_HOOKS</c>, so the processes it starts don't load it.
/// </summary>
internal static class WatchRelay
{
    public const string PipeVariable = "CODALUS_WATCH_RELAY_PIPE";

    private const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    /// <summary>The entry assembly of <c>dotnet watch</c>'s own process.</summary>
    private const string DotnetWatch = "dotnet-watch";

    /// <summary>Whether this process is the <c>dotnet-watch</c> to relay (then the hook does nothing else).</summary>
    public static bool Start(string entryAssembly)
    {
        var name = Environment.GetEnvironmentVariable(PipeVariable);
        if (string.IsNullOrEmpty(name) || entryAssembly != DotnetWatch)
        {
            return false;
        }

        Environment.SetEnvironmentVariable(PipeVariable, null);
        RemoveFromStartupHooks();
        try
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
            pipe.Connect(10_000);
            var relay = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetError(new TeeWriter(Console.Error, relay));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The IDE is gone: dotnet watch runs on in its console.
        }

        return true;
    }

    private static void RemoveFromStartupHooks()
    {
        var hooks = Environment.GetEnvironmentVariable(StartupHooksVariable);
        var self = typeof(WatchRelay).Assembly.Location;
        if (string.IsNullOrEmpty(hooks) || string.IsNullOrEmpty(self))
        {
            return;
        }

        var others = hooks.Split(Path.PathSeparator).Where(h => h.Length > 0 && !string.Equals(h, self, StringComparison.OrdinalIgnoreCase)).ToArray();
        Environment.SetEnvironmentVariable(StartupHooksVariable, others.Length == 0 ? null : string.Join(Path.PathSeparator.ToString(), others));
    }

    /// <summary>Writes to the console and to the IDE; once the IDE's end fails (it closed), to the console only.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly object _gate = new object();
        private TextWriter? _relay;

        public TeeWriter(TextWriter console, TextWriter relay)
            : base(console.FormatProvider)
        {
            _console = console;
            _relay = relay;
            NewLine = console.NewLine;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _console.Write(value);
                Relay(r => r.Write(value));
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _console.Write(value);
                Relay(r => r.Write(value));
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                _console.Write(buffer, index, count);
                Relay(r => r.Write(buffer, index, count));
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                _console.Flush();
                Relay(r => r.Flush());
            }
        }

        private void Relay(Action<TextWriter> write)
        {
            if (_relay is not { } relay)
            {
                return;
            }

            try
            {
                write(relay);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _relay = null;
            }
        }
    }
}
