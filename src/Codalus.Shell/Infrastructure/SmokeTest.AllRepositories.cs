using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>git.allRepositories</c> (ADR 0085): in the shipped app, a second repository joins the solution's (Add Repository…),
/// Git Changes shows both, one tree each, with Commit All Repos; the branch drop-down lists Common branches and each
/// repository's; one message commits in both. Screenshots of the window and the drop-down. The repository is left out
/// again at the end, so the later steps see one repository.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task AllRepositoriesStepAsync()
    {
        var current = shell.Git.Status?.Branch ?? throw new SmokeFailure("The solution's repository has no branch.");
        var library = Path.Combine(Work, "Library");
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(Path.Combine(library, "Library.cs"), "namespace Library;\n\npublic static class Shared { }\n").ConfigureAwait(true);
        GitIn(library, "init", "-q", "-b", current);
        GitIn(library, "config", "user.name", "Smoke Test");
        GitIn(library, "config", "user.email", "smoke@example.com");
        GitIn(library, "config", "commit.gpgsign", "false");
        GitIn(library, "add", ".");
        GitIn(library, "commit", "-q", "-m", "initial");
        GitIn(library, "branch", "develop");
        library = Path.GetFullPath(library);

        await shell.AddRepositoryAsync(library).ConfigureAwait(true);
        var changes = shell.GitChanges;
        await WaitAsync(() => changes.IsAllRepositories && shell.Git.Siblings.Count == 1, "the second repository joins, and all are shown").ConfigureAwait(true);
        Check(changes.RepositoryButtonText == "2 Repositories" && changes.Repositories.First().Name == "All Repositories (2)", "the picker says All Repositories");

        // A change in each: a tree for each repository.
        var note = Path.Combine(_folder, "ALL-REPOSITORIES.md");
        await File.WriteAllTextAsync(note, "Committed with the library.\n").ConfigureAwait(true);
        await File.AppendAllTextAsync(Path.Combine(library, "Library.cs"), "// changed with the solution\n").ConfigureAwait(true);
        await shell.Git.RefreshAsync().ConfigureAwait(true);
        await shell.Git.Siblings[0].RefreshAsync().ConfigureAwait(true);
        await WaitAsync(() => changes.Changes.Select(r => r.Repository).Distinct().Count() == 2, "Changes lists each repository's change").ConfigureAwait(true);
        Check(changes.CommitText == "Commit All Repos", "the commit button commits in every repository");

        // The trees are built while the window is shown (M56).
        shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await WaitAsync(() => window.GetVisualDescendants().OfType<GitChangesView>().Any(), "the Git Changes window shows").ConfigureAwait(true);
        await WaitAsync(() => changes.ChangesTree.Count == 2, "Changes has a tree for each repository").ConfigureAwait(true);
        var view = window.GetVisualDescendants().OfType<GitChangesView>().Single();
        changes.Message = "Smoke test: the solution and its library";
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-all-repositories.png");

        // The branch drop-down: Common branches, then each repository's.
        var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BranchButton");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => shell.GitBranches.IsOpen && shell.GitBranches.IsAllRepositories && shell.GitBranches.Rows.Any(r => r.IsCommon && r.IsCurrent), "the drop-down lists the common branches, the current one checked").ConfigureAwait(true);
        Check(shell.GitBranches.Rows.Any(r => r.IsHeader && r.HeaderText == $"Library ({current})"), "each repository's branches are under its name and branch");
        var popup = view.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "BranchPopup");
        await WaitAsync(() => popup.IsOpen, "the drop-down is shown").ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-all-repositories-branches.png", [popup]);
        shell.GitBranches.IsOpen = false;
        await WaitAsync(() => !popup.IsOpen, "the drop-down closes").ConfigureAwait(true);

        // Commit All Repos: one message, a commit in each.
        await changes.CommitAsync(all: true).ConfigureAwait(true);
        await WaitAsync(() => changes.Changes.Count == 0 && changes.Staged.Count == 0, "both repositories are clean after the commit").ConfigureAwait(true);
        var subjects = new[] { _folder, library }.Select(r => shell.Git.ServiceFor(Path.Combine(r, "x")).LogAsync(new Workbench.Git.GitLogQuery(), 0, 1)).ToList();
        foreach (var log in subjects)
        {
            Check((await log.ConfigureAwait(true))[0].Subject == "Smoke test: the solution and its library", "each repository has the commit");
        }

        await shell.RemoveRepositoryAsync(library).ConfigureAwait(true);
        await WaitAsync(() => !changes.HasSeveralRepositories && shell.Git.Siblings.Count == 0, "the library is left out again").ConfigureAwait(true);
    }
}
