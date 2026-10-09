using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DaedalusStudio.Platform.Pty;

/// <summary>
/// <c>openpty</c> plus <c>posix_spawn</c> on Linux and macOS (ADR 0014). The child is set up entirely by libc: a new
/// session (<c>POSIX_SPAWN_SETSID</c>), the pty opened by name as its controlling terminal on fds 0–2, the working
/// directory, default signal dispositions and an empty signal mask. No managed code runs between fork and exec, which
/// <c>forkpty</c> from .NET can't promise (the child would JIT and bind P/Invoke stubs in a copy of a multi-threaded
/// runtime, and can deadlock).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed partial class UnixPseudoTerminal : PseudoTerminal
{
    private const int SigHup = 1;
    private const int SigKill = 9;
    private const int ORdWr = 2;
    private const int EIntr = 4;

    // spawn.h: the same on glibc and macOS except SETSID; macOS also has CLOEXEC_DEFAULT (close every other fd).
    private const int SpawnSetSigDefault = 0x04;
    private const int SpawnSetSigMask = 0x08;
    private static readonly int SpawnSetSid = OperatingSystem.IsMacOS() ? 0x0400 : 0x80;
    private static readonly int SpawnCloseOnExecDefault = OperatingSystem.IsMacOS() ? 0x4000 : 0;

    // posix_spawnattr_t and posix_spawn_file_actions_t are 336 and 80 bytes on glibc and a pointer on macOS;
    // sigset_t is 128 bytes on glibc and 4 on macOS. These buffers fit all of them.
    private const int OpaqueSize = 1024;
    private const int SigSetSize = 256;

    private readonly int _master;
    private readonly Thread _reader;
    private int _closed;

    private UnixPseudoTerminal(int master, int pid)
    {
        _master = master;
        ProcessId = pid;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty reader" };
        _reader.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty wait" }.Start();
    }

    public static unsafe UnixPseudoTerminal Create(PtyStartInfo info)
    {
        var file = ResolveExecutable(info.FileName, info.Environment) ?? throw new FileNotFoundException($"'{info.FileName}' was not found.", info.FileName);
        var size = new WinSize { Rows = (ushort)Math.Max(1, info.Rows), Columns = (ushort)Math.Max(1, info.Columns) };
        if (OpenPty(out var master, out var slave, &size) != 0)
        {
            throw new IOException($"openpty failed: errno {Marshal.GetLastPInvokeError()}.");
        }

        // openpty's descriptors aren't close-on-exec; keep them out of programs other threads start meanwhile.
        var closeOnExec = OperatingSystem.IsMacOS() ? 0x20006601UL : 0x5451UL; // FIOCLEX
        _ = ioctl(master, closeOnExec, null);
        _ = ioctl(slave, closeOnExec, null);

        var allocations = new List<nint>();
        nint Native(int bytes)
        {
            var pointer = Marshal.AllocHGlobal(bytes);
            new Span<byte>((void*)pointer, bytes).Clear();
            allocations.Add(pointer);
            return pointer;
        }

        nint Utf8(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var pointer = Native(bytes.Length + 1);
            bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length));
            return pointer;
        }

        nint Array(IReadOnlyList<string> values)
        {
            var array = Native(nint.Size * (values.Count + 1));
            for (var i = 0; i < values.Count; i++)
            {
                Marshal.WriteIntPtr(array, i * nint.Size, Utf8(values[i]));
            }

            return array;
        }

        var attributes = Native(OpaqueSize);
        var actions = Native(OpaqueSize);
        var attributesReady = false;
        var actionsReady = false;
        try
        {
            var name = Native(1024);
            Check(ttyname_r(slave, name, 1024), "ttyname_r");

            Check(posix_spawnattr_init(attributes), "posix_spawnattr_init");
            attributesReady = true;
            var allSignals = Native(SigSetSize);
            var noSignals = Native(SigSetSize);
            _ = sigfillset(allSignals);
            _ = sigemptyset(noSignals);
            Check(posix_spawnattr_setsigdefault(attributes, allSignals), "posix_spawnattr_setsigdefault");
            Check(posix_spawnattr_setsigmask(attributes, noSignals), "posix_spawnattr_setsigmask");
            Check(posix_spawnattr_setflags(attributes, (short)(SpawnSetSid | SpawnSetSigDefault | SpawnSetSigMask | SpawnCloseOnExecDefault)), "posix_spawnattr_setflags");

            // Opening the pty by name in the new session makes it the controlling terminal (no O_NOCTTY).
            Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            actionsReady = true;
            Check(posix_spawn_file_actions_addopen(actions, 0, name, ORdWr, 0), "posix_spawn_file_actions_addopen");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "posix_spawn_file_actions_adddup2");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "posix_spawn_file_actions_adddup2");
            if (!OperatingSystem.IsMacOS())
            {
                // macOS closes them through CLOEXEC_DEFAULT; on Linux, close the parent's ends explicitly.
                Check(posix_spawn_file_actions_addclose(actions, master), "posix_spawn_file_actions_addclose");
                if (slave > 2)
                {
                    Check(posix_spawn_file_actions_addclose(actions, slave), "posix_spawn_file_actions_addclose");
                }
            }

            if (Directory.Exists(info.WorkingDirectory))
            {
                Check(posix_spawn_file_actions_addchdir_np(actions, Utf8(info.WorkingDirectory)), "posix_spawn_file_actions_addchdir_np");
            }

            var argv = Array([file, .. info.Arguments]);
            var envp = Array([.. info.Environment.Select(e => $"{e.Key}={e.Value}")]);
            int pid;
            Check(posix_spawn(&pid, Utf8(file), actions, attributes, argv, envp), "posix_spawn");
            _ = close(slave);
            return new UnixPseudoTerminal(master, pid);
        }
        catch
        {
            _ = close(slave);
            _ = close(master);
            throw;
        }
        finally
        {
            if (actionsReady)
            {
                _ = posix_spawn_file_actions_destroy(actions);
            }

            if (attributesReady)
            {
                _ = posix_spawnattr_destroy(attributes);
            }

            foreach (var pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    /// <summary>The posix_spawn family returns an error number instead of setting errno.</summary>
    private static void Check(int result, string function)
    {
        if (result != 0)
        {
            throw new IOException($"{function} failed: errno {(result > 0 ? result : Marshal.GetLastPInvokeError())}.");
        }
    }

    private static unsafe int OpenPty(out int master, out int slave, WinSize* size)
    {
        // glibc 2.34+ and macOS have openpty in libc; older glibc has it in libutil.
        try
        {
            return openpty_libc(out master, out slave, 0, 0, size);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return openpty_libutil(out master, out slave, 0, 0, size);
        }
    }

    private unsafe void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        fixed (byte* pointer = buffer)
        {
            while (true)
            {
                var read = read_(_master, pointer, buffer.Length);
                if (read > 0)
                {
                    RaiseOutput(buffer.AsMemory(0, (int)read).ToArray());
                }
                else if (read == 0 || Marshal.GetLastPInvokeError() != EIntr)
                {
                    break; // EIO on Linux once the child side is closed
                }
            }
        }
    }

    private void WaitLoop()
    {
        int status;
        int result;
        do
        {
            result = waitpid(ProcessId, out status, 0);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == EIntr);

        _reader.Join(TimeSpan.FromSeconds(5));
        var code = (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
        RaiseExited(result < 0 ? -1 : code);
        Close();
    }

    public override unsafe void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            var offset = 0;
            while (offset < data.Length && Volatile.Read(ref _closed) == 0)
            {
                var written = write_(_master, pointer + offset, data.Length - offset);
                if (written < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EIntr)
                    {
                        continue;
                    }

                    return;
                }

                offset += (int)written;
            }
        }
    }

    public override unsafe void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        var size = new WinSize { Rows = (ushort)Math.Max(1, rows), Columns = (ushort)Math.Max(1, columns) };
        if (OperatingSystem.IsMacOS())
        {
            // ioctl is variadic; Apple's arm64 ABI passes variadic arguments on the stack, so the pointer goes after
            // six padding arguments that fill the remaining registers (harmless on x64, where it is in a register).
            const ulong TiocSWinSz = 0x80087467;
            _ = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? ioctl_variadic(_master, TiocSWinSz, 0, 0, 0, 0, 0, 0, &size)
                : ioctl(_master, TiocSWinSz, &size);
        }
        else
        {
            _ = ioctl(_master, 0x5414, &size); // TIOCSWINSZ
        }
    }

    public override void Kill()
    {
        // The program leads its own session and process group: signal the whole group.
        _ = kill(-ProcessId, SigHup);
        _ = kill(ProcessId, SigKill);
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _ = close(_master);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (ExitCode is null)
        {
            Kill();
        }

        // The wait thread closes the descriptor once the child is reaped (closing it earlier could hand the number
        // to another file while the reader still uses it).
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixel;
        public ushort YPixel;
    }

    [LibraryImport("libc", EntryPoint = "openpty", SetLastError = true)]
    private static unsafe partial int openpty_libc(out int master, out int slave, nint name, nint termios, WinSize* size);

    [LibraryImport("libutil.so.1", EntryPoint = "openpty", SetLastError = true)]
    private static unsafe partial int openpty_libutil(out int master, out int slave, nint name, nint termios, WinSize* size);

    [LibraryImport("libc")]
    private static partial int ttyname_r(int fd, nint buffer, nint size);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn(int* pid, nint path, nint fileActions, nint attributes, nint argv, nint envp);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_init(nint attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_destroy(nint attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setflags(nint attributes, short flags);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigdefault(nint attributes, nint signals);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigmask(nint attributes, nint signals);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(nint actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(nint actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_addopen(nint actions, int fd, nint path, int flags, int mode);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_adddup2(nint actions, int fd, int newFd);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_addclose(nint actions, int fd);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_addchdir_np(nint actions, nint path);

    [LibraryImport("libc")]
    private static partial int sigfillset(nint set);

    [LibraryImport("libc")]
    private static partial int sigemptyset(nint set);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint read_(int fd, byte* buffer, nint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint write_(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int ioctl(int fd, ulong request, WinSize* size);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe partial int ioctl_variadic(int fd, ulong request, nint pad1, nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, WinSize* size);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
