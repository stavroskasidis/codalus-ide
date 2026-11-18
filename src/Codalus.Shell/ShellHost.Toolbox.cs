using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// The Toolbox's settings and context menu (#460, the classic IDE's): Choose Toolbox Items checks which of the active
/// project's items the Toolbox lists and adds an assembly's controls by its path (dropping one adds the reference),
/// Sort Items Alphabetically, Show All, Collapse All and Refresh.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The assemblies added with Choose Toolbox Items (paths).</summary>
    public const string ToolboxAssembliesSetting = "windowsForms.toolbox.assemblies";

    /// <summary>The items unchecked in Choose Toolbox Items (full type names).</summary>
    public const string ToolboxHiddenItemsSetting = "windowsForms.toolbox.hiddenItems";

    /// <summary>The assemblies chosen in Choose Toolbox Items, for the C# host's Toolbox request.</summary>
    internal IReadOnlyList<string> ToolboxAssemblies => [.. Workbench.Settings.GetStrings(ToolboxAssembliesSetting).Select(s => s.Trim()).Where(s => s.Length > 0)];

    private void InitializeToolbox()
    {
        ApplyToolboxHidden();
        PublishToolboxState();
        Toolbox.StateChanged += (_, _) => PublishToolboxState();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(ToolboxHiddenItemsSetting))
            {
                Ui.Post(ApplyToolboxHidden);
            }

            if (e.Affects(ToolboxAssembliesSetting))
            {
                Ui.Post(() => _ = RefreshToolboxAsync());
            }
        };
    }

    private void ApplyToolboxHidden() => Toolbox.Hidden = Workbench.Settings.GetStrings(ToolboxHiddenItemsSetting).ToHashSet(StringComparer.Ordinal);

    /// <summary>The context menu's check entries.</summary>
    private void PublishToolboxState()
    {
        Workbench.Context.Set("toolboxSortedAlphabetically", Toolbox.SortAlphabetically);
        Workbench.Context.Set("toolboxShowAll", Toolbox.ShowAll);
    }

    /// <summary>Refresh: the active designer asks the C# host for its project's items again.</summary>
    public Task RefreshToolboxAsync() => Layout.ActiveDocument is WinFormsDesignerDocument designer ? designer.LoadToolboxAsync() : Task.CompletedTask;

    /// <summary>
    /// Choose Toolbox Items (#460): the active project's items (its own, its packages', the chosen assemblies') in a
    /// checklist by group, checked unless hidden; OK keeps the unchecked ones out of the Toolbox. Browse… adds an
    /// assembly's controls by its path and comes back with them listed (checked); one with none says so. The framework's
    /// controls are always listed.
    /// </summary>
    public async Task ChooseToolboxItemsAsync()
    {
        string? added = null;
        while (Layout.ActiveDocument is WinFormsDesignerDocument designer)
        {
            var items = Toolbox.ProjectItems;
            var hidden = Toolbox.Hidden;
            var choices = new List<DialogChoice>();
            foreach (var group in items.GroupBy(i => i.Tab ?? Localizer.Format("msg.toolbox.projectComponents", "{0} Components", i.Project)))
            {
                choices.Add(new DialogChoice("group:" + group.Key, group.Key, false) { Checkable = false, IsCode = false });
                choices.AddRange(group.Select(i => new DialogChoice(i.Type, i.Name, !hidden.Contains(i.Type), Detail(i)) { Level = 1, IsCode = false, Icon = ToolboxViewModel.IconFor(i.Type, i.IsControl) }));
            }

            var message = added is null
                ? Localizer.Get("msg.toolbox.chooseItems", "Check the items the Toolbox lists for this project. Browse… adds an assembly's controls; dropping one on a form adds the reference to the project. The framework's controls are always listed.")
                : Localizer.Format("msg.toolbox.chooseItemsAdded", "Added {0}. Check the items the Toolbox lists for this project.", Path.GetFileName(added));
            var ok = Localizer.Get("msg.toolbox.ok", "OK");
            var browse = Localizer.Get("msg.toolbox.browse", "Browse…");
            var answer = await Dialogs.ShowAsync(new DialogRequest(Localizer.Get("msg.toolbox.chooseItemsTitle", "Choose Toolbox Items"), message, [ok, browse, Localizer.Get("msg.toolbox.cancel", "Cancel")])
            {
                Choices = choices,
                Icon = "toolbox",
                DefaultButton = ok,
            }).ConfigureAwait(true);
            if (answer.Button == browse)
            {
                added = await BrowseToolboxAssemblyAsync(designer).ConfigureAwait(true) ?? added;
                continue;
            }

            if (answer.Button == ok)
            {
                var listed = items.Select(i => i.Type).ToHashSet(StringComparer.Ordinal);
                var newHidden = hidden.Where(t => !listed.Contains(t)).Concat(items.Select(i => i.Type).Where(t => !answer.Checked.Contains(t))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                await Workbench.Settings.SetAsync(ToolboxHiddenItemsSetting, JsonSerializer.SerializeToElement(newHidden, Docking.ShellJsonContext.Default.StringArray), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }

            return;
        }

        static string Detail(LanguageServices.Protocol.WinFormsToolboxItem item) =>
            item.Assembly is { } assembly ? $"{Namespace(item.Type)} ({Path.GetFileName(assembly)})" : Namespace(item.Type);

        static string Namespace(string type) => type.LastIndexOf('.') is var dot and > 0 ? type[..dot] : string.Empty;
    }

    /// <summary>Browse…: an assembly picked and added to the chosen ones, the Toolbox listed again; its path, or null (cancelled, or no Toolbox items, which a notification says).</summary>
    private async Task<string?> BrowseToolboxAssemblyAsync(WinFormsDesignerDocument designer)
    {
        if (Window is null || await Window.PickFileAsync(Localizer.Get("msg.toolbox.chooseItemsTitle", "Choose Toolbox Items"), Localizer.Get("msg.toolbox.assemblies", "Assemblies (.dll)"), ["*.dll"]).ConfigureAwait(true) is not { } path)
        {
            return null;
        }

        path = Path.GetFullPath(path);
        var chosen = ToolboxAssemblies.ToList();
        if (!chosen.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            chosen.Add(path);
            await Workbench.Settings.SetAsync(ToolboxAssembliesSetting, JsonSerializer.SerializeToElement(chosen.ToArray(), Docking.ShellJsonContext.Default.StringArray), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }

        await designer.LoadToolboxAsync().ConfigureAwait(true);
        if (!Toolbox.ProjectItems.Any(i => string.Equals(i.Assembly, path, StringComparison.OrdinalIgnoreCase))
            && !Toolbox.ProjectItems.Any(i => i.Assembly is null && string.Equals(i.Project, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)))
        {
            Workbench.Notifications.Info(Localizer.Format("msg.toolbox.noItemsInAssembly", "{0} has no Toolbox items: no public controls or components with a public parameterless constructor.", Path.GetFileName(path)));
            return null;
        }

        return path;
    }
}
