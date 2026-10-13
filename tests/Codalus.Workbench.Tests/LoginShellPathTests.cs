using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Finding dotnet for an app started from the macOS Finder or a Linux desktop, whose PATH lacks the user's folders
/// (the Mac's "Path to dotnet executable is not set" when opening a project).
/// </summary>
public sealed class LoginShellPathTests
{
    [Fact]
    public void The_login_shells_folders_come_first_and_none_twice()
    {
        Assert.Equal("/usr/local/share/dotnet:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin",
            LoginShellPath.Merge("/usr/bin:/bin:/usr/sbin:/sbin", "/usr/local/share/dotnet:/opt/homebrew/bin:/usr/bin:/bin"));
        Assert.Equal("/a", LoginShellPath.Merge(null, "/a::"));
    }

    [Fact]
    public void Only_an_app_started_outside_a_terminal_asks_its_login_shell()
    {
        var expected = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
        Assert.Equal(expected, LoginShellPath.ShouldImport(_ => null));
        Assert.False(LoginShellPath.ShouldImport(name => name == "TERM" ? "xterm-256color" : null));
        Assert.False(LoginShellPath.ShouldImport(name => name == "CODALUS_NO_SHELL_PATH" ? "1" : null));
    }

    [Fact]
    public void The_login_shells_path_is_read()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Assert.Skip("A Unix shell is needed.");
        }

        var path = LoginShellPath.Read("/bin/sh", TimeSpan.FromSeconds(20));
        Assert.NotNull(path);
        Assert.Contains("/bin", path.Split(':'), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(PlatformKind.MacOS, "/usr/local/share/dotnet/dotnet")]
    [InlineData(PlatformKind.Linux, "/usr/lib64/dotnet/dotnet")]
    [InlineData(PlatformKind.Linux, "/snap/dotnet-sdk/current/dotnet")]
    public void Dotnet_is_found_where_the_installers_put_it_when_the_path_lacks_it(PlatformKind platform, string installed)
    {
        // The Finder's PATH: no dotnet on it, and no DOTNET_ROOT.
        var check = new EnvironmentCheck(platform, name => name switch { "PATH" => "/usr/bin:/bin:/usr/sbin:/sbin", "HOME" => "/home/ada", _ => null }, exists: path => path.Replace('\\', '/') == installed);
        Assert.Equal(installed, check.FindDotnet()!.Replace('\\', '/'));
    }
}
