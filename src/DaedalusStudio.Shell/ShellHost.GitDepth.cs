using System.Globalization;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Shell;

/// <summary>
/// Git depth (M13): history, file and line history, revision comparisons, the merge editor, operations in progress,
/// stash, cherry-pick, revert, reset, rebase (also interactive), tags, remotes, clone and init, ignoring, submodules.
/// </summary>
public sealed partial class ShellHost
{
    private string? _templateLoadedFor;

    /// <summary>Context keys and the commit template, after each status refresh.</summary>
    private void UpdateGitDepthStatus()
    {
        Workbench.Context.Set("gitOperation", Git.Status?.Operation);
        Workbench.Context.Set("gitHasConflicts", Git.Status?.Conflicts.Any() == true);
        GitChanges.Operation = Git.Status?.Operation;
        if (Git.Root is { } root && _templateLoadedFor != root)
        {
            _templateLoadedFor = root;
            _ = LoadCommitTemplateAsync();
        }
    }

    /// <summary>Prefills an empty commit message with the repository's <c>commit.template</c>.</summary>
    private async Task LoadCommitTemplateAsync()
    {
        try
        {
            if (await Git.CommitTemplateAsync().ConfigureAwait(true) is { Length: > 0 } template && string.IsNullOrEmpty(GitChanges.Message))
            {
                GitChanges.Message = template.TrimEnd();
            }
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
        {
        }
    }

    // ------------------------------------------------------------------ history

    /// <summary>Opens the history window for a query (all branches by default).</summary>
    public async Task<GitHistoryDocument?> ShowHistoryAsync(GitLogQuery? query = null, string? title = null, (string Path, int Start, int End)? lines = null)
    {
        if (Git.Root is null)
        {
            StatusBar.ShowMessage("The open solution is not in a git repository.", TimeSpan.FromSeconds(4));
            return null;
        }

        query ??= new GitLogQuery { AllBranches = true };
        var id = "git-history:" + (lines is { } l ? $"{l.Path}:{l.Start.ToString(CultureInfo.InvariantCulture)}-{l.End.ToString(CultureInfo.InvariantCulture)}" : $"{query.Path}|{query.Revision}|{query.AllBranches}");
        if (Layout.FindDocument<GitHistoryDocument>(id) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            return open;
        }

        var viewModel = new GitHistoryViewModel(Git, query, title ?? "History", CommitActionAsync, OpenCommitFileAsync, lines);
        var document = new GitHistoryDocument(viewModel, id);
        Layout.OpenDocument(document, focus: true);
        await GitActionAsync(viewModel.LoadAsync).ConfigureAwait(true);
        return document;
    }

    /// <summary>The history of the active document's file (or the Solution Explorer's selected file or folder).</summary>
    public Task ShowFileHistoryAsync()
    {
        var path = Layout.ActiveDocument is CodeEditorDocument { Model.Path: { } file } ? file : Explorer.Selected?.Node.Path;
        return path is null
            ? Task.CompletedTask
            : ShowHistoryAsync(new GitLogQuery { Path = path }, "History: " + Path.GetFileName(path));
    }

    /// <summary>The commits that changed the selected lines (or the caret's line).</summary>
    public Task ShowLineHistoryAsync()
    {
        if (ActiveEditor is not { Controller: { } controller, Model: { } model })
        {
            return Task.CompletedTask;
        }

        var editor = controller.Editor;
        var first = editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber;
        var last = editor.Document.GetLineByOffset(Math.Max(editor.SelectionStart, editor.SelectionStart + Math.Max(0, editor.SelectionLength - 1))).LineNumber;
        return ShowHistoryAsync(null, $"History: {model.Name} ({first.ToString(CultureInfo.InvariantCulture)}–{last.ToString(CultureInfo.InvariantCulture)})", (model.Path, first, last));
    }

    /// <summary>The commit that last changed the caret's line (blame), in the history.</summary>
    public Task ShowCommitAtCaretAsync() => GitActionAsync(async () =>
    {
        if (ActiveEditor is not { Controller: { } controller, Model: { } model })
        {
            return;
        }

        var line = controller.Editor.TextArea.Caret.Line;
        var blame = await Git.BlameAsync(model.Path, model.Text.Text).ConfigureAwait(true);
        if (blame.FirstOrDefault(r => line >= r.StartLine && line < r.StartLine + r.LineCount) is not { IsUncommitted: false } range)
        {
            StatusBar.ShowMessage("This line isn't committed yet.", TimeSpan.FromSeconds(4));
            return;
        }

        await ShowHistoryAsync(new GitLogQuery { Revision = range.Commit }, "Commit " + range.Commit[..Math.Min(8, range.Commit.Length)]).ConfigureAwait(true);
    });

    /// <summary>Compares the active file with its version on a branch, tag or commit.</summary>
    public Task CompareWithRevisionAsync() => GitActionAsync(async () =>
    {
        if (ActiveEditor is not { Model: { } model })
        {
            return;
        }

        var items = new List<QuickPickItem> { new("HEAD") { Tag = "HEAD", Detail = "the last commit" } };
        items.AddRange((await Git.BranchesAsync().ConfigureAwait(true)).Where(b => !b.IsCurrent).Select(b => new QuickPickItem(b.Name) { Tag = b.Name, Detail = b.IsRemote ? "remote branch" : "branch" }));
        items.AddRange((await Git.TagsAsync().ConfigureAwait(true)).Select(t => new QuickPickItem(t.Name) { Tag = t.Name, Detail = "tag" }));
        items.Add(new QuickPickItem("Another Commit…") { Tag = "commit" });
        var picked = await Palette.ShowAsync(items, $"Compare {model.Name} with").ConfigureAwait(true);
        var revision = picked?.Tag as string;
        if (revision == "commit")
        {
            var answer = await Dialogs.ShowAsync(new DialogRequest("Compare with Commit", "A commit hash or revision (main~3, v1.0^…):", ["Compare", "Cancel"]) { Input = string.Empty }).ConfigureAwait(true);
            revision = answer is { Button: "Compare", Input: { Length: > 0 } input } ? input.Trim() : null;
        }

        if (revision is null)
        {
            return;
        }

        if (await Git.ResolveAsync(revision).ConfigureAwait(true) is null)
        {
            throw new GitException($"'{revision}' isn't a commit.", -1);
        }

        var path = model.Path;
        await OpenRevisionDiffAsync(path, $"{revision}↔{path}", revision, async () => await Git.ShowAsync(path, revision).ConfigureAwait(true) ?? string.Empty, "Working tree", () => Task.FromResult(model.Text.Text)).ConfigureAwait(true);
    });

    private async Task OpenRevisionDiffAsync(string path, string key, string oldTitle, Func<Task<string>> oldText, string newTitle, Func<Task<string>> newText)
    {
        var viewModel = new DiffViewModel(path, key, oldTitle, oldText, newTitle, newText);
        var document = Layout.FindDocument<DiffDocument>("diff:revisions:" + key) ?? new DiffDocument(viewModel, EditorFont);
        await document.ViewModel.LoadAsync().ConfigureAwait(true);
        Layout.OpenDocument(document, focus: true);
    }

    /// <summary>A file of a commit against the commit's first parent.</summary>
    private Task OpenCommitFileAsync(GitCommit commit, GitCommitFileRow file) => GitActionAsync(() =>
    {
        var parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var oldPath = file.OldPath ?? file.Path;
        return OpenRevisionDiffAsync(
            file.Path,
            $"{commit.Hash}:{file.Path}",
            parent is null ? "(none)" : parent[..Math.Min(8, parent.Length)],
            async () => parent is null || file.Status == 'A' ? string.Empty : await Git.ShowAsync(oldPath, parent).ConfigureAwait(true) ?? string.Empty,
            commit.ShortHash,
            async () => file.Status == 'D' ? string.Empty : await Git.ShowAsync(file.Path, commit.Hash).ConfigureAwait(true) ?? string.Empty);
    });

    /// <summary>A command on a commit of the history.</summary>
    private Task CommitActionAsync(string action, GitCommit commit) => GitActionAsync(async () =>
    {
        switch (action)
        {
            case "checkout":
                await Git.CheckoutAsync(commit.Hash).ConfigureAwait(true);
                break;
            case "branch":
                if (await AskNameAsync("New Branch", $"A new branch at {commit.ShortHash} (and switch to it):", "Create") is { } branch)
                {
                    await Git.CreateBranchAtAsync(branch, commit.Hash, switchTo: true).ConfigureAwait(true);
                }

                break;
            case "tag":
                await CreateTagAsync(commit.Hash).ConfigureAwait(true);
                break;
            case "cherryPick":
                await StopOnConflictAsync(() => Git.CherryPickAsync(commit.Hash), "Cherry-picking").ConfigureAwait(true);
                break;
            case "revert":
                await StopOnConflictAsync(() => Git.RevertAsync(commit.Hash), "Reverting").ConfigureAwait(true);
                break;
            case "reset-soft" or "reset-mixed":
                await Git.ResetAsync(commit.Hash, action[6..]).ConfigureAwait(true);
                break;
            case "reset-hard":
                var answer = await Dialogs.ShowAsync(new DialogRequest(
                    $"Reset {Git.Status?.Branch ?? "HEAD"} to {commit.ShortHash}?",
                    "Every uncommitted change will be lost, and the commits after it leave the branch.",
                    ["Reset", "Cancel"]) { DefaultButton = "Cancel" }).ConfigureAwait(true);
                if (answer.Button == "Reset")
                {
                    await Git.ResetAsync(commit.Hash, "hard").ConfigureAwait(true);
                }

                break;
            case "rebaseInteractive":
                await InteractiveRebaseAsync(commit.Hash, commit.ShortHash).ConfigureAwait(true);
                break;
            case "copyHash":
                if (Window?.Clipboard is { } clipboard)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, commit.Hash).ConfigureAwait(true);
                }

                break;
        }

        await RefreshHistoryAsync().ConfigureAwait(true);
    });

    private async Task RefreshHistoryAsync()
    {
        foreach (var history in Layout.Documents.OfType<GitHistoryDocument>())
        {
            await history.ViewModel.ReloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Runs an operation that may stop on conflicts; then says so and shows Git Changes.</summary>
    private async Task StopOnConflictAsync(Func<Task> operation, string what)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (GitException) when (Git.Status is { Operation: not null })
        {
            Workbench.Notifications.Warning($"{what} stopped on conflicts. Resolve them (the merge editor opens from Git Changes), then Continue, or Abort.");
            Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        }
    }

    private async Task<string?> AskNameAsync(string title, string message, string button, string initial = "")
    {
        var answer = await Dialogs.ShowAsync(new DialogRequest(title, message, [button, "Cancel"])
        {
            Input = initial,
            Validate = name => string.IsNullOrWhiteSpace(name) ? "Enter a name."
                : name.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') || name.Contains("..", StringComparison.Ordinal) ? "Not a valid name."
                : null,
        }).ConfigureAwait(true);
        return answer.Button == button ? answer.Input?.Trim() : null;
    }

    // ------------------------------------------------------------------ the operation in progress and conflicts

    public Task ContinueOperationAsync() => GitActionAsync(async () =>
    {
        if (Git.Status?.Conflicts.Any() == true)
        {
            throw new GitException("Resolve the conflicts first (open each in the merge editor, or stage it).", -1);
        }

        await StopOnConflictAsync(() => Git.ContinueAsync(), "Continuing").ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
    });

    public Task AbortOperationAsync() => GitActionAsync(async () =>
    {
        await Git.AbortAsync().ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
    });

    public Task SkipOperationAsync() => GitActionAsync(() => StopOnConflictAsync(() => Git.SkipAsync(), "Continuing"));

    /// <summary>Opens a conflicted file in the merge editor.</summary>
    public Task<MergeEditorDocument?> OpenMergeEditorAsync(string path) => GitActionResultAsync(async () =>
    {
        if (Layout.FindDocument<MergeEditorDocument>(MergeEditorDocument.IdFor(path)) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            return open;
        }

        var (@base, ours, theirs) = await Git.ConflictVersionsAsync(path).ConfigureAwait(true);
        var result = File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(true) : string.Empty;
        MergeEditorDocument? document = null;
        var viewModel = new MergeEditorViewModel(path, result, ours, theirs, @base, async text =>
        {
            await Git.ResolveAsync(path, text).ConfigureAwait(true);
            if (_models.TryGetValue(Path.GetFullPath(path), out var model) && !model.IsModified)
            {
                await model.ReloadAsync(CancellationToken.None).ConfigureAwait(true);
            }

            if (document is not null)
            {
                Layout.CloseDocument(document);
            }

            StatusBar.ShowMessage($"{Path.GetFileName(path)} is resolved.", TimeSpan.FromSeconds(5));
        });
        document = new MergeEditorDocument(viewModel, EditorFont);
        Layout.OpenDocument(document, focus: true);
        return document;
    });

    /// <summary>
    /// <c>daedalus merge LOCAL REMOTE BASE MERGED</c> (git mergetool): the merge editor on git's four files. True when the
    /// result was accepted (written to MERGED); false when the tab was closed without it.
    /// </summary>
    public async Task<bool> MergeFilesAsync(string local, string remote, string @base, string merged)
    {
        static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (Layout.FindDocument<MergeEditorDocument>(MergeEditorDocument.IdFor(merged)) is { } old)
        {
            Layout.CloseDocument(old);
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MergeEditorDocument? document = null;
        var viewModel = new MergeEditorViewModel(merged, Read(merged), Read(local), Read(remote), Read(@base), async text =>
        {
            await File.WriteAllTextAsync(merged, text).ConfigureAwait(true);
            done.TrySetResult(true);
            if (document is not null)
            {
                Layout.CloseDocument(document);
            }
        }, "Local (ours)", "Remote (theirs)");
        document = new MergeEditorDocument(viewModel, EditorFont);
        void OnClosed(object? sender, Docking.ShellDocument closed)
        {
            if (closed == document)
            {
                Layout.DocumentClosed -= OnClosed;
                done.TrySetResult(false);
            }
        }

        Layout.DocumentClosed += OnClosed;
        Layout.OpenDocument(document, focus: true);
        return await done.Task.ConfigureAwait(true);
    }

    /// <summary><c>daedalus --wait FILE</c> (git's core.editor): opens the file and returns when its tab closes.</summary>
    public async Task<bool> EditAndWaitAsync(string file)
    {
        if (await OpenFileAsync(file).ConfigureAwait(true) is not { } document)
        {
            return false;
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, Docking.ShellDocument closed)
        {
            if (closed is CodeEditorDocument editor && DaedalusStudio.Workbench.Projects.SolutionService.PathComparer.Equals(editor.Model.Path, document.Model.Path)
                && !Layout.Documents.OfType<CodeEditorDocument>().Any(d => d != closed && DaedalusStudio.Workbench.Projects.SolutionService.PathComparer.Equals(d.Model.Path, document.Model.Path)))
            {
                Layout.DocumentClosed -= OnClosed;
                done.TrySetResult(true);
            }
        }

        Layout.DocumentClosed += OnClosed;
        StatusBar.ShowMessage($"Waiting: close {Path.GetFileName(file)} when you're done editing.", TimeSpan.FromSeconds(10));
        return await done.Task.ConfigureAwait(true);
    }

    private async Task<T?> GitActionResultAsync<T>(Func<Task<T>> action)
        where T : class
    {
        T? result = null;
        await GitActionAsync(async () => result = await action().ConfigureAwait(true)).ConfigureAwait(true);
        return result;
    }

    /// <summary>Resolves a conflicted file by taking a whole side.</summary>
    public Task ResolveWithSideAsync(string path, bool ours) => GitActionAsync(() => Git.ResolveWithAsync(path, ours));

    // ------------------------------------------------------------------ stash

    public Task StashAsync() => GitActionAsync(async () =>
    {
        var answer = await Dialogs.ShowAsync(new DialogRequest("Stash Changes", "A message for the stash (optional):", ["Stash", "Cancel"])
        {
            Input = string.Empty,
            Choices = [new DialogChoice("untracked", "Include untracked files", false)],
        }).ConfigureAwait(true);
        if (answer.Button == "Stash")
        {
            await Git.StashAsync(answer.Input?.Trim(), answer.Checked.Contains("untracked")).ConfigureAwait(true);
            StatusBar.ShowMessage("Changes stashed.", TimeSpan.FromSeconds(4));
        }
    });

    /// <summary>The stashes: apply, pop or drop one.</summary>
    public Task ShowStashesAsync() => GitActionAsync(async () =>
    {
        var stashes = await Git.StashesAsync().ConfigureAwait(true);
        if (stashes.Count == 0)
        {
            StatusBar.ShowMessage("No stashes.", TimeSpan.FromSeconds(4));
            return;
        }

        if (await Palette.ShowAsync([.. stashes.Select(s => new QuickPickItem(s.Message) { Detail = s.Name, Tag = s })], "Stashes").ConfigureAwait(true) is not { Tag: GitStash stash })
        {
            return;
        }

        switch ((await Palette.ShowAsync([new("Apply") { Tag = "apply" }, new("Pop (apply and drop)") { Tag = "pop" }, new("Drop") { Tag = "drop" }], stash.Message).ConfigureAwait(true))?.Tag)
        {
            case "apply":
                await Git.ApplyStashAsync(stash, pop: false).ConfigureAwait(true);
                break;
            case "pop":
                await Git.ApplyStashAsync(stash, pop: true).ConfigureAwait(true);
                break;
            case "drop":
                await Git.DropStashAsync(stash).ConfigureAwait(true);
                break;
        }
    });

    // ------------------------------------------------------------------ rebase

    public Task RebaseAsync() => GitActionAsync(async () =>
    {
        var branches = (await Git.BranchesAsync().ConfigureAwait(true)).Where(b => !b.IsCurrent).ToList();
        if (await Palette.ShowAsync([.. branches.Select(b => new QuickPickItem(b.Name) { Tag = b.Name, Detail = b.IsRemote ? "remote" : null })], $"Rebase {Git.Status?.Branch ?? "HEAD"} onto").ConfigureAwait(true) is { Tag: string onto })
        {
            await StopOnConflictAsync(() => Git.RebaseAsync(onto), "The rebase").ConfigureAwait(true);
            await RefreshHistoryAsync().ConfigureAwait(true);
        }
    });

    /// <summary>The interactive rebase editor for the commits after <paramref name="onto"/> (asks for it when null).</summary>
    public Task InteractiveRebaseAsync() => GitActionAsync(async () =>
    {
        var branches = (await Git.BranchesAsync().ConfigureAwait(true)).Where(b => !b.IsCurrent).ToList();
        var items = new List<QuickPickItem> { new("HEAD~5") { Tag = "HEAD~5", Detail = "the last five commits" } };
        items.AddRange(branches.Select(b => new QuickPickItem(b.Name) { Tag = b.Name, Detail = b.IsRemote ? "remote" : null }));
        if (await Palette.ShowAsync(items, "Rebase interactively onto (or pick a commit in the history)").ConfigureAwait(true) is { Tag: string onto })
        {
            await InteractiveRebaseAsync(onto, onto).ConfigureAwait(true);
        }
    });

    private async Task InteractiveRebaseAsync(string onto, string title)
    {
        var steps = await Git.RebaseTodoAsync(onto).ConfigureAwait(true);
        if (steps.Count == 0)
        {
            StatusBar.ShowMessage($"No commits after {title} to rebase.", TimeSpan.FromSeconds(5));
            return;
        }

        if (Layout.FindDocument<InteractiveRebaseDocument>(InteractiveRebaseDocument.DocumentId) is { } old)
        {
            Layout.CloseDocument(old);
        }

        InteractiveRebaseDocument? document = null;
        var viewModel = new InteractiveRebaseViewModel(onto, title, steps, async chosen =>
        {
            try
            {
                await Git.RebaseInteractiveAsync(onto, chosen).ConfigureAwait(true);
                StatusBar.ShowMessage("Rebased.", TimeSpan.FromSeconds(5));
            }
            finally
            {
                await RefreshHistoryAsync().ConfigureAwait(true);
                if (Git.Status is { Operation: "rebase" })
                {
                    Workbench.Notifications.Warning("The rebase stopped on conflicts. Resolve them, then Continue, or Abort.");
                    Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
                }
            }
        });
        viewModel.Finished += (_, _) =>
        {
            if (document is not null && !viewModel.HasError)
            {
                Layout.CloseDocument(document);
            }
        };
        document = new InteractiveRebaseDocument(viewModel);
        Layout.OpenDocument(document, focus: true);
    }

    // ------------------------------------------------------------------ tags and remotes

    private async Task CreateTagAsync(string? hash)
    {
        var answer = await Dialogs.ShowAsync(new DialogRequest("Create Tag", $"A tag at {(hash is null ? "HEAD" : hash[..Math.Min(8, hash.Length)])} (a message makes it annotated):", ["Create", "Cancel"])
        {
            Input = string.Empty,
            Validate = name => string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) ? "Enter a name without spaces." : null,
        }).ConfigureAwait(true);
        if (answer is not { Button: "Create", Input: { } name })
        {
            return;
        }

        var message = await Dialogs.ShowAsync(new DialogRequest("Tag Message", $"A message for {name.Trim()} (empty: a lightweight tag):", ["OK"]) { Input = string.Empty }).ConfigureAwait(true);
        await Git.CreateTagAsync(name.Trim(), hash, message.Input?.Trim()).ConfigureAwait(true);
    }

    /// <summary>Tags: create, push, delete.</summary>
    public Task ManageTagsAsync() => GitActionAsync(async () =>
    {
        var items = new List<QuickPickItem> { new("+ Create Tag…") { Tag = "create" } };
        items.AddRange((await Git.TagsAsync().ConfigureAwait(true)).Select(t => new QuickPickItem(t.Name) { Tag = t, Detail = t.Message ?? t.Commit[..Math.Min(8, t.Commit.Length)] }));
        switch (await Palette.ShowAsync(items, "Tags").ConfigureAwait(true))
        {
            case { Tag: "create" }:
                await CreateTagAsync(null).ConfigureAwait(true);
                break;
            case { Tag: GitTag tag }:
                switch ((await Palette.ShowAsync([new("Push to origin") { Tag = "push" }, new("Delete") { Tag = "delete" }, new("Delete from origin") { Tag = "deleteRemote" }], tag.Name).ConfigureAwait(true))?.Tag)
                {
                    case "push":
                        await Git.PushTagAsync(tag.Name).ConfigureAwait(true);
                        break;
                    case "delete":
                        await Git.DeleteTagAsync(tag.Name).ConfigureAwait(true);
                        break;
                    case "deleteRemote":
                        await Git.DeleteRemoteTagAsync(tag.Name).ConfigureAwait(true);
                        break;
                }

                break;
        }
    });

    /// <summary>Remotes: add, rename, change the URL, remove.</summary>
    public Task ManageRemotesAsync() => GitActionAsync(async () =>
    {
        var items = new List<QuickPickItem> { new("+ Add Remote…") { Tag = "add" } };
        items.AddRange((await Git.RemotesAsync().ConfigureAwait(true)).Select(r => new QuickPickItem(r.Name) { Tag = r, Detail = r.FetchUrl }));
        switch (await Palette.ShowAsync(items, "Remotes").ConfigureAwait(true))
        {
            case { Tag: "add" }:
                if (await AskNameAsync("Add Remote", "The remote's name:", "Next", "origin") is { } name
                    && await Dialogs.ShowAsync(new DialogRequest("Add Remote", $"The URL of {name}:", ["Add", "Cancel"]) { Input = string.Empty }).ConfigureAwait(true) is { Button: "Add", Input: { Length: > 0 } url })
                {
                    await Git.AddRemoteAsync(name, url.Trim()).ConfigureAwait(true);
                }

                break;
            case { Tag: GitRemote remote }:
                switch ((await Palette.ShowAsync([new("Rename…") { Tag = "rename" }, new("Change URL…") { Tag = "url" }, new("Remove") { Tag = "remove" }], remote.Name).ConfigureAwait(true))?.Tag)
                {
                    case "rename":
                        if (await AskNameAsync("Rename Remote", $"A new name for {remote.Name}:", "Rename", remote.Name) is { } newName)
                        {
                            await Git.RenameRemoteAsync(remote.Name, newName).ConfigureAwait(true);
                        }

                        break;
                    case "url":
                        if (await Dialogs.ShowAsync(new DialogRequest("Change URL", $"The URL of {remote.Name}:", ["Change", "Cancel"]) { Input = remote.FetchUrl }).ConfigureAwait(true) is { Button: "Change", Input: { Length: > 0 } newUrl })
                        {
                            await Git.SetRemoteUrlAsync(remote.Name, newUrl.Trim()).ConfigureAwait(true);
                        }

                        break;
                    case "remove":
                        await Git.RemoveRemoteAsync(remote.Name).ConfigureAwait(true);
                        break;
                }

                break;
        }
    });

    // ------------------------------------------------------------------ repositories

    /// <summary>Clones a repository, then opens its solution when it has one.</summary>
    public async Task CloneRepositoryAsync()
    {
        var url = await Dialogs.ShowAsync(new DialogRequest("Clone Repository", "The repository's URL:", ["Next", "Cancel"]) { Input = string.Empty }).ConfigureAwait(true);
        if (url is not { Button: "Next", Input: { Length: > 0 } address })
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(address.Trim().TrimEnd('/'));
        var parent = Solutions.Solution is { } solution ? Path.GetDirectoryName(solution.Directory)! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        var target = await Dialogs.ShowAsync(new DialogRequest("Clone Repository", "Clone into:", ["Clone", "Cancel"]) { Input = Path.Combine(parent, name) }).ConfigureAwait(true);
        if (target is not { Button: "Clone", Input: { Length: > 0 } destination })
        {
            return;
        }

        try
        {
            StatusBar.ShowMessage($"Cloning {address.Trim()}…");
            await Git.CloneAsync(address.Trim(), destination.Trim(), CancellationToken.None).ConfigureAwait(true);
        }
        catch (GitException ex)
        {
            ReportGitError(ex);
            return;
        }
        finally
        {
            StatusBar.ClearMessage();
        }

        var solutions = Directory.EnumerateFiles(destination.Trim(), "*.sln*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (solutions.Count == 1)
        {
            await OpenSolutionAsync(solutions[0]).ConfigureAwait(true);
        }
        else
        {
            Workbench.Notifications.Info($"Cloned into {destination.Trim()}." + (solutions.Count == 0 ? " It has no solution at its root; open one with File → Open." : " Open one of its solutions with File → Open."));
        }
    }

    /// <summary>Creates a repository for the open solution's folder, with a .NET .gitignore.</summary>
    public async Task CreateRepositoryAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage("Open a solution first.", TimeSpan.FromSeconds(4));
            return;
        }

        var folder = solution.Directory;
        if (Git.Root is not null)
        {
            StatusBar.ShowMessage("The solution is already in a git repository.", TimeSpan.FromSeconds(4));
            return;
        }

        try
        {
            await Git.InitAsync(folder, CancellationToken.None).ConfigureAwait(true);
            await OpenRepositoryAsync(folder).ConfigureAwait(true);
            Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        }
        catch (GitException ex)
        {
            ReportGitError(ex);
        }
    }

    /// <summary>Adds the Solution Explorer's selected file or folder to .gitignore.</summary>
    public Task AddToGitignoreAsync() =>
        Explorer.Selected?.Node.Path is { } path ? GitActionAsync(() => Git.IgnoreAsync(path)) : Task.CompletedTask;

    public Task UpdateSubmodulesAsync() => GitActionAsync(() => Git.UpdateSubmodulesAsync(), "Updating submodules…");
}
