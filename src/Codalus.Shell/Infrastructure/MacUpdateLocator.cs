using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Velopack.Locators;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Velopack's macOS locator with its packages folder on the app bundle's volume. Velopack's updater (UpdateMac)
/// extracts the new bundle under the packages folder (<c>~/Library/Caches/velopack/&lt;id&gt;/packages</c>) and swaps
/// it in with <c>rename</c>, which can't cross volumes: with the home folder on another disk than
/// <c>/Applications</c>, every update failed with "Cross-device link" and the old version started again, offering the
/// same update. The user's cache folder under <c>/var/folders</c> (or the temporary folder) is on the system's data
/// volume, as <c>/Applications</c> is.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacUpdateLocator() : OsxVelopackLocator(null, null)
{
    private const int DarwinUserCacheDir = 65538; // _CS_DARWIN_USER_CACHE_DIR

    private string? _packages;

    public override string? PackagesDir => _packages ??= Packages();

    private string? Packages()
    {
        var velopack = base.PackagesDir;
        if (RootAppDir is not { } bundle || velopack is null || AppId is not { } id)
        {
            return velopack;
        }

        var candidates = new[] { UserCacheFolder(), Path.GetTempPath() }
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => Path.Combine(c!, "velopack", id, "packages"));
        if (Installation.SameVolumeFolder(bundle, velopack, candidates, Device) is not { } packages)
        {
            return velopack;
        }

        try
        {
            Directory.CreateDirectory(packages);
            Log.Log(Velopack.Logging.VelopackLogLevel.Information, $"{velopack} is on another volume than {bundle}: packages go to {packages}.", null);
            return packages;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return velopack;
        }
    }

    /// <summary>The device of <paramref name="path"/>, or of its nearest folder that exists.</summary>
    internal static long? Device(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                var buffer = new byte[512]; // struct stat is 144 bytes; st_dev (an int32) comes first
                var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? StatInode64(current, buffer) : Stat(current, buffer);
                return result == 0 ? BitConverter.ToInt32(buffer, 0) : null;
            }
        }

        return null;
    }

    /// <summary><c>getconf DARWIN_USER_CACHE_DIR</c>: the user's cache folder under <c>/var/folders</c>.</summary>
    private static string? UserCacheFolder()
    {
        var buffer = new byte[1024];
        var length = (int)Confstr(DarwinUserCacheDir, buffer, (nuint)buffer.Length);
        return length is > 1 and <= 1024 ? Encoding.UTF8.GetString(buffer, 0, length - 1) : null;
    }

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(LibSystem, EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, [Out] byte[] buffer);

    [LibraryImport(LibSystem, EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatInode64(string path, [Out] byte[] buffer);

    [LibraryImport(LibSystem, EntryPoint = "confstr")]
    private static partial nuint Confstr(int name, [Out] byte[] buffer, nuint length);
}
