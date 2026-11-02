using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Views;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Codalus.Shell;

/// <summary>
/// Document tabs colored by project or by file extension (M34d, <c>workbench.editor.colorTabsBy</c>): each project of
/// the solution, in name order, or each extension takes one of the eight <c>Shell.TabColor.N</c> tokens, and its files'
/// tabs show it as a stripe. Before <c>colorTabsBy</c>, <c>workbench.editor.colorTabsByProject</c> turned the project's
/// colors on; it still does while <c>colorTabsBy</c> isn't set.
/// Pinned tabs on a row of their own (<c>workbench.editor.pinnedTabsOnSeparateRow</c>, on by default as in the classic
/// IDE) and tabs in multiple rows (<c>workbench.editor.tabsInMultipleRows</c>): the settings go to the application's
/// resources <c>Shell.PinnedTabsOnSeparateRow</c> and <c>Shell.TabsInMultipleRows</c>, which every group's
/// <see cref="DocumentTabPanel"/> takes, the floating windows' too. The mouse wheel over a group's tabs turns multiple
/// rows on and off (<c>workbench.editor.toggleMultipleRowsWithMouseWheel</c>).
/// Each group's tab list (<see cref="TabList"/>) and tab options (the menu <see cref="BuiltIns.TabOptionsMenu"/>) are
/// the buttons beside its tabs (<see cref="TabStripButtons"/>); the tabs go on the top, left or right of every group
/// (<c>workbench.editor.tabPlacement</c>).
/// </summary>
public sealed partial class ShellHost
{
    public const string ColorTabsByProjectSetting = "workbench.editor.colorTabsByProject";

    public const string ColorTabsBySetting = "workbench.editor.colorTabsBy";

    public const string PinnedTabsOnSeparateRowSetting = "workbench.editor.pinnedTabsOnSeparateRow";

    public const string TabPlacementSetting = "workbench.editor.tabPlacement";

    public const string TabsInMultipleRowsSetting = "workbench.editor.tabsInMultipleRows";

    public const string MultipleRowsWithMouseWheelSetting = "workbench.editor.toggleMultipleRowsWithMouseWheel";

    private const int TabColors = 8;

    /// <summary>The mouse wheel's last write of <see cref="TabsInMultipleRowsSetting"/> (the UI thread's), and its value.</summary>
    private Task? _tabRowsWrite;

    private bool _tabRowsWritten;

    private static readonly StringComparison s_pathComparison =
        SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The open tab list, while it shows (tests).</summary>
    internal TabList? OpenTabList { get; private set; }

    private void InitializeTabs()
    {
        Layout.ActiveDocumentChanged += (_, _) => UpdateTabColors();
        Solutions.SolutionChanged += (_, _) => Ui.Post(UpdateTabColors);
        var tabColors = Debouncer(UpdateTabColors, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        Solutions.ProjectChanged += (_, _) => tabColors.Request();
        Theme.Changed += (_, _) => UpdateTabColors();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(ColorTabsByProjectSetting) || e.Affects(ColorTabsBySetting))
            {
                Ui.Post(UpdateTabColors);
            }

            if (e.Affects(PinnedTabsOnSeparateRowSetting) || e.Affects(TabsInMultipleRowsSetting) || e.Affects(TabPlacementSetting))
            {
                Ui.Post(UpdateTabLayout);
            }
        };
        Layout.Factory.ShowTabList = ShowTabList;
        Layout.Factory.ShowTabOptions = ShowTabOptions;
        Layout.Factory.TabStripWheel = OnTabStripWheel;
        UpdateTabLayout();
    }

    /// <summary>How tabs are colored: <c>none</c>, <c>project</c> or <c>fileExtension</c>.</summary>
    public string TabColorBy =>
        Workbench.Settings.IsModified(ColorTabsBySetting) || !Workbench.Settings.GetBoolean(ColorTabsByProjectSetting, false)
            ? Workbench.Settings.GetString(ColorTabsBySetting, "none")
            : "project";

    /// <summary>Where the tabs go: <c>top</c>, <c>left</c> or <c>right</c>.</summary>
    public string TabPlacement => Workbench.Settings.GetString(TabPlacementSetting, "top");

    private void UpdateTabLayout()
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.Resources["Shell.PinnedTabsOnSeparateRow"] = Workbench.Settings.GetBoolean(PinnedTabsOnSeparateRowSetting, true);
            app.Resources["Shell.TabsInMultipleRows"] = Workbench.Settings.GetBoolean(TabsInMultipleRowsSetting, false);
        }

        Layout.SetTabsLayout(TabPlacement switch
        {
            "left" => DocumentTabLayout.Left,
            "right" => DocumentTabLayout.Right,
            _ => DocumentTabLayout.Top,
        });
    }

    /// <summary>Sets every open document's <see cref="ShellDocument.TabAccent"/> from its project or extension, or clears them.</summary>
    public void UpdateTabColors()
    {
        var by = TabColorBy;
        var projects = by == "project"
            ? Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select((p, i) => (Folder: Path.GetDirectoryName(p.Path) + Path.DirectorySeparatorChar, Index: i)).ToList()
            : [];
        foreach (var document in Layout.Documents)
        {
            int? index = null;
            if (document.FilePath is { } path)
            {
                if (by == "project")
                {
                    // The innermost project folder that holds the file.
                    var owner = projects.Where(p => path.StartsWith(p.Folder, s_pathComparison)).OrderByDescending(p => p.Folder.Length).FirstOrDefault();
                    index = owner.Folder is null ? null : owner.Index;
                }
                else if (by == "fileExtension" && Path.GetExtension(path) is { Length: > 0 } extension)
                {
                    index = ExtensionColor(extension);
                }
            }

            IBrush? brush = null;
            if (index is { } at
                && Avalonia.Application.Current?.TryGetResource($"Shell.TabColor.{at % TabColors + 1}", Avalonia.Application.Current.ActualThemeVariant, out var value) == true)
            {
                brush = value as IBrush;
            }

            document.TabAccent = brush;
        }
    }

    /// <summary>The common kinds of file, each with a color of its own (a kind's extensions share one).</summary>
    private static readonly Dictionary<string, int> s_extensionColors = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = 0, [".vb"] = 0, [".fs"] = 0, [".fsx"] = 0,
        [".razor"] = 1, [".cshtml"] = 1, [".html"] = 1, [".htm"] = 1,
        [".xaml"] = 2, [".axaml"] = 2,
        [".json"] = 3, [".jsonc"] = 3, [".yml"] = 3, [".yaml"] = 3,
        [".csproj"] = 4, [".fsproj"] = 4, [".vbproj"] = 4, [".props"] = 4, [".targets"] = 4, [".xml"] = 4, [".config"] = 4, [".resx"] = 4,
        [".js"] = 5, [".ts"] = 5, [".jsx"] = 5, [".tsx"] = 5,
        [".css"] = 6, [".scss"] = 6, [".less"] = 6,
        [".md"] = 7, [".txt"] = 7, [".sql"] = 7,
    };

    /// <summary>
    /// An extension's color (0–7): the same in every session and whatever else is open, so a kind of file is always one
    /// color. The common kinds have their own; any other takes a hash of its lowercase name.
    /// </summary>
    internal static int ExtensionColor(string extension)
    {
        if (s_extensionColors.TryGetValue(extension, out var color))
        {
            return color;
        }

        var hash = 2166136261u;
        foreach (var c in extension.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % TabColors);
    }

    /// <summary>A group's tab list below its <c>…</c> button: every tab by name; choosing one opens it.</summary>
    internal void ShowTabList(IDocumentDock dock, Control anchor)
    {
        var list = new TabList();
        var flyout = new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        flyout.FlyoutPresenterClasses.Add("tabList");
        list.Show(TabListEntry.For(dock));
        list.Activate += (_, dockable) =>
        {
            flyout.Hide();
            ActivateTab(dockable);
        };
        list.Close += (_, dockable) =>
        {
            Layout.Factory.CloseDockable(dockable);
            // A document with unsaved changes asks first and stays: the list shows what is still open.
            Ui.Post(() =>
            {
                var entries = TabListEntry.For(dock);
                if (entries.Count == 0)
                {
                    flyout.Hide();
                }
                else
                {
                    list.Show(entries);
                }
            });
        };
        flyout.Opened += (_, _) => Ui.Post(() => (list.List.ContainerFromItem(list.List.SelectedItem!) as Control ?? list.List).Focus(Avalonia.Input.NavigationMethod.Directional));
        flyout.Closed += (_, _) => OpenTabList = null;
        OpenTabList = list;
        flyout.ShowAt(anchor);
    }

    private void ActivateTab(IDockable dockable)
    {
        if (dockable is ShellDocument document)
        {
            Layout.OpenDocument(document, focus: true);
        }
        else if (dockable is ToolWindowDockable tool)
        {
            Layout.ShowToolWindow(tool.Id);
        }
    }

    /// <summary>The tab options menu at a group's gear, its entries checked by the settings.</summary>
    internal void ShowTabOptions(Control anchor)
    {
        if (TabOptionsMenu() is { } menu)
        {
            menu.Placement = PlacementMode.BottomEdgeAlignedRight;
            menu.PlacementTarget = anchor;
            menu.Open(anchor);
        }
    }

    internal ContextMenu? TabOptionsMenu() =>
        MenuBuilder.Context(this, BuiltIns.TabOptionsMenu, new MenuBuilder.MenuShape(c => CommandFor(c)) { Scope = TabOptionsScope() });

    /// <summary>The keys the tab options menu checks its entries by, set for that menu only.</summary>
    internal Workbench.Context.IContextKeyReader TabOptionsScope()
    {
        var menuContext = new MenuContext();
        menuContext.Set("tabPlacement", TabPlacement);
        menuContext.Set("tabColorBy", TabColorBy);
        menuContext.Set("tabsInMultipleRows", Workbench.Settings.GetBoolean(TabsInMultipleRowsSetting, false));
        menuContext.Set("tabsMultipleRowsWithMouseWheel", Workbench.Settings.GetBoolean(MultipleRowsWithMouseWheelSetting, true));
        return Workbench.Context.With(menuContext.Keys);
    }

    /// <summary>Sets a tab option for the user (the tab options menu's entries).</summary>
    internal Task SetTabOptionAsync(string key, string value) =>
        Workbench.Settings.SetAsync(key, value, SettingScope.User, CancellationToken.None);

    internal Task SetTabOptionAsync(string key, bool value) =>
        Workbench.Settings.SetAsync(key, value, SettingScope.User, CancellationToken.None);

    /// <summary>
    /// The wheel over a row of tabs, while turned on: down shows the tabs in multiple rows, up in one, as the classic IDE
    /// does. The setting changes for every group.
    /// </summary>
    private bool OnTabStripWheel(double delta)
    {
        if (!Workbench.Settings.GetBoolean(MultipleRowsWithMouseWheelSetting, true))
        {
            return false;
        }

        // While the wheel's last write is on its way, against what it wrote: the setting changes only once the write has
        // landed, and a wheel up before a wheel down's write landed found one row, wrote nothing, and the rows came on.
        var multiple = delta < 0;
        var rows = _tabRowsWrite is { IsCompleted: false } ? _tabRowsWritten : Workbench.Settings.GetBoolean(TabsInMultipleRowsSetting, false);
        if (rows != multiple)
        {
            _tabRowsWritten = multiple;
            _tabRowsWrite = SetTabOptionAsync(TabsInMultipleRowsSetting, multiple);
        }

        return true;
    }
}
