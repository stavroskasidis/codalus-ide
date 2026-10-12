using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Keybindings;
using DaedalusStudio.Workbench.Menus;
using MenuItem = Avalonia.Controls.MenuItem;

namespace DaedalusStudio.Shell.Tests;

/// <summary>M34a (the UX pass): menus as trees with icons, output that follows, the docked minimap, completion keys.</summary>
public class UxPassTests
{
    /// <summary>The most entries a menu may show (in any context): about 32 rows of 24 px fit a 1080p screen.</summary>
    private const int MaxEntries = 32;

    // ------------------------------------------------------------------ menus

    [AvaloniaFact]
    public async Task Every_command_the_menus_offered_before_the_restructuring_is_still_reachable()
    {
        await using var f = ShellFixture.Create();
        var menus = f.Shell.Workbench.Menus;
        var baseline = File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "tests", "DaedalusStudio.Shell.Tests", "TestData", "menu-commands.txt"));
        string? root = null;
        var missing = new List<string>();
        foreach (var line in baseline.Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            if (line.StartsWith('['))
            {
                root = line.Trim('[', ']');
                continue;
            }

            if (!Reachable(menus, root!).Contains(line))
            {
                missing.Add($"{root}: {line}");
            }
        }

        Assert.Empty(missing);

        // And every submenu that has items hangs off a menu.
        var submenus = menus.Rules.Where(r => r.Submenu is not null).Select(r => r.Submenu!).ToHashSet(StringComparer.Ordinal);
        var orphans = menus.Rules.Select(r => r.Location)
            .Where(l => l.Count(c => c == '/') >= 2 && !submenus.Contains(l) && l != MenuLocations.ToolWindowTitle)
            .Distinct()
            .ToList();
        Assert.Empty(orphans);
    }

    [AvaloniaFact]
    public async Task No_menu_is_longer_than_a_1080p_screen_and_the_edit_menu_is_a_tree()
    {
        await using var f = ShellFixture.Create();
        var menus = f.Shell.Workbench.Menus;

        // Idle, and in the busiest context: debugging a C# file in a Git repository with everything available.
        var busy = new ContextKeyService();
        foreach (var key in new[] { "debugActive", "debugStopped", "debugRunning", "programRunning", "hotReloadSupported", "hotReloadRunning", "profilerCollecting", "efProject", "gitRepository", "gitLfs", "solutionOpen", "solutionTrusted", "folderMode" })
        {
            busy.Set(key, true);
        }

        busy.Set("editorLanguageId", "csharp");
        foreach (var scope in new IContextKeyReader[] { f.Shell.Workbench.Context, busy })
        {
            foreach (var kind in new[] { "solution", "project", "file", "folder" })
            {
                busy.Set("explorerItemKind", kind);
                foreach (var location in AllLocations(menus, scope))
                {
                    var count = menus.GetItems(location, scope).Count;
                    Assert.True(count <= MaxEntries, $"{location} has {count} entries ({kind})");
                }
            }
        }

        // Edit is a classic IDE's tree: the editing commands live in submenus.
        var edit = menus.GetItems(MenuLocations.MenuBar("edit"));
        Assert.Equal(
            ["Go To", "Find and Replace", "Advanced", "Bookmarks", "IntelliSense", "Multiple Carets", "Outlining", "Refactor"],
            edit.Where(i => i.Submenu is not null && i.Title != "Paste Special").Select(i => i.Title));
        var intelliSense = menus.GetItems(BuiltIns.EditIntelliSenseMenu);
        Assert.Equal(["editor.action.triggerSuggest", "editor.action.triggerParameterHints", "editor.action.showHover"], intelliSense.Take(3).Select(i => i.Command));
        var mac = f.Shell.Workbench.Platform == PlatformKind.MacOS;
        Assert.Equal(mac ? "⌃Space" : "Ctrl+Space", intelliSense[0].KeyLabel);
        Assert.Equal(mac ? "⌃⇧Space" : "Ctrl+Shift+Space", intelliSense[1].KeyLabel);
        Assert.Contains(menus.GetItems(BuiltIns.EditAdvancedMenu), i => i.Command == "editor.action.changeEndOfLine");
        Assert.Contains(menus.GetItems(BuiltIns.EditAdvancedMenu), i => i.Command == "editor.action.duplicateSelection");
        Assert.Contains(menus.GetItems(BuiltIns.EditFindMenu), i => i.Command == "workbench.action.findInFiles");
        Assert.Equal(mac ? "⌘Z" : "Ctrl+Z", edit.Single(i => i.Command == "edit.undo").KeyLabel);

        // No "Visual Studio" in any menu title.
        Assert.DoesNotContain(menus.Rules, r => r.Title?.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) == true);
    }

    [AvaloniaFact]
    public async Task Open_menus_fit_the_screen_and_show_icons_shortcuts_and_tooltips()
    {
        await using var f = ShellFixture.Create();
        if (f.Shell.Workbench.Platform == PlatformKind.MacOS)
        {
            return; // the global menu bar: native menus, measured by macOS
        }

        f.Window.Width = 1920;
        f.Window.Height = 1040;
        ShellFixture.Pump();
        var bar = f.Window.GetVisualDescendants().OfType<Menu>().First(m => m.Name == "MenuBar");
        foreach (var top in bar.Items.OfType<MenuItem>())
        {
            top.Open();
            ShellFixture.Pump();
            var presenter = PopupContent(top);
            Assert.True(presenter.Bounds.Height is > 0 and <= 950, $"{top.Header} is {presenter.Bounds.Height} px tall");
            foreach (var sub in top.Items.OfType<MenuItem>().Where(i => i.ItemCount > 0))
            {
                sub.Open();
                ShellFixture.Pump();
                Assert.True(PopupContent(sub).Bounds.Height <= 950, $"{top.Header} ▸ {sub.Header}");
                sub.Close();
            }

            top.Close();
            ShellFixture.Pump();
        }

        var file = bar.Items.OfType<MenuItem>().First(i => Equals(i.Header, "_File"));
        file.Open();
        ShellFixture.Pump();
        var save = file.Items.OfType<MenuItem>().First(i => Avalonia.Automation.AutomationProperties.GetName(i) == "Save");
        Assert.True(save.Icon is Icon { Kind: "save" } icon && icon.Classes.Contains("accent-save"));
        Assert.Equal("File: Save (Ctrl+S)", ToolTip.GetTip(save));
        file.Close();
    }

    [AvaloniaFact]
    public async Task Context_menus_nest_their_submenus()
    {
        await using var f = ShellFixture.Create();
        var scope = new ContextKeyService();
        scope.Set("explorerItemKind", "project");
        scope.Set("gitRepository", true);
        var items = f.Shell.Workbench.Menus.GetItems(SolutionExplorerView.ContextMenuLocation, scope);
        Assert.Contains(items, i => i.Submenu == SolutionExplorerView.AddMenuLocation && i.Title == "Add");
        Assert.Contains(items, i => i.Submenu == SolutionExplorerView.GitMenuLocation);
        Assert.Contains(f.Shell.Workbench.Menus.GetItems(SolutionExplorerView.AddMenuLocation, scope), i => i.Command == "project.addNewItem" && i.Title == "New Item…");

        f.Shell.Workbench.Context.Set("explorerItemKind", "project");
        var menu = MenuBuilder.Context(f.Shell, SolutionExplorerView.ContextMenuLocation);
        Assert.NotNull(menu);
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => i.ItemCount > 0);
    }

    [AvaloniaFact]
    public void Every_icon_is_compiled_and_every_command_icon_exists()
    {
        foreach (var (name, file) in IconSet.FluentNames)
        {
            var icon = IconSet.GetFilled(name);
            Assert.True(icon is not null, $"{name} ({file}) has no compiled geometry");
            var bounds = icon.Geometry.Bounds;
            Assert.True(bounds.Width > 1 && bounds.Height > 1 && bounds.Right <= icon.DesignSize + 0.01 && bounds.Bottom <= icon.DesignSize + 0.01, $"{name}: {bounds}");
        }

        Assert.All(CommandIcons.All, pair => Assert.True(IconSet.GetFilled(pair.Value.Kind) is not null || IconSet.Get(pair.Value.Kind) is not null, pair.Key));
        Assert.All(FluentIcons.Names, name => Assert.Contains(name, File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "src", "DaedalusStudio.Shell", "Icons", "fluent-icons.txt"))));
    }

    [AvaloniaFact]
    public async Task Icon_accents_are_colored_in_every_theme_but_High_Contrast()
    {
        await using var f = ShellFixture.Create();
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeService.Blue, ThemeService.HighContrast })
        {
            foreach (var family in new[] { "Run", "Stop", "Save", "Open", "Build", "Git", "Test", "Navigate" })
            {
                Assert.True(Application.Current!.TryGetResource("Shell.Icon." + family, variant, out var value) && value is ISolidColorBrush, $"{variant} {family}");
                var color = ((ISolidColorBrush)value!).Color;
                if (variant == ThemeService.HighContrast)
                {
                    Assert.Equal(Colors.White, color);
                }
                else
                {
                    Assert.NotEqual(Colors.White, color);
                }
            }
        }
    }

    // ------------------------------------------------------------------ output follows

    [AvaloniaFact]
    public async Task Output_follows_new_output_until_the_user_scrolls_up_and_resumes_at_the_end()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        f.Shell.ShowLogs();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<OutputView>().Any(v => v.IsEffectivelyVisible && v.Bounds.Height > 50));
        var view = f.Window.GetVisualDescendants().OfType<OutputView>().First(v => v.IsEffectivelyVisible);
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First();
        var viewer = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
        bool AtEnd() => viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2;

        // The text starts after the standard padding, not at the left edge.
        Assert.True(editor.TextArea.TextView.TranslatePoint(default, editor) is { X: >= 4 }, $"{editor.TextArea.TextView.TranslatePoint(default, editor)}");

        for (var i = 0; i < 300; i++)
        {
            f.Shell.Workbench.Log.AppendLine($"line {i}");
        }

        f.Shell.Workbench.Log.AppendLine("a long line " + new string('x', 600));
        await ShellFixture.PumpUntil(() => view.Text.Contains("line 299", StringComparison.Ordinal) && viewer.Extent.Height > viewer.Viewport.Height * 2 && AtEnd());
        Assert.True(view.IsFollowing, "following at the end");
        Assert.Equal(0, viewer.Offset.X); // following never scrolls sideways (the first characters stay visible)

        // The end shows the last lines, not an empty page below the document.
        var lines = editor.TextArea.TextView.VisualLines;
        Assert.True(lines.Count > 3 && lines.Any(l => l.FirstDocumentLine.LineNumber == editor.Document.LineCount - 1), $"{lines.Count} visible lines");

        // Scrolling up stops following: new output doesn't move the view.
        viewer.Offset = new Vector(0, 0);
        await ShellFixture.PumpUntil(() => !view.IsFollowing);
        f.Shell.Workbench.Log.AppendLine("while scrolled up");
        await ShellFixture.PumpUntil(() => view.Text.Contains("while scrolled up", StringComparison.Ordinal));
        ShellFixture.Pump();
        Assert.Equal(0, viewer.Offset.Y);

        // Back at the end, it follows again.
        await ShellFixture.PumpUntil(() =>
        {
            viewer.ScrollToEnd(); // as the user would, until layout has the last line's extent
            ShellFixture.Pump();
            return view.IsFollowing;
        });
        for (var i = 0; i < 50; i++)
        {
            f.Shell.Workbench.Log.AppendLine($"more {i}");
        }

        await ShellFixture.PumpUntil(() => view.Text.Contains("more 49", StringComparison.Ordinal) && AtEnd());
    }

    [AvaloniaFact]
    public async Task A_log_text_box_follows_its_end()
    {
        await using var f = ShellFixture.Create();
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 100, Width = 300 };
        var tail = FollowTail.Attach(box);
        f.Window.Content = box;
        ShellFixture.Pump();
        box.Text = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"log {i}"));
        await ShellFixture.PumpUntil(() => box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer
            && viewer.Extent.Height > viewer.Viewport.Height && viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2);
        Assert.True(tail.IsFollowing);
    }

    // ------------------------------------------------------------------ minimap

    [AvaloniaFact]
    public async Task The_minimap_stays_docked_at_the_editor_right_edge_with_short_and_long_lines()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var edges = new List<double>();
        foreach (var (name, text) in new[] { ("short.txt", "a\nb\nc\n"), ("long.txt", string.Join('\n', Enumerable.Range(0, 40).Select(i => new string('x', 400 + i)))), ("medium.txt", "int x = 1;\nreturn x;\n") })
        {
            var path = Path.Combine(f.UserData, name);
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
            var document = await f.Shell.OpenFileAsync(path, preview: false, null);
            Assert.NotNull(document);
            await ShellFixture.PumpUntil(() => document.View is { Minimap.Bounds.Width: > 0 });
            ShellFixture.Pump();
            var view = document.View!;
            var minimap = view.Minimap;
            var right = minimap.TranslatePoint(new Point(minimap.Bounds.Width, 0), view)!.Value.X;
            Assert.True(Math.Abs(right - view.Bounds.Width) <= 1, $"{name}: the minimap ends at {right}, the editor at {view.Bounds.Width}");
            edges.Add(minimap.TranslatePoint(default, view)!.Value.X);
        }

        Assert.True(edges.Max() - edges.Min() <= 1, $"the minimap moved: {string.Join(", ", edges)}");
    }

    // ------------------------------------------------------------------ completion keys

    [Theory]
    [InlineData(PlatformKind.Windows)]
    [InlineData(PlatformKind.Linux)]
    [InlineData(PlatformKind.MacOS)]
    public void Completion_keys_suit_each_OS(PlatformKind platform)
    {
        var rules = BuiltIns.ParseDefaultKeybindings(platform);
        string[] Keys(string command) => [.. rules.Where(r => r.Command == command).Select(r => r.Keys!.ToString())];

        // Ctrl+Space everywhere: the physical Control key on macOS too, where Cmd+Space is Spotlight.
        var suggest = Keys("editor.action.triggerSuggest");
        Assert.Contains("ctrl+space", suggest);
        Assert.Contains("ctrl+j", suggest); // the classic List Members, and the way round an input method's Ctrl+Space (IBus)
        Assert.DoesNotContain("meta+space", suggest);
        if (platform == PlatformKind.MacOS)
        {
            Assert.Contains("alt+escape", suggest); // Option+Esc, when Control+Space switches input sources
        }

        Assert.Equal(["ctrl+shift+space"], Keys("editor.action.triggerParameterHints"));

        // The menus show Ctrl+Space (the last binding wins the label).
        var service = new KeybindingService(new ContextKeyService(), platform);
        service.SetDefaults(rules);
        Assert.Equal("ctrl+space", service.GetBinding("editor.action.triggerSuggest")!.ToString());
    }

    /// <summary>Shortcuts each OS keeps for itself (M34a's audit, docs/user-guide/keyboard.md).</summary>
    public static TheoryData<PlatformKind, string> ReservedShortcuts() => new()
    {
        { PlatformKind.MacOS, "meta+space" },          // Spotlight
        { PlatformKind.MacOS, "meta+alt+space" },      // Finder search
        { PlatformKind.MacOS, "meta+m" },              // Minimize
        { PlatformKind.MacOS, "meta+h" },              // Hide the app
        { PlatformKind.MacOS, "meta+tab" },            // App switcher
        { PlatformKind.MacOS, "meta+alt+escape" },     // Force Quit
        { PlatformKind.MacOS, "ctrl+up" },             // Mission Control
        { PlatformKind.MacOS, "ctrl+down" },           // App Exposé
        { PlatformKind.MacOS, "ctrl+left" },           // Spaces
        { PlatformKind.MacOS, "ctrl+right" },
        { PlatformKind.MacOS, "ctrl+alt+space" },      // Next input source
        { PlatformKind.Linux, "alt+f2" },              // GNOME/KDE run dialog
        { PlatformKind.Linux, "alt+f10" },             // GNOME maximize
        { PlatformKind.Linux, "ctrl+alt+f1" },         // virtual terminals
        { PlatformKind.Linux, "ctrl+alt+f2" },
        { PlatformKind.Linux, "ctrl+alt+up" },         // workspaces
        { PlatformKind.Linux, "ctrl+alt+down" },
        { PlatformKind.Linux, "ctrl+alt+backspace" },  // X server zap, where enabled
        { PlatformKind.Linux, "ctrl+alt+t" },          // terminal
        { PlatformKind.Linux, "meta+space" },          // input sources
        { PlatformKind.Windows, "meta+space" },        // input sources
        { PlatformKind.Windows, "ctrl+shift+escape" }, // Task Manager
        { PlatformKind.Windows, "alt+space" },         // the window menu
        { PlatformKind.Windows, "ctrl+escape" },       // Start
    };

    [Theory]
    [MemberData(nameof(ReservedShortcuts))]
    public void No_default_binding_takes_a_shortcut_the_OS_keeps(PlatformKind platform, string reserved)
    {
        var chord = KeyChord.Parse(reserved, platform);
        var taken = BuiltIns.ParseDefaultKeybindings(platform).Where(r => r.Keys!.Chords[0] == chord).Select(r => r.Command).ToList();
        Assert.Empty(taken);
    }

    [AvaloniaFact]
    public async Task Ctrl_Space_opens_completion_in_a_CSharp_file()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var folder = await f.OpenLanguageFeaturesAsync();
        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "App", "Program.cs"));
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!;
        var text = editor.Model.Text.Text;
        editor.Editor.CaretOffset = text.IndexOf("Reporter.Run", StringComparison.Ordinal) + "Rep".Length;
        f.Window.GetVisualDescendants().OfType<StandardToolbar>().First().GetVisualDescendants().OfType<ComboBox>().First().Focus();
        ShellFixture.Pump();
        editor.Editor.TextArea.Focus();
        ShellFixture.Pump();

        var executed = new List<string>();
        f.Shell.Workbench.Commands.Executed += (_, e) => executed.Add(e.CommandId);
        Assert.True(Equals(f.Shell.Workbench.Context.GetValue("editorTextFocus"), true), $"the editor has the keyboard focus: {f.Shell.Workbench.Context.GetValue("focusedView")}, {f.Window.FocusManager?.GetFocusedElement()}, {editor.Editor.TextArea.IsFocused}, {PaneHost.Of(editor.Editor.TextArea)?.Dockable}");
        f.Press(Key.Space, RawInputModifiers.Control);
        Assert.Contains("editor.action.triggerSuggest", executed);
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Reporter"), seconds: 60);
        Assert.Equal(text, editor.Model.Text.Text); // no space typed
        f.Press(Key.Escape);

        // Ctrl+Shift+Space is Parameter Info.
        editor.Editor.CaretOffset = text.IndexOf("Describe(", StringComparison.Ordinal) + "Describe(".Length;
        f.Press(Key.Space, RawInputModifiers.Control | RawInputModifiers.Shift);
        await ShellFixture.PumpUntil(() => executed.Contains("editor.action.triggerParameterHints"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The commands reachable from <paramref name="root"/> through its submenus, whatever the context.</summary>
    private static HashSet<string> Reachable(MenuRegistry menus, string root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var locations = new Queue<string>(root == MenuLocations.MenuBarPrefix ? menus.MenuBar.Select(m => MenuLocations.MenuBar(m.Id)) : [root]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (locations.TryDequeue(out var location))
        {
            if (!seen.Add(location))
            {
                continue;
            }

            foreach (var rule in menus.Rules.Where(r => r.Location == location))
            {
                if (rule.Submenu is { } submenu)
                {
                    locations.Enqueue(submenu);
                }
                else
                {
                    result.Add(rule.Command);
                }
            }
        }

        return result;
    }

    /// <summary>Every menu location: the menu bar's menus, the context menus and all their submenus.</summary>
    private static IEnumerable<string> AllLocations(MenuRegistry menus, IContextKeyReader scope) =>
        menus.Rules.Select(r => r.Location).Concat(menus.Rules.Where(r => r.Submenu is not null).Select(r => r.Submenu!))
            .Where(l => l.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal) || l.Contains("/context", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    private static Control PopupContent(MenuItem item) =>
        item.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First().Child as Control
        ?? throw new InvalidOperationException("The menu has no popup.");
}
