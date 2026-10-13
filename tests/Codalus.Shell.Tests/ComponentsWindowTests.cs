using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>The Components window (M22d): every on-demand component listed, and a layout that isn't one refused.</summary>
public sealed class ComponentsWindowTests
{
    [AvaloniaFact]
    public async Task The_window_lists_the_on_demand_components_and_refuses_a_folder_that_isnt_a_layout()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Layout.ShowToolWindow(ShellHost.ComponentsWindow);
        await f.Shell.RefreshComponentsAsync();

        var table = f.Shell.ComponentsRoot().Children!.Single(c => c.Id == "components");
        var names = table.Rows!.Select(r => r[0]).ToList();
        Assert.Contains("Node.js", names);
        Assert.Contains("vscode-js-debug", names);
        Assert.Contains("netcoredbg for remote targets", names);
        Assert.Contains("Converted icon themes", names);
        Assert.All(table.Rows!, r => Assert.NotEmpty(r[4])); // every one has its licence

        await f.Shell.OnComponentsEventAsync("components", "select", 0);
        Assert.Contains(f.Shell.ComponentsRoot().Children!, c => c.Kind == "stack" && c.Children!.Any(b => b.Id == "remove"));

        var notALayout = Directory.CreateTempSubdirectory("codalus-not-a-layout-").FullName;
        try
        {
            await f.Shell.OnComponentsEventAsync("import", "click", null, notALayout);
            Assert.Contains(f.Shell.ComponentsRoot().Children!, c => c.Text?.Contains("isn't an offline layout", StringComparison.Ordinal) == true);
        }
        finally
        {
            Directory.Delete(notALayout, recursive: true);
        }
    }
}
