using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>git.repository</c>: in the shipped app, Git → Manage Branches opens the Git Repository document; its tree has the
/// branches of the earlier steps in their folders (<c>smoke/branch</c> under <c>smoke</c>, the current one in bold) and
/// the tag, and selecting a branch shows its history beside the tree. A screenshot of it.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task GitRepositoryStepAsync()
    {
        shell.Run("git.manageBranches");
        await WaitAsync(() => shell.Layout.ActiveDocument is GitRepositoryDocument { ViewModel: { IsLoading: false } vm } && vm.Find("refs/heads/smoke/branch") is not null, "Manage Branches opens the Git Repository document with the branches").ConfigureAwait(true);
        var vm = ((GitRepositoryDocument)shell.Layout.ActiveDocument!).ViewModel;
        await WaitAsync(() => window.GetVisualDescendants().OfType<GitRepositoryView>().Any(), "the Git Repository view renders").ConfigureAwait(true);

        var current = vm.Find("refs/heads/smoke/branch")!;
        Check(current.IsCurrent && current.Text == "branch", "the current branch is in the smoke folder, in bold");
        Check(vm.Find("refs/heads/smoke/theirs") is not null && vm.Find("refs/tags/smoke-tag") is not null, "the other branch and the tag are in the tree");
        Check(vm.Roots.Select(r => r.Text).SequenceEqual(["Branches / Tags", "Pull Requests", "Submodules"]), "the tree's groups are the classic IDE's");

        vm.Selected = vm.Find("refs/heads/smoke/theirs");
        await WaitAsync(() => vm.History is { IsLoading: false } history && history.Rows.Any(r => r.Subject == "theirs"), "selecting a branch shows its history").ConfigureAwait(true);
        Check(vm.Header == "Branch / Tag: smoke/theirs", "the history says whose it is");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-git-repository.png");
    }
}
