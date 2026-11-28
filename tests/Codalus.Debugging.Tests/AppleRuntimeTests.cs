using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Codalus.Debugging.Host.CoreClr;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>
/// ADR 0114: what attaching to a CoreCLR app in an iOS simulator needs beyond a desktop attach. The platform a Mach-O
/// library was built for, read from its header; a process's temporary folder, read from its environment; and the
/// copies of the runtime's debugging libraries that macOS loads, made once with Xcode's tools (stand-ins here: the
/// real ones need a simulator's runtime, and are checked by hand on a Mac).
/// </summary>
public sealed class AppleRuntimeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-apple-runtime-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A 64-bit Mach-O header with one other load command, then LC_BUILD_VERSION for <paramref name="platform"/>.</summary>
    private static byte[] MachO(int platform)
    {
        var file = new byte[32 + 24 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 2); // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), 0x1b); // LC_UUID
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56), 0x32); // LC_BUILD_VERSION
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(64), (uint)platform);
        return file;
    }

    [Fact]
    public void A_librarys_platform_is_read_from_its_build_version()
    {
        Assert.Equal(7, AppleRuntime.Platform(MachO(7), Architecture.Arm64)); // the iOS simulator
        Assert.Equal(AppleRuntime.MacOS, AppleRuntime.Platform(MachO(1), Architecture.Arm64));
        Assert.Null(AppleRuntime.Platform("not a Mach-O file, but long enough to be one"u8, Architecture.Arm64));
        Assert.Null(AppleRuntime.Platform([0xcf, 0xfa], Architecture.Arm64));

        // A universal file: the slice of this architecture, at its offset.
        var fat = new byte[8 + 40];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xcafebabe);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8), 0x01000007); // x86_64
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(28), 0x0100000c); // arm64
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(36), 8192);
        Assert.Equal(6, AppleRuntime.Platform(fat, Architecture.Arm64, offset => MachO(offset == 8192 ? 6 : 1)));
        Assert.Equal(1, AppleRuntime.Platform(fat, Architecture.X64, offset => MachO(offset == 8192 ? 6 : 1)));
    }

    [Fact]
    public void A_variable_is_read_from_a_processs_arguments_and_environment()
    {
        // kern.procargs2: the argument count, the executable, padding, the arguments, the environment.
        var block = new List<byte>();
        block.AddRange(BitConverter.GetBytes(2));
        block.AddRange(Encoding.UTF8.GetBytes("/Apps/App.app/App\0\0\0\0App\0TMPDIR=/not/this/one\0HOME=/Users/u\0TMPDIR=/Containers/Data/tmp\0CFFIXED=1\0\0"));
        var bytes = block.ToArray();
        Assert.Equal("/Containers/Data/tmp", AppleRuntime.EnvironmentVariable(bytes, "TMPDIR"));
        Assert.Equal("/Users/u", AppleRuntime.EnvironmentVariable(bytes, "HOME"));
        Assert.Null(AppleRuntime.EnvironmentVariable(bytes, "TMP"));
        Assert.Null(AppleRuntime.EnvironmentVariable([1, 0], "TMPDIR"));
    }

    [Fact]
    public void This_processs_temporary_folder_is_its_TMPDIR()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "A process's environment is read through macOS's sysctl.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("TMPDIR") is null, "This process has no TMPDIR.");
        Assert.Equal(Environment.GetEnvironmentVariable("TMPDIR"), AppleRuntime.TempFolder(Environment.ProcessId));
        Assert.Null(AppleRuntime.TempFolder(int.MaxValue - 7));
    }

    [Fact]
    public void The_debugging_libraries_are_retargeted_once_and_a_failed_tool_says_which()
    {
        var bundle = Directory.CreateDirectory(Path.Combine(_dir, "App.app")).FullName;
        var runtime = Path.Combine(bundle, "libcoreclr.dylib");
        var missing = Assert.Throws<ClrException>(() => AppleRuntime.DebugLibraries(runtime, (_, _) => (0, string.Empty), Path.Combine(_dir, "cache")));
        Assert.Contains("libmscordbi.dylib", missing.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(bundle, "libmscordbi.dylib"), "dbi");
        File.WriteAllText(Path.Combine(bundle, "libmscordaccore.dylib"), "dac");
        var runs = new List<string>();
        (int, string) Tools(string file, IReadOnlyList<string> arguments)
        {
            runs.Add(Path.GetFileName(file) + " " + string.Join(' ', arguments.Select(a => a.StartsWith(_dir, StringComparison.Ordinal) ? Path.GetFileName(a) : a)));
            if (arguments[0] == "vtool")
            {
                File.Copy(arguments[^1], arguments[^2]);
            }

            return (0, string.Empty);
        }

        var folder = AppleRuntime.DebugLibraries(runtime, Tools, Path.Combine(_dir, "cache"));
        Assert.Equal("dbi", File.ReadAllText(Path.Combine(folder, "libmscordbi.dylib")));
        Assert.Equal("dac", File.ReadAllText(Path.Combine(folder, "libmscordaccore.dylib")));
        Assert.Equal(
            [
                "xcrun vtool -set-build-version macos 12.0 15.0 -replace -output libmscordbi.dylib libmscordbi.dylib",
                "xcrun vtool -set-build-version macos 12.0 15.0 -replace -output libmscordaccore.dylib libmscordaccore.dylib",
                "xcrun install_name_tool -change @rpath/libmscordaccore.dylib @loader_path/libmscordaccore.dylib libmscordbi.dylib",
                "codesign --force --sign - libmscordbi.dylib libmscordaccore.dylib",
            ],
            runs);
        Assert.Equal([Path.GetFileName(folder)], Directory.GetFileSystemEntries(Path.Combine(_dir, "cache")).Select(Path.GetFileName)); // nothing left aside

        // The same libraries again: the copies made before, and no tool.
        Assert.Equal(folder, AppleRuntime.DebugLibraries(runtime, (_, _) => throw new InvalidOperationException("no tool runs"), Path.Combine(_dir, "cache")));

        // Another build of them: made again; a tool that fails says which and why, and leaves nothing behind.
        File.WriteAllText(Path.Combine(bundle, "libmscordbi.dylib"), "a newer dbi");
        var failed = Assert.Throws<ClrException>(() => AppleRuntime.DebugLibraries(runtime, (_, arguments) => arguments[0] == "vtool" ? (1, "vtool: no such platform\n") : (0, string.Empty), Path.Combine(_dir, "cache")));
        Assert.Contains("vtool ended with exit code 1. vtool: no such platform", failed.Message, StringComparison.Ordinal);
        Assert.Single(Directory.GetFileSystemEntries(Path.Combine(_dir, "cache")));

        var absent = Assert.Throws<ClrException>(() => AppleRuntime.DebugLibraries(runtime, (_, _) => throw new System.ComponentModel.Win32Exception(2), Path.Combine(_dir, "cache")));
        Assert.Contains("Xcode's command line tools", absent.Message, StringComparison.Ordinal);
    }
}
