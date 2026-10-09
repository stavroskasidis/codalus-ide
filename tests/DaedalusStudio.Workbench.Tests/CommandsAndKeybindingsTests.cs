using System.Text.Json;
using DaedalusStudio.Platform;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Keybindings;
using DaedalusStudio.Workbench.Menus;

namespace DaedalusStudio.Workbench.Tests;

public class CommandRegistryTests
{
    private readonly ContextKeyService _context = new();

    [Fact]
    public async Task Executes_handlers_and_tracks_recent_commands()
    {
        var registry = new CommandRegistry(_context);
        var calls = new List<string>();
        registry.Register(new CommandDescriptor("a.one", "One", ContributionSource.Shell), () => calls.Add("one"));
        registry.Register(new CommandDescriptor("a.two", "Two", ContributionSource.Shell) { Category = "A" }, () => calls.Add("two"));

        await registry.ExecuteAsync("a.one", TestContext.Current.CancellationToken);
        await registry.ExecuteAsync("a.two", TestContext.Current.CancellationToken);
        await registry.ExecuteAsync("a.one", TestContext.Current.CancellationToken);
        Assert.Empty(registry.RecentCommandIds);
        registry.MarkRecent("a.one");
        registry.MarkRecent("a.two");
        registry.MarkRecent("a.one");

        Assert.Equal(["one", "two", "one"], calls);
        Assert.Equal(["a.one", "a.two"], registry.RecentCommandIds);
        Assert.Equal("A: Two", registry.Find("a.two")!.DisplayTitle);
    }

    [Fact]
    public async Task Enablement_is_enforced()
    {
        var registry = new CommandRegistry(_context);
        registry.Register(new CommandDescriptor("a.save", "Save", ContributionSource.Shell) { Enablement = WhenClause.Parse("dirty") }, () => { });

        Assert.False(registry.IsEnabled("a.save"));
        await Assert.ThrowsAsync<CommandNotEnabledException>(() => registry.ExecuteAsync("a.save", TestContext.Current.CancellationToken));
        _context.Set("dirty", true);
        await registry.ExecuteAsync("a.save", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Declared_commands_without_handlers_trigger_the_missing_handler_hook()
    {
        var registry = new CommandRegistry(_context);
        registry.Declare(new CommandDescriptor("plugin.cmd", "Plugin", ContributionSource.Plugin("p", builtIn: false)));
        registry.MissingHandler = (id, _) =>
        {
            registry.RegisterHandler(id, (args, _) => Task.FromResult<JsonElement?>(JsonDocument.Parse("42").RootElement.Clone()));
            return Task.CompletedTask;
        };

        var result = await registry.ExecuteAsync("plugin.cmd", TestContext.Current.CancellationToken);

        Assert.Equal(42, result!.Value.GetInt32());
        await Assert.ThrowsAsync<CommandNotFoundException>(() => registry.ExecuteAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_a_registration_removes_only_the_handler()
    {
        var registry = new CommandRegistry(_context);
        var registration = registry.Register(new CommandDescriptor("a.x", "X", ContributionSource.Shell), () => { });
        registration.Dispose();

        Assert.NotNull(registry.Find("a.x"));
        await Assert.ThrowsAsync<CommandNotFoundException>(() => registry.ExecuteAsync("a.x", TestContext.Current.CancellationToken));
    }
}

public class KeybindingServiceTests
{
    private static KeySequence Keys(string text) => KeySequence.Parse(text, PlatformKind.Windows);

    private static KeybindingRule Rule(string keys, string command, KeybindingSource source = KeybindingSource.Default, string? when = null) =>
        new(Keys(keys), command, WhenClause.Parse(when), source);

    private static KeyChord Chord(string text) => KeyChord.Parse(text, PlatformKind.Windows);

    [Fact]
    public void Dispatches_single_and_chorded_bindings()
    {
        var service = new KeybindingService(new ContextKeyService(), PlatformKind.Windows);
        service.SetDefaults([Rule("ctrl+shift+p", "palette"), Rule("ctrl+k ctrl+s", "shortcuts"), Rule("ctrl+k ctrl+t", "theme")]);

        Assert.Equal("palette", service.Dispatch(Chord("ctrl+shift+p")).Rule!.Command);
        Assert.Equal(KeyDispatchKind.Pending, service.Dispatch(Chord("ctrl+k")).Kind);
        Assert.Equal("theme", service.Dispatch(Chord("ctrl+t")).Rule!.Command);
        Assert.Equal(KeyDispatchKind.NoMatch, service.Dispatch(Chord("ctrl+t")).Kind);
    }

    [Fact]
    public void A_broken_chord_restarts_with_the_new_key()
    {
        var service = new KeybindingService(new ContextKeyService(), PlatformKind.Windows);
        service.SetDefaults([Rule("ctrl+k ctrl+s", "shortcuts"), Rule("ctrl+shift+p", "palette")]);

        Assert.Equal(KeyDispatchKind.Pending, service.Dispatch(Chord("ctrl+k")).Kind);
        Assert.Equal("palette", service.Dispatch(Chord("ctrl+shift+p")).Rule!.Command);
    }

    [Fact]
    public void User_overrides_plugin_overrides_default_and_when_clauses_filter()
    {
        var context = new ContextKeyService();
        var service = new KeybindingService(context, PlatformKind.Windows);
        service.SetDefaults([Rule("f5", "default.run"), Rule("f6", "default.focus")]);
        service.SetPluginRules("p", [Rule("f5", "plugin.run", KeybindingSource.Plugin), Rule("f6", "plugin.only", KeybindingSource.Plugin, "pluginView")]);

        Assert.Equal("plugin.run", service.Dispatch(Chord("f5")).Rule!.Command);
        Assert.Equal("default.focus", service.Dispatch(Chord("f6")).Rule!.Command);
        context.Set("pluginView", true);
        Assert.Equal("plugin.only", service.Dispatch(Chord("f6")).Rule!.Command);

        service.SetUserRules([Rule("f5", "user.run", KeybindingSource.User)]);
        Assert.Equal("user.run", service.Dispatch(Chord("f5")).Rule!.Command);
    }

    [Fact]
    public void Removal_rules_hide_inherited_bindings()
    {
        var service = new KeybindingService(new ContextKeyService(), PlatformKind.Windows);
        service.SetDefaults([Rule("ctrl+w", "close")]);
        service.SetUserRules([new KeybindingRule(null, "-close", WhenClause.Always, KeybindingSource.User)]);

        Assert.Equal(KeyDispatchKind.NoMatch, service.Dispatch(Chord("ctrl+w")).Kind);
        Assert.Null(service.GetBinding("close"));
    }

    [Fact]
    public void Rebinding_round_trips_through_keybindings_json()
    {
        var service = new KeybindingService(new ContextKeyService(), PlatformKind.Windows);
        service.SetDefaults([Rule("ctrl+shift+p", "palette")]);

        var json = service.RebindForUser("palette", Keys("f1"));
        Assert.Contains("\"-palette\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ctrl+shift+p\"", json, StringComparison.Ordinal);
        var reloaded = new KeybindingService(new ContextKeyService(), PlatformKind.Windows);
        reloaded.SetDefaults([Rule("ctrl+shift+p", "palette")]);
        reloaded.SetUserRules(KeybindingService.ParseRules(json, KeybindingSource.User, PlatformKind.Windows, null, null));

        Assert.Equal("palette", reloaded.Dispatch(Chord("f1")).Rule!.Command);
        Assert.Equal(KeyDispatchKind.NoMatch, reloaded.Dispatch(Chord("ctrl+shift+p")).Kind);
        Assert.Equal(Keys("f1"), reloaded.GetBinding("palette"));

        reloaded.ResetForUser("palette");
        Assert.Equal(Keys("ctrl+shift+p"), reloaded.GetBinding("palette"));
    }

    [Fact]
    public void Parses_platform_specific_keys_and_reports_bad_entries()
    {
        const string Json = """
            // comment
            [
              { "key": "ctrl+alt+h", "mac": "cmd+alt+h", "command": "hello" },
              { "key": "ctrl+x" },
              { "key": "hyper+x", "command": "bad" },
            ]
            """;
        var diagnostics = new List<string>();

        var mac = KeybindingService.ParseRules(Json, KeybindingSource.Plugin, PlatformKind.MacOS, "p", diagnostics);
        var win = KeybindingService.ParseRules(Json, KeybindingSource.Plugin, PlatformKind.Windows, "p", null);

        Assert.Equal(KeySequence.Parse("cmd+alt+h", PlatformKind.MacOS), Assert.Single(mac).Keys);
        Assert.Equal(KeySequence.Parse("ctrl+alt+h", PlatformKind.Windows), Assert.Single(win).Keys);
        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public void Finds_conflicts()
    {
        var service = new KeybindingService(new ContextKeyService(), PlatformKind.Windows);
        service.SetDefaults([Rule("ctrl+j", "a"), Rule("ctrl+j", "b"), Rule("ctrl+j", "c", when: "x")]);

        var conflict = Assert.Single(service.FindConflicts());
        Assert.Equal(["a", "b"], conflict.Select(r => r.Command));
    }
}

public class MenuRegistryTests
{
    [Fact]
    public void Groups_orders_filters_and_labels_items()
    {
        var context = new ContextKeyService();
        var commands = new CommandRegistry(context);
        foreach (var (id, title) in new[] { ("open", "Open"), ("save", "Save"), ("exit", "Exit"), ("secret", "Secret") })
        {
            commands.Register(new CommandDescriptor(id, title, ContributionSource.Shell), () => { });
        }

        var keybindings = new KeybindingService(context, PlatformKind.Windows);
        keybindings.SetDefaults([new KeybindingRule(KeySequence.Parse("ctrl+s", PlatformKind.Windows), "save", WhenClause.Always, KeybindingSource.Default)]);
        var menus = new MenuRegistry(commands, keybindings, context);
        var source = ContributionSource.Shell;
        menus.Add(
        [
            new MenuItemRule("menuBar/file", "exit", source) { Group = "exit" },
            new MenuItemRule("menuBar/file", "save", source) { Group = "file", Order = 2 },
            new MenuItemRule("menuBar/file", "open", source) { Group = "file", Order = 1 },
            new MenuItemRule("menuBar/file", "secret", source) { When = WhenClause.Parse("debugMode") },
        ]);

        var items = menus.GetItems("menuBar/file");

        Assert.Equal(["open", "save", "exit"], items.Select(i => i.Command));
        Assert.Equal("Ctrl+S", items[1].KeyLabel);
        Assert.Equal("exit", items[2].Group);
    }

    [Fact]
    public void Palette_rules_only_restrict_the_commands_they_name()
    {
        var context = new ContextKeyService();
        var commands = new CommandRegistry(context);
        var menus = new MenuRegistry(commands, new KeybindingService(context, PlatformKind.Windows), context);
        menus.Add([new MenuItemRule(MenuLocations.CommandPalette, "debugOnly", ContributionSource.Shell) { When = WhenClause.Parse("isDevelopment") }]);

        Assert.True(menus.IsVisibleInPalette("anything", context));
        Assert.False(menus.IsVisibleInPalette("debugOnly", context));
        context.Set("isDevelopment", true);
        Assert.True(menus.IsVisibleInPalette("debugOnly", context));
    }
}
