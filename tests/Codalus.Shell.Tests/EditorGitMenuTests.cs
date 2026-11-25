using System.Diagnostics;
using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>
/// The code editor's Git ▸ (<c>editor/context/git</c>), as the classic IDE's: Commit or Stash…, Undo Changes, View
/// History, Compare with Unmodified…, Blame (Annotate), Ignore and Untrack item, then the line's history and other
/// comparisons. Undo Changes shows only while the file has changes, and Ignore and Untrack only while git doesn't ignore
/// it. On a plain folder made into a repository, so no build or language host starts.
/// </summary>
public sealed class EditorGitMenuTests
{
    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult()}");
    }

    [AvaloniaFact]
    public async Task The_editors_git_submenu_has_the_classic_IDEs_entries_and_undoes_and_ignores_the_file()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "App");
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "committed\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");

        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is not null);
        var document = await f.Shell.OpenFileAsync(file);
        Assert.NotNull(document);
        var context = f.Shell.Workbench.Context;
        string[] Items() => [.. f.Shell.Workbench.Menus.GetItems(BuiltIns.EditorGitMenu).Select(i => i.Title)];
        string[] more = ["View Line History", "Show Commit for Line", "Compare with Branch or Commit…"];

        // Unchanged and tracked: no Undo Changes.
        await ShellFixture.PumpUntil(() => Equals(context.GetValue("gitActiveFileChanged"), false) && Equals(context.GetValue("gitActiveFileIgnored"), false));
        Assert.Equal(["Commit or Stash…", "View History", "Compare with Unmodified…", "Blame (Annotate)", "Ignore and Untrack item", .. more], Items());
        Assert.Equal(["1-commit", "2-history", "2-history", "2-history", "3-ignore", "4-more", "4-more", "4-more"], f.Shell.Workbench.Menus.GetItems(BuiltIns.EditorGitMenu).Select(i => i.Group));

        // Changed, staged and not: Undo Changes asks, Cancel the default, then takes it back to the last commit.
        File.WriteAllText(file, "staged\n");
        Git(folder, "add", ".");
        File.WriteAllText(file, "staged\nand not\n");
        await f.Shell.Git.RefreshAsync();
        await ShellFixture.PumpUntil(() => Equals(context.GetValue("gitActiveFileChanged"), true));
        Assert.Equal(["Commit or Stash…", "Undo Changes", "View History", "Compare with Unmodified…", "Blame (Annotate)", "Ignore and Untrack item", .. more], Items());
        f.Shell.Run("git.undoFileChanges");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Undo changes to notes.txt?", f.Shell.Dialogs.Title);
        Assert.Equal("notes.txt goes back to the last commit: its staged and unstaged changes will be lost. This can't be undone.", f.Shell.Dialogs.Message);
        Assert.Equal("Cancel", f.Shell.Dialogs.Buttons.Single(b => b.IsDefault).Label);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Undo Changes").Command.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Files.Count: 0 } && document!.Model.Text.Text == "committed\n");
        await ShellFixture.PumpUntil(() => Equals(context.GetValue("gitActiveFileChanged"), false));

        // Ignore and Untrack: the file stays, its removal is staged, and the entry goes once git ignores it. The flag can
        // come before the status that has the removal: the status read after the .gitignore's write starts check-ignore,
        // which on a slow runner answers after `git rm --cached` has run, while that one's status read is still on its
        // way (Windows CI, PR #549's run 45655870329: the status had only .gitignore). So it waits for both.
        f.Shell.Run("git.ignoreAndUntrack");
        await ShellFixture.PumpUntil(() => Equals(context.GetValue("gitActiveFileIgnored"), true)
            && f.Shell.Git.Status?.Files.Any(s => s.Path == file && s.Index == 'D') == true,
            describe: () => $"ignored: {context.GetValue("gitActiveFileIgnored")}; the status: {string.Join(", ", f.Shell.Git.Status?.Files.Select(s => $"{s.RelativePath} {s.Index}{s.WorkTree}") ?? [])}");
        Assert.True(File.Exists(file));
        Assert.Equal("/notes.txt\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
        Assert.DoesNotContain("Ignore and Untrack item", Items());
    }
}
