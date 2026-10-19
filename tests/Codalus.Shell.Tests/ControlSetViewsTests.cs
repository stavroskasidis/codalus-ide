using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    public async Task Counters_process_id_box_and_process_list_are_one_editable_combo_box()
    {
        // The shell loads the English catalogue the view's names come from (run first in a process, nothing else had).
        await using var f = ShellFixture.Create();
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

            // Listing the processes again keeps what the box names: the picked process, or the typed text (M34h review).
            box.SelectedIndex = 0;
            ShellFixture.Pump();
            vm.SetProcesses([new DotnetProcess(48213, "WebApi", "dotnet WebApi.dll"), new DotnetProcess(512, "Worker", null)]);
            ShellFixture.Pump();
            Assert.Equal("WebApi (48213)", vm.Target);
            Assert.Equal("WebApi (48213)", box.Text);
            Assert.Equal(48213, vm.SelectedProcess?.Id);
            box.Text = "Work";
            ShellFixture.Pump();
            vm.SetProcesses([new DotnetProcess(512, "Worker", null)]);
            ShellFixture.Pump();
            Assert.Equal("Work", vm.Target);
            Assert.Equal("Work", box.Text);

            // Large values and long units fit their columns, sized to the widest cell (they were fixed at 72 and 40 px).
            var counter = new CounterRowViewModel(new Codalus.Workbench.Diagnostics.CounterKey("System.Runtime", "gc-heap-size", null)) { Value = "1,234,567,890.5", Unit = "bytes/sec" };
            vm.Rows.Add(counter);
            ShellFixture.Pump();
            ShellFixture.Pump();
            var problems = PolishTests.Clipped(window).Concat(PolishTests.Overlapping(window)).ToList();
            Assert.True(problems.Count == 0, string.Join('\n', problems));

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

        // The toolbar makes that pair active while the document is open: the chip and Make Active follow (M34h review).
        var other = new Codalus.Projects.Protocol.SolutionConfigurationInfo(vm.BuildType!, vm.Platform!);
        await f.Shell.Solutions.SetConfigurationAsync(other, CancellationToken.None);
        await ShellFixture.PumpUntil(() => vm.IsActivePair && chip.IsVisible);
        Assert.False(makeActive.IsEffectivelyEnabled);

        // Make Active marks the pair active once the solution has switched, not before.
        var first = vm.BuildTypes.First(b => b != vm.BuildType);
        vm.BuildType = first;
        ShellFixture.Pump();
        Assert.False(vm.IsActivePair);
        await vm.ActivateAsync();
        Assert.Equal(first, f.Shell.Solutions.Solution!.Active!.Configuration);
        await ShellFixture.PumpUntil(() => vm.IsActivePair && chip.IsVisible);

        // The attached buttons' focus ring draws past the shape that clips them.
        var remove = attached[0].GetVisualDescendants().OfType<Button>().Last();
        Assert.False(Avalonia.Controls.Primitives.AdornerLayer.GetIsClipEnabled(remove.FocusAdorner!.Build()!));
    }

    [AvaloniaFact]
    public async Task Options_header_trims_a_long_settings_path_inside_the_page()
    {
        // Windows' settings path (C:\Users\…\AppData\Roaming\Codalus\settings.json) is longer than Linux's, and a stack
        // panel around it measured it unconstrained: CI on Windows found its ellipsis cut off. A long user-data folder
        // stands for it on every OS.
        var userData = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + new string('n', 100) + "-" + Guid.NewGuid().ToString("N"));
        await using var f = ShellFixture.Create(userData);
        f.Shell.OpenSettings();
        var document = f.Shell.Layout.ActiveDocument;
        ShellFixture.Pump();
        var window = Show(new ContentControl { Content = document }, 640, 480);
        try
        {
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.True(((SettingsViewModel)view.DataContext!).FileLabel.Length > 150);
            var problems = PolishTests.Clipped(window).ToList();
            Assert.True(problems.Count == 0, "Text cut off in Options at 640 px:\n" + string.Join('\n', problems));
        }
        finally
        {
            window.Close();
        }
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

        // Without a solution there is no workspace: the scope's segmented control is hidden.
        Assert.False(view.GetVisualDescendants().OfType<SegmentedControl>().Single().IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Options_scope_is_a_segmented_control_that_switches_to_the_workspace()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.WorkspaceFile is not null, seconds: 120);
        f.Shell.OpenSettings();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SettingsView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<SettingsView>().Single();
        var vm = (SettingsViewModel)view.DataContext!;
        await ShellFixture.PumpUntil(() => vm.HasWorkspace);
        ShellFixture.Pump();

        // User or Workspace is one visible segmented control (not two toggle buttons); choosing a segment is the scope.
        var scope = view.GetVisualDescendants().OfType<SegmentedControl>().Single();
        Assert.True(scope.IsEffectivelyVisible);
        Assert.Equal(2, scope.ItemCount);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<ToggleButton>(), t => t.Content is "User" or "Workspace");
        Assert.Equal(0, scope.SelectedIndex);
        Assert.Equal(SettingScope.User, vm.Scope);
        scope.SelectedIndex = 1;
        ShellFixture.Pump();
        Assert.Equal(SettingScope.Workspace, vm.Scope);
        Assert.StartsWith("Workspace settings: ", vm.FileLabel, StringComparison.Ordinal);
        vm.IsUserScope = true;
        ShellFixture.Pump();
        Assert.Equal(0, scope.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task Containers_with_images_but_no_containers_keep_their_tabs()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Layout.ShowToolWindow(ContainersViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ContainersView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<ContainersView>().Single();
        var vm = (ContainersViewModel)view.DataContext!;

        // An engine with an image and no container: the tabs stay, so the Images tab and Remove Image are reachable.
        vm.Images.Add(new ImageRow(new Codalus.Workbench.Containers.ImageSummary("codalus-sample-web", "latest", "sha256:1234", "220MB", "2 hours ago")));
        vm.CountsChanged();
        ShellFixture.Pump();
        Assert.False(vm.IsEmpty);
        Assert.False(view.FindControl<StackPanel>("EmptyState")!.IsVisible);
        var tabs = view.GetVisualDescendants().OfType<TabItem>().ToList();
        Assert.Contains(tabs, t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) == "Images");
        // The tabs with a count in their header are named by their titles, not by their panels' types.
        Assert.Contains(tabs, t => AutomationProperties.GetName(t) == "Containers");
        Assert.Equal("Images", Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(tabs.First(t => AutomationProperties.GetName(t) == "Images")).GetName());

        vm.Images.Clear();
        vm.CountsChanged();
        ShellFixture.Pump();
        Assert.True(vm.IsEmpty);
        Assert.True(view.FindControl<StackPanel>("EmptyState")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Service_references_selected_row_commands_are_reachable_from_the_keyboard()
    {
        await using var f = ShellFixture.Create();
        var verbs = new List<string>();
        var vm = new ServiceReferencesViewModel(Path.Combine(f.UserData, "Client.csproj"), (_, verb) =>
        {
            verbs.Add(verb);
            return Task.CompletedTask;
        });
        vm.SetReferences(
            new Codalus.Projects.Protocol.ServiceReferencesResult([], []),
            [new Codalus.Projects.Protocol.WcfReferenceInfo("CalculatorService", Path.Combine(f.UserData, "Connected Services", "CalculatorService"), "ConnectedService.json", "Reference.cs", ["http://localhost:5280/CalculatorService.svc?wsdl"], "Client.Calculator", null, null, false, true, false, null)]);
        var view = new ServiceReferencesView { DataContext = vm };
        var window = Show(view, 900, 700);
        try
        {
            var list = view.FindControl<ListBox>("ReferenceList")!;
            Assert.Equal(KeyboardNavigationMode.Continue, KeyboardNavigation.GetTabNavigation(list));
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
            var commands = row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            Assert.Equal(["plug", "cloud-download", "link-external", "trash"], commands.Select(b => ButtonIcon.GetKind(b)));
            Assert.All(commands, b => Assert.True(b.Focusable && KeyboardNavigation.GetIsTabStop(b)));

            // The context menu has them too, and Enter opens the document, Delete removes the reference.
            var menu = Assert.IsType<ContextMenu>(list.ContextMenu);
            Assert.Equal(4, menu.Items.Count);
            list.Focus();
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = list });
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete, Source = list });
            ShellFixture.Pump();
            Assert.Equal(["open", "remove"], verbs);
        }
        finally
        {
            window.Close();
        }
    }
}
