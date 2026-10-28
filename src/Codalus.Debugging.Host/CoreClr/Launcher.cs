using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using Codalus.Platform;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// <c>codalus-debughost --launch &lt;pipe&gt; &lt;program&gt; [args]</c> (M54, ADR 0086's launch): starts the program
/// for CoreCLR's debugger adapter, which listens on the pipe. The program is started through dbgshim's
/// <c>CreateProcessForLaunch</c>, suspended, so that it is this process's child (mscordbi in the adapter reaps its own
/// children off Windows, and their exit codes with them) and runs no code before the adapter has registered for its
/// runtime's startup. It inherits this process's environment, working directory and standard handles, which the
/// adapter (or the terminal) gave it: nothing is added to the program's environment or output.
/// </summary>
/// <remarks>
/// <para>The pipe carries lines: the launcher sends <c>pid N</c> (or <c>error message</c>), then waits for
/// <c>resume</c>; once the program ends it sends <c>exit C</c> and exits with that code itself. The adapter may send
/// <c>detach</c>, after which a closed pipe leaves the program running.</para>
/// <para>A pipe that closes first (the adapter ended or went away) ends the program: one never resumed ignores SIGTERM
/// (ADR 0086's spike 3), so it is killed, as is one that runs, with its children. Ctrl+C in a terminal reaches the
/// program, which is in the same process group; the launcher ignores it and waits for the program.</para>
/// </remarks>
internal static class Launcher
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: codalus-debughost --launch <pipe> <program> [args]");
            return 2;
        }

        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        // Asynchronous: on Windows a synchronous handle serializes its I/O, and the exit code's write would wait for the
        // read that watches for the adapter going away.
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(30_000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Console.Error.WriteLine($"codalus-debughost --launch: the debugger isn't listening ({ex.Message}).");
            return 2;
        }

        // Not disposed: they write through at once (AutoFlush), and the pipe may be broken by then (the adapter went away).
        var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        CreateProcessForLaunchResult launch;
        try
        {
            launch = DbgShimLibrary.Shim.CreateProcessForLaunch(DesktopClr.CommandLine(args[1], args[2..]), bSuspendProcess: true, lpEnvironment: IntPtr.Zero, lpCurrentDirectory: null);
        }
        catch (Exception ex) when (ex is DebugException or NetFx.ClrException or DllNotFoundException)
        {
            Send(writer, $"error {args[1]} couldn't be started: {ex.Message}");
            return 127;
        }

        var exit = new ChildExit(launch.ProcessId);
        var program = Process.GetProcessById(launch.ProcessId);
        Send(writer, $"pid {launch.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        if (Read(reader) != "resume")
        {
            // The adapter went away before the program ran any of its code.
            End(program);
            DbgShimLibrary.Shim.TryCloseResumeHandle(launch.ResumeHandle);
            exit.Wait();
            return 1;
        }

        DbgShimLibrary.Shim.ResumeProcess(launch.ResumeHandle);
        DbgShimLibrary.Shim.CloseResumeHandle(launch.ResumeHandle);

        var ended = 0;
        var watch = new Thread(() =>
        {
            var detached = false;
            while (Read(reader) is { } message)
            {
                detached |= message == "detach";
            }

            if (!detached && Volatile.Read(ref ended) == 0)
            {
                End(program);
            }
        })
        { IsBackground = true, Name = "launcher pipe" };
        watch.Start();

        var code = exit.Wait();
        Volatile.Write(ref ended, 1);
        Send(writer, $"exit {(code ?? -1).ToString(CultureInfo.InvariantCulture)}");
        return code ?? 1;
    }

    private static void End(Process program)
    {
        try
        {
            ProcessTree.Kill(program);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string? Read(StreamReader reader)
    {
        try
        {
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Send(StreamWriter writer, string message)
    {
        try
        {
            writer.WriteLine(message);
        }
        catch (IOException)
        {
            // The adapter went away; the program's end is still this process's exit code.
        }
    }
}
