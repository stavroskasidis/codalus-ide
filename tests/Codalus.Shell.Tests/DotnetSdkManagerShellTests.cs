using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Sdks;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// Tools → .NET SDKs (ADR 0113) in the shell: a .NET folder of the test's own (in a temporary folder) and the samples'
/// copy of the release metadata. The list with its updates, a removal that deletes only after the confirmation, an
/// install whose command goes to a stand-in, and Check Environment's Manage…. Nothing reaches the network or installs.
/// </summary>
public sealed class DotnetSdkManagerShellTests
{
    private static string Samples => Path.Combine(
        typeof(DotnetSdkManagerShellTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!, "samples", "DotnetReleases");

    private static ViewNodeDto? Find(ViewNodeDto node, string id) => node.Id == id ? node : node.Children?.Select(c => Find(c, id)).FirstOrDefault(c => c is not null);

    private sealed class Machine : IAsyncDisposable
    {
        public Machine()
        {
            Root = Directory.CreateTempSubdirectory("codalus-sdks-").FullName;
            foreach (var folder in new[]
            {
                "sdk/10.0.110", "sdk/11.0.100",
                "shared/Microsoft.NETCore.App/10.0.10", "shared/Microsoft.NETCore.App/11.0.0",
                "packs/Microsoft.NETCore.App.Ref/10.0.10", "templates/10.0.10",
                "shared/Microsoft.AspNetCore.App/11.0.0",
            })
            {
                Directory.CreateDirectory(Path.Combine(Root, folder));
            }
        }

        public string Root { get; }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<ShellFixture> OpenAsync(Machine machine)
    {
        var f = ShellFixture.Create();
        f.Shell.SdkMachineStandIn = new LocalDotnetMachine(PlatformInfo.Current);
        f.Shell.SdkRootsStandIn = () => ([machine.Root], machine.Root);
        f.Shell.DotnetReleases = new DotnetReleaseSource(Path.Combine(machine.Root, "..", Path.GetFileName(machine.Root) + "-cache"), Samples, (_, _) => throw new InvalidOperationException("no network in tests"));
        f.Shell.OpenDotnetSdks();
        await ShellFixture.PumpUntil(() => f.Shell.DotnetSdksLoaded && !f.Shell.DotnetSdksBusy); // the buttons are off until the first read ends
        return f;
    }

    [AvaloniaFact]
    public async Task The_window_lists_the_folders_sdks_with_their_updates_and_whats_available()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);

        var root = f.Shell.DotnetSdksRoot();
        var sdks = Find(root, "sdks:0")!.Rows!;
        Assert.Equal(["11.0.100", "10.0.110"], sdks.Select(r => r[0]));
        Assert.Equal("Up to date", sdks[0][1]);
        Assert.Equal("Security update: 10.0.112", sdks[1][1]);
        Assert.Equal(3, sdks[1].Length); // the folder and its owner are its section's, not every row's
        Assert.Contains(root.Children!, c => c is { Style: "heading" } && c.Text == machine.Root);
        Assert.Contains(root.Children!, c => c.Text == "The .NET Codalus IDE uses. Managed by you.");
        Assert.Contains(Find(root, "runtimes:0")!.Rows!, r => r[0] == ".NET Runtime" && r[1] == "10.0.10" && r[2] == "Security update: 10.0.12");
        Assert.Contains(Find(root, "available")!.Rows!, r => r[0] == ".NET SDK 10.0.401");
        Assert.DoesNotContain(Find(root, "available")!.Rows!, r => r[1] == "9.0");

        await f.Shell.OnDotnetSdksEventAsync("showOutOfSupport", "change", "true", null);
        Assert.Contains(Find(f.Shell.DotnetSdksRoot(), "available")!.Rows!, r => r[0] == ".NET SDK 9.0.308");
    }

    [AvaloniaFact]
    public async Task Remove_lists_the_folders_and_deletes_them_only_after_the_yes()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        await f.Shell.OnDotnetSdksEventAsync("sdks:0", "select", null, 1); // 10.0.110

        var cancelled = f.Shell.OnDotnetSdksEventAsync("sdkRemove:0", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Contains(Path.Combine(machine.Root, "sdk", "10.0.110"), f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Contains(".NET Runtime 10.0.10 stays: it's the newest .NET 10.0 runtime here", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        f.Shell.Dialogs.Cancel();
        await cancelled;
        Assert.True(Directory.Exists(Path.Combine(machine.Root, "sdk", "10.0.110")));

        // 11.0.100 stays, so 10.0.110 isn't the last SDK; its runtime isn't the newest of .NET 10 but the only one: it stays.
        var removing = f.Shell.OnDotnetSdksEventAsync("sdkRemove:0", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await removing;

        Assert.False(Directory.Exists(Path.Combine(machine.Root, "sdk", "10.0.110")));
        Assert.True(Directory.Exists(Path.Combine(machine.Root, "shared", "Microsoft.NETCore.App", "10.0.10")));
        Assert.Equal(["11.0.100"], Find(f.Shell.DotnetSdksRoot(), "sdks:0")!.Rows!.Select(r => r[0]));
        Assert.Contains(f.Shell.DotnetSdksRoot().Children!, c => c.Text == ".NET SDK 10.0.110 is removed.");
    }

    [AvaloniaFact]
    public async Task Removing_the_last_sdk_of_the_folder_in_use_is_refused_with_the_reason()
    {
        await using var machine = new Machine();
        Directory.Delete(Path.Combine(machine.Root, "sdk", "10.0.110"));
        await using var f = await OpenAsync(machine);
        await f.Shell.OnDotnetSdksEventAsync("sdks:0", "select", null, 0);

        await f.Shell.OnDotnetSdksEventAsync("sdkRemove:0", "click", null, null);

        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.True(Directory.Exists(Path.Combine(machine.Root, "sdk", "11.0.100")));
        Assert.Contains(f.Shell.DotnetSdksRoot().Children!, c => c.Text?.Contains("is the last SDK in", StringComparison.Ordinal) == true);
    }

    [AvaloniaFact]
    public async Task Install_shows_the_exact_command_and_runs_it_after_the_yes()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var ran = new List<string>();
        f.Shell.SdkCommandStandIn = (program, arguments, _, _) =>
        {
            ran.Add(program + " " + string.Join(' ', arguments));
            Directory.CreateDirectory(Path.Combine(machine.Root, "sdk", "10.0.401"));
            return Task.FromResult((0, string.Empty));
        };
        var row = Find(f.Shell.DotnetSdksRoot(), "available")!.Rows!.ToList().FindIndex(r => r[0] == ".NET SDK 10.0.401");
        await f.Shell.OnDotnetSdksEventAsync("available", "select", null, row);

        var installing = f.Shell.OnDotnetSdksEventAsync("install", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        var expected = OperatingSystem.IsWindows() ? "dotnet-install.ps1" : $"--version 10.0.401 --install-dir \"{machine.Root}\"";
        Assert.Contains(expected, f.Shell.Dialogs.Message, StringComparison.Ordinal);
        f.Shell.Dialogs.Accept();
        await installing;

        Assert.Contains(expected, Assert.Single(ran), StringComparison.Ordinal);
        Assert.Contains("10.0.401", Find(f.Shell.DotnetSdksRoot(), "sdks:0")!.Rows!.Select(r => r[0]));
        Assert.Contains(f.Shell.DotnetSdksRoot().Children!, c => c.Text == ".NET SDK 10.0.401 is installed.");
    }

    [AvaloniaFact]
    public async Task A_global_json_prompts_install_opens_the_manager_with_the_pinned_sdk_selected()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var ran = new List<string>();
        f.Shell.SdkCommandStandIn = (program, arguments, _, _) =>
        {
            ran.Add(string.Join(' ', arguments));
            Directory.CreateDirectory(Path.Combine(machine.Root, "sdk", "10.0.111"));
            return Task.FromResult((0, string.Empty));
        };
        var mismatch = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Mismatch, "builds") { WantedVersion = "10.0.111", Version = "11.0.100" };

        await f.Shell.InstallFromPromptAsync(mismatch);

        var available = Find(f.Shell.DotnetSdksRoot(), "available")!.Rows!;
        Assert.Equal([".NET SDK 10.0.111", "10.0", "The open solution's global.json asks for it"], available[0]);
        Assert.False(Find(f.Shell.DotnetSdksRoot(), "install")!.Disabled); // selected
        Assert.False(f.Shell.Dialogs.IsOpen); // nothing runs until Install

        var installing = f.Shell.OnDotnetSdksEventAsync("install", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await installing;

        Assert.Contains("--version 10.0.111", Assert.Single(ran), StringComparison.Ordinal);
        Assert.DoesNotContain(Find(f.Shell.DotnetSdksRoot(), "available")!.Rows!, r => r[0] == ".NET SDK 10.0.111");
    }

    [AvaloniaFact]
    public async Task Without_release_information_a_missing_sdks_prompt_installs_as_check_environment_does()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        f.Shell.DotnetReleases = new DotnetReleaseSource(Path.Combine(machine.Root, "no-cache"), "https://releases.example/", (_, _) => throw new HttpRequestException("offline"));
        var missing = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Missing, "builds");

        var prompt = f.Shell.InstallFromPromptAsync(missing);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);

        Assert.Equal("Install .NET SDK", f.Shell.Dialogs.Title);
        f.Shell.Dialogs.Cancel();
        await prompt;
        Assert.Contains(f.Shell.DotnetSdksRoot().Children!, c => c.Text?.StartsWith("Microsoft's release information couldn't be read", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The window as it renders: the list, a global.json prompt's SDK selected, and a removal's confirmation over it,
    /// saved to <c>CODALUS_UI_SNAPSHOTS</c> when it's set (a visual check; the website's picture comes from the smoke step).
    /// </summary>
    [AvaloniaFact]
    public async Task The_window_renders_its_list_a_prompts_selection_and_a_removals_confirmation()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var view = new ShellHost.DotnetSdksView(f.Shell);
        var dialog = new Codalus.Shell.Views.DialogView { DataContext = f.Shell.Dialogs, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var window = new Avalonia.Controls.Window { Width = 1100, Height = 900, Content = new Avalonia.Controls.Panel { Children = { view, dialog } } };
        window.Show();
        var snapshots = Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS");
        void Snap(string name)
        {
            dialog.IsVisible = f.Shell.Dialogs.IsOpen; // the shell's overlay shows the dialog layer only while one is open
            ShellFixture.Pump();
            ShellFixture.Pump();
            if (snapshots is { Length: > 0 })
            {
                Directory.CreateDirectory(snapshots);
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                frame!.Save(Path.Combine(snapshots, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }

        await f.Shell.OnDotnetSdksEventAsync("sdks:0", "select", null, 1);
        Snap("dotnet-sdks-list.png");

        await f.Shell.InstallFromPromptAsync(new ToolStatus("dotnet", ".NET SDK", true, ToolState.Mismatch, "builds") { WantedVersion = "10.0.111", Version = "11.0.100" });
        Snap("dotnet-sdks-prompt.png");
        Assert.Equal(0, view.View.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "available").SelectedIndex);
        Assert.Equal(".NET SDK 10.0.111", Find(f.Shell.DotnetSdksRoot(), "available")!.Rows![0][0]);

        await f.Shell.OnDotnetSdksEventAsync("sdks:0", "select", null, 1);
        var removing = f.Shell.OnDotnetSdksEventAsync("sdkRemove:0", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Snap("dotnet-sdks-remove.png");
        f.Shell.Dialogs.Cancel();
        await removing;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Check_environments_dotnet_row_opens_the_manager()
    {
        await using var f = ShellFixture.Create();
        var dotnet = new ToolRowViewModel(new ToolStatus("dotnet", ".NET SDK", true, ToolState.Found, "builds"), null, _ => Task.CompletedTask, f.Shell.OpenDotnetSdks);
        var git = new ToolRowViewModel(new ToolStatus("git", "Git", true, ToolState.Found, "source control"), null, _ => Task.CompletedTask);

        Assert.True(dotnet.CanManage);
        Assert.False(git.CanManage);
        Assert.NotNull(f.Shell.Workbench.Commands.Find("dotnetSdks.show"));
    }
}
