using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Menus;

namespace Codalus.Shell.Tests;

/// <summary>Tools → Customize (M23b): menus and toolbars with the user's commands, removals, order and toolbars.</summary>
public sealed class CustomizeTests
{
    private const string Tools = "menuBar/tools";

    [AvaloniaFact]
    public async Task Menus_and_toolbars_take_added_removed_and_moved_commands_and_reset_restores_them()
    {
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var menus = shell.Workbench.Menus;
        IReadOnlyList<string> ToolsMenu() => [.. menus.GetItems(Tools).Select(i => i.Submenu ?? i.Command)];
        var contributed = ToolsMenu();
        Assert.Contains("workbench.openFontsAndColors", contributed);

        // A menu: added at the end, removed, moved up.
        await shell.CustomizeAddAsync(Tools, "editor.action.formatDocument");
        Assert.Equal("editor.action.formatDocument", ToolsMenu()[^1]);
        await shell.CustomizeRemoveAsync(Tools, "workbench.openFontsAndColors");
        Assert.DoesNotContain("workbench.openFontsAndColors", ToolsMenu());
        await shell.CustomizeMoveAsync(Tools, "editor.action.formatDocument", -1);
        Assert.Equal("editor.action.formatDocument", ToolsMenu()[^2]);
        Assert.Contains("\"editor.action.formatDocument\"", shell.Workbench.Settings.GetValue(MenuCustomizations.SettingKey).GetRawText(), StringComparison.Ordinal);

        // Reset: as contributed.
        await shell.CustomizeResetAsync(Tools);
        Assert.Equal(contributed, ToolsMenu());

        // The standard toolbar: a built-in button removed, a command added; a toolbar of one's own.
        await shell.CustomizeRemoveAsync(MenuCustomizations.StandardToolbar, "file.saveAll");
        await shell.CustomizeAddAsync(MenuCustomizations.StandardToolbar, "build.buildSolution");
        await shell.NewToolbarAsync("Mine");
        await shell.CustomizeAddAsync(MenuCustomizations.ToolbarLocation("Mine"), "workbench.action.findInFiles");
        ShellFixture.Pump();
        var toolbar = f.Window.GetVisualDescendants().OfType<StandardToolbar>().Single();
        IReadOnlyList<string?> Buttons() => [.. toolbar.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).Select(b => b.Tag as string)];
        Assert.DoesNotContain("file.saveAll", Buttons());
        Assert.Contains("file.save", Buttons());
        Assert.Contains("build.buildSolution", Buttons());
        Assert.Contains("workbench.action.findInFiles", Buttons());

        // Restored: the removed built-in comes back.
        await shell.CustomizeRemoveAsync(MenuCustomizations.StandardToolbar, "file.saveAll");
        ShellFixture.Pump();
        Assert.Contains("file.saveAll", Buttons());

        // The page lists the toolbars and menus, marks what changed, and its buttons act on the chosen target.
        shell.Run("workbench.customize");
        await ShellFixture.PumpUntil(() => shell.Layout.ActiveDocument is CustomizeDocument);
        var page = ((CustomizeDocument)shell.Layout.ActiveDocument!).ViewModel;
        Assert.Contains(page.Targets, t => t.Title == "Toolbar: Mine");
        Assert.Contains(page.Targets, t => t.Title == "Menu: Tools");
        Assert.Contains(page.Targets, t => t.Title.StartsWith("Menu: Edit › ", StringComparison.Ordinal));
        page.Target = page.Targets.Single(t => t.Location == MenuCustomizations.StandardToolbar);
        Assert.Equal("added", page.Items.Single(i => i.Key == "build.buildSolution").Note);
        page.Selected = page.Items.Single(i => i.Key == "build.buildSolution");
        page.RemoveCommand.Execute(null);
        await ShellFixture.PumpUntil(() => page.Items.All(i => i.Key != "build.buildSolution"));
        ShellFixture.Pump();
        Assert.DoesNotContain("build.buildSolution", Buttons());

        page.Target = page.Targets.Single(t => t.Title == "Toolbar: Mine");
        page.DeleteToolbarCommand.Execute(null);
        await ShellFixture.PumpUntil(() => shell.Customizations.Toolbars.Count == 0);
        ShellFixture.Pump();
        Assert.DoesNotContain("workbench.action.findInFiles", Buttons());
    }
}
