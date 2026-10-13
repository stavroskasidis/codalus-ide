using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Tests;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>The Plugin Gallery window (M22c, ADR 0060) against a mock NuGet feed.</summary>
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

        f.Shell.Layout.ShowToolWindow(ShellHost.PluginGalleryWindow);
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
        Assert.Contains("Installed from a gallery", Text(), StringComparison.Ordinal);

        // Uninstall: at the next start.
        await f.Shell.OnPluginGalleryEventAsync("installed", "select", null, 0);
        var uninstall = f.Shell.OnPluginGalleryEventAsync("uninstall", "click", null, null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 30);
        f.Shell.Dialogs.Accept();
        await uninstall;
        Assert.Contains("Waiting for a restart", Text(), StringComparison.Ordinal);
        Assert.Contains("uninstall", Text(), StringComparison.Ordinal);
    }
}
