using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Plugins;

namespace Codalus.Shell.Tests;

/// <summary>
/// The static half of the command sweep (the smoke step <c>shell.commandSweep</c> is the runtime half): every menu
/// item, key binding and toolbar button runs a registered command whose enablement can hold, every command is reached
/// from somewhere, and no default key binding takes another's keys where both apply. With every built-in plugin loaded.
/// </summary>
public sealed partial class CommandSweepTests
{
    private static readonly PlatformKind[] s_platforms = [PlatformKind.Windows, PlatformKind.Linux, PlatformKind.MacOS];
    private static readonly string[] s_schemes = ["default", "vscode", "resharper", "emacs"];

    /// <summary>
    /// Commands no menu, palette entry, key or toolbar button reaches: they're for plugins, CodeLens, the start page and
    /// the agent (they take arguments). A new one fails the test: give it an entry point, or list it here with its user.
    /// </summary>
    private static readonly HashSet<string> s_apiCommands = new(StringComparer.Ordinal)
    {
        "editor.activePosition",        // plugins: the active editor's position
        "editor.action.showReferences", // C# CodeLens
        "fsharp.showReferences",        // F# CodeLens
        "project.addReferenceTo",       // plugins and the templates plugin: a reference between two projects
        "solution.addProjectFile",      // the templates plugin: adds a created project
        "solution.getInfo",             // plugins: the solution's projects
        "welcome.nextTip",              // the start page's tip
        "workbench.getRecentSolutions", // the start page
        "workbench.openExternal",       // the start page's links
        "workbench.openFile",           // plugins: opens a file at a line
        "workbench.setSetting",         // plugins
    };

    /// <summary>
    /// Keys two default rules share where both apply, the later one winning on purpose: a narrower context
    /// (F# Interactive in F# files, the terminal's own split), or contexts that never meet (a XAML file and a form; hot
    /// reload and the Windows-only forms designer on Linux).
    /// </summary>
    private static readonly HashSet<string> s_intendedOverrides = new(StringComparer.Ordinal)
    {
        "ctrl+e ctrl+e: interactive.sendSelection < fsi.sendSelection",
        "meta+e meta+e: interactive.sendSelection < fsi.sendSelection",
        "meta+\\: workbench.splitEditorRight < terminal.split",
        "shift+f7: xaml.showPreview < winforms.viewDesigner",
        "shift+alt+f10: run.applyCodeChanges < winforms.smartTag",
        "f5: debug.start < wcf.invoke", // the WCF Test Client focused: F5 invokes the operation, as in the classic tool
    };

    /// <summary>
    /// A single chord that starts another binding's sequence where both apply, on purpose: in a focused terminal ⌘\
    /// splits it (VS Code's key on macOS), so the Error List's ⌘\ ⌘E works from everywhere else. (Split Editor Right's
    /// Ctrl+\ is the VS Code scheme's, where the Error List has no chord.)
    /// </summary>
    private static readonly HashSet<string> s_intendedPrefixes = new(StringComparer.Ordinal)
    {
        "meta+\\ terminal.split / meta+\\ meta+e workbench.view.errorList",
    };

    [AvaloniaFact]
    public async Task Every_menu_item_key_binding_and_toolbar_button_runs_a_registered_command_that_can_be_enabled()
    {
        await using var f = AllPlugins();
        var workbench = f.Shell.Workbench;
        var known = ContextKeysSetBySource();
        var problems = new List<string>();

        foreach (var rule in workbench.Menus.Rules.Where(r => r.Submenu is null))
        {
            CheckCommand(rule.Command, $"menu {rule.Location}");
            CheckClause(rule.When, $"menu {rule.Location} → {rule.Command}");
        }

        foreach (var platform in s_platforms)
        {
            foreach (var scheme in s_schemes)
            {
                foreach (var rule in BuiltIns.ParseDefaultKeybindings(platform, scheme))
                {
                    CheckCommand(rule.Command, $"{platform} {scheme} key {rule.Keys}");
                    CheckClause(rule.When, $"{platform} {scheme} key {rule.Keys} → {rule.Command}");
                }
            }

            foreach (var plugin in PluginDiscovery.Scan([(PluginsDir, true)], platform))
            {
                foreach (var binding in plugin.Manifest?.Contributes?.Keybindings ?? [])
                {
                    var rule = KeybindingService.TryParseRule(binding, KeybindingSource.Plugin, platform, plugin.Id, null);
                    Assert.NotNull(rule);
                    CheckCommand(rule.Command, $"{platform} {plugin.Id} key {rule.Keys}");
                    CheckClause(rule.When, $"{platform} {plugin.Id} key {rule.Keys} → {rule.Command}");
                }
            }
        }

        foreach (var command in ToolbarCommands())
        {
            CheckCommand(command, "the standard toolbar");
        }

        foreach (var descriptor in workbench.Commands.Descriptors)
        {
            CheckClause(descriptor.Enablement, $"command {descriptor.Id}");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems.Distinct()));

        void CheckCommand(string id, string where)
        {
            if (workbench.Commands.Find(id) is null)
            {
                problems.Add($"{where}: '{id}' is not registered");
            }
            else if (!HasHandler(workbench.Commands, id) && !DeclaredByPlugin(f, id))
            {
                problems.Add($"{where}: '{id}' has no handler and no plugin declares it");
            }
        }

        void CheckClause(WhenClause when, string where)
        {
            foreach (var key in Clause.Of(when.Text).Keys.Where(k => !known.Contains(k)))
            {
                problems.Add($"{where}: the when-clause '{when.Text}' reads '{key}', which nothing sets");
            }

            if (!Clause.Of(when.Text).CanHold(when))
            {
                problems.Add($"{where}: the when-clause '{when.Text}' can never hold");
            }
        }
    }

    [AvaloniaFact]
    public async Task Every_command_is_reached_from_a_menu_the_palette_a_key_or_the_toolbar()
    {
        await using var f = AllPlugins();
        var workbench = f.Shell.Workbench;
        var reached = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in workbench.Menus.Rules.Where(r => r.Submenu is null && r.Location != MenuLocations.CommandPalette))
        {
            reached.Add(rule.Command);
        }

        foreach (var platform in s_platforms)
        {
            foreach (var scheme in s_schemes)
            {
                reached.UnionWith(BuiltIns.ParseDefaultKeybindings(platform, scheme).Select(r => r.Command));
            }
        }

        var hiddenFromPalette = workbench.Menus.Rules.Where(r => r.Location == MenuLocations.CommandPalette && r.When.Text == "false").Select(r => r.Command).ToHashSet(StringComparer.Ordinal);
        reached.UnionWith(workbench.Commands.Descriptors.Where(d => d.ShowInPalette && !hiddenFromPalette.Contains(d.Id)).Select(d => d.Id));
        reached.UnionWith(ToolbarCommands());

        // Only new orphans fail: a plugin registers its API commands as it activates (welcome.nextTip), maybe not yet.
        var orphans = workbench.Commands.Descriptors.Select(d => d.Id).Where(id => !reached.Contains(id) && !s_apiCommands.Contains(id)).Order(StringComparer.Ordinal).ToList();
        Assert.True(orphans.Count == 0, $"Commands nothing reaches (give each an entry point, or list it in s_apiCommands with its user): {string.Join(", ", orphans)}");

        // Every submenu with items hangs off a menu that is shown somewhere.
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(workbench.Menus.Rules.Select(r => r.Location).Where(l => l.Count(c => c == '/') <= 1 || l.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal) && l.Count(c => c == '/') == 1));
        while (queue.TryDequeue(out var location))
        {
            if (shown.Add(location))
            {
                foreach (var rule in workbench.Menus.Rules.Where(r => r.Location == location && r.Submenu is not null))
                {
                    queue.Enqueue(rule.Submenu!);
                }
            }
        }

        var hidden = workbench.Menus.Rules.Select(r => r.Location).Distinct().Where(l => !shown.Contains(l)).ToList();
        Assert.True(hidden.Count == 0, $"Menu locations no menu shows: {string.Join(", ", hidden)}");
    }

    [Theory]
    [InlineData(PlatformKind.Windows)]
    [InlineData(PlatformKind.Linux)]
    [InlineData(PlatformKind.MacOS)]
    public void No_default_key_binding_takes_the_keys_of_another_where_both_apply(PlatformKind platform)
    {
        var problems = new List<string>();
        foreach (var scheme in s_schemes)
        {
            // ReSharper's and Emacs's rules come last to take keys from the default scheme on purpose (BuiltIns.Keymaps.cs):
            // only their clashes with each other count.
            var rules = BuiltIns.ParseDefaultKeybindings(platform, scheme);
            bool Own(KeybindingRule rule) => scheme is "default" or "vscode" || rule.When.Text.Contains($"keyboardScheme == {scheme}", StringComparison.Ordinal);
            for (var i = 0; i < rules.Count; i++)
            {
                for (var j = i + 1; j < rules.Count; j++)
                {
                    var (earlier, later) = (rules[i], rules[j]);
                    if (earlier.Command == later.Command || !Own(earlier) || !Own(later) || !Clause.CanHoldTogether(earlier.When, later.When, scheme))
                    {
                        continue;
                    }

                    if (earlier.Keys!.Equals(later.Keys))
                    {
                        var pair = $"{earlier.Keys}: {earlier.Command} < {later.Command}";
                        if (!s_intendedOverrides.Contains(pair))
                        {
                            problems.Add($"{scheme}: {pair} ({earlier.When.Text} / {later.When.Text})");
                        }
                    }
                    else if (IsPrefix(earlier.Keys, later.Keys!) || IsPrefix(later.Keys!, earlier.Keys))
                    {
                        // The single chord runs at once (KeybindingService.Dispatch), so the sequence can't be typed.
                        var (shorter, longer) = earlier.Keys.Chords.Count < later.Keys!.Chords.Count ? (earlier, later) : (later, earlier);
                        if (!s_intendedPrefixes.Contains($"{shorter.Keys} {shorter.Command} / {longer.Keys} {longer.Command}"))
                        {
                            problems.Add($"{scheme}: {earlier.Keys} ({earlier.Command}) and {later.Keys} ({later.Command}) start alike where both apply");
                        }
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems.Distinct()));
    }

    private static bool IsPrefix(KeySequence shorter, KeySequence longer) =>
        shorter.Chords.Count < longer.Chords.Count && shorter.Chords.SequenceEqual(longer.Chords.Take(shorter.Chords.Count));

    // ------------------------------------------------------------------ helpers

    private static string PluginsDir => Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "plugins");

    /// <summary>A shell with every built-in plugin, as installed (the shell's build copies them next to it).</summary>
    private static ShellFixture AllPlugins()
    {
        Assert.True(Directory.Exists(PluginsDir), $"{PluginsDir} has the built-in plugins (the shell's build copies them)");
        var f = ShellFixture.Create(builtInPlugins: PluginsDir);
        Assert.True(f.Shell.Plugins.Plugins.Count(p => p.IsBuiltIn) >= 8, "every built-in plugin is discovered");
        return f;
    }

    private static bool HasHandler(CommandRegistry commands, string id) =>
        ((IDictionary)typeof(CommandRegistry).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(commands)!).Contains(id);

    /// <summary>A plugin's declared command runs once the plugin activates (<see cref="CommandRegistry.MissingHandler"/>).</summary>
    private static bool DeclaredByPlugin(ShellFixture f, string id) =>
        f.Shell.Plugins.Plugins.Any(p => p.Manifest?.Contributes?.Commands?.Any(c => c.Id == id) == true);

    /// <summary>The standard toolbar's commands (ShellHost.Toolbar.cs, ToolbarViewModel).</summary>
    private static IEnumerable<string> ToolbarCommands()
    {
        var source = string.Concat(
            File.ReadAllText(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "ShellHost.Toolbar.cs")),
            File.ReadAllText(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "ViewModels", "ToolbarViewModel.cs")));
        return CommandLiteral().Matches(source).Select(m => m.Groups[1].Value).Where(id => id.Contains('.', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal);
    }

    /// <summary>The context keys the shell, the workbench and the plugins set (<c>Context.Set("key", …)</c>), and the tool window title scope's.</summary>
    private static HashSet<string> ContextKeysSetBySource()
    {
        var keys = new HashSet<string>(["view", "focusedView"], StringComparer.Ordinal);
        foreach (var folder in new[] { "Codalus.Shell", "Codalus.Workbench", "Plugins" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(ShellFixture.RepoRoot, "src", folder), "*.cs", SearchOption.AllDirectories))
            {
                keys.UnionWith(ContextSet().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value));
            }
        }

        return keys;
    }

    [GeneratedRegex("""Context\.Set\(\s*"([^"]+)"[,)]""")]
    private static partial Regex ContextSet();

    [GeneratedRegex("""["]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)["]""")]
    private static partial Regex CommandLiteral();

    /// <summary>The keys a when-clause reads and the values it compares them with, to try every combination.</summary>
    private sealed partial class Clause
    {
        private readonly Dictionary<string, HashSet<string>> _values = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _values.Keys;

        public static Clause Of(string text) => new Clause().Add(text);

        /// <summary>Names to try against <c>=~</c> patterns (the Solution Explorer's item names).</summary>
        private static readonly string[] s_sampleNames = ["App.csproj", "Strings.resx", "Template.tt", "docker-compose.yml", "App.dcproj", "index.html", "package.json", ".editorconfig"];

        private Clause Add(string text)
        {
            foreach (Match match in Comparison().Matches(text))
            {
                var values = Values(match.Groups["key"].Value);
                if (match.Groups["op"].Value == "=~")
                {
                    // Item names for the Solution Explorer's patterns, and the pattern's own words for alternatives (textFile|resx).
                    values.UnionWith(s_sampleNames);
                    values.UnionWith(Word().Matches(match.Groups["value"].Value).Select(m => m.Value));
                }
                else
                {
                    values.Add(match.Groups["value"].Value.Trim('\'', '"'));
                }
            }

            foreach (Match match in Word().Matches(Comparison().Replace(text, " ")))
            {
                if (match.Value is not ("true" or "false"))
                {
                    Values(match.Value);
                }
            }

            return this;

            HashSet<string> Values(string key) => _values.TryGetValue(key, out var known) ? known : _values[key] = new HashSet<string>(StringComparer.Ordinal);
        }

        public bool CanHold(WhenClause when) => Contexts(null).Any(when.Evaluate);

        /// <summary>Whether both clauses can hold at once in <paramref name="scheme"/>, one view having the focus.</summary>
        public static bool CanHoldTogether(WhenClause a, WhenClause b, string scheme)
        {
            var clause = Of(a.Text).Add(b.Text);
            return clause.Contexts(scheme).Any(c => a.Evaluate(c) && b.Evaluate(c));
        }

        private IEnumerable<IContextKeyReader> Contexts(string? scheme)
        {
            // editorTextFocus is focusedView == textFile (MainWindow.OnFocusChanged): one view has the focus.
            var domains = _values.Where(p => p.Key != "editorTextFocus").ToDictionary(
                p => p.Key,
                p => p.Key == "keyboardScheme" && scheme is not null ? [scheme] : (object?[])[null, true, .. p.Value],
                StringComparer.Ordinal);
            if (_values.ContainsKey("editorTextFocus"))
            {
                domains["focusedView"] = [.. domains.GetValueOrDefault("focusedView") ?? [null, true], "textFile"];
            }

            var names = domains.Keys.ToList();
            var total = names.Aggregate(1L, (n, k) => n * domains[k].Length);
            Assert.True(total <= 1 << 16, $"too many combinations for {string.Join(", ", names)}");
            for (var index = 0L; index < total; index++)
            {
                var context = new Dictionary<string, object?>(StringComparer.Ordinal);
                var rest = index;
                foreach (var name in names)
                {
                    context[name] = domains[name][rest % domains[name].Length];
                    rest /= domains[name].Length;
                }

                context["editorTextFocus"] = Equals(context.GetValueOrDefault("focusedView"), "textFile");
                yield return new Reader(context);
            }
        }

        [GeneratedRegex("""(?<key>[A-Za-z_][\w.\-:/]*)\s*(?<op>==|!=|=~|<=|>=|<|>)\s*(?<value>'[^']*'|"[^"]*"|/(?:[^/\\]|\\.)*/|[\w.\-:]+)""")]
        private static partial Regex Comparison();

        [GeneratedRegex("""[A-Za-z_][\w.\-:/]*""")]
        private static partial Regex Word();

        private sealed class Reader(Dictionary<string, object?> values) : IContextKeyReader
        {
            public object? GetValue(string key) => values.GetValueOrDefault(key);
        }
    }
}
