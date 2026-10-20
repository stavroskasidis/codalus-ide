using System.Globalization;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Git;
using Codalus.Workbench.Output;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>Git (ADR 0008): the repository of the open solution, the Changes view, diffs, branches, remotes and blame.</summary>
public sealed partial class ShellHost
{
    private const string BranchStatusItem = "codalus.git.branch";

    public GitService Git { get; private set; } = null!;

    public GitChangesViewModel GitChanges { get; private set; } = null!;

    GitService IEditorServices.Git => Git;

    public OutputChannel GitLog => Workbench.Output.GetOrCreate("codalus.git", "Git");

    private void InitializeGit()
    {
        Git = new GitService(watch: Options.WatchFiles);
        Git.Log += (_, line) => GitLog.AppendLine(line);
        Git.StatusChanged += (_, _) => Ui.Post(UpdateGitStatus);
        GitChanges = new GitChangesViewModel(Git, (path, staged) => OpenDiffAsync(path, staged), command => RunGitCommandAsync(command));
        GitChanges.OpenFile = path => OpenFileAsync(path);
        GitChangesView.ReportError = ReportGitError;
        DiffView.ReportError = ReportGitError;
        MergeEditorView.ReportError = ReportGitError;
        UpdateGitStatus();
    }

    private void ReportGitError(Exception ex)
    {
        GitLog.AppendLine("error: " + ex.Message);
        Workbench.Notifications.Error("Git: " + ex.Message);
    }

    /// <summary>Runs a Git action, reporting git's errors as notifications (never throws for them).</summary>
    private async Task GitActionAsync(Func<Task> action, string? progress = null)
    {
        if (Git.Root is null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.git.theOpenSolutionIsNotIn", "The open solution is not in a git repository."), TimeSpan.FromSeconds(4));
            return;
        }

        try
        {
            if (progress is not null)
            {
                StatusBar.ShowMessage(progress);
            }

            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportGitError(ex);
        }
        finally
        {
            if (progress is not null)
            {
                StatusBar.ClearMessage();
            }
        }
    }

    private Task RunGitCommandAsync(string command)
    {
        if (command.StartsWith("git.discard:", StringComparison.Ordinal))
        {
            return DiscardAsync(command["git.discard:".Length..]);
        }

        if (command.StartsWith("git.mergeEditor:", StringComparison.Ordinal))
        {
            return OpenMergeEditorAsync(command["git.mergeEditor:".Length..]);
        }

        if (command.StartsWith("git.dropStash:", StringComparison.Ordinal))
        {
            return DropStashAsync(command["git.dropStash:".Length..]);
        }

        Run(command);
        return Task.CompletedTask;
    }

    /// <summary>Opens the repository of the solution in <paramref name="folder"/> (none: Git features stay hidden).</summary>
    private async Task OpenRepositoryAsync(string folder)
    {
        try
        {
            if (!await Git.OpenAsync(folder, CancellationToken.None).ConfigureAwait(true))
            {
                UpdateGitStatus();
            }
        }
        catch (GitException ex)
        {
            GitLog.AppendLine(ex.Message);
            UpdateGitStatus();
        }
    }

    private void UpdateGitStatus()
    {
        Workbench.Context.Set("gitRepository", Git.Status is not null);
        Workbench.Context.Set("gitMerging", Git.Status?.IsMerging ?? false);
        UpdateGitDepthStatus();
        if (Git.Status is not { } status)
        {
            Workbench.StatusBar.Remove(BranchStatusItem);
            return;
        }

        var branch = status.Branch ?? "(detached)";
        var sync = status.Upstream is null ? string.Empty : $" ↑{status.Ahead.ToString(CultureInfo.InvariantCulture)} ↓{status.Behind.ToString(CultureInfo.InvariantCulture)}";
        var changes = status.Files.Count;
        Workbench.StatusBar.Set(new StatusBarItem(BranchStatusItem, $"$(branch) {branch}{sync}{(changes > 0 ? "  •" + changes.ToString(CultureInfo.InvariantCulture) : string.Empty)}", StatusBarAlignment.Right, 950, ContributionSource.Shell.Id)
        {
            Command = "git.switchBranch",
            Tooltip = $"Branch {branch}{(status.Upstream is { } upstream ? $" (tracking {upstream})" : " (not published)")}; {changes.ToString(CultureInfo.InvariantCulture)} changed file(s). Click to switch branches.",
            AccessibleName = $"Git branch {branch}, {changes.ToString(CultureInfo.InvariantCulture)} changes",
        });
    }

    // ------------------------------------------------------------------ diffs and files

    /// <summary>Compares two files side by side (<c>codalus diff</c>, diff tools such as Verify's).</summary>
    public async Task<DiffDocument> OpenFileDiffAsync(string left, string right)
    {
        if (Layout.FindDocument<DiffDocument>(DiffDocument.IdFor(left, right)) is not { } document)
        {
            document = new DiffDocument(new DiffViewModel(left, right), EditorFont);
        }

        await document.ViewModel.LoadAsync().ConfigureAwait(true);
        Layout.OpenDocument(document, focus: true);
        return document;
    }

    /// <summary>Opens a file's diff: work tree against the index, or (<paramref name="staged"/>) the index against HEAD.</summary>
    public async Task<DiffDocument?> OpenDiffAsync(string path, bool staged)
    {
        if (Layout.FindDocument<DiffDocument>(DiffDocument.IdFor(path, staged)) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            await open.ViewModel.LoadAsync().ConfigureAwait(true);
            return open;
        }

        var document = new DiffDocument(new DiffViewModel(Git, path, staged), EditorFont);
        try
        {
            await document.ViewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (GitException ex)
        {
            ReportGitError(ex);
            return null;
        }

        Layout.OpenDocument(document, focus: true);
        return document;
    }

    /// <summary>The active file's working-tree diff.</summary>
    public Task ShowActiveFileDiffAsync() =>
        ActiveEditor?.Model.Path is { } path ? GitActionAsync(() => OpenDiffAsync(path, staged: false)) : Task.CompletedTask;

    public async Task DiscardAsync(string path)
    {
        var name = Path.GetFileName(path);
        var untracked = Git.Status?.Files.Any(f => f.IsUntracked && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, path)) == true;
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            $"Discard changes to {name}?",
            untracked ? $"{name} is not tracked by git: it will be deleted." : $"The changes to {name} that are not staged will be lost.",
            ["Discard", "Cancel"]) { DefaultButton = "Cancel" }).ConfigureAwait(true);
        if (choice.Button == "Discard")
        {
            await GitActionAsync(() => Git.DiscardAsync([path])).ConfigureAwait(true);
        }
    }

    public Task ToggleBlameAsync() => ActiveEditor?.Controller is { } controller ? controller.ToggleBlameAsync() : Task.CompletedTask;

    public Task StageActiveFileAsync() =>
        ActiveEditor?.Model.Path is { } path ? GitActionAsync(async () =>
        {
            await SaveActiveDocumentAsync().ConfigureAwait(true);
            await Git.StageAsync([path]).ConfigureAwait(true);
        }) : Task.CompletedTask;

    // ------------------------------------------------------------------ branches and remotes

    public Task SwitchBranchAsync() => GitActionAsync(async () =>
    {
        var branches = await Git.BranchesAsync().ConfigureAwait(true);
        var locals = branches.Where(b => !b.IsRemote).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var items = new List<QuickPickItem> { new("+ Create New Branch…") { Tag = "create" } };
        items.AddRange(branches
            .Where(b => !b.IsRemote || !locals.Contains(b.Name[(b.Name.IndexOf('/', StringComparison.Ordinal) + 1)..]))
            .OrderBy(b => b.IsRemote)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new QuickPickItem(b.Name) { Tag = b, Detail = b.IsCurrent ? "current" : b.IsRemote ? "remote" : b.Upstream }));
        switch (await Palette.ShowAsync(items, "Switch to a branch", Math.Max(0, items.FindIndex(i => i.Tag is GitBranch { IsCurrent: true }))).ConfigureAwait(true))
        {
            case { Tag: "create" }:
                await CreateBranchAsync().ConfigureAwait(true);
                break;
            case { Tag: GitBranch { IsCurrent: false } branch }:
                await Git.SwitchAsync(branch).ConfigureAwait(true);
                break;
        }
    });

    public async Task CreateBranchAsync()
    {
        var result = await Dialogs.ShowAsync(new DialogRequest(
            "Create Branch",
            $"New branch from {Git.Status?.Branch ?? "HEAD"} (and switch to it):",
            ["Create", "Cancel"])
        {
            Input = string.Empty,
            Validate = name => string.IsNullOrWhiteSpace(name) ? "Enter a name."
                : name.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') || name.Contains("..", StringComparison.Ordinal) ? "Not a valid branch name."
                : null,
        }).ConfigureAwait(true);
        if (result is { Button: "Create", Input: { } name })
        {
            await GitActionAsync(() => Git.CreateBranchAsync(name.Trim(), switchTo: true)).ConfigureAwait(true);
        }
    }

    public Task MergeBranchAsync() => GitActionAsync(async () =>
    {
        var branches = (await Git.BranchesAsync().ConfigureAwait(true)).Where(b => !b.IsCurrent).ToList();
        var items = branches.Select(b => new QuickPickItem(b.Name) { Tag = b.Name, Detail = b.IsRemote ? "remote" : null }).ToList();
        if (await Palette.ShowAsync(items, $"Merge a branch into {Git.Status?.Branch ?? "HEAD"}").ConfigureAwait(true) is { Tag: string branch })
        {
            try
            {
                await Git.MergeAsync(branch).ConfigureAwait(true);
                StatusBar.ShowMessage(Localizer.Format("msg.git.merged", "Merged {0}.", branch), TimeSpan.FromSeconds(5));
            }
            catch (GitException) when (Git.Status is { IsMerging: true })
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.git.mergingProducedConflictsResolveThemIn", "Merging {0} produced conflicts. Resolve them in the Git Changes window, stage the files and commit, or abort the merge.", branch));
                Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
            }
        }
    });

    public Task FetchAsync() => GitActionAsync(() => Git.FetchAsync(), "Fetching…");

    public Task PullAsync() => GitActionAsync(() => Git.PullAsync(), "Pulling…");

    public Task PushAsync() => GitActionAsync(() => Git.PushAsync(), "Pushing…");

    /// <summary>Pulls, then pushes the branch's own commits.</summary>
    public Task SyncAsync() => GitActionAsync(async () =>
    {
        if (Git.Status?.Upstream is not null)
        {
            await Git.PullAsync().ConfigureAwait(true);
        }

        await Git.PushAsync().ConfigureAwait(true);
    }, "Syncing…");

    public Task AbortMergeAsync() => GitActionAsync(() => Git.AbortMergeAsync());

    public Task CommitFromCommandAsync()
    {
        Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId, focus: true);
        return Task.CompletedTask;
    }
}
