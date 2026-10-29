using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>M13 in the shell: the history window, the merge editor, the operation banner, interactive rebase and per-line staging.</summary>
public sealed class GitDepthShellTests
{
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    /// <summary>BuildAndRun as a repository with a feature branch that conflicts with main in Greeter.cs.</summary>
    private static async Task<(ShellFixture Fixture, string Folder, string Greeter)> OpenRepositoryAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        var original = File.ReadAllText(greeter);

        Git(folder, "switch", "-q", "-c", "feature");
        File.WriteAllText(greeter, original.Replace("Hello from", "Howdy from", StringComparison.Ordinal));
        Git(folder, "commit", "-q", "-am", "Say howdy");
        Git(folder, "switch", "-q", "main");
        File.WriteAllText(greeter, original.Replace("Hello from", "Greetings from", StringComparison.Ordinal));
        Git(folder, "commit", "-q", "-am", "Say greetings");

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" } && Equals(f.Shell.Workbench.Context.GetValue("gitRepository"), true));
        return (f, folder, greeter);
    }

    /// <summary>The history beside the Git Repository document's tree, once it has <paramref name="rows"/> commits or more.</summary>
    private static async Task<GitHistoryViewModel> RepositoryHistoryAsync(ShellFixture f, int rows)
    {
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<GitRepositoryDocument>().Any(d => d.ViewModel.History is { IsLoading: false } h && h.Rows.Count >= rows));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var documents = string.Join(", ", f.Shell.Layout.Documents.Select(d => $"{d.GetType().Name}:{(d as GitRepositoryDocument)?.ViewModel.History?.Rows.Count}"));
            throw new InvalidOperationException($"Documents: {documents}; active {f.Shell.Layout.ActiveDocument?.GetType().Name}; notifications: {string.Join(" | ", f.Shell.Notifications.Items.Select(n => n.Message))}; git log: {f.Shell.GitLog.GetText()}", ex);
        }

        return f.Shell.Layout.Documents.OfType<GitRepositoryDocument>().Single().ViewModel.History!;
    }

    [AvaloniaFact]
    public async Task View_history_opens_the_git_repository_window_and_a_commit_opens_in_its_own_tab()
    {
        var (f, _, greeter) = await OpenRepositoryAsync();
        await using var fixture = f;

        // View History: the Git Repository document, on the current branch.
        await f.RunAsync("git.history");
        var history = await RepositoryHistoryAsync(f, 2);
        Assert.Equal(["Say greetings", "initial"], history.Rows.Select(r => r.Subject));
        Assert.Equal("Branch / Tag: main", f.Shell.Layout.Documents.OfType<GitRepositoryDocument>().Single().ViewModel.Header);
        Assert.Contains(history.Rows[0].RefChips, r => r is { Name: "main", Kind: GitRefKind.Local, IsCurrent: true });
        Assert.Empty(f.Shell.Layout.Documents.OfType<GitHistoryDocument>());

        // Show All Branches: main and feature side by side, and remembered.
        Assert.True(history.CanShowAllBranches);
        history.AllBranches = true;
        await ShellFixture.PumpUntil(() => history is { IsLoading: false, Rows.Count: 3 });
        Assert.Equal(2, history.Lanes);
        Assert.Contains(history.Rows, r => r.Refs.Contains("feature"));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetBoolean(BuiltIns.HistoryAllBranchesSetting, false));

        // Tags and remote branches can leave the Branch / Tag column.
        history.ShowTags = false;
        Assert.All(history.Rows, r => Assert.DoesNotContain(r.ShownRefs, c => c.Kind == GitRefKind.Tag));

        // Filter History: by message, by author, by a commit id (a prefix of one).
        history.Filter = "greetings";
        await ShellFixture.PumpUntil(() => history is { IsLoading: false } && history.Rows.Select(r => r.Subject).SequenceEqual(["Say greetings"]));
        var howdy = Git(f.Shell.Git.Root!, "rev-parse", "feature").Trim();
        history.AllBranches = false;
        history.Filter = howdy[..7];
        await ShellFixture.PumpUntil(() => history is { IsLoading: false } && history.Rows.Select(r => r.Subject).SequenceEqual(["Say howdy"]));
        history.Filter = "nothing like this";
        await ShellFixture.PumpUntil(() => history is { IsLoading: false, IsEmpty: true });
        Assert.Equal("No commits match “nothing like this”.", history.EmptyText);

        // Double-click (View Commit Details): the commit's tab, its first file's diff against the parent.
        history.Filter = howdy[..7];
        await ShellFixture.PumpUntil(() => history is { IsLoading: false, Rows.Count: 1 });
        history.Selected = history.Rows.Single();
        await history.OpenSelectedAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is GitCommitDocument { ViewModel.HasDetails: true });
        var commit = (GitCommitDocument)f.Shell.Layout.ActiveDocument!;
        Assert.Equal($"Commit {howdy[..8]}", commit.Title);
        Assert.Equal("Say howdy", commit.ViewModel.Message);
        Assert.Equal("Ada Tester (Author)", commit.ViewModel.Author);
        Assert.Equal("Ada Tester (Committer)", commit.ViewModel.Committer);
        Assert.Equal("Changes (1)", commit.ViewModel.ChangesHeader);
        Assert.Equal(["src/Greetings/Greeter.cs"], commit.ViewModel.Files.Select(file => file.RelativePath));
        Assert.Equal($"src{Path.DirectorySeparatorChar}Greetings", commit.ViewModel.Tree.Single().Name);
        Assert.False(commit.ViewModel.CanExplain); // AI is off
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitCommitView>().Any(v => v.DiffView is { } d && d.RightText.Contains("Howdy from", StringComparison.Ordinal)));
        var diff = f.Window.GetVisualDescendants().OfType<GitCommitView>().Single().DiffView!;
        Assert.Contains("Hello from", diff.LeftText, StringComparison.Ordinal);

        // The same commit again brings its tab back.
        await history.OpenSelectedAsync();
        Assert.Single(f.Shell.Layout.Documents.OfType<GitCommitDocument>());

        // File history and line history.
        await f.Shell.OpenFileAsync(greeter);
        await f.RunAsync("git.fileHistory");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<GitHistoryDocument>().Any(d => d is { Title: "History: Greeter.cs", ViewModel.Rows.Count: 2 }));
        await f.Shell.OpenFileAsync(greeter);
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("activeDocumentKind"), "textFile"));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
        Assert.True(f.Shell.Workbench.Commands.Find("git.lineHistory")!.Enablement.Evaluate(f.Shell.Workbench.Context), "git.lineHistory is disabled");
        await f.RunAsync("git.lineHistory");
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<GitHistoryDocument>().Any(d => d is { ViewModel.Lines: not null, ViewModel.Rows.Count: > 0 }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var documents = string.Join(", ", f.Shell.Layout.Documents.OfType<GitHistoryDocument>().Select(d => $"{d.Title}:{d.ViewModel.Lines}:{d.ViewModel.Rows.Count}"));
            throw new InvalidOperationException($"Histories: {documents}; notifications: {string.Join(" | ", f.Shell.Notifications.Items.Select(n => n.Message))}", ex);
        }
    }

    [AvaloniaFact]
    public async Task A_conflict_is_resolved_in_the_merge_editor_and_the_merge_continued()
    {
        var (f, folder, greeter) = await OpenRepositoryAsync();
        await using var fixture = f;

        await Assert.ThrowsAsync<GitException>(() => f.Shell.Git.MergeAsync("feature"));
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges is { HasOperation: true, HasConflicts: true });
        Assert.Equal("merge", f.Shell.Workbench.Context.GetValue("gitOperation"));
        Assert.Contains("Resolve the conflicts", f.Shell.GitChanges.OperationText, StringComparison.Ordinal);

        var document = await f.Shell.OpenMergeEditorAsync(greeter);
        Assert.NotNull(document);
        Assert.Equal(1, document.ViewModel.Remaining);
        Assert.Contains("Greetings from", document.ViewModel.Ours, StringComparison.Ordinal);
        Assert.Contains("Howdy from", document.ViewModel.Theirs, StringComparison.Ordinal);

        // The tab is the file's name; the headers name the branches; the result shows the merge base's text, not markers.
        Assert.Equal("Greeter.cs", document.Title);
        Assert.Equal(("Current: main", "Incoming: feature", "Result: Greeter.cs"), (document.ViewModel.CurrentTitle, document.ViewModel.IncomingTitle, document.ViewModel.ResultTitle));
        Assert.Equal("1 Conflict (1 Remaining)", document.ViewModel.Summary);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MergeEditorView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<MergeEditorView>().Single();
        Assert.Contains("Hello from", view.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<<<<<", view.ResultText, StringComparison.Ordinal);
        Assert.Equal(document.ViewModel.Ours, view.EditorFor(MergeSide.Current).Text);
        Assert.Equal(document.ViewModel.Theirs, view.EditorFor(MergeSide.Incoming).Text);

        view.Accept("incoming");
        Assert.Equal(0, document.ViewModel.Remaining);
        Assert.Equal("1 Conflict (0 Remaining)", document.ViewModel.Summary);
        Assert.Equal("Greeter.cs*", document.Title);
        Assert.Contains("Howdy from", view.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello from", view.ResultText, StringComparison.Ordinal);

        // Accept Merge writes the file and stages it.
        await view.AcceptMergeAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.GitChanges.HasConflicts);
        Assert.Contains("Howdy from", File.ReadAllText(greeter), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Git(folder, "ls-files", "-u").Trim());
        Assert.Contains("Greeter.cs", Git(folder, "diff", "--cached", "--name-only"), StringComparison.Ordinal);
        Assert.Empty(f.Shell.Layout.Documents.OfType<MergeEditorDocument>());

        await f.RunAsync("git.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Operation: null });
        Assert.Contains("Merge branch 'feature'", Git(folder, "log", "-1", "--format=%s"), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => !f.Shell.GitChanges.HasOperation); // the view model follows through Ui.Post
    }

    [AvaloniaFact]
    public async Task The_merge_editor_parses_markers_with_a_base_and_accepts_both()
    {
        const string text = "a\n<<<<<<< HEAD\nours\n||||||| base\nold\n=======\ntheirs\n>>>>>>> feature\nb\n";
        var conflict = Assert.Single(MergeEditorViewModel.Parse(text));
        Assert.Equal(("ours\n", "old\n", "theirs\n", "HEAD", "feature"), (conflict.Ours, conflict.Base, conflict.Theirs, conflict.OursLabel, conflict.TheirsLabel));
        Assert.Equal("a\nours\ntheirs\nb\n", text[..conflict.Start] + MergeEditorViewModel.ReplacementFor(conflict, "both") + text[(conflict.Start + conflict.Length)..]);
        var viewModel = new MergeEditorViewModel("x", text, null, null, null, _ => Task.CompletedTask);
        Assert.Equal("a\nold\nb\n", viewModel.Result.Text); // diff3's base section, in place of the markers
        await Assert.ThrowsAsync<InvalidOperationException>(viewModel.AcceptAsync);
    }

    [AvaloniaFact]
    public async Task Interactive_rebase_editor_squashes_and_rewords()
    {
        var (f, folder, _) = await OpenRepositoryAsync();
        await using var fixture = f;
        File.WriteAllText(Path.Combine(folder, "one.txt"), "1");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "one");
        File.WriteAllText(Path.Combine(folder, "two.txt"), "2");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "two");
        await f.Shell.Git.RefreshAsync();

        await f.RunAsync("git.history");
        var history = await RepositoryHistoryAsync(f, 4);
        history.Selected = history.Rows.Single(r => r.Subject == "Say greetings");
        await history.RunAsync("rebaseInteractive");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<InteractiveRebaseDocument>().Any());
        var rebase = f.Shell.Layout.Documents.OfType<InteractiveRebaseDocument>().Single().ViewModel;
        Assert.Equal(["one", "two"], rebase.Steps.Select(s => s.Subject));

        rebase.Steps[0].Action = "reword";
        rebase.Steps[0].Message = "one and two";
        rebase.Steps[1].Action = "fixup";
        await rebase.StartAsync();
        Assert.False(rebase.HasError, rebase.Error);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<InteractiveRebaseDocument>().Any() == false);
        Assert.Equal(["one and two", "Say greetings", "initial"], Git(folder, "log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.True(File.Exists(Path.Combine(folder, "two.txt")));
    }

    [AvaloniaFact]
    public async Task Selected_lines_of_a_diff_are_staged_alone()
    {
        var (f, folder, greeter) = await OpenRepositoryAsync();
        await using var fixture = f;
        var lines = File.ReadAllLines(greeter).ToList();
        lines.Insert(0, "// first added line");
        lines.Insert(1, "// second added line");
        File.WriteAllText(greeter, string.Join('\n', lines) + "\n");
        await f.Shell.Git.RefreshAsync();

        await f.Shell.OpenDiffAsync(greeter, staged: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any(v => v.RightText.StartsWith("// first", StringComparison.Ordinal)));
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.RightText.StartsWith("// first", StringComparison.Ordinal));
        var right = view.ActiveEditor;
        right.TextArea.Caret.Line = 1; // the first added line only
        Assert.True(await view.ApplyToSelectedLinesAsync(DiffAction.Stage));

        var staged = Git(folder, "diff", "--cached");
        Assert.Contains("+// first added line", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("second added line", staged, StringComparison.Ordinal);
        Assert.Contains("+// second added line", Git(folder, "diff"), StringComparison.Ordinal);
    }
}
