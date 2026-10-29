using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>git.repository</c>: in the shipped app, Git → Manage Branches opens the Git Repository document; its tree has the
/// branches of the earlier steps in their folders (<c>smoke/branch</c> under <c>smoke</c>, the current one in bold) and
/// the tag, and selecting a branch shows its history beside the tree; a double-click on a commit opens its tab with its
/// file's diff and the details pane. A screenshot of each.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task GitRepositoryStepAsync()
    {
        // git.depth opened the document already, with the refs of then: Manage Branches brings it back and reads them
        // again (awaited), and the branch and the tag git.depth made after it arrive (Linux CI, run 40902428637).
        await shell.RunAsync("git.manageBranches", []).ConfigureAwait(true);
        await WaitAsync(
            () => shell.Layout.ActiveDocument is GitRepositoryDocument { ViewModel: { IsLoading: false } vm }
                && vm.Find("refs/heads/smoke/branch") is not null && vm.Find("refs/heads/smoke/theirs") is not null && vm.Find("refs/tags/smoke-tag") is not null,
            "Manage Branches opens the Git Repository document with the branches and the tag").ConfigureAwait(true);
        var vm = ((GitRepositoryDocument)shell.Layout.ActiveDocument!).ViewModel;
        await WaitAsync(() => window.GetVisualDescendants().OfType<GitRepositoryView>().Any(), "the Git Repository view renders").ConfigureAwait(true);

        var current = vm.Find("refs/heads/smoke/branch")!;
        Check(current.IsCurrent && current.Text == "branch", "the current branch is in the smoke folder, in bold");
        Check(vm.Roots.Select(r => r.Text).SequenceEqual(["Branches / Tags", "Pull Requests", "Submodules"]), "the tree's groups are the classic IDE's");

        vm.Selected = vm.Find("refs/heads/smoke/theirs");
        await WaitAsync(() => vm.History is { IsLoading: false } history && history.Rows.Any(r => r.Subject == "theirs"), "selecting a branch shows its history").ConfigureAwait(true);
        Check(vm.Header == "Branch / Tag: smoke/theirs", "the history says whose it is");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-git-repository.png");

        // A double-click on a commit (View Commit Details): its tab, with its file's diff and the details pane.
        var history = vm.History!;
        history.Selected = history.Rows.First(r => r.Subject == "theirs");
        await history.OpenSelectedAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Layout.ActiveDocument is GitCommitDocument { ViewModel: { HasDetails: true, HasDiff: true } }, "the commit opens in its own tab").ConfigureAwait(true);
        await WaitAsync(() => window.GetVisualDescendants().OfType<GitCommitView>().Any(v => v.DiffView is { } d && d.RightText.Contains("Theirs from", StringComparison.Ordinal)), "the commit's tab shows its file's diff").ConfigureAwait(true);
        var commit = (GitCommitDocument)shell.Layout.ActiveDocument!;
        Check(commit.ViewModel.Message == "theirs" && commit.ViewModel.ChangesHeader == "Changes (1)", "the details pane has the message and the changed file");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-git-commit.png");
    }
}
