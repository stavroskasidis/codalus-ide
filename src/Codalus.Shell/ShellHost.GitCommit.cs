using System.Globalization;
using System.Text;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// A commit's tab (a double-click in a history, View Commit Details, Show Commit for Line): the selected file's diff
/// against the commit's first parent, and the details pane with Revert, Reset, Explain (AI, when it's on), the message,
/// the author and committer, and the changed files.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Opens (or brings back) the tab of <paramref name="hash"/>, a commit of <paramref name="git"/> (the open repository by default).</summary>
    public async Task<GitCommitDocument?> ShowCommitAsync(string hash, GitService? git = null)
    {
        git ??= Repo;
        if (git.Root is null)
        {
            return null;
        }

        var id = GitCommitDocument.IdFor(hash, git == Git ? null : git.Root);
        if (Layout.FindDocument<GitCommitDocument>(id) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            return open;
        }

        var repository = git;
        var viewModel = new GitCommitViewModel(repository, hash, new GitCommitActions
        {
            Run = (action, commit) => InRepositoryAsync(repository, () => CommitActionAsync(action, commit)),
            CreateDiff = (commit, file) => CommitFileDiff(repository, commit, file),
            Explain = Ai.IsEnabled ? details => ExplainCommitWithAiAsync(repository, details) : null,
        });
        var document = new GitCommitDocument(viewModel, EditorFont, id);
        Layout.OpenDocument(document, focus: true);
        await viewModel.LoadAsync().ConfigureAwait(true);
        return document;
    }

    /// <summary>A file of a commit against the commit's first parent, as the commit tab's diff.</summary>
    private DiffViewModel CommitFileDiff(GitService git, GitCommit commit, GitCommitFileRow file)
    {
        var parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var oldPath = file.OldPath ?? file.Path;
        var viewModel = new DiffViewModel(
            file.Path,
            $"{commit.Hash}:{file.Path}",
            parent is null ? "-" : parent[..Math.Min(8, parent.Length)],
            async () => parent is null || file.Status == 'A' ? string.Empty : await git.ShowAsync(oldPath, parent).ConfigureAwait(true) ?? string.Empty,
            commit.ShortHash,
            async () => file.Status == 'D' ? string.Empty : await git.ShowAsync(file.Path, commit.Hash).ConfigureAwait(true) ?? string.Empty);
        ConfigureDiff(viewModel);
        return viewModel;
    }

    /// <summary>Explain: AI Chat explains the commit, from its message and its diff (excluded files left out, ADR 0079's checks).</summary>
    private async Task ExplainCommitWithAiAsync(GitService git, GitCommitDetails details)
    {
        if (!await EnsureAiAsync().ConfigureAwait(true) || git.Root is not { } root)
        {
            return;
        }

        try
        {
            var patch = await git.CommitPatchAsync(details.Commit.Hash).ConfigureAwait(true);
            var (diff, excluded) = await AiActions.FilterDiffAsync(Ai, patch, root, CancellationToken.None).ConfigureAwait(true);
            const int Limit = 60_000;
            var text = new StringBuilder()
                .Append("commit ").AppendLine(details.Commit.Hash)
                .Append("Author: ").Append(details.Commit.Author).Append(", ").AppendLine(details.Commit.Date.ToString("u", CultureInfo.InvariantCulture))
                .AppendLine()
                .AppendLine(details.Message)
                .AppendLine()
                .Append(diff.Length > Limit ? diff[..Limit] : diff);
            if (excluded.Count > 0)
            {
                text.AppendLine().Append("(Left out by the AI exclusions: ").Append(string.Join(", ", excluded)).Append(')');
            }

            await AiChat.AskAsync(AiActions.ExplainCommitQuestion, [new AiContextItem(AiContextKind.Changes, Localizer.Format("msg.gitCommit.title", "Commit {0}", details.Commit.ShortHash), text.ToString())]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is AiUnavailableException or GitException)
        {
            ReportAiFailure(ex);
        }
    }
}
