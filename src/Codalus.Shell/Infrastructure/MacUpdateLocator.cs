using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Velopack.Locators;
using Velopack.Logging;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Velopack's macOS locator with its packages folder on the app bundle's volume. Velopack's updater (UpdateMac)
/// extracts the new bundle under the packages folder (<c>~/Library/Caches/velopack/&lt;id&gt;/packages</c>) and swaps
/// it in with <c>rename</c>, which can't cross volumes: with the home folder on another disk than
/// <c>/Applications</c>, every update failed with "Cross-device link" and the old version started again, offering the
/// same update. The user's cache folder under <c>/var/folders</c> (or the temporary folder) is on the system's data
/// volume, as <c>/Applications</c> is; an app on another volume stages next to itself.
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

        // The user's cache folder and the temporary folder are on the system's data volume, with /Applications; an app
        // on another volume (~/Applications linked to an external disk) stages next to itself, in a hidden folder.
        var candidates = new[] { UserCacheFolder(), Path.GetTempPath() }
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => Path.Combine(c!, "velopack", id, "packages"))
            .Append(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bundle))!, $".{id}.velopack", "packages"));
        var packages = Installation.SameVolumeFolder(bundle, velopack, candidates, Device, Create);
        if (packages is null)
        {
            Log.Log(VelopackLogLevel.Warning, $"{velopack} is on another volume than {bundle}, and no folder on its volume is writable: updates can't be applied.", null);
            return velopack;
        }

        if (packages != velopack)
        {
            Log.Log(VelopackLogLevel.Information, $"{velopack} is on another volume than {bundle}: packages go to {packages}.", null);
        }

        return packages;
    }

    private static bool Create(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether replacing <paramref name="bundle"/> needs an administrator: UpdateMac moves the bundle into its packages
    /// folder, which needs write access to the bundle itself and to its folder. The .pkg gives the app to the user who installs
    /// it (eng/packaging/macos/user-owned-pkg.sh), but an older one left it root's in <c>/Applications</c>: such an install
    /// needs it once; the bundle that replaces it is the user's, and later updates don't.
    /// </summary>
    internal static bool NeedsAdministrator(string bundle)
    {
        bundle = Path.TrimEndingDirectorySeparator(bundle);
        return Access(bundle, WriteAccess) != 0 || Access(Path.GetDirectoryName(bundle) ?? "/", WriteAccess) != 0;
    }

    private const int WriteAccess = 2; // W_OK

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

    [LibraryImport(LibSystem, EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    [LibraryImport(LibSystem, EntryPoint = "confstr")]
    private static partial nuint Confstr(int name, [Out] byte[] buffer, nuint length);
}
