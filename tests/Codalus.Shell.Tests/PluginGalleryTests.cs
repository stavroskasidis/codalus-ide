using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Tests;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>The Plugins page's gallery (M22c, ADR 0060) against a mock NuGet feed, and its Installed tab's uninstall.</summary>
public sealed class PluginGalleryTests
{
    [AvaloniaFact]
    public async Task The_gallery_searches_shows_permissions_and_installs_a_plugin_that_loads_at_once()
    {
        await using var feed = new MockNuGetFeed();
        feed.AddPlugin("contoso.fetcher", "1.0.0", description: "Fetches things.", manifestJson: """
            {
              "id": "contoso.fetcher", "version": "1.0.0", "displayName": "Fetcher", "publisher": "contoso",
              "engines": { "codalus": "^1.0.0" },
              "permissions": ["network"],
              "contributes": { "settings": { "title": "Fetcher", "properties": { "fetcher.url": { "type": "string", "default": "https://example.invalid" } } } }
            }
            """);
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(ShellHost.PluginSourcesSetting, JsonSerializer.SerializeToElement(new[] { feed.Source }), SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.PluginSignatureSetting, "none", SettingScope.User, CancellationToken.None);

        string Text()
        {
            static IEnumerable<string> Walk(Codalus.Plugins.Protocol.ViewNodeDto node) =>
                new[] { node.Text, node.Value }.Where(t => t is not null).Select(t => t!).Concat((node.Rows ?? []).SelectMany(r => r)).Concat((node.Children ?? []).SelectMany(Walk));
            return string.Join(" | ", Walk(f.Shell.GalleryRoot()));
        }

        f.Shell.ShowPluginsPage(ViewModels.PluginsPageViewModel.BrowseTab);
        ShellFixture.Pump();
        var page = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(f.Window).OfType<Views.PluginsPageView>().Single();
        Assert.Equal(ViewModels.PluginsPageViewModel.BrowseTab, page.Tabs.SelectedIndex);
        Assert.IsType<ShellHost.PluginGalleryView>(page.GalleryView);
        await f.Shell.OnPluginGalleryEventAsync("query", "change", "fetch", null);
        Assert.Contains("contoso.fetcher", Text(), StringComparison.Ordinal);
        await f.Shell.OnPluginGalleryEventAsync("results", "select", null, 0);
        Assert.Contains("Fetches things.", Text(), StringComparison.Ordinal);

        // Install: the permissions come first, in a dialog.
        var install = f.Shell.OnPluginGalleryEventAsync("install", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 30);
        Assert.Contains("the network", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Contains("aren't enforced", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        f.Shell.Dialogs.Accept();
        await install;

        var plugin = Assert.Single(f.Shell.Plugins.Plugins, p => p.Id == "contoso.fetcher");
        Assert.Equal(Workbench.Plugins.PluginState.Ready, plugin.State);
        Assert.Contains(settings.Descriptors, d => d.Key == "fetcher.url"); // its contributions apply at once

        // Uninstall, on its row of the Installed tab (built-in plugins have none): at the next start.
        f.Shell.ShowPluginsPage(ViewModels.PluginsPageViewModel.InstalledTab);
        ShellFixture.Pump();
        Assert.Equal(ViewModels.PluginsPageViewModel.InstalledTab, page.Tabs.SelectedIndex);
        Assert.All(f.Shell.PluginList.Plugins.Where(r => r.Id != "contoso.fetcher"), r => Assert.False(r.CanUninstall, $"{r.Id} can be uninstalled"));
        var row = Assert.Single(f.Shell.PluginList.Plugins, r => r.Id == "contoso.fetcher");
        Assert.True(row.CanUninstall);
        row.UninstallCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 30);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => f.Shell.PluginList.Plugins.Single(r => r.Id == "contoso.fetcher").State.Contains("uninstalled at the next start", StringComparison.Ordinal));
        Assert.False(f.Shell.PluginList.Plugins.Single(r => r.Id == "contoso.fetcher").CanUninstall);
        Assert.Contains("Waiting for a restart", Text(), StringComparison.Ordinal);
        Assert.Contains("uninstall", Text(), StringComparison.Ordinal);
    }
}
