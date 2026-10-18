using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Codalus.Diagnostics.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Diagnostics;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h: the control set applied to the views (docs/design/controls/ToolWindowToolbars.dc.html and Options.dc.html). Every
/// tool window's toolbar holds icon buttons or icon and text buttons, never plain text ones, and an icon-only button has
/// a name and a tooltip; Counters' process id box and process list are one editable combo box; the Containers window's
/// empty state has its icon and Refresh; Options has its category icons, a page header, User or Workspace as a
/// segmented control, modified settings as chips and booleans as one check box.
/// </summary>
public class ControlSetViewsTests
{
    /// <summary>The views other work redesigns (Find Results, HTTP Response, AI Chat): not part of this round.</summary>
    private static readonly HashSet<string> s_redesignedElsewhere = new(StringComparer.Ordinal)
    {
        FindResultsViewModel.ToolWindowId,
        HttpResponseViewModel.ToolWindowId,
        AiChatViewModel.ToolWindowId,
    };

    private static Window Show(Control content, double width = 900, double height = 420)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        ShellFixture.Pump();
        ShellFixture.Pump();
        return window;
    }

    private static bool HasIcon(ContentControl control) =>
        ButtonIcon.GetKind(control) is { Length: > 0 }
        || control.Content is Icon
        || (control.Content is Panel panel && panel.GetSelfAndLogicalDescendants().OfType<Icon>().Any());

    /// <summary>Whether the button shows text next to (or instead of) its icon.</summary>
    private static bool ShowsText(ContentControl control) =>
        control.Content is string { Length: > 0 }
        || (control.Content is Panel panel && panel.GetSelfAndLogicalDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text)));

    /// <summary>Problems of the buttons in the toolbars under <paramref name="root"/>.</summary>
    internal static IEnumerable<string> ToolbarProblems(Visual root)
    {
        foreach (var toolbar in root.GetVisualDescendants().OfType<ToolbarPanel>())
        {
            foreach (var button in toolbar.Children.OfType<ContentControl>().Where(c => c is Button or ToggleButton or SplitButton && !c.Classes.Contains("overflow")))
            {
                var label = AutomationProperties.GetName(button) ?? button.Content?.ToString() ?? button.Name ?? button.GetType().Name;
                if (!HasIcon(button))
                {
                    yield return $"'{label}' is a text button in a toolbar (an icon, or an icon and its text)";
                }
                else if (!ShowsText(button) && !button.Classes.Contains("iconText"))
                {
                    if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                    {
                        yield return $"an icon button ({ButtonIcon.GetKind(button)}) without a name";
                    }

                    if (ToolTip.GetTip(button) is null)
                    {
                        yield return $"'{label}' has no tooltip";
                    }
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task Every_tool_windows_toolbar_has_icon_buttons_with_names_and_tooltips()
    {
        await using var f = ShellFixture.Create();
        var problems = new List<string>();
        var checkedWindows = 0;
        foreach (var descriptor in f.Shell.Workbench.ToolWindows.All.ToList())
        {
            if (s_redesignedElsewhere.Contains(descriptor.Id) || f.Shell.Layout.ShowToolWindow(descriptor.Id, focus: false) is not { } tool)
            {
                continue;
            }

            ShellFixture.Pump();
            var window = Show(new ContentControl { Content = tool }, 1200, 480);
            problems.AddRange(ToolbarProblems(window).Select(p => $"{descriptor.Id}: {p}"));
            checkedWindows++;
            window.Close();
            ShellFixture.Pump();
        }

        Assert.True(checkedWindows > 20, $"{checkedWindows} tool windows");
        Assert.True(problems.Count == 0, "Toolbar buttons:\n" + string.Join('\n', problems));
    }

    [AvaloniaFact]
    public void Counters_process_id_box_and_process_list_are_one_editable_combo_box()
    {
        var diagnostics = new DiagnosticsService(() => null);
        var vm = new CountersViewModel(diagnostics, (_, _, _, _) => Task.FromResult<string?>(null));
        vm.Processes.Add(new ProcessRowViewModel(new DotnetProcess(48213, "WebApi", "dotnet WebApi.dll")));
        vm.Processes.Add(new ProcessRowViewModel(new DotnetProcess(512, "Worker", null)));
        var view = new CountersView { DataContext = vm };
        var window = Show(view);
        try
        {
            var toolbar = view.GetVisualDescendants().OfType<ToolbarPanel>().Single();
            var box = Assert.Single(toolbar.Children.OfType<ComboBox>());
            Assert.True(box.IsEditable);
            Assert.Empty(toolbar.Children.OfType<TextBox>());
            Assert.Equal("Running .NET processes", AutomationProperties.GetName(box));

            // Picking a running process shows it by its name and id, and Start reads the id from it.
            box.SelectedIndex = 0;
            ShellFixture.Pump();
            Assert.Equal("WebApi (48213)", vm.Target);
            Assert.Equal("WebApi (48213)", box.Text);
            Assert.Equal(48213, CountersViewModel.ProcessIdOf(vm.Target));

            // Typing an id or a name goes to the same box.
            box.Text = "512";
            ShellFixture.Pump();
            Assert.Equal("512", vm.Target);
            Assert.Equal(512, CountersViewModel.ProcessIdOf(vm.Target));
            box.Text = "Worker";
            ShellFixture.Pump();
            Assert.Equal("Worker", vm.Target);
            Assert.Null(CountersViewModel.ProcessIdOf(vm.Target));
            Assert.Equal(7, CountersViewModel.ProcessIdOf(" 7 "));
            Assert.Null(CountersViewModel.ProcessIdOf("Worker (x)"));

            // Export is one split button: CSV, its menu JSON too; Start keeps its label.
            var export = Assert.Single(toolbar.Children.OfType<SplitButton>());
            Assert.Equal(2, Assert.IsType<MenuFlyout>(export.Flyout).Items.Count);
            Assert.Contains(toolbar.Children.OfType<Button>(), b => b.Classes.Contains("iconText") && Equals(b.Content, "Start"));
            Assert.Empty(ToolbarProblems(view));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_containers_window_without_containers_shows_its_icon_status_and_refresh()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Layout.ShowToolWindow(ContainersViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ContainersView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<ContainersView>().Single();
        var vm = (ContainersViewModel)view.DataContext!;
        Assert.True(vm.IsEmpty);
        var empty = view.FindControl<StackPanel>("EmptyState")!;
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Contains(empty.Children.OfType<Icon>(), i => i.Kind == "container");
        var refresh = empty.Children.OfType<Button>().Single();
        Assert.Equal("refresh", ButtonIcon.GetKind(refresh));
        Assert.Contains("iconText", refresh.Classes);

        // The toolbar: nine icons in three groups, the container's commands disabled without a selection.
        var toolbar = view.GetVisualDescendants().OfType<ToolbarPanel>().First();
        var buttons = toolbar.Children.OfType<Button>().Where(b => !b.Classes.Contains("overflow")).ToList();
        Assert.Equal(["refresh", "play", "stop", "restart", "trash", "globe", "terminal", "debug", "window-remote"], buttons.Select(b => ButtonIcon.GetKind(b)));
        Assert.Equal(2, toolbar.Children.Count(c => c.Classes.Contains("toolbarSeparator")));
        Assert.All(buttons.Skip(1), b => Assert.False(b.IsEnabled));
        Assert.Empty(ToolbarProblems(view));
    }

    [AvaloniaFact]
    public async Task Configuration_manager_attaches_new_and_remove_to_their_combo_box_and_chips_the_active_pair()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count > 0 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var vm = f.Shell.OpenConfigurationManager()!;
        await ShellFixture.PumpUntil(() => vm.BuildTypes.Count > 1 && vm.Rows.Count > 0, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ConfigurationManagerView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<ConfigurationManagerView>().Single();
        ShellFixture.Pump();

        // Each solution combo box shows its choice, with New… and Remove attached to it.
        var attached = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("attached")).ToList();
        Assert.Equal(2, attached.Count);
        var configuration = attached[0].GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal(vm.BuildType, configuration.SelectedItem);
        Assert.Equal(vm.BuildType, configuration.SelectionBoxItem);
        Assert.Equal(vm.Platform, attached[1].GetVisualDescendants().OfType<ComboBox>().First().SelectedItem);
        Assert.Equal(vm.Platform, attached[1].GetVisualDescendants().OfType<ComboBox>().First().SelectionBoxItem);
        Assert.All(attached, a => Assert.Equal(["add", "trash"], a.GetVisualDescendants().OfType<Button>().Select(b => ButtonIcon.GetKind(b))));

        // The active pair: a chip, and Make Active disabled; another pair: no chip, Make Active enabled.
        var chip = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("chip"));
        var makeActive = view.GetVisualDescendants().OfType<Button>().Single(b => ButtonIcon.GetKind(b) == "check-circle");
        Assert.True(vm.IsActivePair);
        Assert.True(chip.IsVisible);
        Assert.False(makeActive.IsEffectivelyEnabled);
        vm.BuildType = vm.BuildTypes.First(b => b != vm.BuildType);
        ShellFixture.Pump();
        Assert.False(vm.IsActivePair);
        Assert.False(chip.IsVisible);
        Assert.True(makeActive.IsEffectivelyEnabled);
        Assert.Equal(vm.BuildType, configuration.SelectedItem);
    }

    [AvaloniaFact]
    public async Task Options_has_category_icons_a_page_header_chips_and_booleans_as_one_check_box()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenSettings();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SettingsView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<SettingsView>().Single();
        var vm = (SettingsViewModel)view.DataContext!;

        // The tree: every node with an icon in its palette color.
        Assert.Equal("list", vm.Categories[0].Icon);
        Assert.All(vm.Categories.SelectMany(c => c.Children.Prepend(c)), c => Assert.True(CommandIcons.HasColor(c.Icon), $"{c.Name}: {c.Icon}"));
        Assert.Equal("document-text", vm.Categories.Single(c => c.Name == "Text Editor").Icon);

        // A category's page: its header, and its settings by their titles.
        var editor = vm.Categories.Single(c => c.Name == "Text Editor");
        vm.SelectedCategory = editor;
        ShellFixture.Pump();
        Assert.Equal("Text Editor", vm.PageTitle);
        Assert.Equal("document-text", vm.PageIcon);
        Assert.StartsWith($"{vm.Items.Count} settings", vm.PageSummary, StringComparison.Ordinal);
        var leaf = editor.Children.First(c => c.Count > 0);
        vm.SelectedCategory = leaf;
        ShellFixture.Pump();
        Assert.All(vm.Items, i => Assert.Equal(i.Title, i.DisplayTitle));
        vm.SelectedCategory = vm.Categories[0];
        ShellFixture.Pump();
        Assert.All(vm.Items, i => Assert.Equal(i.Heading, i.DisplayTitle));

        // A boolean is one check box whose label is its title, once; changed, it is marked with a chip.
        vm.Search = "editor.wordWrap";
        ShellFixture.Pump();
        var item = vm.Items.OfType<BoolSettingViewModel>().Single(i => i.Key == "editor.wordWrap");
        vm.Search = "Wrap long lines in the editor";
        ShellFixture.Pump();
        Assert.Same(item, Assert.Single(vm.Items));
        await f.Shell.Workbench.Settings.SetAsync(item.Key, !item.Value, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => item.IsModified);
        ShellFixture.Pump();
        var row = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("setting"));
        Assert.Contains("modified", row.Classes);
        Assert.Single(row.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible);
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == item.DisplayTitle && t.FindAncestorOfType<CheckBox>() is null);
        Assert.Contains(row.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip") && b.IsEffectivelyVisible);
        await f.Shell.Workbench.Settings.SetAsync(item.Key, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None);

        // User or Workspace is a segmented control (shown while a solution gives a workspace), its index the scope.
        var scope = view.GetVisualDescendants().OfType<SegmentedControl>().Single();
        Assert.Equal(2, scope.ItemCount);
        Assert.Equal(0, vm.ScopeIndex);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<ToggleButton>(), t => t.Content is "User" or "Workspace");
    }
}
