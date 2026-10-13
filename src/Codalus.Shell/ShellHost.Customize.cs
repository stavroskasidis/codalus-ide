using Codalus.Shell.ViewModels;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// Customizing menus and toolbars (M23b, Tools → Customize): add any command to a menu, a context menu or the standard
/// toolbar, remove items, reorder them, and create toolbars of one's own. Everything is the setting
/// <see cref="MenuCustomizations.SettingKey"/> (so it belongs to the profile and syncs); the menu registry applies it to
/// menus and context menus, and the standard toolbar shows the toolbars.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The standard toolbar's built-in buttons, by command (the ones Customize can remove).</summary>
    public static IReadOnlyList<string> StandardToolbarCommands { get; } =
    [
        "workbench.navigateBack", "workbench.navigateForward", "workbench.openSolution", "file.save", "file.saveAll", "edit.undo", "edit.redo",
        "debug.start", "run.startWithoutDebugging", "debug.pause", "debug.stop", "debug.restart", "debug.stepInto", "debug.stepOver", "debug.stepOut",
    ];

    public MenuCustomizations Customizations => Workbench.Menus.Customizations;

    private void InitializeCustomizations()
    {
        ApplyCustomizations();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(MenuCustomizations.SettingKey))
            {
                Infrastructure.Ui.Post(ApplyCustomizations);
            }
        };
    }

    private void ApplyCustomizations()
    {
        var customizations = MenuCustomizations.Parse(Workbench.Settings.GetValue(MenuCustomizations.SettingKey));
        Workbench.Menus.Customizations = customizations;
        if (Toolbar is null)
        {
            return;
        }

        ToolbarCommandItem Item(MenuItem item) => new(item.Command, item.Title, CommandFor(item.Command));
        var extra = new List<(string?, IReadOnlyList<ToolbarCommandItem>)>
        {
            (null, [.. Workbench.Menus.GetAllItems(MenuCustomizations.StandardToolbar).Select(Item)]),
        };
        extra.AddRange(customizations.Toolbars.Select(t => ((string?)t.Name, (IReadOnlyList<ToolbarCommandItem>)[.. Workbench.Menus.GetAllItems(MenuCustomizations.ToolbarLocation(t.Name)).Select(Item)])));
        Toolbar.ApplyCustomization(new HashSet<string>(customizations.For(MenuCustomizations.StandardToolbar).Remove, StringComparer.Ordinal), extra);
    }

    private async Task SaveCustomizationsAsync(MenuCustomizations customizations)
    {
        await Workbench.Settings.SetAsync(MenuCustomizations.SettingKey, customizations.ToJson(), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        ApplyCustomizations();
    }

    /// <summary>The keys of a location's items as shown now: commands, or submenu locations.</summary>
    public IReadOnlyList<string> CustomizeKeys(string location)
    {
        if (location == MenuCustomizations.StandardToolbar)
        {
            return [.. StandardToolbarCommands, .. Customizations.For(location).Add];
        }

        if (location.StartsWith(MenuCustomizations.ToolbarPrefix, StringComparison.Ordinal))
        {
            var name = location[MenuCustomizations.ToolbarPrefix.Length..];
            return [.. Customizations.Toolbars.Where(t => t.Name == name).SelectMany(t => t.Commands)];
        }

        return [.. Workbench.Menus.GetAllItems(location).Select(i => i.Submenu ?? i.Command)];
    }

    public Task CustomizeAddAsync(string location, string command)
    {
        var customizations = Customizations;
        if (UserToolbar(location) is { } name)
        {
            return SaveCustomizationsAsync(customizations with
            {
                Toolbars = [.. customizations.Toolbars.Select(t => t.Name == name && !t.Commands.Contains(command) ? (t.Name, (IReadOnlyList<string>)[.. t.Commands, command]) : t)],
            });
        }

        var current = customizations.For(location);
        var removedHere = current.Remove.Contains(command);
        return SaveCustomizationsAsync(customizations.With(location, current with
        {
            // A removed item comes back where it was; anything else is added at the end.
            Remove = [.. current.Remove.Where(r => r != command)],
            Add = removedHere || current.Add.Contains(command) ? current.Add : [.. current.Add, command],
        }));
    }

    /// <summary>Removes an item; on the standard toolbar a removed built-in button comes back.</summary>
    public Task CustomizeRemoveAsync(string location, string key)
    {
        var customizations = Customizations;
        if (UserToolbar(location) is { } name)
        {
            return SaveCustomizationsAsync(customizations with
            {
                Toolbars = [.. customizations.Toolbars.Select(t => t.Name == name ? (t.Name, (IReadOnlyList<string>)[.. t.Commands.Where(c => c != key)]) : t)],
            });
        }

        var current = customizations.For(location);
        if (current.Add.Contains(key))
        {
            return SaveCustomizationsAsync(customizations.With(location, current with { Add = [.. current.Add.Where(a => a != key)] }));
        }

        return SaveCustomizationsAsync(customizations.With(location, current with
        {
            Remove = current.Remove.Contains(key) ? [.. current.Remove.Where(r => r != key)] : [.. current.Remove, key],
        }));
    }

    /// <summary>Moves an item up (-1) or down (+1) among its location's items.</summary>
    public Task CustomizeMoveAsync(string location, string key, int delta)
    {
        var customizations = Customizations;
        static List<string> Moved(IEnumerable<string> keys, string key, int delta)
        {
            var list = keys.ToList();
            var index = list.IndexOf(key);
            var target = index + delta;
            if (index >= 0 && target >= 0 && target < list.Count)
            {
                (list[index], list[target]) = (list[target], list[index]);
            }

            return list;
        }

        if (UserToolbar(location) is { } name)
        {
            return SaveCustomizationsAsync(customizations with
            {
                Toolbars = [.. customizations.Toolbars.Select(t => t.Name == name ? (t.Name, (IReadOnlyList<string>)Moved(t.Commands, key, delta)) : t)],
            });
        }

        var current = customizations.For(location);
        if (location == MenuCustomizations.StandardToolbar)
        {
            // The built-in buttons keep their places; the added commands move among themselves.
            return SaveCustomizationsAsync(customizations.With(location, current with { Add = Moved(current.Add, key, delta) }));
        }

        return SaveCustomizationsAsync(customizations.With(location, current with { Order = Moved(CustomizeKeys(location), key, delta) }));
    }

    /// <summary>Back to the location as contributed.</summary>
    public Task CustomizeResetAsync(string location)
    {
        var customizations = Customizations;
        var locations = customizations.Locations.Where(l => l.Key != location).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        return SaveCustomizationsAsync(customizations with { Locations = locations });
    }

    public Task NewToolbarAsync(string name)
    {
        var customizations = Customizations;
        return customizations.Toolbars.Any(t => t.Name == name)
            ? Task.CompletedTask
            : SaveCustomizationsAsync(customizations with { Toolbars = [.. customizations.Toolbars, (name, [])] });
    }

    public Task DeleteToolbarAsync(string name)
    {
        var customizations = Customizations;
        return SaveCustomizationsAsync(customizations with { Toolbars = [.. customizations.Toolbars.Where(t => t.Name != name)] });
    }

    public void OpenCustomize() =>
        Layout.OpenDocument(Layout.FindDocument<CustomizeDocument>(CustomizeDocument.DocumentId) ?? new CustomizeDocument(new CustomizeViewModel(this)));

    private static string? UserToolbar(string location) =>
        location.StartsWith(MenuCustomizations.ToolbarPrefix, StringComparison.Ordinal) ? location[MenuCustomizations.ToolbarPrefix.Length..] : null;
}
