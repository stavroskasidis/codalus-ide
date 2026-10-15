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

    // ------------------------------------------------------------------ macOS: updates staged on the bundle's volume

    private static readonly Dictionary<string, long> s_devices = new(StringComparer.Ordinal)
    {
        ["/Applications"] = 1,
        ["/Users/me/Library/Caches/velopack/Codalus/packages"] = 1,
        ["/Volumes/M2/Users/me/Library/Caches/velopack/Codalus/packages"] = 2,
        ["/var/folders/x/C/velopack/Codalus/packages"] = 1,
        ["/Volumes/M2/tmp/velopack/Codalus/packages"] = 2,
    };

    private static long? Device(string path) => s_devices.TryGetValue(path.Replace('\\', '/'), out var device) ? device : null;

    [Fact]
    public void A_home_on_the_bundles_volume_keeps_velopacks_packages_folder()
    {
        Assert.Null(Installation.SameVolumeFolder("/Applications/Codalus IDE.app", "/Users/me/Library/Caches/velopack/Codalus/packages", ["/var/folders/x/C/velopack/Codalus/packages"], Device));
    }

    [Fact]
    public void A_home_on_another_disk_stages_updates_in_the_first_folder_on_the_bundles_volume()
    {
        // The Mac's home on an external disk: UpdateMac's rename onto /Applications failed with "Cross-device link".
        Assert.Equal(
            "/var/folders/x/C/velopack/Codalus/packages",
            Installation.SameVolumeFolder(
                "/Applications/Codalus IDE.app/",
                "/Volumes/M2/Users/me/Library/Caches/velopack/Codalus/packages",
                ["/Volumes/M2/tmp/velopack/Codalus/packages", "/var/folders/x/C/velopack/Codalus/packages"],
                Device));
        Assert.Null(Installation.SameVolumeFolder("/Applications/Codalus IDE.app", "/Volumes/M2/Users/me/Library/Caches/velopack/Codalus/packages", ["/Volumes/M2/tmp/velopack/Codalus/packages"], Device));
        Assert.Null(Installation.SameVolumeFolder("/Unknown/Codalus IDE.app", "/Volumes/M2/Users/me/Library/Caches/velopack/Codalus/packages", ["/var/folders/x/C/velopack/Codalus/packages"], Device));
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
}
