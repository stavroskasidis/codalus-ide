using Codalus.Debugging.Host.CoreClr;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The CoreCLR library a process loaded, found without dbgshim's search (M58b's follow-up, ADR 0094): from Linux's maps
/// and from macOS's <c>lsof</c>, on this test process itself, which runs on one; and next to a program's runtimes.
/// </summary>
public sealed class RuntimeLocatorTests
{
    [Fact]
    public void This_process_has_its_own_runtimes_library()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The held start is for Linux and macOS (dbgshim's own search is used on Windows).");
        var name = OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so";
        var expected = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), name);

        // Listed, not guessed: no program is given to look next to. On macOS a failure says what each way of listing
        // found (the regions, as dbgshim walks them, and lsof): neither had run on a Mac when this was written.
        var found = RuntimeLocator.Find(Environment.ProcessId);

        var ways = OperatingSystem.IsMacOS()
            ? $" The regions: {RuntimeLocator.FromRegions(Environment.ProcessId, name) ?? "none"}; lsof: {RuntimeLocator.FromLsof(RuntimeLocator.Lsof(Environment.ProcessId), name) ?? "none"}."
            : string.Empty;
        Assert.True(found.Count == 1, $"Found {found.Count} libraries for this process ({string.Join(", ", found)}), expected {expected}.{ways}");
        Assert.True(
            Codalus.Platform.RealPath.Resolve(expected) == Codalus.Platform.RealPath.Resolve(found[0]),
            $"Found {found[0]}, expected {expected}.{ways}");
    }

    [Fact]
    public void A_process_that_is_gone_has_none()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The held start is for Linux and macOS.");
        Assert.Empty(RuntimeLocator.Find(int.MaxValue - 7));
    }

    [Fact]
    public void Maps_and_lsof_lines_are_read_for_the_library_by_its_file_name()
    {
        string[] maps =
        [
            "55d0c0a00000-55d0c0a01000 r--p 00000000 fd:01 1048 /usr/share/dotnet/dotnet",
            "7f1c2c000000-7f1c2c021000 rw-p 00000000 00:00 0 ",
            "7f1c30a00000-7f1c30b9d000 r--p 00000000 fd:01 2211 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.13/libcoreclrtraceptprovider.so",
            "7f1c31000000-7f1c3125a000 r--p 00000000 fd:01 2207 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.13/libcoreclr.so",
            "7f1c3125a000-7f1c316f1000 r-xp 0025a000 fd:01 2207 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.13/libcoreclr.so",
            "7ffd5a1f0000-7ffd5a211000 rw-p 00000000 00:00 0 [stack]",
        ];
        Assert.Equal("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.13/libcoreclr.so", RuntimeLocator.FromMaps(maps, "libcoreclr.so"));
        Assert.Null(RuntimeLocator.FromMaps(maps[..3], "libcoreclr.so"));

        // A folder with a space in its name: the path is the rest of the line.
        Assert.Equal("/opt/my runtimes/libcoreclr.so", RuntimeLocator.FromMaps(["7f00-7f01 r--p 00000000 fd:01 9 /opt/my runtimes/libcoreclr.so"], "libcoreclr.so"));

        string[] lsof =
        [
            "p4242",
            "ftxt",
            "n/usr/local/share/dotnet/x64/dotnet",
            "ftxt",
            "n/usr/local/share/dotnet/x64/shared/Microsoft.NETCore.App/5.0.17/libhostpolicy.dylib",
            "ftxt",
            "n/usr/local/share/dotnet/x64/shared/Microsoft.NETCore.App/5.0.17/libcoreclr.dylib",
            "ftxt",
            "n/usr/lib/dyld",
        ];
        Assert.Equal("/usr/local/share/dotnet/x64/shared/Microsoft.NETCore.App/5.0.17/libcoreclr.dylib", RuntimeLocator.FromLsof(lsof, "libcoreclr.dylib"));
        Assert.Null(RuntimeLocator.FromLsof(lsof[..5], "libcoreclr.dylib"));
    }

    [Fact]
    public void Next_to_a_program_are_its_own_library_and_its_dotnets_runtimes_newest_first()
    {
        var root = Directory.CreateTempSubdirectory("codalus-runtime-locator-").FullName;
        try
        {
            foreach (var version in new[] { "6.0.36", "10.0.13", "8.0.31", "11.0.0-rc.1.2" })
            {
                Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", version));
                File.WriteAllText(Path.Combine(root, "shared", "Microsoft.NETCore.App", version, "libcoreclr.dylib"), string.Empty);
            }

            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "9.0.1")); // a folder without the library
            File.WriteAllText(Path.Combine(root, "dotnet"), string.Empty);

            var found = RuntimeLocator.NextTo(Path.Combine(root, "dotnet"), "libcoreclr.dylib");

            Assert.Equal(["11.0.0-rc.1.2", "10.0.13", "8.0.31", "6.0.36"], found.Select(p => Path.GetFileName(Path.GetDirectoryName(p))));

            // A self-contained program: the library beside it, first.
            File.WriteAllText(Path.Combine(root, "libcoreclr.dylib"), string.Empty);
            Assert.Equal(Path.Combine(root, "libcoreclr.dylib"), RuntimeLocator.NextTo(Path.Combine(root, "dotnet"), "libcoreclr.dylib")[0]);
            Assert.Empty(RuntimeLocator.NextTo(null, "libcoreclr.dylib"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
