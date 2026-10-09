using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Git in the shell (ADR 0008) on a repository made from samples/BuildAndRun.</summary>
public sealed class GitShellTests
{
    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
    }

    [AvaloniaFact]
    public async Task Markers_changes_view_hunk_staging_commit_blame_and_branches()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Left.Any(i => i.Item.Id == "daedalus.git.branch" && i.Text.Contains("main", StringComparison.Ordinal)));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("gitRepository"));

        // Typing adds a gutter marker before anything is saved.
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        var editor = await f.Shell.OpenFileAsync(greeter);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        var controller = editor!.Controller!;
        controller.Editor.Document.Insert(0, "// a new first line\n");
        await ShellFixture.PumpUntil(() => controller.GitMargin.Changes.Any(c => c is { Kind: LineChangeKind.Added, Line: 1 }));

        // Saved: the Changes view lists it; its diff stages the hunk.
        await f.Shell.SaveActiveDocumentAsync();
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Changes.Any(r => r.Name == "Greeter.cs"));
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());

        var diff = await f.Shell.OpenDiffAsync(greeter, staged: false);
        Assert.NotNull(diff);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();
        Assert.StartsWith("// a new first line", view.RightText, StringComparison.Ordinal);
        Assert.StartsWith("\n", view.LeftText, StringComparison.Ordinal); // a filler row on the old side
        await view.HunkActionAsync("stage");
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Staged.Any(r => r.Name == "Greeter.cs") && !f.Shell.GitChanges.Changes.Any(r => r.Name == "Greeter.cs"));

        // Commit from the Changes view.
        f.Shell.GitChanges.Message = "Add a comment";
        await f.Shell.GitChanges.CommitAsync(all: false);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Files.Count: 0 });
        Assert.Equal(string.Empty, f.Shell.GitChanges.Message);
        // The index-changed notice (from the file watcher) refreshes the markers; it can lag on Linux.
        await controller.UpdateGitMarkersAsync();
        await ShellFixture.PumpUntil(() => controller.GitMargin.Changes.Count == 0);

        // Blame annotates the editor; a new branch shows in the status bar.
        f.Shell.Layout.OpenDocument(editor);
        f.Shell.Run("git.toggleBlame");
        await ShellFixture.PumpUntil(() => controller.Blame is { Ranges.Count: > 0 });
        Assert.Equal("Add a comment", controller.Blame!.Ranges[0].Summary);
        Assert.Contains("Ada Tester", BlameMargin.Describe(controller.Blame.Ranges[0]), StringComparison.Ordinal);

        await f.Shell.Git.CreateBranchAsync("feature/x", switchTo: true, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Left.Any(i => i.Item.Id == "daedalus.git.branch" && i.Text.Contains("feature/x", StringComparison.Ordinal)));
    }
}
