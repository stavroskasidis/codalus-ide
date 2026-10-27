using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.Platform;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>The ReSharper and Emacs keyboard schemes (M23a), over the default one.</summary>
public sealed class KeyboardSchemeTests
{
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;

    private static async Task SetSchemeAsync(ShellFixture f, string scheme)
    {
        await f.Shell.Workbench.Settings.SetAsync(BuiltIns.KeyboardSchemeSetting, scheme, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("keyboardScheme"), scheme));
    }

    private static string? CommandFor(ShellFixture f, string keys) =>
        f.Shell.Workbench.Keybindings.Peek(KeyChord.Parse(keys, PlatformInfo.Current)).Rule?.Command;

    [AvaloniaFact]
    public async Task Emacs_keys_move_mark_kill_and_save_in_the_editor()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "notes.txt");
        await File.WriteAllTextAsync(path, "alpha beta\ngamma delta\nlast\n", TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!;
        editor.Editor.CaretOffset = 0;
        editor.Editor.TextArea.Focus();
        await SetSchemeAsync(f, "emacs");

        // Ctrl+N down a line, Ctrl+E to its end, Ctrl+A back to its start.
        f.Press(Key.N, Ctrl);
        Assert.Equal(2, editor.Caret.Line);
        f.Press(Key.E, Ctrl);
        Assert.Equal("alpha beta\ngamma delta".Length, editor.Editor.CaretOffset);
        f.Press(Key.A, Ctrl);
        Assert.Equal("alpha beta\n".Length, editor.Editor.CaretOffset);

        // Ctrl+Space sets the mark: Ctrl+F extends the selection; Ctrl+G cancels it.
        f.Press(Key.Space, Ctrl);
        for (var i = 0; i < 5; i++)
        {
            f.Press(Key.F, Ctrl);
        }

        Assert.Equal("gamma", editor.Editor.SelectedText);
        f.Press(Key.G, Ctrl);
        Assert.False(editor.IsMarkSet);
        Assert.Equal(0, editor.Editor.SelectionLength);

        // Ctrl+K kills to the line's end, then the line break; Ctrl+D deletes a character.
        f.Press(Key.K, Ctrl);
        Assert.Equal("alpha beta\ngamma\nlast\n", editor.Model.Text.Text);
        f.Press(Key.A, Ctrl);
        f.Press(Key.K, Ctrl);
        Assert.Equal("alpha beta\n\nlast\n", editor.Model.Text.Text);
        f.Press(Key.K, Ctrl);
        Assert.Equal("alpha beta\nlast\n", editor.Model.Text.Text);
        f.Press(Key.D, Ctrl);
        Assert.Equal("alpha beta\nast\n", editor.Model.Text.Text);

        // Ctrl+X Ctrl+S saves (Ctrl+S alone searches).
        f.Press(Key.X, Ctrl);
        Assert.Contains("Waiting for the next key", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        f.Press(Key.S, Ctrl);
        await ShellFixture.PumpUntil(() => ShellFixture.ReadText(path) == "alpha beta\nast\n");
        Assert.Equal("editor.action.find", CommandFor(f, "ctrl+s"));
        Assert.Equal("workbench.showCommandPalette", CommandFor(f, "alt+x"));
        Assert.Equal("ctrl+x ctrl+s", f.Shell.Workbench.Keybindings.GetBinding("file.save")?.ToString()); // what the menus show

        // Ctrl+X Ctrl+F finds a file (Go To File), as Emacs's find-file asks for one: the command sweep found it ran the
        // plugins' workbench.openFile, which fails without a path.
        var ran = new List<Codalus.Workbench.Commands.CommandExecutedEventArgs>();
        f.Shell.Workbench.Commands.Executed += (_, e) => ran.Add(e);
        editor.Editor.TextArea.Focus();
        f.Press(Key.X, Ctrl);
        f.Press(Key.F, Ctrl);
        await ShellFixture.PumpUntil(() => ran.Any(e => e.CommandId == "workbench.action.gotoFile"));
        Assert.All(ran, e => Assert.Null(e.Error));
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Severity == Codalus.Workbench.Notifications.NotificationSeverity.Error);
        f.Shell.CodeSearch.Close();

        // ReSharper's: over the default scheme, which still holds where it doesn't bind a key.
        await SetSchemeAsync(f, "resharper");
        Assert.Equal("editor.action.quickFix", CommandFor(f, "ctrl+shift+r"));
        Assert.Equal("editor.action.smartSelect.expand", CommandFor(f, "ctrl+w"));
        Assert.Equal("editor.action.referenceSearch", CommandFor(f, OperatingSystem.IsLinux() ? "shift+alt+f12" : "alt+f7")); // GNOME takes Alt+F7
        Assert.Equal("editor.action.quickFix", CommandFor(f, "alt+enter"));
        Assert.Equal("explorer.syncWithActiveDocument", CommandFor(f, "shift+alt+l")); // Locate in Solution Explorer, not the OS file manager
        Assert.Equal("build.buildSolution", CommandFor(f, "f6"));
        Assert.Null(CommandFor(f, "ctrl+n"));

        // Back to the default: none of the two schemes' keys.
        await SetSchemeAsync(f, "default");
        Assert.Null(CommandFor(f, "ctrl+shift+r"));
        Assert.Null(CommandFor(f, "alt+x"));
        Assert.Equal(KeySequence.Parse("primary+s", PlatformInfo.Current), f.Shell.Workbench.Keybindings.GetBinding("file.save"));
    }
}
