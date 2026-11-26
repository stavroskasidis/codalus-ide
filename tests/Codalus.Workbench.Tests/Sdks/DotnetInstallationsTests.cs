using Codalus.Platform;
using Codalus.Workbench.Sdks;

namespace Codalus.Workbench.Tests.Sdks;

/// <summary>The .NET folders of described machines (ADR 0111): what's in each, read from its folders, and who owns it.</summary>
public sealed class DotnetInstallationsTests
{
    [Fact]
    public void Each_folder_lists_its_sdks_and_runtimes_the_one_in_use_first_and_missing_or_empty_ones_left_out()
    {
        var machine = new FakeDotnetMachine()
            .WithRoot("/home/u/.dotnet", ["11.0.100", "10.0.112"], (DotnetFrameworks.NetCore, "11.0.0"), (DotnetFrameworks.NetCore, "10.0.12"), (DotnetFrameworks.AspNetCore, "11.0.0"))
            .WithRoot("/usr/lib/dotnet", ["8.0.122"], (DotnetFrameworks.NetCore, "8.0.22"))
            .With("/home/u/.dotnet/sdk/NuGetFallbackFolder", "/usr/local/share/dotnet");

        var list = DotnetInstallations.Read(machine, ["/usr/lib/dotnet", "/home/u/.dotnet/", "/snap/dotnet-sdk/current", "/usr/local/share/dotnet"], "/home/u/.dotnet");

        Assert.Equal(["/home/u/.dotnet", "/usr/lib/dotnet"], list.Select(i => i.Root));
        var user = list[0];
        Assert.True(user.InUse);
        Assert.Equal(DotnetRootOwner.User, user.Owner);
        Assert.Equal(["10.0.112", "11.0.100"], user.Sdks);
        Assert.Equal([new DotnetRuntime(DotnetFrameworks.NetCore, "10.0.12"), new DotnetRuntime(DotnetFrameworks.NetCore, "11.0.0"), new DotnetRuntime(DotnetFrameworks.AspNetCore, "11.0.0")], user.Runtimes);
        Assert.Equal(DotnetRootOwner.LinuxPackage, list[1].Owner);
        Assert.False(list[1].InUse);
    }

    [Theory]
    [InlineData("/snap/dotnet-sdk/current", false, DotnetRootOwner.Snap)]
    [InlineData("/usr/share/dotnet", false, DotnetRootOwner.LinuxPackage)]
    [InlineData("/opt/dotnet", false, DotnetRootOwner.Administrator)]
    [InlineData("/opt/dotnet", true, DotnetRootOwner.User)]
    [InlineData("/home/u/tools/dotnet", false, DotnetRootOwner.User)]
    public void Who_owns_a_linux_folder(string root, bool writable, DotnetRootOwner owner)
    {
        var machine = new FakeDotnetMachine().With(root);
        if (writable)
        {
            machine.Writable.Add(root);
        }

        Assert.Equal(owner, DotnetInstallations.OwnerOf(machine, root));
    }

    [Fact]
    public void On_a_mac_the_pkgs_folder_and_on_windows_program_files_belong_to_their_installers()
    {
        Assert.Equal(DotnetRootOwner.MacPackage, DotnetInstallations.OwnerOf(new FakeDotnetMachine(PlatformKind.MacOS, "/Users/u"), "/usr/local/share/dotnet"));
        Assert.Equal(DotnetRootOwner.User, DotnetInstallations.OwnerOf(new FakeDotnetMachine(PlatformKind.MacOS, "/Users/u"), "/Users/u/.dotnet"));

        var windows = new FakeDotnetMachine(PlatformKind.Windows, @"C:\Users\u");
        string? Variables(string name) => name == "ProgramFiles" ? @"C:\Program Files" : null;
        Assert.Equal(DotnetRootOwner.WindowsInstaller, DotnetInstallations.OwnerOf(windows, @"C:\Program Files\dotnet", Variables));
        Assert.Equal(DotnetRootOwner.User, DotnetInstallations.OwnerOf(windows, @"C:\Users\u\AppData\Local\Microsoft\dotnet", Variables));
    }
}
