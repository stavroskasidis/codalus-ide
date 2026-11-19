using System.Diagnostics;
using System.Text.Json.Nodes;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Build;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The WSL launch profile's .NET (<see cref="WslDotnet"/>): which <c>dotnet</c> in the distribution runs the app, the SDK
/// to install there when none does, with Linux's choices run through <c>wsl.exe</c>, and the static web assets an ASP.NET
/// Core app reads there. Nothing installs.
/// </summary>
public sealed class WslDotnetTests
{
    private static readonly string[] s_toolFolders = ["/bin/", "/usr/bin/"];

    private static readonly string[] s_probeTools = ["readlink", "dirname"];

    private const string Ubuntu = """
        home=/home/me
        os=ubuntu debian
        shell=/usr/bin/dotnet
        dotnet=/usr/bin/dotnet
        root=/usr/lib/dotnet
        Microsoft.AspNetCore.App 6.0.36 [/usr/lib/dotnet/shared/Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 6.0.36 [/usr/lib/dotnet/shared/Microsoft.NETCore.App]
        dotnet=/home/me/.dotnet/dotnet
        root=/home/me/.dotnet
        Microsoft.AspNetCore.App 8.0.11 [/home/me/.dotnet/shared/Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 8.0.11 [/home/me/.dotnet/shared/Microsoft.NETCore.App]
        """;

    private static RuntimeConfig Web(string version) => new("App.runtimeconfig.json",
    [
        FrameworkReference.Create(FrameworkReference.NetCoreApp, FrameworkVersion.Parse(version)),
        FrameworkReference.Create("Microsoft.AspNetCore.App", FrameworkVersion.Parse(version)),
    ]);

    private static ToolStatus Sdk(string channel) => new("dotnet", $".NET SDK {channel} in WSL: Ubuntu", false, ToolState.Missing, "Running App in WSL") { WantedChannel = channel };

    [Fact]
    public void The_probe_lists_each_dotnet_with_its_frameworks()
    {
        var probe = WslDotnet.Parse(Ubuntu.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(("/home/me", "ubuntu debian", "/usr/bin/dotnet"), (probe.Home, probe.Distribution, probe.Shell));
        Assert.Equal(["/usr/bin/dotnet", "/home/me/.dotnet/dotnet"], probe.Installs.Select(i => i.Path));
        Assert.Equal(["/usr/lib/dotnet", "/home/me/.dotnet"], probe.Installs.Select(i => i.Root));
        Assert.Equal(["8.0.11"], probe.Installs[1].Versions("Microsoft.AspNetCore.App").Select(v => v.Text));

        var bare = WslDotnet.Parse("home=/root\nos=alpine\nshell=\n");
        Assert.Equal(("/root", "alpine", (string?)null), (bare.Home, bare.Distribution, bare.Shell));
        Assert.Empty(bare.Installs);
        Assert.Equal(string.Empty, WslDotnet.Parse("wsl: There is no distribution with the supplied name.").Home);
    }

    [Fact]
    public void The_dotnet_that_has_the_apps_frameworks_runs_it_the_shells_first()
    {
        var probe = WslDotnet.Parse(Ubuntu);

        // An 8.0 web app: the shell's dotnet has 6.0 only, dotnet-install's ~/.dotnet has 8.0.
        var eight = WslDotnet.Choose(probe, Web("8.0.0"));
        Assert.Equal("/home/me/.dotnet/dotnet", eight.Dotnet?.Path);

        // A 6.0 app runs on the shell's.
        Assert.Equal("/usr/bin/dotnet", WslDotnet.Choose(probe, Web("6.0.0")).Dotnet?.Path);

        // A 10.0 app runs on neither: what the shell's dotnet lacks, and what it has.
        var ten = WslDotnet.Choose(probe, Web("10.0.0"));
        Assert.Null(ten.Dotnet);
        Assert.Equal("/usr/bin/dotnet", ten.Found?.Path);
        Assert.Equal(["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"], ten.Missing.Select(m => m.Reference.Name));
        Assert.Equal(["6.0.36"], ten.Missing[0].Installed.Select(v => v.Text));
        Assert.Equal("10.0", WslDotnet.Channel(Web("10.0.0"), "net10.0"));

        // No dotnet at all.
        var none = WslDotnet.Choose(WslDotnet.Parse("home=/home/me\nos=ubuntu debian\nshell=\n"), Web("8.0.0"));
        Assert.Equal((null, null, 0), (none.Dotnet, none.Found, none.Missing.Count));

        // Without a runtimeconfig.json to read (a self-contained app's names no framework), the first dotnet.
        Assert.Equal("/usr/bin/dotnet", WslDotnet.Choose(probe, null).Dotnet?.Path);
        Assert.Equal("/usr/bin/dotnet", WslDotnet.Choose(probe, new RuntimeConfig("App.runtimeconfig.json", [])).Dotnet?.Path);
    }

    [Theory]
    [InlineData("8.0.0", "net8.0", "8.0")]
    [InlineData(null, "net9.0", "9.0")]
    [InlineData(null, "net8.0-windows", "8.0")]
    [InlineData(null, "netstandard2.0", null)]
    [InlineData(null, null, null)]
    public void The_sdk_to_install_is_the_apps_channel(string? runtime, string? targetFramework, string? expected)
    {
        var config = runtime is null ? null : Web(runtime);
        Assert.Equal(expected ?? InstallPlans.HostChannel, WslDotnet.Channel(config, targetFramework));
    }

    [Fact]
    public void Linuxs_install_choices_run_in_the_distribution_and_leave_the_ides_dotnet_alone()
    {
        // No dotnet in Ubuntu, whose packages have the SDK: the package is recommended, in the terminal through wsl.exe.
        var empty = WslDotnet.Parse("home=/home/me\nos=ubuntu debian\nshell=\n");
        var plans = WslDotnet.InstallOptions(Sdk("8.0"), WslDotnet.Facts(empty, "8.0.111-0ubuntu1~24.04.1"), "Ubuntu", "WSL: Ubuntu");
        Assert.Equal(["package", "dotnet-install", "page"], plans.Select(p => p.Option));
        Assert.True(plans[0].Recommended);
        Assert.All(plans, p => Assert.Null(p.AddsToPath));

        var package = plans[0];
        Assert.Equal((InstallKind.Terminal, "Ubuntu", "sudo apt-get install -y dotnet-sdk-8.0"), (package.Kind, package.Wsl, package.Command));
        Assert.EndsWith(", in WSL: Ubuntu", package.Summary, StringComparison.Ordinal);

        var script = plans[1];
        Assert.Equal(InstallKind.Run, script.Kind);
        Assert.Equal("wsl.exe", script.Program);
        Assert.Equal(["-d", "Ubuntu", "--exec", "bash", "-c", script.Command], script.Arguments);
        Assert.Contains("--channel 8.0 --install-dir \"/home/me/.dotnet\"", script.Command, StringComparison.Ordinal);
        Assert.Equal("/home/me/.dotnet isn't on the PATH of WSL: Ubuntu's shells: Codalus IDE runs your apps on this SDK, but typing dotnet there won't find it.", script.Note);

        // The default distribution: no -d.
        var defaultOne = WslDotnet.InstallOptions(Sdk("8.0"), WslDotnet.Facts(empty, null), string.Empty, "WSL: default", "stand-in-wsl");
        Assert.Equal(["dotnet-install", "page"], defaultOne.Select(p => p.Option));
        Assert.Equal(("stand-in-wsl", "--exec"), (defaultOne[0].Program, defaultOne[0].Arguments![0]));
        Assert.Equal(string.Empty, defaultOne[0].Wsl);

        // The shell's dotnet in ~/.dotnet (an earlier dotnet-install): the new SDK goes next to it.
        var home = WslDotnet.Parse("home=/home/me\nos=ubuntu debian\nshell=/home/me/.dotnet/dotnet\ndotnet=/home/me/.dotnet/dotnet\nroot=/home/me/.dotnet\nMicrosoft.NETCore.App 6.0.36 [x]\n");
        var next = WslDotnet.InstallOptions(Sdk("8.0"), WslDotnet.Facts(home, "8.0.111"), "Ubuntu", "WSL: Ubuntu");
        Assert.Equal("dotnet-install", next[0].Option);
        Assert.Null(next[0].Note);

        // The shell's dotnet from the distribution's packages: their SDK fits, dotnet-install says the shell won't see it.
        var packaged = WslDotnet.InstallOptions(Sdk("8.0"), WslDotnet.Facts(WslDotnet.Parse(Ubuntu), "8.0.111"), "Ubuntu", "WSL: Ubuntu");
        Assert.Equal(["package", "dotnet-install", "page"], packaged.Select(p => p.Option));
        Assert.Contains("/usr/lib/dotnet", packaged[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_query_asks_the_distributions_own_package_manager()
    {
        Assert.Equal("apt-cache policy dotnet-sdk-8.0 2>/dev/null", WslDotnet.PackageQueryCommand(WslDotnet.Parse("home=/h\nos=ubuntu debian\n"), "8.0"));
        Assert.Equal("dnf -C info dotnet-sdk-9.0 2>/dev/null", WslDotnet.PackageQueryCommand(WslDotnet.Parse("home=/h\nos=fedora\n"), "9.0"));
        Assert.Null(WslDotnet.PackageQueryCommand(WslDotnet.Parse("home=/h\nos=alpine\n"), "8.0"));
    }

    [Fact]
    public void Static_web_assets_get_the_content_roots_wsl_sees()
    {
        // The maintainer's report: in WSL, ASP.NET Core stopped with "The path must be absolute" on the build's manifest.
        const string manifest = """{"ContentRoots":["C:\\src\\Web\\wwwroot\\","C:\\Users\\me\\.nuget\\packages\\lib\\1.0.0\\staticwebassets\\","/already/linux/"],"Root":{"Children":{"a.txt":{"Children":null,"Asset":{"ContentRootIndex":0,"SubPath":"a.txt"},"Patterns":null}},"Asset":null,"Patterns":null}}""";
        var translated = JsonNode.Parse(WslDotnet.TranslateStaticWebAssets(manifest, p => WslLaunch.ToLinuxPath(p))!)!;
        Assert.Equal(["/mnt/c/src/Web/wwwroot/", "/mnt/c/Users/me/.nuget/packages/lib/1.0.0/staticwebassets/", "/already/linux/"], translated["ContentRoots"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal("a.txt", translated["Root"]!["Children"]!["a.txt"]!["Asset"]!["SubPath"]!.GetValue<string>());

        Assert.Null(WslDotnet.TranslateStaticWebAssets("<StaticWebAssets Version=\"1.0\" />", p => p));
        Assert.Null(WslDotnet.TranslateStaticWebAssets("{}", p => p));

        var info = new RunInfo(@"C:\src\Web\Web.csproj", "net8.0", "Exe", @"C:\src\Web\bin\Debug\net8.0\Web.dll", null, null, null, null);
        Assert.Equal(@"C:\src\Web\bin\Debug\net8.0\Web.staticwebassets.runtime.json", WslDotnet.StaticWebAssetsManifest(info));
        Assert.Equal(@"C:\src\Web\bin\Debug\net8.0\Web.runtimeconfig.json", WslDotnet.RuntimeConfigPath(info));
        Assert.Null(WslDotnet.RuntimeConfigPath(info with { TargetPath = @"C:\src\Web\bin\Debug\net8.0\Web.exe" }));
    }

    [Fact]
    public async Task The_probe_script_finds_the_shells_dotnet_and_the_one_in_the_home_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The probe runs in a Linux shell.");
            return;
        }

        // Resolved: macOS's temp folder is under /var, a link to /private/var, and the probe reports an install's root
        // with readlink -f (the 1.0 validation on the Mac, 2026-11-19).
        var home = Codalus.Platform.RealPath.Resolve(Directory.CreateTempSubdirectory("codalus-wsl-probe-").FullName);
        try
        {
            // A stand-in dotnet in ~/.dotnet, and the same one linked from a folder on PATH: listed once, as the shell's.
            var dotnetDir = Directory.CreateDirectory(Path.Combine(home, ".dotnet")).FullName;
            var dotnet = Path.Combine(dotnetDir, "dotnet");
            await File.WriteAllTextAsync(dotnet, "#!/bin/sh\n[ \"$1\" = --list-runtimes ] && echo \"Microsoft.NETCore.App 8.0.11 [$HOME/.dotnet/shared/Microsoft.NETCore.App]\"\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(dotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // The PATH holds only the tools the probe uses: a runner's own dotnet (/usr/bin/dotnet on GitHub's Ubuntu
            // images) would be the shell's (Linux CI, run 44517642464).
            var tools = Directory.CreateDirectory(Path.Combine(home, "tools")).FullName;
            foreach (var tool in s_probeTools)
            {
                File.CreateSymbolicLink(Path.Combine(tools, tool), s_toolFolders.Select(folder => folder + tool).First(File.Exists));
            }

            var withoutPath = WslDotnet.Parse(await ProbeAsync(home, tools));
            Assert.Equal((home, (string?)null), (withoutPath.Home, withoutPath.Shell));
            Assert.Equal([dotnet], withoutPath.Installs.Select(i => i.Path));
            Assert.Equal(["8.0.11"], withoutPath.Installs[0].Versions(FrameworkReference.NetCoreApp).Select(v => v.Text));

            var bin = Directory.CreateDirectory(Path.Combine(home, "bin")).FullName;
            File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), dotnet);
            var onPath = WslDotnet.Parse(await ProbeAsync(home, bin + ":" + tools));
            Assert.Equal(Path.Combine(bin, "dotnet"), onPath.Shell);
            Assert.Equal([Path.Combine(bin, "dotnet")], onPath.Installs.Select(i => i.Path));
            Assert.Equal(dotnetDir, onPath.Installs[0].Root);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>The probe as wsl.exe runs it (<c>sh -c</c>), with the home folder and the PATH a login shell would give.</summary>
    private static async Task<string> ProbeAsync(string home, string path)
    {
        // The login shell reads the profile files, which may set their own PATH: here a plain shell, with the PATH set.
        var script = WslDotnet.ProbeScript;
        var start = new ProcessStartInfo(s_toolFolders.Select(folder => folder + "sh").First(File.Exists), ["-c", script]) { RedirectStandardOutput = true, UseShellExecute = false };
        start.Environment["HOME"] = home;
        start.Environment["PATH"] = path;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        return output;
    }
}
