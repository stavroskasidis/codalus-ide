using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Feedback;

namespace Codalus.Shell.Tests;

/// <summary>
/// Help → About Codalus IDE, headless, with the browser faked: the command is the Help menu's last entry, in a group of
/// its own; it opens a modal window over the main one (not a notification) with the version and what the IDE runs on;
/// Copy Info copies them; the links open in the browser; choosing About again activates the open window; OK, Enter and
/// Escape close it. OK comes first on Windows and Linux and last on macOS.
/// </summary>
public sealed class AboutWindowTests
{
    private static T Named<T>(Window window, string name)
        where T : Control => window.FindControl<T>(name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}");

    private static async Task<AboutWindow> OpenAsync(ShellFixture f)
    {
        await f.RunAsync("help.about");
        await ShellFixture.PumpUntil(() => f.Shell.AboutWindowView is { IsVisible: true });
        return Assert.IsType<AboutWindow>(f.Shell.AboutWindowView);
    }

    [AvaloniaFact]
    public async Task About_is_the_last_entry_of_the_Help_menu_in_a_group_of_its_own()
    {
        await using var f = ShellFixture.Create();

        var help = f.Shell.Workbench.Menus.GetItems("menuBar/help");
        Assert.Equal("help.about", help[^1].Command);
        Assert.NotEqual(help[^2].Group, help[^1].Group);
        Assert.Equal("About Codalus IDE", help[^1].Title);
    }

    [AvaloniaFact]
    public async Task About_opens_a_modal_window_with_the_version_and_the_runtime_not_a_notification()
    {
        await using var f = ShellFixture.Create();
        var before = f.Shell.Workbench.Notifications.Items.Count;

        var window = await OpenAsync(f);

        Assert.Equal("About Codalus IDE", window.Title);
        Assert.Same(f.Window, window.Owner);
        Assert.Equal(before, f.Shell.Workbench.Notifications.Items.Count);
        Assert.Equal(ShellInfo.ProductName, Named<TextBlock>(window, "ProductText").Text);
        Assert.Equal($"Version {ShellInfo.Version}", Named<TextBlock>(window, "VersionText").Text);
        var model = Assert.IsType<AboutViewModel>(window.DataContext);
        var environment = IssueEnvironment.Current(ShellInfo.Version);
        Assert.Equal([".NET runtime", "Operating system", "Architecture"], model.Details.Select(d => d.Label));
        Assert.Equal([environment.Runtime, environment.OsDescription, environment.Architecture], model.Details.Select(d => d.Value));
        Assert.Equal(3, Named<ItemsControl>(window, "DetailList").ItemCount);

        window.Close();
        await ShellFixture.PumpUntil(() => f.Shell.AboutWindowView is null);
    }

    [AvaloniaFact]
    public async Task Copy_Info_copies_the_version_and_the_runtime()
    {
        await using var f = ShellFixture.Create();
        var window = await OpenAsync(f);
        var model = (AboutViewModel)window.DataContext!;

        await model.CopyAsync();

        var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(f.Window.Clipboard!);
        Assert.Equal(model.InfoText, text);
        Assert.StartsWith($"Codalus IDE {ShellInfo.Version}\n.NET runtime: ", text, StringComparison.Ordinal);
        Assert.Contains("\nOperating system: ", text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_links_open_in_the_browser()
    {
        await using var f = ShellFixture.Create();
        var opened = new List<Uri>();
        f.Shell.OpenExternalUrl = opened.Add;
        var window = await OpenAsync(f);

        foreach (var link in new[] { "WebsiteLink", "SourceLink", "LicenseLink", "NoticesLink" })
        {
            var button = Named<Button>(window, link);
            button.Command!.Execute(button.CommandParameter);
        }

        Assert.Equal(
            [AboutViewModel.WebsiteUrl, AboutViewModel.RepositoryUrl, AboutViewModel.LicenseUrl, AboutViewModel.NoticesUrl],
            opened.Select(u => u.ToString()));
        Assert.Equal("https://github.com/stavroskasidis/codalus-ide", AboutViewModel.RepositoryUrl);
        window.Close();
    }

    [AvaloniaFact]
    public async Task About_again_keeps_one_window_and_OK_Enter_and_Escape_close_it()
    {
        await using var f = ShellFixture.Create();
        var window = await OpenAsync(f);

        f.Shell.ShowAbout();
        ShellFixture.Pump();
        Assert.Same(window, f.Shell.AboutWindowView);
        Assert.Single(f.Window.OwnedWindows.OfType<AboutWindow>());

        var ok = Named<Button>(window, "OkButton");
        ok.Command!.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.AboutWindowView is null);
        Assert.False(window.IsVisible);

        window = await OpenAsync(f);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await ShellFixture.PumpUntil(() => f.Shell.AboutWindowView is null);

        window = await OpenAsync(f);
        Assert.True(Named<Button>(window, "OkButton").IsDefault);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await ShellFixture.PumpUntil(() => f.Shell.AboutWindowView is null);
    }

    [AvaloniaFact]
    public void OK_comes_first_on_Windows_and_Linux_and_last_on_macOS()
    {
        var model = new AboutViewModel(IssueEnvironment.Current("1.2.3"), _ => Task.CompletedTask, _ => { });

        string[] Order(bool primaryLast)
        {
            var window = new AboutWindow(primaryLast) { DataContext = model };
            return [.. Named<StackPanel>(window, "FooterButtons").Children.Select(c => c.Name ?? string.Empty)];
        }

        Assert.Equal(["OkButton", "CopyButton"], Order(primaryLast: false));
        Assert.Equal(["CopyButton", "OkButton"], Order(primaryLast: true));
    }
}
