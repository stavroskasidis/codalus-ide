using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>Run and Debug Plugin in Development Instance (M22b): how the second instance starts.</summary>
public sealed class PluginDevelopmentTests
{
    [Fact]
    public void The_debug_plugin_host_option_is_a_full_path()
    {
        var options = ShellOptions.Parse(["--plugin-dir", "out", "--debug-plugin-host", "host.pid"]);
        Assert.Empty(options.Errors);
        Assert.Equal(Path.GetFullPath("host.pid"), options.DebugPluginHost);
        Assert.Equal([Path.GetFullPath("out")], options.PluginDirs);
    }

    [AvaloniaFact]
    public async Task The_development_instance_loads_the_build_output_with_its_own_user_data()
    {
        await using var f = ShellFixture.Create();
        var project = Path.Combine(f.UserData, "Greeter", "Greeter.csproj");
        var output = Path.Combine(f.UserData, "Greeter", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, ShellHost.PluginManifestFile), """{ "id": "contoso.greeter", "enableProposedApi": true }""");

        var run = f.Shell.PluginDevelopmentStart(project, output, debugFile: null)!;
        var args = run.ArgumentList.ToList();
        Assert.Equal(Path.Combine(f.UserData, "Greeter", ".codalus", "plugin-dev"), args[args.IndexOf("--user-data-dir") + 1]);
        Assert.Equal(output, args[args.IndexOf("--plugin-dir") + 1]);
        Assert.Contains("--dev", args);
        Assert.Contains("--enable-proposed-api", args); // the manifest asks for it
        Assert.DoesNotContain("--debug-plugin-host", args);

        var debug = f.Shell.PluginDevelopmentStart(project, output, debugFile: "host.pid")!.ArgumentList.ToList();
        Assert.Equal("host.pid", debug[debug.IndexOf("--debug-plugin-host") + 1]);
    }
}
