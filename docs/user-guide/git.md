# Git

When the open solution is in a git repository, Daedalus Studio shows its state and lets you work with it. Git operations run the `git` you have installed, so your configuration, credential helper, SSH keys, hooks and commit signing all apply.

## The Git Changes window

Git → Git Changes (`Ctrl+0 Ctrl+G`).

- **At the top**: the branch, with commits ahead ↑ and behind ↓ its upstream; Fetch, Pull and Push.
- **Commit**: write a message, then **Commit Staged**, or **Commit All** (every change to tracked files). `Ctrl+Enter` in the message box commits. **Amend** fills in the last commit's message and rewrites that commit.
- **Staged Changes** and **Changes**: each file has buttons to stage (`+`), unstage (`−`) and discard. **Stage All** and **Unstage All** act on the whole list. Double-click a file to see its diff.
- **Stash**, **Stashes** and **History** buttons under the commit box.
- **An operation in progress** (a merge, rebase, cherry-pick or revert) shows a banner with **Continue**, **Skip** (to leave out the commit that stopped it) and **Abort**.
- **Merge Conflicts**: double-click one to open it in the merge editor.

## Resolving conflicts

The merge editor shows the current version (yours) and the incoming one side by side, and the result below it, which starts as the file with git's conflict markers.
- **Accept Current**, **Accept Incoming** or **Accept Both** for the conflict at the caret, or **Accept All Current** / **Accept All Incoming**. You can also edit the result directly.
- **Previous Conflict** and **Next Conflict** move between them; the toolbar says how many are left.
- **Accept Merge** (once none are left) saves the file and stages it. When every conflict is resolved, **Continue** in Git Changes finishes the merge, rebase, cherry-pick or revert.

**Using Daedalus Studio as git's merge tool and editor:**

```
git config --global merge.tool daedalus
git config --global mergetool.daedalus.cmd "daedalus merge \"$LOCAL\" \"$REMOTE\" \"$BASE\" \"$MERGED\""
git config --global mergetool.daedalus.trustExitCode true
git config --global core.editor "daedalus --wait"
```

`git mergetool` then opens each conflict in the merge editor of your running window, and the merge counts when you Accept Merge. Commit messages and interactive rebases from the command line open as tabs, and git continues when you close them.

## History

Git → View History (`Ctrl+0 Ctrl+H`) shows the commits of every branch as a graph, with branch and tag names.
- **Filters**: a branch, an author, words in the message.
- **Select a commit** to see its message and the files it changed; double-click a file to compare it with the commit's parent.
- **On a commit** (right-click): Checkout, New Branch Here, Create Tag, Cherry-Pick, Revert, Reset (keeping changes staged, keeping changes, or discarding them, after a confirmation), Interactive Rebase from Here, Copy Commit Hash.
- **File history**: Git → View File History, or the Solution Explorer's context menu, for the active file or a folder. Renames are followed.
- **Line history**: select lines and choose View Line History (editor context menu): only the commits that changed them.
- **Show Commit for Line**: the commit that last changed the caret's line, from blame.
- **Compare with Branch or Commit**: the active file against its version on a branch, a tag, or any commit.

## Stash, rebase, tags and remotes

- **Stash Changes** (with untracked files if you like) and **Stashes** to apply, pop or drop one.
- **Rebase Current Branch** onto another branch.
- **Interactive Rebase**: from Git → Interactive Rebase or a commit in the history. The commits after the base are listed oldest first; reorder them and choose pick, reword (with a new message), squash, fixup or drop for each. No text editor opens.
- **Tags**: create (annotated with a message, or lightweight), push, delete locally or on origin.
- **Remotes**: add, rename, change the URL, remove.
- **Submodules**: Git → Update Submodules initializes and updates them.

## Repositories

- **Clone Repository** (Git menu): a URL and a destination folder. If the repository has one solution at its root, it opens.
- **Create Git Repository**: for an open solution that isn't in one, with a .NET `.gitignore`.
- **Add to .gitignore**: from the Solution Explorer's context menu.

## Diffs

A file's diff opens in a document:
- **Side by side**, with the two versions aligned and scrolling together, or **inline**.
- **Previous Change** and **Next Change** move between changes.
- **Stage Hunk**, **Unstage Hunk** and **Discard Hunk** act on the change at the caret.
- **Stage Selected Lines**, **Unstage Selected Lines** and **Discard Selected Lines** act on just the selected lines (or the caret's line) of a change.

Git → Compare with Unmodified opens the active file's diff.

## In the editor

- **Change markers** in the gutter, as you type, against the staged version: green for added lines, blue for modified lines, a red wedge for deleted lines.
- **Blame** (Git → Toggle Blame, `Ctrl+Alt+B`): the commit, author, date and summary of each block of lines. Lines you haven't committed say so.

## Branches and remotes

- **The status bar's branch item** (or Git → Switch Branch) lists local and remote branches, and offers to create a new one. Switching to a remote branch creates a local branch that tracks it.
- **Git → Merge Branch** merges another branch into the current one.
- **Git → Fetch, Pull and Push**. The first push of a new branch publishes it to `origin` (or your only remote) and sets its upstream.
- **Credentials** come from your git credential helper (Git Credential Manager, the macOS keychain, …). Daedalus Studio doesn't prompt in a terminal, so if no helper is set up, the operation fails with git's message.

Errors from git are shown as notifications. The full log of git commands is in View → Output → Git.
