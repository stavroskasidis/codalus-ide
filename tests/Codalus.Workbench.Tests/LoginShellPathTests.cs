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

    [Fact]
    public void The_login_shells_proxies_and_certificates_are_imported_with_its_path()
    {
        // The Mac's case: git from a Finder launch couldn't reach a server behind the proxy that ~/.zshrc exports.
        var login = LoginShellPath.ParseEnvironment(
            "PATH=/opt/homebrew/bin:/usr/bin\nHTTPS_PROXY=http://proxy.corp:8080\nno_proxy=localhost,.corp\n" +
            "SSL_CERT_FILE=/etc/corp.pem\nPROMPT=line one\nline two\nTERM=xterm-256color\nSHLVL=1\nHTTP_PROXY=\n");

        Assert.Equal("line one", login["PROMPT"]);
        Assert.Equal(
            ["PATH=/opt/homebrew/bin:/usr/bin", "HTTPS_PROXY=http://proxy.corp:8080", "no_proxy=localhost,.corp", "SSL_CERT_FILE=/etc/corp.pem"],
            LoginShellPath.Imported(login).Select(v => $"{v.Key}={v.Value}"));
    }

    [Fact]
    public void The_login_shells_environment_is_read()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Assert.Skip("A Unix shell is needed.");
        }

        var environment = LoginShellPath.ReadEnvironment("/bin/sh", TimeSpan.FromSeconds(20));
        Assert.NotNull(environment);
        Assert.Contains("/bin", environment["PATH"].Split(':'), StringComparer.Ordinal);
    }

    /// <summary>
    /// A profile that starts a background job holding the shell's output open (M56): the read ends a second after the shell
    /// did, as for a shell that takes too long, instead of when the job ends. The IDE's start waited for it, and then its
    /// processes did, without a word.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_background_job_holding_the_login_shells_output_doesnt_hold_the_read()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Assert.Skip("A Unix shell is needed.");
        }

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codalus-loginshell-" + Guid.NewGuid().ToString("N"))).FullName;
        var job = Path.Combine(folder, "job.pid");
        var shell = Path.Combine(folder, "shell");
        try
        {
            // Called as `shell -ilc <command>`: runs the command, then leaves a job behind with the output.
            File.WriteAllText(shell, $"#!/bin/sh\n/bin/sh -c \"$2\"\nsleep 60 &\necho $! > '{job}'\nexit 0\n");
            File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var read = Task.Run(() => LoginShellPath.ReadEnvironment(shell, TimeSpan.FromSeconds(20)), TestContext.Current.CancellationToken);

            Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(job) && int.TryParse(File.ReadAllText(job).Trim(), out var pid))
            {
                try
                {
                    using var sleeping = System.Diagnostics.Process.GetProcessById(pid);
                    sleeping.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // It ended already.
                }
            }

            Directory.Delete(folder, recursive: true);
        }
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
            // No root has the hosts' runtime on this described machine (M49), whatever this machine's own folders hold.
            HostsRuntime = "11.0.0-rc.2.26504.105",
            InstalledFrameworks = (_, _) => [],
            DirectoryExists = _ => false,
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
