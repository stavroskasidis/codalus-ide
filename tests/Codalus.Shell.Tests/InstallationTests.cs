using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>The Windows installer's `codalus` command (M31): its PATH entry and the script behind it.</summary>
public sealed class InstallationTests
{
    private const string Bin = @"C:\Users\me\AppData\Local\Codalus\bin";

    [Fact]
    public void Installing_adds_the_folder_once_and_keeps_the_other_entries_as_they_are()
    {
        const string path = @"%USERPROFILE%\.dotnet\tools;C:\Tools";

        var added = Installation.WithEntry(path, Bin);

        Assert.Equal(@"%USERPROFILE%\.dotnet\tools;C:\Tools;" + Bin, added);
        Assert.Equal(added, Installation.WithEntry(added, Bin + @"\")); // an update doesn't add it again
        Assert.Equal(Bin, Installation.WithEntry(string.Empty, Bin));
    }

    [Fact]
    public void Uninstalling_removes_only_that_folder_in_any_case_or_form()
    {
        var path = @"C:\Tools;" + Bin.ToUpperInvariant() + @"\;%USERPROFILE%\.dotnet\tools";

        Assert.Equal(@"C:\Tools;%USERPROFILE%\.dotnet\tools", Installation.WithoutEntry(path, Bin));
        Assert.Equal(@"C:\Tools", Installation.WithoutEntry(@"C:\Tools", Bin));
    }

    [Fact]
    public void The_command_runs_the_installed_app_waits_for_it_and_passes_its_exit_code_on()
    {
        Assert.Equal("@echo off\r\n\"%~dp0..\\current\\codalus.exe\" %*\r\nexit /b %ERRORLEVEL%\r\n", Installation.CommandScript);
    }

    // ------------------------------------------------------------------ the working folder

    private const string Home = @"C:\Users\me";
    private static readonly string[] s_app = [@"C:\Users\me\AppData\Local\Codalus", @"C:\Users\me\AppData\Local\Codalus\current\"];

    [Fact]
    public void Started_in_its_install_folder_the_app_moves_to_the_users_home()
    {
        // Velopack starts it in current\, and adb's server, started from there, kept the folder the update replaces.
        Assert.Equal(Home, Installation.WorkingFolderOutside(@"C:\Users\me\AppData\Local\Codalus\current", s_app, Home, ignoreCase: true));
        Assert.Equal(Home, Installation.WorkingFolderOutside(@"c:\users\me\appdata\local\codalus\current\plugins\", s_app, Home, ignoreCase: true));
        Assert.Equal(Home, Installation.WorkingFolderOutside(@"C:\Users\me\AppData\Local\Codalus", s_app, Home, ignoreCase: true));
        Assert.Equal("/home/me", Installation.WorkingFolderOutside("/opt/codalus/plugins", ["/opt/codalus/"], "/home/me", ignoreCase: false));
    }

    [Fact]
    public void Started_anywhere_else_the_app_keeps_its_working_folder()
    {
        Assert.Null(Installation.WorkingFolderOutside(@"D:\Projects\app", s_app, Home, ignoreCase: true)); // `codalus .` in a terminal
        Assert.Null(Installation.WorkingFolderOutside(@"C:\Users\me\AppData\Local\Codalus-dev", s_app, Home, ignoreCase: true));
        Assert.Null(Installation.WorkingFolderOutside(Home, s_app, Home, ignoreCase: true));
        Assert.Null(Installation.WorkingFolderOutside("/opt/Codalus", ["/opt/codalus"], "/home/me", ignoreCase: false));
        Assert.Null(Installation.WorkingFolderOutside(@"C:\Users\me\AppData\Local\Codalus\current", s_app, string.Empty, ignoreCase: true)); // no home
        Assert.Null(Installation.WorkingFolderOutside(@"C:\apps\codalus\bin", [@"C:\apps\codalus"], @"C:\apps\codalus\home", ignoreCase: true));
    }

    // ------------------------------------------------------------------ macOS: updates staged on the bundle's volume

    private const string HomeCache = "/Users/me/Library/Caches/velopack/Codalus/packages";
    private const string ExternalHomeCache = "/Volumes/M2/Users/me/Library/Caches/velopack/Codalus/packages";
    private const string UserCache = "/var/folders/x/C/velopack/Codalus/packages";
    private const string ExternalTemp = "/Volumes/M2/tmp/velopack/Codalus/packages";

    private static readonly Dictionary<string, long> s_devices = new(StringComparer.Ordinal)
    {
        ["/Applications"] = 1,
        [HomeCache] = 1,
        [UserCache] = 1,
        ["/Volumes/M2/Applications"] = 2,
        ["/Volumes/M2/Applications/.Codalus.velopack/packages"] = 2,
        [ExternalHomeCache] = 2,
        [ExternalTemp] = 2,
        ["/Volumes/Image/Library/Caches/velopack/Codalus/packages"] = 3,
    };

    private static long? Device(string path) => s_devices.TryGetValue(path.Replace('\\', '/'), out var device) ? device : null;

    private static string? Staging(string bundle, string packages, string[] candidates, Func<string, bool>? create = null) =>
        Installation.SameVolumeFolder(bundle, packages, candidates, Device, create ?? (_ => true));

    [Fact]
    public void A_home_on_the_bundles_volume_keeps_velopacks_packages_folder()
    {
        Assert.Equal(HomeCache, Staging("/Applications/Codalus IDE.app", HomeCache, [UserCache]));
        Assert.Equal(ExternalHomeCache, Staging("/Unknown/Codalus IDE.app", ExternalHomeCache, [UserCache])); // a volume we can't tell
    }

    [Fact]
    public void A_home_on_another_disk_stages_updates_in_the_first_folder_on_the_bundles_volume()
    {
        // The Mac's home on an external disk: UpdateMac's rename onto /Applications failed with "Cross-device link".
        Assert.Equal(UserCache, Staging("/Applications/Codalus IDE.app/", ExternalHomeCache, [ExternalTemp, UserCache]));
        Assert.Null(Staging("/Applications/Codalus IDE.app", ExternalHomeCache, [ExternalTemp]));
    }

    [Fact]
    public void An_app_on_another_volume_than_the_system_stages_next_to_itself_when_it_can()
    {
        // ~/Applications linked to an external disk, and a home elsewhere again (eng/test-update.sh's disk image).
        const string beside = "/Volumes/M2/Applications/.Codalus.velopack/packages";
        string[] candidates = [UserCache, beside];
        Assert.Equal(beside, Staging("/Volumes/M2/Applications/Codalus IDE.app", "/Volumes/Image/Library/Caches/velopack/Codalus/packages", candidates));
        Assert.Null(Staging("/Volumes/M2/Applications/Codalus IDE.app", "/Volumes/Image/Library/Caches/velopack/Codalus/packages", candidates, create: _ => false));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void A_folder_that_doesnt_exist_yet_is_on_its_nearest_existing_folders_volume()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");
        var temp = Path.GetTempPath();

        Assert.NotNull(MacUpdateLocator.Device(temp));
        Assert.Equal(MacUpdateLocator.Device(temp), MacUpdateLocator.Device(Path.Combine(temp, "velopack", Guid.NewGuid().ToString("N"), "packages")));
        Assert.Equal(MacUpdateLocator.Device("/Applications"), MacUpdateLocator.Device(temp)); // both on the system's data volume
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void A_bundle_the_user_cant_write_needs_an_administrator_to_be_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");
        Assert.SkipWhen(Environment.UserName == "root", "root writes everything");
        var folder = Directory.CreateTempSubdirectory("codalus-bundle-").FullName;
        var bundle = Path.Combine(folder, "Codalus IDE.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
        try
        {
            Assert.False(MacUpdateLocator.NeedsAdministrator(bundle));

            File.SetUnixFileMode(bundle, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Assert.True(MacUpdateLocator.NeedsAdministrator(bundle)); // a root-owned bundle from the .pkg: 755, not ours
            Assert.True(MacUpdateLocator.NeedsAdministrator(bundle + "/"));

            File.SetUnixFileMode(bundle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.True(MacUpdateLocator.NeedsAdministrator(bundle)); // its folder isn't writable either
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(bundle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(folder, recursive: true);
        }
    }
}
