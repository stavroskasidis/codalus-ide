using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// The code editor's Git ▸ (its context menu, <c>editor/context/git</c>), as the classic IDE's: Commit or Stash…, Undo
/// Changes, View History, Compare with Unmodified…, Blame (Annotate) and Ignore and Untrack item, on the active file. Its
/// context keys say whether that file has changes (<c>gitActiveFileChanged</c>) and whether git ignores it
/// (<c>gitActiveFileIgnored</c>); an entry that doesn't apply is hidden.
/// </summary>
public sealed partial class ShellHost
{
    private CancellationTokenSource? _activeFileGitKeys;

    /// <summary>The active file's git keys, after it changes and after each status read.</summary>
    private void UpdateActiveFileGitKeys()
    {
        // On the UI thread, as every read's end: the one in flight is for the file before.
        _activeFileGitKeys?.Cancel();
        _activeFileGitKeys?.Dispose();
        _activeFileGitKeys = null;

        // The first status read comes before the layout exists; the first active document sets them again.
        var path = Layout is null ? null : ActiveEditor?.Model.Path;
        var git = path is null ? null : Git.ServiceFor(path);
        if (path is null || git?.Status is not { } status || !File.Exists(path))
        {
            _activeFileIgnoredKey = null;
            Workbench.Context.Set("gitActiveFileChanged", false);
            Workbench.Context.Set("gitActiveFileIgnored", false);
            return;
        }

        // A conflicted file is resolved in the merge editor, not undone. A file the status lists isn't ignored, unless its
        // removal from the index is staged (Ignore and Untrack's own): then only check-ignore knows.
        var file = StatusOf(status, path);
        Workbench.Context.Set("gitActiveFileChanged", file is { IsConflict: false });
        if (file is not null and not { Index: 'D' })
        {
            _activeFileIgnoredKey = null;
            Workbench.Context.Set("gitActiveFileIgnored", false);
            return;
        }

        // Asked again only when the file, its status or a changed .gitignore is new: a status read comes after every save.
        var key = IgnoredKey(status, path, file);
        if (key == _activeFileIgnoredKey)
        {
            return;
        }

        var cancellation = _activeFileGitKeys = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = ReadIgnoredAsync(git, path, key, cancellation);
    }

    private string? _activeFileIgnoredKey;

    /// <summary>What git's answer for the file depends on, as far as the status shows it: the file, and the .gitignore files changed.</summary>
    private static string IgnoredKey(GitStatus status, string path, GitFileStatus? file)
    {
        var key = new System.Text.StringBuilder(path).Append('|').Append(file?.Index).Append(file?.WorkTree);
        foreach (var ignore in status.Files.Where(f => Path.GetFileName(f.Path) == ".gitignore"))
        {
            key.Append('|').Append(ignore.Path).Append(ignore.Index).Append(ignore.WorkTree)
                .Append(File.Exists(ignore.Path) ? File.GetLastWriteTimeUtc(ignore.Path).Ticks : 0);
        }

        return key.ToString();
    }

    private async Task ReadIgnoredAsync(GitService git, string path, string key, CancellationTokenSource cancellation)
    {
        try
        {
            var ignored = await git.IsIgnoredAsync(path, cancellation.Token).ConfigureAwait(true);
            if (ReferenceEquals(_activeFileGitKeys, cancellation))
            {
                _activeFileIgnoredKey = key;
                Workbench.Context.Set("gitActiveFileIgnored", ignored);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or GitException or InvalidOperationException)
        {
            // Moved on, closed, or git failed: the key stays as it was, and the next status read sets it again.
        }
    }

    /// <summary>The status's entry for a file, by either spelling of its path (git's resolves links).</summary>
    private static GitFileStatus? StatusOf(GitStatus status, string path)
    {
        var real = Codalus.Platform.RealPath.Resolve(path);
        return status.Files.FirstOrDefault(f => Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, path) || Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, real));
    }

    /// <summary>
    /// Undo Changes: the active file goes back to the last commit, its staged and unstaged changes (and unsaved edits)
    /// dropped, after asking: Cancel is the default. A file the last commit doesn't have is deleted, and the question says so.
    /// </summary>
    public async Task UndoActiveFileChangesAsync()
    {
        if (ActiveEditor?.Model is not { Path: { } path } model || Git.ServiceFor(path) is not { Status: { } status } git || StatusOf(status, path) is not { IsConflict: false } file)
        {
            return;
        }

        var name = Path.GetFileName(path);
        var undo = Localizer.Get("msg.editorGit.undoChanges", "Undo Changes");
        var cancel = Localizer.Get("msg.editorGit.cancel", "Cancel");
        var message = file.IsUntracked
            ? Localizer.Format("msg.editorGit.notTrackedWillBeDeleted", "{0} is not tracked by git: it will be deleted.", name)
            : file.Index is 'A' or 'R' or 'C' || status.Head is null
                ? Localizer.Format("msg.editorGit.notInTheLastCommitWillBeDeleted", "{0} is not in the last commit: it will be deleted.", name)
                : Localizer.Format("msg.editorGit.goesBackToTheLastCommit", "{0} goes back to the last commit: its staged and unstaged changes will be lost. This can't be undone.", name);
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.editorGit.undoChangesTo", "Undo changes to {0}?", name),
            message,
            [undo, cancel])
        {
            DefaultButton = cancel,
            Icon = "undo",
            IsDanger = true,
            Context = git.Root,
            Details = model.IsModified ? [new DetailLine(Localizer.Format("msg.git.unsavedEditsLostToo", "{0} also has unsaved edits, and they will be lost too.", name), "note")] : null,
        }).ConfigureAwait(true);
        if (choice.Button != undo)
        {
            return;
        }

        await GitActionAsync(async () =>
        {
            // Saved first, so the file watcher doesn't take the change on disk for a conflict with the edits.
            if (model.IsModified && !await SaveAsync(model).ConfigureAwait(true))
            {
                return;
            }

            await git.UndoChangesAsync(path).ConfigureAwait(true);
            if (File.Exists(path))
            {
                await model.ReloadAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Ignore and Untrack item: the active file goes into the root <c>.gitignore</c> and, when git tracks it, out of the
    /// index (the file stays on disk; its removal is staged). Undone by unstaging the removal and removing the line, so it
    /// doesn't ask; the status bar says what it did.
    /// </summary>
    public Task IgnoreAndUntrackActiveFileAsync()
    {
        if (ActiveEditor?.Model.Path is not { } path)
        {
            return Task.CompletedTask;
        }

        var git = Git.ServiceFor(path);
        return GitActionAsync(async () =>
        {
            var untracked = git.Status is { } status && StatusOf(status, path) is { IsUntracked: true };
            await git.IgnoreAndUntrackAsync(path).ConfigureAwait(true);
            var name = Path.GetFileName(path);
            StatusBar.ShowMessage(
                untracked
                    ? Localizer.Format("msg.editorGit.addedToGitignore", "{0} was added to .gitignore.", name)
                    : Localizer.Format("msg.editorGit.ignoredAndUntracked", "{0} was added to .gitignore and is no longer tracked: its removal is staged.", name),
                TimeSpan.FromSeconds(4));
        });
    }
}
