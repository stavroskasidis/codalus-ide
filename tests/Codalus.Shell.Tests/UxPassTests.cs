using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Codalus.Shell.Views;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using MenuItem = Avalonia.Controls.MenuItem;

namespace Codalus.Shell.Tests;

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
        var baseline = File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Shell.Tests", "TestData", "menu-commands.txt"));
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
        Assert.Equal(mac ? "⌥⎋" : "Ctrl+Space", intelliSense[0].KeyLabel); // macOS takes ⌃Space for input sources by default
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
            var bounds = icon.Bounds;
            Assert.True(bounds.Width > 1 && bounds.Height > 1 && bounds.Right <= icon.DesignSize + 0.01 && bounds.Bottom <= icon.DesignSize + 0.01, $"{name}: {bounds}");
        }

        Assert.All(CommandIcons.All, pair => Assert.True(IconSet.GetFilled(pair.Value.Kind) is not null || IconSet.Get(pair.Value.Kind) is not null, pair.Key));
        var listed = File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Icons", "fluent-icons.txt"))
            .Concat(File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Icons", "material-icons.txt"))).ToHashSet(StringComparer.Ordinal);
        // The classic icons are eng/icons/classic-icons.py's, which writes the whole of Icons/Classic, and the code symbol
        // icons eng/icons/symbol-icons.py's, which writes Icons/Symbols.
        Assert.All(CompiledIcons.Names.Where(n => !n.StartsWith("classic-", StringComparison.Ordinal) && !n.StartsWith("symbol-", StringComparison.Ordinal)), name => Assert.Contains(name, listed));
        Assert.Equal(IconSet.SymbolNames.Order(StringComparer.Ordinal), CompiledIcons.Names.Where(n => n.StartsWith("symbol-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task Icon_accents_are_colored_in_every_theme_but_High_Contrast()
    {
        await using var f = ShellFixture.Create();
        foreach (var variant in ThemeService.Themes.Select(t => t.Variant))
        {
            foreach (var family in new[] { "Run", "Stop", "Save", "Open", "Build", "Git", "Test", "Navigate" })
            {
                Assert.True(Application.Current!.TryGetResource("Shell.Icon." + family, variant, out var value) && value is ISolidColorBrush, $"{variant} {family}");
                var color = ((ISolidColorBrush)value!).Color;
                if (ThemeService.IsHighContrast(variant))
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
            edges.Add(view.Bounds.Width - minimap.TranslatePoint(default, view)!.Value.X); // its offset from the editor's right edge
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

        // The menus show Ctrl+Space, and ⌥⎋ on macOS (the last binding wins the label).
        var service = new KeybindingService(new ContextKeyService(), platform);
        service.SetDefaults(rules);
        Assert.Equal(platform == PlatformKind.MacOS ? "alt+escape" : "ctrl+space", service.GetBinding("editor.action.triggerSuggest")!.ToString());
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
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is { } w && w.CompletionList.SelectedItem is not null && w.CompletionList.CompletionData.Any(d => d.Text == "Reporter"), seconds: 60);
        Assert.Equal("Reporter", editor.CompletionWindow!.CompletionList.SelectedItem?.Text); // the typed prefix's best match
        Assert.Equal(text, editor.Model.Text.Text); // no space typed
        f.Press(Key.Escape);

        // After "Con": the shortest of the equal matches, Console (not ConcurrentExclusiveSchedulerPair).
        editor.Editor.CaretOffset = text.IndexOf("Console.WriteLine", StringComparison.Ordinal) + "Con".Length;
        f.Press(Key.Space, RawInputModifiers.Control);
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is { } w && w.CompletionList.SelectedItem is not null && w.CompletionList.CompletionData.Any(d => d.Text == "Console"), seconds: 60);
        Assert.Equal("Console", editor.CompletionWindow!.CompletionList.SelectedItem!.Text);
        f.Press(Key.Escape);

        // Ctrl+Shift+Space is Parameter Info.
        editor.Editor.CaretOffset = text.IndexOf("Describe(", StringComparison.Ordinal) + "Describe(".Length;
        f.Press(Key.Space, RawInputModifiers.Control | RawInputModifiers.Shift);
        await ShellFixture.PumpUntil(() => executed.Contains("editor.action.triggerParameterHints"));
    }

    // ------------------------------------------------------------------ file icons, the navigation bar, outlining

    [AvaloniaFact]
    public void File_folder_and_project_icons_are_colored_and_compiled()
    {
        // The built-in icons are the classic ones: outline pages with a colored badge, outline folders, project boxes.
        Assert.Equal("cl:csharp", FileIcons.ForFile("Program.cs"));
        Assert.Equal("cl:razor", FileIcons.ForFile("Counter.razor"));
        Assert.Equal("cl:xaml", FileIcons.ForFile("MainWindow.axaml"));
        Assert.Equal("cl:msbuild", FileIcons.ForFile("Directory.Build.props"));
        Assert.Equal("cl:json", FileIcons.ForFile("appsettings.Development.json"));
        Assert.Equal("cl:config", FileIcons.ForFile("NuGet.Config"));
        Assert.Equal("cl:json", FileIcons.ForFile("data.json"));
        Assert.Equal("cl:yaml", FileIcons.ForFile("ci.yml"));
        Assert.Equal("cl:markdown", FileIcons.ForFile("README.md"));
        Assert.Equal("cl:text", FileIcons.ForFile("notes.txt"));
        Assert.Equal("cl:document", FileIcons.ForFile("unknown.xyz"));
        Assert.Equal("cl:document", FileIcons.ForFile(".gitmessage"));
        Assert.Equal("cl:solution", FileIcons.ForFile("App.slnx"));
        Assert.Equal("cl:folder-properties", FileIcons.ForFolder("Properties", open: false));
        Assert.Equal("cl:folder-web", FileIcons.ForFolder("wwwroot", open: true));
        Assert.Equal("cl:folder-open", FileIcons.ForFolder("Anything", open: true));
        Assert.Equal("cl:folder", FileIcons.ForFolder("Anything", open: false));
        ProjectInfo Info(string? outputType, bool test = false, params string[] items) =>
            new("p.csproj", "p", "loaded", null, ["net10.0"], outputType, [.. items.Select(i => new ProjectItemInfo("Content", i, null, null))], [], [], true, test);
        Assert.Equal("cl:project-csharp", FileIcons.ForProject("Lib.csproj", Info("Library")));
        Assert.Equal("cl:project-csharp", FileIcons.ForProject("App.csproj", Info("Exe")));
        Assert.Equal("cl:project-test", FileIcons.ForProject("Tests.csproj", Info("Exe", test: true)));
        Assert.Equal("cl:project-web", FileIcons.ForProject("Web.csproj", Info("Exe", false, "wwwroot/site.css")));
        Assert.Equal("cl:project-fsharp", FileIcons.ForProject("Lib.fsproj", null));

        // Material Icon Theme is the other built-in theme.
        FileIcons.Material = true;
        try
        {
            Assert.Equal("mi:csharp", FileIcons.ForFile("Program.cs"));
            Assert.Equal("mi:tune", FileIcons.ForFile("Directory.Build.props"));
            Assert.Equal("mi:settings", FileIcons.ForFile("appsettings.Development.json"));
            Assert.Equal("mi:readme", FileIcons.ForFile("README.md"));
            Assert.Equal("mi:document", FileIcons.ForFile("unknown.xyz"));
            Assert.Equal("solution@Build", FileIcons.ForFile("App.slnx"));
            Assert.Equal("mi:folder-config", FileIcons.ForFolder("Properties", open: false));
            Assert.Equal("mi:folder-public-open", FileIcons.ForFolder("wwwroot", open: true));
            Assert.Equal("mi:folder-open", FileIcons.ForFolder("Anything", open: true));
            Assert.Equal("mi:lib", FileIcons.ForProject("Lib.csproj", Info("Library")));
            Assert.Equal("mi:console", FileIcons.ForProject("App.csproj", Info("Exe")));
            Assert.Equal("beaker@Test", FileIcons.ForProject("Tests.csproj", Info("Exe", test: true)));
            Assert.Equal("globe@Navigate", FileIcons.ForProject("Web.csproj", Info("Exe", false, "wwwroot/site.css")));
        }
        finally
        {
            FileIcons.Material = false;
        }

        // Every icon these produce exists, and Material's keep their own colors.
        var names = File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Icons", "material-icons.txt")).Where(l => l.Length > 0 && !l.StartsWith('#'));
        foreach (var name in names)
        {
            var icon = IconSet.GetFilled(IconSet.MaterialPrefix + name);
            Assert.True(icon is not null && icon.Layers.Count > 0 && icon.Layers.Any(l => l.Color != 0), name);
            Assert.True(icon.Bounds.Right <= icon.DesignSize + 0.5 && icon.Bounds.Bottom <= icon.DesignSize + 0.5 && icon.Bounds.X >= -0.5, $"{name}: {icon.Bounds} in {icon.DesignSize}");
        }

        // The classic icons are 16 px drawings that FileIcons can name: every name it maps to exists.
        var classic = Directory.GetFiles(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Icons", "Classic"), "classic-*.svg")
            .Select(p => Path.GetFileNameWithoutExtension(p)["classic-".Length..]).ToList();
        Assert.Contains("document", classic);
        foreach (var name in classic)
        {
            var icon = IconSet.GetFilled(IconSet.ClassicPrefix + name);
            Assert.True(icon is not null && icon.Layers.Count > 0 && icon.DesignSize == 16, name);
            Assert.True(icon.Bounds.Right <= 16 && icon.Bounds.Bottom <= 16 && icon.Bounds.X >= 0 && icon.Bounds.Y >= 0, $"{name}: {icon.Bounds}");
        }

        var files = new[] { "a.cs", "a.fs", "a.razor", "a.axaml", "a.xml", "a.html", "a.props", "a.json", "a.yml", "a.md", "a.css", "a.scss", "a.js",
            "a.ts", "a.ps1", "a.sh", "a.sql", "a.png", "a.svg", "a.http", "a.proto", "a.log", "a.pfx", "LICENSE", "a.snk", "packages.lock.json",
            "a.dll", "a.exe", "a.zip", "a.pdf", "a.csv", "a.docx", "a.tt", "web.config", ".editorconfig", ".gitignore", "Dockerfile", "CHANGELOG", "a.txt" };
        Assert.All(files, file => Assert.Contains(FileIcons.ForFile(file)[IconSet.ClassicPrefix.Length..], classic));
    }

    [AvaloniaFact]
    public async Task The_explorer_tabs_and_navigation_bar_show_file_and_symbol_icons_and_the_outlining_markers_follow_the_theme()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var folder = await f.OpenLanguageFeaturesAsync();
        var explorer = f.Shell.Explorer;
        var program = Path.Combine(folder, "src", "App", "Program.cs");
        explorer.Reveal("f:" + program);
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Name == "Program.cs"));
        Assert.Equal("cl:csharp", explorer.Rows.First(r => r.Name == "Program.cs").Icon);
        Assert.Equal(FileIcons.Solution, explorer.Rows.First(r => r.Node.Kind == Codalus.Workbench.Projects.ExplorerNodeKind.Solution).Icon);
        Assert.Equal("cl:project-csharp", explorer.Rows.First(r => r.Name == "App").Icon);

        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        Assert.NotNull(document);
        Assert.Equal("cl:csharp", document.TabIcon);
        await ShellFixture.PumpUntil(() => document.View is { NavigationBar.Types.Count: > 0 });
        var bar = document.View!.NavigationBar;
        var pickers = bar.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(2, pickers.Count);
        Assert.All(pickers, p => Assert.True(p.Classes.Contains("navigationBar") && p.Bounds.Height <= 24, $"{p.Bounds}"));
        Assert.Contains("@", bar.Types[0].Icon, StringComparison.Ordinal); // a colored symbol icon

        // The outlining markers take the editor's background and a muted glyph, in every theme.
        var margin = document.View.Editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Folding.FoldingMargin>().FirstOrDefault();
        Assert.NotNull(margin);
        foreach (var theme in new[] { "dark", "light", "blue", "highContrast" })
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);
            ShellFixture.Pump();
            if (margin is not null)
            {
                Assert.Equal(((ISolidColorBrush)document.View.Editor.Background!).Color, ((ISolidColorBrush)margin.FoldingMarkerBackgroundBrush).Color);
                Assert.NotEqual(Colors.White, theme is "dark" or "highContrast" ? ((ISolidColorBrush)margin.FoldingMarkerBackgroundBrush).Color : default);
            }
        }
    }

    // ------------------------------------------------------------------ keyboard (M34g)

    [AvaloniaFact]
    public async Task The_new_views_reach_every_control_with_Tab_in_reading_order()
    {
        // M34g: the views M40–M47 added, with data, walked with Tab from their first control: every visible, enabled
        // control a user works with is reached, the way a keyboard user goes through them.
        await using var f = ShellFixture.Create();
        var problems = new List<string>();
        void Walk(string surface, Control view, double width, double height)
        {
            var host = new Window { Width = width, Height = height, Content = view };
            host.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            problems.AddRange(Unreached(host).Select(p => $"{surface}: {p}"));
            host.Content = null;
            host.Close();
            ShellFixture.Pump();
        }

        // The WCF Test Client with the samples' WSDL file read and an operation's form.
        using (var client = new Codalus.Workbench.Wcf.WcfTestClient())
        {
            var wcf = new ViewModels.WcfTestClientViewModel(client, Path.Combine(ShellFixture.RepoRoot, "samples", "Wcf", "contracts", "CalculatorService.wsdl"), () => Task.FromResult<IReadOnlyList<Codalus.Workbench.Wcf.DiscoveredService>>([]), () => Task.FromResult<string?>(null));
            await wcf.LoadAsync();
            Assert.True(wcf.Error is null, wcf.Error);
            wcf.Select("Greet");
            Walk("wcf-test-client", new WcfTestClientView { DataContext = wcf }, 900, 640);
        }

        // The Toolbox with a designer, the Live Property Explorer with properties.
        var toolbox = new ViewModels.ToolboxViewModel { IsAvailable = true };
        Walk("toolbox", new ToolboxView { DataContext = toolbox }, 280, 420);
        var properties = new ViewModels.LivePropertiesViewModel(() => null);
        await properties.ShowAsync(new ViewModels.LiveNodeViewModel(1, "Button", "OkButton", false, false));
        properties.Rows.Add(new ViewModels.LivePropertyRow(new Codalus.Workbench.Previewer.LiveProperty("Margin", "4", "Thickness", "Local", false), (_, _) => Task.CompletedTask));
        Walk("live-property-explorer", new LivePropertiesView { DataContext = properties }, 280, 420);

        // The setup window's settings step, and Code Search's floating window over the main window (Feature Search).
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        await f.Shell.SetupModel!.NextAsync();
        await f.Shell.SetupModel.NextAsync();
        ShellFixture.Pump();
        problems.AddRange(Unreached(f.Shell.SetupWindowView!).Select(p => $"setup (settings): {p}"));
        await f.Shell.SetupModel.FinishAsync(done: false);

        await f.Shell.FeatureSearchAsync();
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearch is { IsFloatingOpen: true });
        f.Shell.CodeSearch.Query = "build";
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearch.Rows.Count > 0 && !f.Shell.CodeSearch.IsSearching);
        var search = f.Window.GetVisualDescendants().OfType<CodeSearchView>().First(v => v.IsEffectivelyVisible);
        problems.AddRange(Unreached(search).Select(p => $"code-search: {p}"));
        f.Shell.CodeSearch.Close();

        // M43: Convert to SDK-Style's preview (its notes and changes, then the buttons), and the Call Stack with a
        // desktop-CLR stack (the thread picker, then the frames).
        var dialog = new ViewModels.DialogViewModel();
        var preview = dialog.ShowAsync(new ViewModels.DialogRequest("Convert LegacyApp to SDK-style", "The project file is rewritten as an SDK-style project (Microsoft.NET.Sdk). Review the changes:", ["Convert", "Cancel"])
        {
            Details =
            [
                new ViewModels.DetailLine("• packages.config's 1 package(s) became PackageReference items, and packages.config goes.", "note"),
                new ViewModels.DetailLine("LegacyApp/LegacyApp.csproj", "header"),
                new ViewModels.DetailLine("- <Project ToolsVersion=\"15.0\">", "removed"),
                new ViewModels.DetailLine("+ <Project Sdk=\"Microsoft.NET.Sdk\">", "added"),
            ],
        });
        Walk("convert-to-sdk-style", new DialogView { DataContext = dialog }, 640, 560);
        dialog.Cancel();
        await preview;

        var callStack = new ViewModels.CallStackViewModel(f.Shell.Debugger, (_, _) => Task.CompletedTask);
        callStack.Threads.Add(new Codalus.Debugging.DapThread(1, "Main Thread"));
        callStack.SelectedThread = callStack.Threads[0];
        callStack.Frames.Add(new ViewModels.FrameRow(new Codalus.Debugging.StackFrame(1, "NetFxDebuggee.Program.Main(string[] args)", new Codalus.Debugging.Source("Program.cs", "/src/Program.cs"), 18, 9), true, true));
        callStack.Frames.Add(new ViewModels.FrameRow(new Codalus.Debugging.StackFrame(2, "[Managed to Native Transition]", null, 0, 0, PresentationHint: "label"), false, false));
        Walk("call-stack", new CallStackView { DataContext = callStack }, 280, 420);

        Assert.True(problems.Count == 0, "Not reached with Tab:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// The controls under <paramref name="root"/> that Tab, from the first one, never reaches (a list or tree counts as
    /// reached when its item is); a toolbar's items in its overflow menu are left out (the menu reaches them).
    /// </summary>
    internal static IEnumerable<string> Unreached(Visual root)
    {
        static bool Overflowed(Control c) => c.FindAncestorOfType<ToolbarPanel>() is { } toolbar && toolbar.Overflowed.Any(o => o == c);
        static bool Interactive(Control c) => c is Button or Avalonia.Controls.Primitives.ToggleButton or ComboBox or TextBox or ListBox or TreeView or Slider
            && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Focusable && KeyboardNavigation.GetIsTabStop(c) && c.Bounds.Width >= 1
            && (c.TemplatedParent is null || c.TemplatedParent is Avalonia.Controls.Presenters.ContentPresenter)
            && c.FindAncestorOfType<ListBoxItem>() is null && c.FindAncestorOfType<TreeViewItem>() is null && !Overflowed(c);

        var targets = root.GetVisualDescendants().OfType<Control>().Where(Interactive).ToList();
        if (targets.Count == 0 || TopLevel.GetTopLevel(root) is not { } top)
        {
            yield break;
        }

        // Tab pressed in the window, from the first control, until the focus comes back, leaves the view or stops moving.
        var visited = new List<IInputElement>();
        targets[0].Focus(NavigationMethod.Tab);
        ShellFixture.Pump();
        for (var step = 0; step < 300; step++)
        {
            if (top.FocusManager?.GetFocusedElement() is not { } focused || focused is Visual v && v != root && !root.IsVisualAncestorOf(v) || step > 0 && focused == visited[0] || visited.Contains(focused) && focused == visited[^1])
            {
                break;
            }

            visited.Add(focused);
            if (top is Window window)
            {
                Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            }

            ShellFixture.Pump();
        }

        // The walk itself: a view with several controls must have moved the focus through more than one.
        if (visited.Count < Math.Min(2, targets.Count))
        {
            yield return $"Tab didn't move the focus from {targets[0].GetType().Name} ({visited.Count} focused of {targets.Count})";
            yield break;
        }

        foreach (var target in targets)
        {
            if (!visited.Any(v => v == target || v is Visual visual && target.IsVisualAncestorOf(visual)))
            {
                var name = Avalonia.Automation.AutomationProperties.GetName(target) is { Length: > 0 } n ? n : (target as ContentControl)?.Content?.ToString() ?? target.Name ?? string.Empty;
                yield return $"{target.GetType().Name} '{name}'";
            }
        }
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
