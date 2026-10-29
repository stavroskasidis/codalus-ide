using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Context;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;

namespace Codalus.Shell;

/// <summary>
/// The Git Changes window's menus for a file, a folder or the repository node (#23, D38): built from the registered
/// location <c>gitChanges/context</c> with keys scoped to that menu, in place of the XAML menus it had. Its commands get the
/// node's path, its section and the files the window shows under it (question 2): with a filter, a folder action acts on
/// the files shown, and with no third argument on the whole folder.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>
    /// The context <c>gitChanges/context</c>'s when-clauses read for a node: the global context with its kind (<c>file</c>,
    /// <c>folder</c>, <c>repository</c>), its section, whether every file under it is untracked, whether its path exists,
    /// and its repository's folder (ADR 0085: one of several while all are shown, <c>gitAllRepositories</c>).
    /// </summary>
    internal IContextKeyReader GitChangeScope(string path, string section, string kind)
    {
        var rows = GitChanges.RowsUnder(path, section, shownOnly: false);
        var menuContext = new MenuContext();
        menuContext.Set("gitChangeKind", kind);
        menuContext.Set("gitChangeSection", section);
        menuContext.Set("gitChangeRepository", Git.ServiceFor(path).Root);
        menuContext.Set("gitChangeAllUntracked", rows.Count > 0 && rows.All(r => r.Status.IsUntracked) && kind != "repository");
        menuContext.Set("gitChangeExists", File.Exists(path) || Directory.Exists(path));
        return Workbench.Context.With(menuContext.Keys);
    }

    /// <summary>The keys of a menu's own scope, set by name (so the command sweep's check of when-clause keys sees them).</summary>
    internal sealed class MenuContext
    {
        public Dictionary<string, object?> Keys { get; } = new(StringComparer.Ordinal);

        public void Set(string key, object? value) => Keys[key] = value;
    }

    /// <summary>
    /// The menu of a Git Changes node: <paramref name="kind"/> is <c>file</c>, <c>folder</c> or <c>repository</c>,
    /// <paramref name="shown"/> the files the window shows under it. Its entries show only their rule's keys (question 13).
    /// Null when no entry applies.
    /// </summary>
    internal Avalonia.Controls.ContextMenu? GitChangesMenu(string path, string section, string kind, IReadOnlyList<string> shown)
    {
        var scope = GitChangeScope(path, section, kind);
        var args = GitChangesViewModel.ArgumentsFor(path, section, shown).ToArray();
        var git = Git.ServiceFor(path);
        return MenuBuilder.Context(this, MenuLocations.GitChangesContext, new MenuBuilder.MenuShape(c => new RelayCommand(() => _ = InRepositoryAsync(git, () => RunAsync(c, args, scope))))
        {
            Scope = scope,
            KeyFor = item => item.RuleKeyLabel,
        });
    }

    /// <summary>The visible entries of a node's menu, as built (tests and the smoke test).</summary>
    internal IReadOnlyList<Workbench.Menus.MenuItem> GitChangesMenuItems(string path, string section, string kind) =>
        Workbench.Menus.GetItems(MenuLocations.GitChangesContext, GitChangeScope(path, section, kind));

    /// <summary>Runs a hover button's command on a node (T1), in the node's scope.</summary>
    private Task RunOnGitChangeAsync(string command, IReadOnlyList<JsonElement> args)
    {
        var (path, section, _) = ChangeArguments(args);
        if (path is null || section is null)
        {
            return RunAsync(command, args);
        }

        var git = Git.ServiceFor(path);
        var kind = File.Exists(path) || GitChanges.SectionRows(section).Any(r => Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(r.Status.Path, path)) ? "file"
            : git.Root is { } root && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path)) ? "repository"
            : "folder";
        return InRepositoryAsync(git, () => RunAsync(command, args, GitChangeScope(path, section, kind)));
    }

    /// <summary>A <c>gitChanges/context</c> command's arguments: the path, the section and the files shown (null: the whole folder).</summary>
    private static (string? Path, string? Section, IReadOnlyList<string>? Shown) ChangeArguments(IReadOnlyList<JsonElement> args)
    {
        var path = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
        var section = args.Count > 1 && args[1].ValueKind == JsonValueKind.String ? args[1].GetString() : null;
        IReadOnlyList<string>? shown = args.Count > 2 && args[2].ValueKind == JsonValueKind.Array
            ? [.. args[2].EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : null;
        return (path, section, shown);
    }

    /// <summary>
    /// The files a command acts on: the section's files under the path, only those of <c>shown</c> when it's given
    /// (question 2); and how many more under it a filter hides.
    /// </summary>
    private (IReadOnlyList<GitFileRow> Files, int Hidden) ChangeTargets(string path, string section, IReadOnlyList<string>? shown)
    {
        var all = GitChanges.RowsUnder(path, section, shownOnly: false);
        if (shown is null)
        {
            return (all, 0);
        }

        var set = shown.ToHashSet(Codalus.Workbench.Projects.SolutionService.PathComparer);
        var files = all.Where(r => set.Contains(r.Status.Path)).ToList();
        return (files, all.Count - files.Count);
    }

    /// <summary>Stage on a node (gitChanges/context): its files in Changes, or in Merge Conflicts (which marks them resolved).</summary>
    public Task StageChangesAsync(IReadOnlyList<JsonElement> args) => GitActionAsync(async () =>
    {
        if (ChangeArguments(args) is not (string path, string section, var shown) || ChangeTargets(path, section, shown).Files is not { Count: > 0 } files)
        {
            return;
        }

        await Git.ServiceFor(path).StageAsync([.. files.Select(f => f.Status.Path)]).ConfigureAwait(true);
    });

    /// <summary>Unstage on a node of Staged Changes.</summary>
    public Task UnstageChangesAsync(IReadOnlyList<JsonElement> args) => GitActionAsync(async () =>
    {
        if (ChangeArguments(args) is not (string path, string section, var shown) || ChangeTargets(path, section, shown).Files is not { Count: > 0 } files)
        {
            return;
        }

        await Git.ServiceFor(path).UnstageAsync([.. files.Select(f => f.Status.Path)]).ConfigureAwait(true);
    });

    /// <summary>
    /// Discard Changes… on a node of Changes (P1, P2): it always asks, Cancel the default at the trailing edge and Discard a
    /// plain button (question 1); the dialog lists exactly the files it acts on (the first ten, then how many more), says
    /// how many untracked files it deletes and how many a filter hides.
    /// </summary>
    public Task DiscardChangesAsync(IReadOnlyList<JsonElement> args) => GitActionAsync(async () =>
    {
        if (ChangeArguments(args) is not (string path, string section, var shown))
        {
            return;
        }

        var (files, hidden) = ChangeTargets(path, section, shown);
        if (files.Count == 0)
        {
            return;
        }

        var discard = Localizer.Get("msg.gitChanges.discard", "Discard");
        var cancel = Localizer.Get("msg.gitChanges.cancel", "Cancel");
        var single = files.Count == 1 && hidden == 0 && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(files[0].Status.Path, path);
        var untracked = files.Count(f => f.Status.IsUntracked);
        var notes = new List<DetailLine>();
        if (!single && untracked > 0)
        {
            notes.Add(new DetailLine(untracked == 1
                ? Localizer.Get("msg.gitChanges.oneUntrackedFileWillBeDeleted", "1 untracked file will be deleted.")
                : Localizer.Format("msg.gitChanges.untrackedFilesWillBeDeleted", "{0} untracked files will be deleted.", untracked), "note"));
        }

        if (hidden > 0)
        {
            notes.Add(new DetailLine(hidden == 1
                ? Localizer.Get("msg.gitChanges.oneMoreFileIsHidden", "1 more file under this folder is hidden by the filter and won't change.")
                : Localizer.Format("msg.gitChanges.moreFilesAreHidden", "{0} more files under this folder are hidden by the filter and won't change.", hidden), "note"));
        }

        var name = ChangeDisplayName(path);
        var request = single
            ? new DialogRequest(
                Localizer.Format("msg.gitChanges.discardChangesTo", "Discard changes to {0}?", name),
                files[0].Status.IsUntracked
                    ? Localizer.Format("msg.gitChanges.notTrackedWillBeDeleted", "{0} is not tracked by git: it will be deleted.", name)
                    : Localizer.Format("msg.gitChanges.unstagedChangesToWillBeLost", "The changes to {0} that are not staged will be lost. This can't be undone.", name),
                [discard, cancel])
            : new DialogRequest(
                Localizer.Format("msg.gitChanges.discardChangesIn", "Discard changes in {0}?", name),
                Localizer.Format("msg.gitChanges.unstagedChangesToTheseFilesWillBeLost", "The unstaged changes to these {0} files will be lost. This can't be undone.", files.Count),
                [discard, cancel])
            {
                Details = [.. notes, .. FileLines(files)],
            };
        var choice = await Dialogs.ShowAsync(request with
        {
            DefaultButton = cancel,
            Icon = "discard",
            IsDanger = true,
            Context = ChangeContext(section),
        }).ConfigureAwait(true);
        if (choice.Button == discard)
        {
            await Git.ServiceFor(path).DiscardAsync([.. files.Select(f => f.Status.Path)]).ConfigureAwait(true);
        }
    });

    /// <summary>
    /// Stash Changes… on a node (P3): only its files, staged and unstaged, with a message; Include untracked files starts
    /// checked, since a folder action means everything under it. With no arguments, the window's Stash All.
    /// </summary>
    public Task StashChangesAsync(IReadOnlyList<JsonElement> args)
    {
        if (ChangeArguments(args) is not (string path, string section, var shown))
        {
            return StashAsync();
        }

        return GitActionAsync(async () =>
        {
            var (files, hidden) = ChangeTargets(path, section, shown);
            if (files.Count == 0)
            {
                return;
            }

            var stash = Localizer.Get("msg.gitChanges.stash", "Stash");
            var untracked = files.Count(f => f.Status.IsUntracked);
            var name = ChangeDisplayName(path);
            var only = untracked == 0
                ? Localizer.Format("msg.gitChanges.onlyTheseFilesAreStashed", "Only these {0} files under {1} are stashed; changes elsewhere stay.", files.Count, name)
                : Localizer.Format("msg.gitChanges.onlyTheseFilesAreStashedUntracked", "Only these {0} files under {1} are stashed, {2} of them untracked; changes elsewhere stay.", files.Count, name, untracked);
            List<DetailLine> notes = [new(only, "note")];
            if (hidden > 0)
            {
                notes.Add(new DetailLine(Localizer.Format("msg.gitChanges.moreFilesAreHiddenNotStashed", "{0} more files under this folder are hidden by the filter and aren't stashed.", hidden), "note"));
            }

            var answer = await Dialogs.ShowAsync(new DialogRequest(
                Localizer.Format("msg.gitChanges.stashChangesIn", "Stash Changes in {0}", name),
                Localizer.Get("msg.gitChanges.aMessageForTheStash", "A message for the stash (optional):"),
                [stash, Localizer.Get("msg.gitChanges.cancel", "Cancel")])
            {
                Input = string.Empty,
                Icon = "stash",
                Context = ChangeContext(section),
                Option = new DialogOption("untracked", Localizer.Get("msg.gitChanges.includeUntrackedFiles", "Include untracked files"), true),
                Details = [.. notes, .. FileLines(files)],
            }).ConfigureAwait(true);
            if (answer.Button == stash)
            {
                var includeUntracked = answer.Checked.Contains("untracked");
                var paths = files.Where(f => includeUntracked || !f.Status.IsUntracked).Select(f => f.Status.Path).ToList();
                if (paths.Count > 0)
                {
                    await Git.ServiceFor(path).StashAsync(answer.Input?.Trim(), includeUntracked, paths).ConfigureAwait(true);
                    StatusBar.ShowMessage(Localizer.Get("msg.gitDepth.changesStashed", "Changes stashed."), TimeSpan.FromSeconds(4));
                }
            }
        });
    }

    /// <summary>The dialog's list of files: a header, the first ten with their letters, then how many more.</summary>
    private static IEnumerable<DetailLine> FileLines(IReadOnlyList<GitFileRow> files)
    {
        yield return new DetailLine(Localizer.Get("msg.gitChanges.files", "Files"), "header");
        foreach (var file in files.Take(10))
        {
            yield return new DetailLine($"{file.Letter}  {file.Status.RelativePath.Replace('\\', '/')}");
        }

        if (files.Count > 10)
        {
            yield return new DetailLine(Localizer.Format("msg.gitChanges.andMore", "and {0} more", files.Count - 10));
        }
    }

    /// <summary>"C:\src\BuildAndRun · Changes", "· filtered by “Greeter”" while a filter is on.</summary>
    private string ChangeContext(string section)
    {
        var name = section switch
        {
            "staged" => Localizer.Get("msg.gitChanges.stagedChanges", "Staged Changes"),
            "conflicts" => Localizer.Get("msg.gitChanges.mergeConflicts", "Merge Conflicts"),
            _ => Localizer.Get("msg.gitChanges.changes", "Changes"),
        };
        var context = $"{Repo.Root} · {name}";
        return GitChanges.IsFiltered
            ? context + " · " + Localizer.Format("msg.gitChanges.filteredBy", "filtered by “{0}”", GitChanges.FilterText.Trim())
            : context;
    }

    /// <summary>A node's name in a title: the repository's folder, a folder's path from it, or a file's name.</summary>
    private string ChangeDisplayName(string path)
    {
        if (Repo.Root is not { } root || File.Exists(path))
        {
            return Path.GetFileName(path);
        }

        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? root : relative;
    }

    /// <summary>Open in Merge Editor on a conflicted file.</summary>
    public Task OpenMergeEditorCommandAsync(IReadOnlyList<JsonElement> args) =>
        ChangeArguments(args).Path is { } path ? OpenMergeEditorAsync(path) : Task.CompletedTask;

    /// <summary>Compare with Unmodified: a node's file (its staged or unstaged changes, by its section), else the active file's.</summary>
    public Task ShowDiffCommandAsync(IReadOnlyList<JsonElement> args) =>
        ChangeArguments(args) is (string path, var section, _)
            ? GitActionAsync(() => OpenDiffAsync(path, section == "staged"))
            : ShowActiveFileDiffAsync();

    /// <summary>
    /// View History: of the repository (none, or its folder: the Git Repository document on its current branch), of a
    /// branch or tag (its full ref: the Git Repository document with it selected), or of a file or folder (a File History
    /// tab). The menus' entries pass one, so neither shows git.history's own keys (question 13).
    /// </summary>
    public Task HistoryCommandAsync(IReadOnlyList<JsonElement> args)
    {
        var target = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
        if (target is null)
        {
            return ShowGitRepositoryAsync(Repo);
        }

        if (target.StartsWith("refs/", StringComparison.Ordinal))
        {
            return ShowGitRepositoryAsync(Repo, target);
        }

        var git = Git.ServiceFor(target);
        return git.Root is { } root && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(target))
            ? ShowGitRepositoryAsync(git)
            : ShowHistoryAsync(new GitLogQuery { Path = target }, Localizer.Format("msg.gitChanges.historyOf", "History: {0}", Path.GetFileName(Path.TrimEndingDirectorySeparator(target))));
    }

    /// <summary>Add to .gitignore: the node's path (a folder of untracked files, T5), else the Solution Explorer's selection.</summary>
    public Task AddToGitignoreCommandAsync(IReadOnlyList<JsonElement> args) =>
        ChangeArguments(args).Path is { } path ? GitActionAsync(() => Git.ServiceFor(path).IgnoreAsync(path)) : AddToGitignoreAsync();

    /// <summary>Reveal in File Explorer, Finder or File Manager: the path given, else the Solution Explorer's selection.</summary>
    public void RevealCommand(IReadOnlyList<JsonElement> args)
    {
        if (ChangeArguments(args).Path is not { } path)
        {
            Explorer.RevealInFileManager();
            return;
        }

        try
        {
            RevealPath(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.solutionExplorerViewModel.couldNotOpenTheFileManager", "Could not open the file manager: {0}", ex.Message));
        }
    }

    /// <summary>Copy Path: the path given, else the Solution Explorer's selection.</summary>
    public async Task CopyPathCommandAsync(IReadOnlyList<JsonElement> args)
    {
        if (ChangeArguments(args).Path is not { } path)
        {
            await Explorer.CopyPathAsync().ConfigureAwait(true);
            return;
        }

        await CopyTextAsync(path).ConfigureAwait(true);
    }

    /// <summary>Copy Relative Path: from the repository's folder (with '/'), else from the solution's folder.</summary>
    public async Task CopyRelativePathCommandAsync(IReadOnlyList<JsonElement> args)
    {
        var path = ChangeArguments(args).Path ?? Explorer.Selected?.Node.Path;
        if (path is null)
        {
            return;
        }

        var root = Git.RepositoryFor(path) ?? Git.Root ?? Solutions.Solution?.Directory;
        await CopyTextAsync(root is null ? path : Path.GetRelativePath(root, path).Replace('\\', '/')).ConfigureAwait(true);
    }

    /// <summary>Expand All and Collapse All on a folder or the repository node.</summary>
    public void ExpandUnderCommand(IReadOnlyList<JsonElement> args, bool expanded)
    {
        if (ChangeArguments(args) is (string path, string section, _))
        {
            GitChanges.SetExpandedUnder(section, path, expanded);
        }
    }

    /// <summary>A ref's name as the menus show it: <c>main</c>, <c>origin/main</c>.</summary>
    private static string ShortRefName(string fullRef) =>
        fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullRef["refs/heads/".Length..]
        : fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal) ? fullRef["refs/remotes/".Length..]
        : fullRef;
}
