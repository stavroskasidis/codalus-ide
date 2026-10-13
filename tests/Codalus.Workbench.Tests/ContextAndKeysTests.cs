using Codalus.Platform;
using Codalus.Workbench.Context;

namespace Codalus.Workbench.Tests;

public class WhenClauseTests
{
    private static ContextKeyService Context(params (string Key, object Value)[] values)
    {
        var context = new ContextKeyService();
        foreach (var (key, value) in values)
        {
            context.Set(key, value);
        }

        return context;
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("isMac", false)]
    [InlineData("editorFocus", true)]
    [InlineData("!editorFocus", false)]
    [InlineData("editorFocus && !isMac", true)]
    [InlineData("isMac || editorFocus", true)]
    [InlineData("isMac || (editorFocus && theme == 'dark')", true)]
    [InlineData("theme == dark", true)]
    [InlineData("theme != dark", false)]
    [InlineData("view == 'hello.view'", true)]
    [InlineData("view =~ /^hello\\./", true)]
    [InlineData("count > 2", true)]
    [InlineData("count <= 2", false)]
    [InlineData("emptyText", false)]
    public void Evaluates(string text, bool expected)
    {
        var context = Context(("editorFocus", true), ("theme", "dark"), ("view", "hello.view"), ("count", 3), ("emptyText", ""));
        Assert.Equal(expected, WhenClause.Parse(text).Evaluate(context));
    }

    [Theory]
    [InlineData("a &&")]
    [InlineData("(a")]
    [InlineData("a == 'unterminated")]
    [InlineData("a b")]
    public void Rejects_malformed_clauses(string text)
    {
        Assert.Throws<FormatException>(() => WhenClause.Parse(text));
        Assert.False(WhenClause.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Overlay_wins_over_global_keys()
    {
        var context = Context(("focusedView", "output"));
        var scoped = context.With(new Dictionary<string, object?> { ["focusedView"] = "hello.view" });
        Assert.True(WhenClause.Parse("focusedView == hello.view").Evaluate(scoped));
        Assert.False(WhenClause.Parse("focusedView == hello.view").Evaluate(context));
    }
}

public class KeysTests
{
    [Fact]
    public void Primary_is_cmd_on_mac_and_ctrl_elsewhere()
    {
        Assert.Equal(new KeyChord(KeyModifiers.Meta | KeyModifiers.Shift, "p"), KeyChord.Parse("primary+shift+p", PlatformKind.MacOS));
        Assert.Equal(new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Shift, "p"), KeyChord.Parse("primary+shift+p", PlatformKind.Windows));
        Assert.Equal(new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Shift, "p"), KeyChord.Parse("Shift+CTRL+P", PlatformKind.Linux));
    }

    [Fact]
    public void Labels_follow_platform_conventions()
    {
        var chord = new KeyChord(KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt, "p");
        Assert.Equal("⌥⇧⌘P", chord.ToLabel(PlatformKind.MacOS));
        Assert.Equal("Ctrl+Shift+P", KeyChord.Parse("ctrl+shift+p", PlatformKind.Windows).ToLabel(PlatformKind.Windows));
        Assert.Equal("Ctrl+K Ctrl+S", KeySequence.Parse("ctrl+k ctrl+s", PlatformKind.Linux).ToLabel(PlatformKind.Linux));
        Assert.Equal("F5", KeyChord.Parse("f5", PlatformKind.Windows).ToLabel(PlatformKind.Windows));
    }

    [Fact]
    public void Aliases_and_round_trip()
    {
        Assert.Equal("escape", KeyChord.Parse("esc", PlatformKind.Windows).Key);
        Assert.Equal("ctrl+shift+enter", KeyChord.Parse("shift+ctrl+return", PlatformKind.Windows).ToString());
        var sequence = KeySequence.Parse("ctrl+k ctrl+s", PlatformKind.Windows);
        Assert.Equal(sequence, KeySequence.Parse(sequence.ToString(), PlatformKind.Windows));
    }

    [Fact]
    public void Unknown_modifier_is_an_error() =>
        Assert.Throws<FormatException>(() => KeyChord.Parse("hyper+x", PlatformKind.Windows));
}

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Windows_uses_roaming_config_and_local_data()
    {
        var paths = AppPaths.Resolve(PlatformKind.Windows, Env(("APPDATA", @"C:\U\Roaming"), ("LOCALAPPDATA", @"C:\U\Local"), ("USERPROFILE", @"C:\U")), null);
        Assert.Equal(Path.Combine(@"C:\U\Roaming", "Codalus"), paths.Config);
        Assert.Equal(Path.Combine(@"C:\U\Local", "Codalus", "Logs"), paths.Logs);
    }

    [Fact]
    public void Linux_follows_xdg_with_defaults()
    {
        var paths = AppPaths.Resolve(PlatformKind.Linux, Env(("HOME", "/home/u"), ("XDG_CONFIG_HOME", "/cfg"), ("XDG_CACHE_HOME", "relative/ignored")), null);
        Assert.Equal(Path.Combine("/cfg", "codalus"), paths.Config);
        Assert.Equal(Path.Combine("/home/u", ".local", "share", "codalus"), paths.Data);
        Assert.Equal(Path.Combine("/home/u", ".cache", "codalus"), paths.Cache);
        Assert.Equal(Path.Combine("/home/u", ".local", "state", "codalus"), paths.State);
    }

    [Fact]
    public void MacOS_uses_library_folders()
    {
        var paths = AppPaths.Resolve(PlatformKind.MacOS, Env(("HOME", "/Users/u")), null);
        Assert.Equal(Path.Combine("/Users/u", "Library", "Application Support", "Codalus"), paths.Config);
        Assert.Equal(Path.Combine("/Users/u", "Library", "Caches", "Codalus"), paths.Cache);
        Assert.Equal(Path.Combine("/Users/u", "Library", "Logs", "Codalus"), paths.Logs);
    }

    [Fact]
    public void User_data_dir_override_puts_everything_under_one_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "dd-root");
        var paths = AppPaths.Resolve(PlatformKind.Windows, Env((AppPaths.UserDataDirVariable, root)), null);
        Assert.All(new[] { paths.Config, paths.Data, paths.State, paths.Cache, paths.Logs }, p => Assert.StartsWith(root, p, StringComparison.Ordinal));
        Assert.Equal(Path.Combine(root, "config", "settings.json"), paths.SettingsFile);
    }
}
