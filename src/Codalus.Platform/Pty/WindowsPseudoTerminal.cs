using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Codalus.Platform.Pty;

/// <summary>ConPTY: <c>CreatePseudoConsole</c> with two pipes and a process started with the pseudo-console attribute.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPseudoTerminal : PseudoTerminal
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;
    private const int StartfUseStdHandles = 0x00000100;

    private readonly nint _console;
    private readonly SafeFileHandle _input;
    private readonly SafeFileHandle _output;
    private readonly nint _process;
    private readonly FileStream _writer;
    private readonly Lock _gate = new();
    private readonly Thread _reader;
    private int _consoleClosed;
    private bool _disposed;

    private WindowsPseudoTerminal(nint console, SafeFileHandle input, SafeFileHandle output, nint process, int processId)
    {
        _console = console;
        _input = input;
        _output = output;
        _process = process;
        ProcessId = processId;
        _writer = new FileStream(input, FileAccess.Write, 1, isAsync: false);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty reader" };
        _reader.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "conpty wait" }.Start();
    }

    public static WindowsPseudoTerminal Create(PtyStartInfo info)
    {
        var file = ResolveExecutable(info.FileName, info.Environment) ?? throw new FileNotFoundException($"'{info.FileName}' was not found.", info.FileName);
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0) || !CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            throw new IOException("CreatePipe failed: " + Marshal.GetLastPInvokeError());
        }

        var size = new Coord { X = (short)info.Columns, Y = (short)info.Rows };
        var result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var console);
        // The pseudo-console has its own copies of these ends.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (result != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new IOException($"CreatePseudoConsole failed: 0x{result:X8}.");
        }

        nint attributes = 0;
        try
        {
            nint size2 = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size2);
            attributes = Marshal.AllocHGlobal(size2);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size2)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, nint.Size, 0, 0))
            {
                throw new IOException("Setting up the pseudo-console attribute failed: " + Marshal.GetLastPInvokeError());
            }

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // Null standard handles: otherwise the child inherits ours (redirected in tests and services) instead of
            // the pseudo-console's.
            startup.StartupInfo.dwFlags = StartfUseStdHandles;
            startup.lpAttributeList = attributes;
            // Programs inherit "ignore Ctrl+C" from the process that starts them (a process started in a new process
            // group, as test runners and some launchers do, has it set). Clear it so Ctrl+C in the terminal interrupts.
            _ = SetConsoleCtrlHandler(0, false);
            var commandLine = BuildCommandLine(file, info.Arguments);
            var environment = BuildEnvironment(info.Environment);
            if (!CreateProcessW(null, commandLine, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environment, info.WorkingDirectory, ref startup, out var process))
            {
                var error = Marshal.GetLastPInvokeError();
                ClosePseudoConsole(console);
                inputWrite.Dispose();
                outputRead.Dispose();
                throw new IOException($"Could not start {file} (error {error}).");
            }

            CloseHandle(process.hThread);
            return new WindowsPseudoTerminal(console, inputWrite, outputRead, process.hProcess, process.dwProcessId);
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    /// <summary>Arguments quoted the way <c>CommandLineToArgvW</c> parses them.</summary>
    internal static string BuildCommandLine(string file, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        Append(builder, file);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            Append(builder, argument);
        }

        return builder.ToString();

        static void Append(StringBuilder builder, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            {
                builder.Append(argument);
                return;
            }

            builder.Append('"');
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                }
                else
                {
                    builder.Append('\\', backslashes);
                }

                backslashes = 0;
                builder.Append(c);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
        }
    }

    private static string BuildEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        return builder.Append('\0').ToString();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        using var stream = new FileStream(_output, FileAccess.Read, 1, isAsync: false);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                RaiseOutput(buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void WaitLoop()
    {
        _ = WaitForSingleObject(_process, Infinite);
        _ = GetExitCodeProcess(_process, out var code);

        // Closing the console ends the output pipe once ConPTY has flushed it; the reader then delivers the rest.
        CloseConsole();
        _reader.Join(TimeSpan.FromSeconds(5));
        RaiseExited((int)code);
    }

    /// <summary>Closes the pseudo-console once (it may block until its output is read, so never under the gate).</summary>
    private void CloseConsole()
    {
        if (Interlocked.Exchange(ref _consoleClosed, 1) == 0)
        {
            ClosePseudoConsole(_console);
        }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            _writer.Write(data);
            _writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    public override void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (!_disposed && Volatile.Read(ref _consoleClosed) == 0)
            {
                _ = ResizePseudoConsole(_console, new Coord { X = (short)Math.Max(1, columns), Y = (short)Math.Max(1, rows) });
            }
        }
    }

    public override void Kill() => TerminateProcess(_process, 1);

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        TerminateProcess(_process, 1);
        // ClosePseudoConsole can block until the output is drained: the reader thread keeps reading meanwhile.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CloseConsole();
            _writer.Dispose();
            _input.Dispose();
            CloseHandle(_process);
        });
    }

    // ------------------------------------------------------------------ native

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, int size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint console, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? applicationName,
        string commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        string environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
