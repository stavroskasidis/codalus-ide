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

    /// <summary>
    /// The Mac's case: the login PATH puts Homebrew's dotnet first, a link to a wrapper script whose folder has no SDKs,
    /// so the build host's MSBuildLocator can't use it. The root with the newest SDK is chosen instead.
    /// </summary>
    [Fact]
    public void A_dotnet_without_sdks_on_the_path_is_passed_over_for_the_root_with_the_newest_sdk()
    {
        static string N(string path) => path.Replace('\\', '/');
        var files = new HashSet<string> { "/opt/homebrew/bin/dotnet", "/usr/local/share/dotnet/dotnet", "/Users/ada/.dotnet/dotnet" };
        var sdks = new Dictionary<string, string[]>
        {
            ["/opt/homebrew/Cellar/dotnet/10.0.300/bin"] = [],
            ["/usr/local/share/dotnet"] = ["10.0.301", "9.0.305", "8.0.414"],
            ["/Users/ada/.dotnet"] = ["10.0.401"],
        };
        var check = new EnvironmentCheck(PlatformKind.MacOS, name => name switch { "PATH" => "/Users/ada/.local/bin:/opt/homebrew/bin:/usr/bin:/bin", "HOME" => "/Users/ada", _ => null },
            exists: path => files.Contains(N(path)))
        {
            RealPath = path => N(path) == "/opt/homebrew/bin/dotnet" ? "/opt/homebrew/Cellar/dotnet/10.0.300/bin/dotnet" : path,
            SdkVersions = folder => sdks.GetValueOrDefault(N(folder).TrimEnd('/')) ?? [],
        };

        Assert.Equal("/Users/ada/.dotnet", N(check.FindSdkRoot()!));

        // A real dotnet with SDKs on the PATH is used as it is.
        sdks["/opt/homebrew/Cellar/dotnet/10.0.300/bin"] = ["10.0.300"];
        Assert.Equal("/opt/homebrew/Cellar/dotnet/10.0.300/bin", N(check.FindSdkRoot()!));
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
