using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using ClrDebug;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// What attaching to a CoreCLR app in an iOS simulator or a Mac Catalyst app needs on top of a desktop program's
/// attach (ADR 0114). Such an app is a process of this Mac, and its runtime answers the same debugger transport, but:
/// <list type="bullet">
/// <item>its runtime's debugging libraries (<c>libmscordbi</c>, <c>libmscordaccore</c>, in the app's bundle) are built
/// for its platform, which dyld refuses to load into a macOS process: <see cref="DebugLibraries"/> makes copies marked
/// for macOS with Xcode's own tools, and <see cref="DebugLibraryProvider"/> hands them to dbgshim;</item>
/// <item>the transport's pipes are in the app's own temporary folder (its simulator's or sandbox's container), not in
/// this process's: <see cref="TempFolder"/> reads it from the app's environment and <see cref="UseTempFolder"/> makes
/// it this process's before the debugging library loads.</item>
/// </list>
/// </summary>
internal static partial class AppleRuntime
{
    /// <summary>Mach-O's <c>PLATFORM_MACOS</c>.</summary>
    internal const int MacOS = 1;

    private const uint Magic64 = 0xfeedfacf;
    private const uint FatMagic = 0xcafebabe;
    private const uint BuildVersionCommand = 0x32;

    /// <summary>
    /// Whether the runtime at <paramref name="runtimePath"/> is built for another Apple platform than macOS (an iOS
    /// simulator's, Mac Catalyst's), so its debugging libraries don't load here as they are.
    /// </summary>
    public static bool IsForeign(string runtimePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            using var file = File.OpenRead(runtimePath);
            var header = new byte[(int)Math.Min(file.Length, 64 * 1024)];
            file.ReadExactly(header);
            return Platform(header, RuntimeInformation.ProcessArchitecture, offset => ReadAt(file, offset)) is { } platform && platform != MacOS;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] ReadAt(FileStream file, long offset)
    {
        file.Position = offset;
        var slice = new byte[(int)Math.Min(file.Length - offset, 64 * 1024)];
        file.ReadExactly(slice);
        return slice;
    }

    /// <summary>
    /// The platform a Mach-O file was built for (its <c>LC_BUILD_VERSION</c>: 1 macOS, 2 iOS, 6 Mac Catalyst, 7 the iOS
    /// simulator), from the start of the file; of a universal file, the slice of <paramref name="architecture"/>, read
    /// through <paramref name="slice"/>. Null when it isn't a 64-bit Mach-O file or doesn't say.
    /// </summary>
    internal static int? Platform(ReadOnlySpan<byte> start, Architecture architecture, Func<long, byte[]>? slice = null)
    {
        if (start.Length < 32)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(start) == FatMagic)
        {
            // fat_header: magic, nfat_arch; fat_arch: cputype, cpusubtype, offset, size, align, all big-endian.
            var wanted = architecture == Architecture.X64 ? 0x01000007u : 0x0100000cu;
            var count = BinaryPrimitives.ReadUInt32BigEndian(start[4..]);
            for (var i = 0; i < count && 8 + ((i + 1) * 20) <= start.Length; i++)
            {
                var arch = start.Slice(8 + (i * 20), 20);
                if (BinaryPrimitives.ReadUInt32BigEndian(arch) == wanted && slice is not null)
                {
                    return Platform(slice(BinaryPrimitives.ReadUInt32BigEndian(arch[8..])), architecture);
                }
            }

            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(start) != Magic64)
        {
            return null;
        }

        // mach_header_64 is 32 bytes; each load command starts with its type and its size.
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(start[16..]);
        var at = 32;
        for (var i = 0; i < commands && at + 12 <= start.Length; i++)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(start[(at + 4)..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(start[at..]) == BuildVersionCommand)
            {
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(start[(at + 8)..]);
            }

            if (size <= 0)
            {
                return null;
            }

            at += size;
        }

        return null;
    }

    /// <summary>The debugging libraries of a runtime, by the names dbgshim asks for.</summary>
    private static readonly string[] s_libraries = ["libmscordbi.dylib", "libmscordaccore.dylib"];

    /// <summary>
    /// A folder with the debugging libraries next to <paramref name="runtimePath"/> as macOS loads them: copies whose
    /// platform is macOS (<c>vtool</c>), whose reference to each other is by their own folder
    /// (<c>install_name_tool</c>; the originals' is an <c>@rpath</c> nothing sets), signed again (<c>codesign</c>, ad
    /// hoc: a changed library's signature is void, and Apple silicon runs nothing unsigned). Made once per build of
    /// the libraries, under the temporary folder. The desktop runtime's own libraries of the same commit don't do:
    /// they load, and then can't read the app's runtime.
    /// </summary>
    /// <param name="run">Runs a tool (file name, arguments) and returns its exit code and error output; the tests' seam.</param>
    public static string DebugLibraries(string runtimePath, Func<string, IReadOnlyList<string>, (int ExitCode, string Error)>? run = null, string? cache = null)
    {
        run ??= Run;
        var source = Path.GetDirectoryName(runtimePath)!;
        var key = new StringBuilder();
        foreach (var library in s_libraries)
        {
            var file = new FileInfo(Path.Combine(source, library));
            if (!file.Exists)
            {
                throw new ClrException($"The app's runtime has no {library} next to it ({source}), so it can't be debugged: build the app in the Debug configuration.");
            }

            key.Append(CultureInfo.InvariantCulture, $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks};");
        }

        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())), 0, 12);
        var folder = Path.Combine(cache ?? Path.Combine(Path.GetTempPath(), "codalus-debug-libraries"), name);
        if (s_libraries.All(l => File.Exists(Path.Combine(folder, l))))
        {
            return folder;
        }

        // Made aside and moved into place whole: another session making the same ones never loads half a library.
        var making = folder + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            Directory.CreateDirectory(making);
            foreach (var library in s_libraries)
            {
                Tool(run, "/usr/bin/xcrun", ["vtool", "-set-build-version", "macos", "12.0", "15.0", "-replace", "-output", Path.Combine(making, library), Path.Combine(source, library)]);
            }

            Tool(run, "/usr/bin/xcrun", ["install_name_tool", "-change", "@rpath/libmscordaccore.dylib", "@loader_path/libmscordaccore.dylib", Path.Combine(making, s_libraries[0])]);
            Tool(run, "/usr/bin/codesign", ["--force", "--sign", "-", .. s_libraries.Select(l => Path.Combine(making, l))]);
            try
            {
                Directory.Move(making, folder);
            }
            catch (IOException) when (s_libraries.All(l => File.Exists(Path.Combine(folder, l))))
            {
                // Another session was first.
            }

            return folder;
        }
        finally
        {
            try
            {
                if (Directory.Exists(making))
                {
                    Directory.Delete(making, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Tool(Func<string, IReadOnlyList<string>, (int ExitCode, string Error)> run, string fileName, IReadOnlyList<string> arguments)
    {
        (int ExitCode, string Error) result;
        try
        {
            result = run(fileName, arguments);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ClrException($"The debugger couldn't run {Path.GetFileName(fileName)} to prepare the app's debugging libraries ({ex.Message}): it needs Xcode's command line tools.", ex);
        }

        if (result.ExitCode != 0)
        {
            var tool = fileName.EndsWith("xcrun", StringComparison.Ordinal) ? arguments[0] : Path.GetFileName(fileName);
            throw new ClrException($"The debugger couldn't prepare the app's debugging libraries: {tool} ended with exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}. {result.Error.Trim()}");
        }
    }

    /// <summary>Runs one of the user's tools and waits for it: its exit code and what it wrote to standard error.</summary>
    private static (int ExitCode, string Error) Run(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} didn't start.");
        _ = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            Codalus.Platform.ProcessTree.Kill(process);
            return (-1, "It didn't end in a minute.");
        }

        return (process.ExitCode, error.Wait(5000) ? error.Result : string.Empty);
    }

    /// <summary>
    /// The temporary folder of process <paramref name="processId"/> (its <c>TMPDIR</c>), where its runtime made the
    /// debugger transport's pipes; null when its environment can't be read or doesn't set one.
    /// </summary>
    public static unsafe string? TempFolder(int processId)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            // sysctl kern.procargs2: the arguments and environment the process started with, for our own processes.
            const int Kernel = 1, ProcessArguments = 49, Size = 1024 * 1024;
            var name = stackalloc int[] { Kernel, ProcessArguments, processId };
            var buffer = new byte[Size];
            nuint length = Size;
            fixed (byte* data = buffer)
            {
                if (sysctl(name, 3, data, &length, null, 0) != 0)
                {
                    return null;
                }
            }

            return EnvironmentVariable(buffer.AsSpan(0, (int)length), "TMPDIR");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A variable's value in <c>kern.procargs2</c>'s block: the argument count, the executable's path, zero bytes, that
    /// many arguments, then the environment, each a zero-ended string.
    /// </summary>
    internal static string? EnvironmentVariable(ReadOnlySpan<byte> block, string name)
    {
        if (block.Length < 4)
        {
            return null;
        }

        var arguments = BinaryPrimitives.ReadInt32LittleEndian(block);
        var at = 4;
        at += End(block[at..]);
        while (at < block.Length && block[at] == 0)
        {
            at++;
        }

        for (var i = 0; i < arguments && at < block.Length; i++)
        {
            at += End(block[at..]) + 1;
        }

        var prefix = Encoding.UTF8.GetBytes(name + "=");
        while (at < block.Length && block[at] != 0)
        {
            var end = End(block[at..]);
            var entry = block.Slice(at, end);
            if (entry.StartsWith(prefix))
            {
                return Encoding.UTF8.GetString(entry[prefix.Length..]);
            }

            at += end + 1;
        }

        return null;

        static int End(ReadOnlySpan<byte> text) => text.IndexOf((byte)0) is >= 0 and var end ? end : text.Length;
    }

    /// <summary>
    /// Makes <paramref name="folder"/> this process's temporary folder for the libraries loaded from now on: the
    /// debugging library looks for the app's pipes in its own <c>TMPDIR</c>, read when it loads. One session per
    /// adapter process, so nothing else here is moved by it (.NET keeps the environment it started with).
    /// </summary>
    public static void UseTempFolder(string folder)
    {
        try
        {
            _ = setenv("TMPDIR", folder, 1);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [LibraryImport("libSystem.dylib")]
    private static unsafe partial int sysctl(int* name, uint nameLength, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);

    [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);
}

/// <summary>
/// Hands dbgshim a runtime's debugging libraries from <paramref name="folder"/> (<see cref="AppleRuntime.DebugLibraries"/>)
/// instead of the ones next to the runtime.
/// </summary>
[GeneratedComClass]
internal sealed partial class DebugLibraryProvider(string folder) : ICLRDebuggingLibraryProvider3
{
    public HRESULT ProvideWindowsLibrary(string pwszFileName, string pwszRuntimeModule, LIBRARY_PROVIDER_INDEX_TYPE indexType, uint dwTimestamp, int dwSizeOfImage, out IntPtr ppResolvedModulePath)
    {
        ppResolvedModulePath = IntPtr.Zero;
        return HRESULT.E_NOTIMPL;
    }

    public HRESULT ProvideUnixLibrary(string pwszFileName, string pwszRuntimeModule, LIBRARY_PROVIDER_INDEX_TYPE indexType, byte[] pbBuildId, int iBuildIdSize, out IntPtr ppResolvedModulePath)
    {
        var path = Path.Combine(folder, Path.GetFileName(pwszFileName));
        if (!File.Exists(path))
        {
            ppResolvedModulePath = IntPtr.Zero;
            return HRESULT.E_FAIL;
        }

        // dbgshim frees it with CoTaskMemFree.
        ppResolvedModulePath = Marshal.StringToCoTaskMemUni(path);
        return HRESULT.S_OK;
    }
}
