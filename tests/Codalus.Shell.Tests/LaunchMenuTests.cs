using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Tests;

/// <summary>The Start button's drop-down (launch profiles), on samples/BuildAndRun's Hello and its launch settings.</summary>
public sealed class LaunchMenuTests
{
    [AvaloniaFact]
    public async Task Start_drop_down_lists_the_launch_profiles_picks_one_and_offers_the_web_choices()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        var hello = Path.Combine(folder, "src", "Hello", "Hello.csproj");
        var toolbar = f.Shell.Toolbar;

        // Hello's three profiles, the first that runs chosen; IIS Express listed but disabled, with the reason.
        await ShellFixture.PumpUntil(() => toolbar.LaunchMenu is { Profiles.Count: 3 }, describe: () => $"{toolbar.LaunchMenu}");
        var menu = toolbar.LaunchMenu!;
        Assert.Equal("Hello", menu.ProjectName);
        Assert.Equal(["Hello", "Fails", "IIS Express"], menu.Profiles.Select(p => p.Name));
        Assert.Equal("Hello", menu.Current);
        Assert.Null(menu.Web); // no profile has an application URL
        Assert.Null(menu.Profiles[0].Unsupported);
        Assert.Contains("IISExpress", menu.Profiles[2].Unsupported, StringComparison.Ordinal);

        var view = f.Window.GetVisualDescendants().OfType<StandardToolbar>().Single();
        var items = view.LaunchMenu.Items;
        var start = Assert.IsType<MenuItem>(items[0]);
        Assert.Equal(toolbar.StartLabel, start.Header);
        Assert.NotNull(start.Command);
        var profiles = items.OfType<MenuItem>().Where(i => i.Command == toolbar.SelectLaunchProfileCommand).ToList();
        Assert.Equal(["Hello", "Fails", "IIS Express"], profiles.Select(i => (string)i.Header!));
        Assert.Equal([true, false, false], profiles.Select(i => i.IsChecked));
        Assert.Equal([true, true, false], profiles.Select(i => i.IsEnabled));
        Assert.Equal(menu.Profiles[2].Unsupported, ToolTip.GetTip(profiles[2]));
        Assert.Equal(menu.Profiles[2].Unsupported, AutomationProperties.GetHelpText(profiles[2]));
        Assert.DoesNotContain(items.OfType<MenuItem>(), i => (i.Header as string)?.StartsWith("Web Browser", StringComparison.Ordinal) == true);

        // Picking a profile: the next Start uses it, the button says so, and the menu checks it.
        profiles[1].Command!.Execute(profiles[1].CommandParameter);
        Assert.Equal("Fails", f.Shell.Runs.ChosenProfile(hello));
        await ShellFixture.PumpUntil(() => toolbar.StartLabel == "Hello: Fails" && toolbar.LaunchMenu?.Current == "Fails");
        Assert.Equal([false, true, false], view.LaunchMenu.Items.OfType<MenuItem>().Where(i => i.Command == toolbar.SelectLaunchProfileCommand).Select(i => i.IsChecked));
        Assert.Equal("Hello: Fails", Assert.IsType<MenuItem>(view.LaunchMenu.Items[0]).Header);

        // In a narrow window the "…" copies the drop-down with its checks and disabled profiles.
        var folded = Assert.IsType<MenuItem>(ToolbarPanel.Entry(view.FindControl<SplitButton>("StartButton")!, view));
        var copies = folded.Items.OfType<MenuItem>().Where(i => i.Command == toolbar.SelectLaunchProfileCommand).ToList();
        Assert.Equal([false, true, false], copies.Select(i => i.IsChecked));
        Assert.False(copies[2].IsEnabled);

        // Debug Properties: Project Properties on the Debug page.
        var properties = view.LaunchMenu.Items.OfType<MenuItem>().Single(i => i.Command == toolbar.DebugPropertiesCommand);
        Assert.Equal("Hello Debug Properties", properties.Header);
        properties.Command!.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is ProjectPropertiesDocument);
        Assert.True(((ProjectPropertiesDocument)f.Shell.Layout.ActiveDocument!).ViewModel.IsLaunchProfilesPage);

        // A profile with an application URL, added on disk: opening the drop-down reads it, with the web choices.
        var settings = Path.Combine(folder, "src", "Hello", "Properties", "launchSettings.json");
        File.WriteAllText(settings, File.ReadAllText(settings).Replace(
            "\"profiles\": {",
            "\"profiles\": {\n    \"Web\": { \"commandName\": \"Project\", \"applicationUrl\": \"http://localhost:5123\", \"launchBrowser\": true },",
            StringComparison.Ordinal));
        toolbar.LaunchMenuOpening!();
        await ShellFixture.PumpUntil(() => toolbar.LaunchMenu is { Profiles.Count: 4, Web: not null }, describe: () => $"{toolbar.LaunchMenu}");
        var web = toolbar.LaunchMenu!.Web!;
        Assert.Equal("auto", web.Browser);
        Assert.Equal(["auto", Browsers.Chrome, Browsers.Edge, Browsers.Chromium], web.Browsers.Select(b => b.Setting));
        Assert.True(web.ScriptDebugging);
        var browser = view.LaunchMenu.Items.OfType<MenuItem>().Single(i => (i.Header as string)?.StartsWith("Web Browser (", StringComparison.Ordinal) == true);
        Assert.Equal(4, browser.Items.Count);
        var script = view.LaunchMenu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Script Debugging (Enabled)");

        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            f.Window.Width = 1100;
            f.Window.Height = 600;
            var startButton = view.FindControl<SplitButton>("StartButton")!;
            view.LaunchMenu.ShowAt(startButton);
            ShellFixture.Pump();
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            frame!.Save(Path.Combine(snapshots, "launch-menu.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            view.LaunchMenu.Hide();
            ShellFixture.Pump();
        }

        // Choosing sets the settings, and the drop-down follows them.
        var chromium = browser.Items.OfType<MenuItem>().Single(i => (string?)i.CommandParameter == Browsers.Chromium);
        chromium.Command!.Execute(chromium.CommandParameter);
        var disabled = script.Items.OfType<MenuItem>().Single(i => i.CommandParameter is false);
        disabled.Command!.Execute(disabled.CommandParameter);
        await ShellFixture.PumpUntil(() => toolbar.LaunchMenu?.Web is { Browser: Browsers.Chromium, ScriptDebugging: false }, describe: () => $"{toolbar.LaunchMenu?.Web}");
        Assert.Equal(Browsers.Chromium, f.Shell.Workbench.Settings.GetString(Browsers.SettingKey, "auto"));
        Assert.False(f.Shell.Workbench.Settings.GetBoolean(ShellHost.JavaScriptDebugSetting, true));
        Assert.Contains(view.LaunchMenu.Items.OfType<MenuItem>(), i => (i.Header as string) == "Script Debugging (Disabled)");
    }

    [AvaloniaFact]
    public async Task Start_drop_down_holds_only_start_without_a_solution()
    {
        await using var f = ShellFixture.Create();
        ShellFixture.Pump();
        Assert.Null(f.Shell.Toolbar.LaunchMenu);
        var view = f.Window.GetVisualDescendants().OfType<StandardToolbar>().Single();
        Assert.Equal(f.Shell.Toolbar.StartLabel, Assert.IsType<MenuItem>(Assert.Single(view.LaunchMenu.Items)).Header);
    }
}
