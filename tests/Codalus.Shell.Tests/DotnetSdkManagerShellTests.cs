using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Sdks;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// Tools → .NET SDKs (ADR 0113) in the shell: a .NET folder of the test's own (in a temporary folder) and the samples'
/// copy of the release metadata. The lists with their updates, the details with the command a button runs, a removal
/// that deletes only after the confirmation, an install that runs the details' command without another question (into a
/// stand-in), and Check Environment's Manage…. Nothing reaches the network or installs.
/// </summary>
public sealed class DotnetSdkManagerShellTests
{
    private static string Samples => Path.Combine(
        typeof(DotnetSdkManagerShellTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!, "samples", "DotnetReleases");

    private static DotnetSdkRowViewModel Row(DotnetSdksViewModel model, string title) => model.Rows.Single(r => r.Title == title);

    private static string SdkKey(Machine machine, string version) => $"sdk|{machine.Root}|{version}";

    /// <summary>Selects a row and waits for its details' preview (the plans and what Remove does).</summary>
    private static async Task SelectAsync(DotnetSdksViewModel model, string key)
    {
        Assert.True(model.Select(key), key);
        await model.PreviewTask;
    }

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
        var model = f.Shell.DotnetSdksModel;

        // Installed: the folder once, as a heading; its SDKs; its runtimes folded, with their updates counted.
        var folder = model.Rows[0];
        Assert.True(folder.IsHeader);
        Assert.Equal(machine.Root, folder.Title);
        Assert.Equal("Codalus IDE uses it · managed by you", folder.Detail);
        Assert.Equal(["SDK 11.0.100", "SDK 10.0.110", "Runtimes (3)"], model.Rows.Skip(1).Select(r => r.Title));
        var sdk = Row(model, "SDK 10.0.110");
        Assert.Equal("Security update: 10.0.112", Assert.Single(sdk.Chips, c => c.IsWarning).Tip);
        Assert.Equal("10.0.112", sdk.Chips.Single(c => c.IsWarning).Text);
        Assert.Contains(Row(model, "Runtimes (3)").Chips, c => c.Text == "1 update");
        Assert.Equal(5, model.InstalledCount);
        Assert.Equal(2, model.UpdatesCount);

        model.ToggleGroup(Row(model, "Runtimes (3)"));
        Assert.Contains(Row(model, ".NET Runtime 10.0.10").Chips, c => c.Tip == "Security update: 10.0.12");

        // Updates: only what has a newer patch, by folder.
        model.Tab = DotnetSdksTab.Updates;
        Assert.Equal([machine.Root, "SDK 10.0.110", ".NET Runtime 10.0.10"], model.Rows.Select(r => r.Title));

        // Available: what can be installed into the folder in use; out of support only when asked.
        model.Tab = DotnetSdksTab.Available;
        Assert.Contains(model.Rows, r => r.Title == ".NET SDK 10.0.401");
        Assert.DoesNotContain(model.Rows, r => r.Detail.StartsWith(".NET 9.0", StringComparison.Ordinal));
        model.ShowOutOfSupport = true;
        Assert.Contains(model.Rows, r => r.Title == ".NET SDK 9.0.308");

        model.Search = "9.0.3";
        Assert.Equal([".NET SDK 9.0.308"], model.Rows.Where(r => r.Title.Contains("SDK", StringComparison.Ordinal)).Select(r => r.Title));
    }

    [AvaloniaFact]
    public async Task The_details_show_what_the_sdk_brought_and_the_exact_command_its_update_runs()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var model = f.Shell.DotnetSdksModel;

        await SelectAsync(model, SdkKey(machine, "10.0.110"));

        var details = model.Details!;
        Assert.Equal(".NET SDK 10.0.110", details.Heading);
        Assert.Equal("Update to 10.0.112", details.PrimaryLabel);
        Assert.True(details.HasPrimary);
        Assert.Contains(details.Facts, x => x.Label == "Feature band" && x.Value == "10.0.1xx");
        Assert.Contains(details.Facts, x => x.Label == "Runtimes it brought" && x.Value == ".NET Runtime 10.0.10");
        var option = Assert.Single(details.Options);
        Assert.Contains(OperatingSystem.IsWindows() ? "-Version 10.0.112" : "--version 10.0.112", option.Command, StringComparison.Ordinal);
        Assert.False(details.HasManyOptions); // one way: no radio button
        Assert.True(details.CanRemove);
        Assert.Contains(Path.Combine(machine.Root, "sdk", "10.0.110"), details.RemovalText, StringComparison.Ordinal);

        // The SDK in use has nothing to update: no Update button, and Remove is offered with what it deletes.
        await SelectAsync(model, SdkKey(machine, "11.0.100"));
        Assert.False(model.Details!.HasPrimary);
    }

    [AvaloniaFact]
    public async Task Remove_lists_the_folders_and_deletes_them_only_after_the_yes()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var model = f.Shell.DotnetSdksModel;
        await SelectAsync(model, SdkKey(machine, "10.0.110"));

        var cancelled = model.RemoveAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Contains(Path.Combine(machine.Root, "sdk", "10.0.110"), f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Contains(".NET Runtime 10.0.10 stays: it's the newest .NET 10.0 runtime here", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        f.Shell.Dialogs.Cancel();
        await cancelled;
        Assert.True(Directory.Exists(Path.Combine(machine.Root, "sdk", "10.0.110")));

        // 11.0.100 stays, so 10.0.110 isn't the last SDK; its runtime isn't the newest of .NET 10 but the only one: it stays.
        var removing = model.RemoveAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await removing;

        Assert.False(Directory.Exists(Path.Combine(machine.Root, "sdk", "10.0.110")));
        Assert.True(Directory.Exists(Path.Combine(machine.Root, "shared", "Microsoft.NETCore.App", "10.0.10")));
        Assert.Equal(["SDK 11.0.100"], model.Rows.Where(r => r.Item is InstalledSdk).Select(r => r.Title));
        Assert.Equal(".NET SDK 10.0.110 is removed.", model.Message);
    }

    [AvaloniaFact]
    public async Task The_last_sdk_of_the_folder_in_use_offers_no_remove_and_says_why()
    {
        await using var machine = new Machine();
        Directory.Delete(Path.Combine(machine.Root, "sdk", "10.0.110"));
        await using var f = await OpenAsync(machine);
        var model = f.Shell.DotnetSdksModel;

        await SelectAsync(model, SdkKey(machine, "11.0.100"));

        Assert.False(model.Details!.CanRemove);
        Assert.Contains("is the last SDK in", model.Details.RemoveRefused, StringComparison.Ordinal);
        await model.RemoveAsync();
        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.True(Directory.Exists(Path.Combine(machine.Root, "sdk", "11.0.100")));
    }

    [AvaloniaFact]
    public async Task Install_runs_the_command_the_details_show_without_another_question()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var model = f.Shell.DotnetSdksModel;
        var ran = new List<string>();
        f.Shell.SdkCommandStandIn = (program, arguments, _, _) =>
        {
            ran.Add(program + " " + string.Join(' ', arguments));
            Directory.CreateDirectory(Path.Combine(machine.Root, "sdk", "10.0.401"));
            return Task.FromResult((0, string.Empty));
        };
        model.Tab = DotnetSdksTab.Available;
        await SelectAsync(model, "av|sdk|10.0.401");
        var expected = OperatingSystem.IsWindows() ? "dotnet-install.ps1" : $"--version 10.0.401 --install-dir \"{machine.Root}\"";
        Assert.Contains(expected, Assert.Single(model.Details!.Options).Command, StringComparison.Ordinal);

        await model.PrimaryAsync();

        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.Contains(expected, Assert.Single(ran), StringComparison.Ordinal);
        model.Tab = DotnetSdksTab.Installed;
        Assert.Contains(model.Rows, r => r.Title == "SDK 10.0.401");
        Assert.Equal(".NET SDK 10.0.401 is installed.", model.Message);
    }

    [AvaloniaFact]
    public async Task A_global_json_prompts_install_opens_the_manager_with_the_pinned_sdk_selected()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var model = f.Shell.DotnetSdksModel;
        var ran = new List<string>();
        f.Shell.SdkCommandStandIn = (program, arguments, _, _) =>
        {
            ran.Add(string.Join(' ', arguments));
            Directory.CreateDirectory(Path.Combine(machine.Root, "sdk", "10.0.111"));
            return Task.FromResult((0, string.Empty));
        };
        var mismatch = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Mismatch, "builds") { WantedVersion = "10.0.111", Version = "11.0.100" };

        await f.Shell.InstallFromPromptAsync(mismatch);
        await model.PreviewTask;

        Assert.Equal(DotnetSdksTab.Available, model.Tab);
        Assert.Equal(".NET SDK 10.0.111", model.Rows[0].Title);
        Assert.Equal("The open solution's global.json asks for it", model.Rows[0].Detail);
        Assert.Same(model.Rows[0], model.Selected);
        Assert.True(model.Details!.HasPrimary);
        Assert.False(f.Shell.Dialogs.IsOpen); // nothing runs until Install

        await model.PrimaryAsync();

        // dotnet-install.ps1 takes -Version, dotnet-install.sh --version (Windows CI, run 45942399760).
        Assert.Contains(OperatingSystem.IsWindows() ? "-Version 10.0.111" : "--version 10.0.111", Assert.Single(ran), StringComparison.Ordinal);
        Assert.DoesNotContain(model.Rows, r => r.Title == ".NET SDK 10.0.111");
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
        Assert.StartsWith("Microsoft's release information couldn't be read", f.Shell.DotnetSdksModel.ReleaseInfoProblem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two ways to install (the distribution's package, or <c>dotnet-install</c> into <c>~/.dotnet</c>): the details list
    /// both as radio choices, the recommended one chosen, and the button runs the one the user picks.
    /// </summary>
    [AvaloniaFact]
    public async Task Several_ways_to_install_are_radio_choices_and_the_chosen_one_runs()
    {
        var package = new InstallPlan("dotnet", InstallKind.Terminal, "The distribution's package", "sudo apt-get install dotnet-sdk-10.0") { Recommended = true, Title = "The distribution's package" };
        var script = new InstallPlan("dotnet", InstallKind.Run, "dotnet-install", "dotnet-install.sh --version 10.0.401") { Title = "dotnet-install into ~/.dotnet" };
        var actions = new StandInActions([package, script]);
        var installation = new DotnetInstallation("/usr/lib/dotnet", DotnetRootOwner.LinuxPackage, InUse: true) { Sdks = ["11.0.100"] };
        var facts = new DotnetSdkFacts(new LocalDotnetMachine(PlatformInfo.Current), [installation], new DateOnly(2026, 11, 27));
        var available = new AvailableDotnet("10.0", AvailableDotnet.Sdk, "10.0.401");
        using var model = new DotnetSdksViewModel(actions) { Tab = DotnetSdksTab.Available };
        model.SetData(new DotnetSdksData(facts, DotnetSdkPlanner.InstalledSdks(facts), [], [available]));
        await model.PreviewTask;

        var details = model.Details!;
        Assert.True(details.HasManyOptions);
        Assert.Equal(["The distribution's package", "dotnet-install into ~/.dotnet"], details.Options.Select(o => o.Title));
        Assert.Contains("It runs in the terminal", details.Options[0].Note, StringComparison.Ordinal);
        Assert.Same(package, details.Plan);

        details.Options[1].IsSelected = true;
        details.Options[0].IsSelected = false;
        await model.PrimaryAsync();

        Assert.Same(script, actions.Installed);
    }

    private sealed class StandInActions(IReadOnlyList<InstallPlan> plans) : IDotnetSdksActions
    {
        public InstallPlan? Installed { get; private set; }

        public Task<DotnetSdkPreview> PreviewAsync(object item, CancellationToken cancellationToken) => Task.FromResult(new DotnetSdkPreview(plans, null));

        public Task InstallAsync(AvailableDotnet item, InstallPlan plan)
        {
            Installed = plan;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(object item, InstallPlan plan) => Task.CompletedTask;

        public Task RemoveAsync(object item) => Task.CompletedTask;

        public Task RemoveOldPatchesAsync(InstalledSdk sdk) => Task.CompletedTask;

        public Task RefreshAsync() => Task.CompletedTask;

        public void FiltersChanged()
        {
        }
    }

    /// <summary>
    /// The window as it renders: the list with an SDK's details, a global.json prompt's SDK selected in Available, and a
    /// removal's confirmation over it, saved to <c>CODALUS_UI_SNAPSHOTS</c> when it's set (a visual check; the website's
    /// picture comes from the smoke step).
    /// </summary>
    [AvaloniaFact]
    public async Task The_window_renders_its_list_a_prompts_selection_and_a_removals_confirmation()
    {
        await using var machine = new Machine();
        await using var f = await OpenAsync(machine);
        var model = f.Shell.DotnetSdksModel;
        var view = new Codalus.Shell.Views.DotnetSdksView { DataContext = model };
        var dialog = new Codalus.Shell.Views.DialogView { DataContext = f.Shell.Dialogs, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var window = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = new Avalonia.Controls.Panel { Children = { view, dialog } } };
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

        await SelectAsync(model, SdkKey(machine, "10.0.110"));
        Snap("dotnet-sdks-list.png");
        var list = view.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single(l => l.Name == "List");
        Assert.Equal(2, list.SelectedIndex); // under the folder's heading and 11.0.100
        Assert.True(view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "PrimaryButton").IsEffectivelyVisible);

        await f.Shell.InstallFromPromptAsync(new ToolStatus("dotnet", ".NET SDK", true, ToolState.Mismatch, "builds") { WantedVersion = "10.0.111", Version = "11.0.100" });
        await model.PreviewTask;
        Snap("dotnet-sdks-prompt.png");
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(".NET SDK 10.0.111", model.Rows[0].Title);

        model.Tab = DotnetSdksTab.Installed;
        await SelectAsync(model, SdkKey(machine, "10.0.110"));
        var removing = model.RemoveAsync();
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
