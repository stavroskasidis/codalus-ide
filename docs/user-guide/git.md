# Git

When the open solution is in a git repository, Codalus IDE shows its state and lets you work with it. Git operations run the `git` you have installed, so your configuration, credential helper, SSH keys, hooks and commit signing all apply.

## The Git Changes window

Git → Git Changes (`Ctrl+0 Ctrl+G`).

- **At the top**: the branch (click to switch or create one), then **Fetch**, **Pull**, **Push**, **Sync** (pull, then push) and **⋯** (new branch, merge, rebase, stash, history, remotes, tags, refresh).
- **Outgoing / incoming**: the commits to push and to pull (⇅ 2 / 1); click it, or **View all commits**, for the history. A branch with no upstream offers **Publish branch**. The pull request button creates a pull request on GitHub, Azure DevOps or GitLab.
- **Commit**: a message is required. The button commits the staged changes when there are some (**Commit Staged**), otherwise every change, new files included (**Commit All**); its menu also commits **and Push** or **and Sync**. `Ctrl+Enter` in the message box commits. **Amend** fills in the last commit's message and rewrites that commit. The **#** button (or typing `#`) mentions an issue or work item.
- **Related Items**: the issues and work items the message mentions.
- **Staged Changes** and **Changes**: a tree of the repository, its folders (a folder holding a single folder is shown with it, as `src\Data`) and files, each with its status letter. Hover a file or folder for the buttons that stage (`+`) and unstage (`−`) it, and a file for discard; right-click for the same and to open the file or its diff. **Stage All** (`+`) and **Unstage All** (`−`) act on the whole section. Double-click a file to see its diff.
- **The Changes menu (⋯)**: **Open File as Default Action** (double-click opens the file instead of its diff), **Switch to List View** (Name and Path columns; click a column to sort) or back to the tree, and **Show Filter** (only the files whose path contains the text).
- **Stashes**: double-click one to apply it; right-click to apply, pop or drop it. **Drop All** drops every stash.
- **An operation in progress** (a merge, rebase, cherry-pick or revert) shows a banner with **Continue**, **Skip** (to leave out the commit that stopped it) and **Abort**.
- **Merge Conflicts**: double-click one to open it in the merge editor.

## Resolving conflicts

The merge editor opens as a tab named after the file (with `*` once you've changed something). It shows the two versions side by side, **Incoming** (the branch you're merging, e.g. `Incoming: master`) on the left and **Current** (your branch, e.g. `Current: develop`) on the right, and the **Result** below them. Each conflict is a block highlighted in muted red with a dashed outline in all three panes. Until you resolve it, the result shows the merge base's text for that block (what the file had before either side changed it) instead of git's `<<<<<<<` markers.
- **Check the box beside a block** to take that side's block into the result. Check both boxes to take both: the current block first, then the incoming one, in whichever order you check them. Uncheck a box to take its block out again; with neither checked, the merge base's text comes back and the conflict counts as unresolved.
- **The box in a pane's header** takes every conflict from that side (it shows a dash when only some do); uncheck it to take them all out.
- **Edit the result** directly as well: typing in a block resolves that conflict with what you typed. Undo (`Ctrl+Z`) takes back edits and check boxes alike.
- **The toolbar**: the up and down arrows (`Shift+F8`, `F8`) move to the previous and next conflict, followed by the count, e.g. **1 Conflict (1 Remaining)**. **Accept Merge** (once none remain) saves the file and stages it. When every conflicted file is resolved, **Continue** in Git Changes finishes the merge, rebase, cherry-pick or revert.
- **The gear menu**: the layout (**Versions Above the Result**, or **Three Columns** with the result in the middle), **Incoming on the Left** (uncheck it to put your version on the left), and **Take All Incoming**, **Take All Current** and **Reset All Conflicts**. The layout is remembered (`git.mergeEditor.layout`, `git.mergeEditor.incomingOnLeft`).
- **Scrolling**: the three panes scroll together, up and down and sideways, keeping each conflict's blocks level with each other. `Ctrl` + mouse wheel zooms all three.
- **The status bar** under the panes shows the zoom, the caret's line and character, the indentation (`SPC` or `TABS`), the line endings (`CRLF` or `LF`) and the encoding. Accept Merge keeps the file's encoding.
- **From the Command Palette**: Next Conflict, Previous Conflict, Take Incoming, Take Current and Take Both (for the conflict at the result's caret), Take All Incoming, Take All Current and Accept Merge.

**Using Codalus IDE as git's merge tool and editor:**

```
git config --global merge.tool codalus
git config --global mergetool.codalus.cmd "codalus merge \"$LOCAL\" \"$REMOTE\" \"$BASE\" \"$MERGED\""
git config --global mergetool.codalus.trustExitCode true
git config --global core.editor "codalus --wait"
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

- **Stash Changes** (with untracked files if you like) and **Stashes** to apply, pop or drop one; **Drop All Stashes** clears them. Git Changes lists the stashes too.
- **Rebase Current Branch** onto another branch.
- **Interactive Rebase**: from Git → Interactive Rebase or a commit in the history. The commits after the base are listed oldest first; reorder them and choose pick, reword (with a new message), squash, fixup or drop for each. No text editor opens.
- **Tags**: create (annotated with a message, or lightweight), push, delete locally or on origin.
- **Remotes**: add, rename, change the URL, remove.
- **Submodules**: Git → Update Submodules initializes and updates them.

## Repositories

- **Clone Repository** (Git menu): a URL and a destination folder. If the repository has one solution at its root, it opens.
- **Create Git Repository**: for an open solution that isn't in one, with a .NET `.gitignore`.
- **Add to .gitignore**: from the Solution Explorer's context menu.
- **Publish to a Git Host** (Git menu): creates a repository on GitHub, Azure DevOps or GitLab (you choose the owner: you, an organization or group, or an Azure DevOps project; the name; private or public; a description), adds it as `origin` (or another free name), and pushes the branch. A solution that isn't in a repository yet gets one, with a first commit.
- **Several repositories**: when the solution's projects are in more than one repository (a project in a repository of its own, a submodule), Git Changes shows a **repository picker**, and Git → Switch Repository changes which one Git Changes, the history and the status bar show. Change markers and blame always use each file's own repository.

## Worktrees

Git → Worktrees lists the repository's worktrees: their folder, branch, and whether one is the main worktree, the one open here, locked, or gone.
- **Add** a worktree for a new branch or an existing one, in a folder next to the repository.
- **Open** opens its solution in this window; **Open in New Window** starts another Codalus IDE on it.
- **Lock** (with a reason) and **Unlock**; **Remove** (a worktree with changes asks again before forcing); **Prune** forgets worktrees whose folders are gone.

## Git LFS

Codalus IDE reads which files Git LFS tracks from `.gitattributes`, so it works whether or not git-lfs is installed.
- In Git Changes, LFS files are marked **LFS**; a file whose content wasn't downloaded (only its pointer is in the working tree) is marked **LFS pointer**, and a locked file says who holds the lock.
- With git-lfs installed: **Git LFS Locks** lists the locks (pick one to unlock it, or force-unlock someone else's), **Git LFS: Lock File** and **Unlock File** act on the active file or the Solution Explorer's selection, and **Git LFS: Download Files** runs `git lfs pull`.

## GitHub, Azure DevOps and GitLab

Built-in plugins connect to GitHub (and GitHub Enterprise Server), Azure DevOps and GitLab (and self-managed GitLab). **Nothing is sent to a host until you sign in to it**: before that, the plugins don't even load.

**Signing in** (Git → Git Hosting Accounts):
- **Sign In** shows a code: **Copy Code and Open Browser**, enter it on the host's page and approve. The page shows *Waiting for approval…* until you do; **Cancel** stops.
- **Or paste a personal access token**: GitHub needs the `repo`, `read:org` and `workflow` scopes; GitLab the `api` scope; Azure DevOps Code (read and write), Build (read) and Work Items (read).
- Tokens are kept in the operating system's credential store: Windows Credential Manager, the macOS Keychain, or the Secret Service (GNOME Keyring, KWallet…) through libsecret on Linux. Codalus IDE never writes them to a file. Where no credential store is available, a sign-in lasts until Codalus IDE closes; the environment variable `CODALUS_CREDENTIAL_STORE=memory` does that on purpose (tests). A `--user-data-dir` profile keeps its sign-ins apart from the default profile's.
- **Servers**: `github.serverUrl`, `gitlab.serverUrl`, and `azureDevOps.serverUrl` (with `azureDevOps.authorityUrl` and `azureDevOps.profileUrl`) point at an enterprise or self-managed server. Device sign-in needs the server's OAuth app: its client id goes in `github.oauthClientId`, `gitlab.oauthClientId` or `azureDevOps.oauthClientId`. Without one, sign in with a token.
- **Sign Out** deletes the token. A session that expires or is revoked asks you to sign in again.

The repository's host is found from its remotes (`origin` first). Then:
- **Pull Requests** (Git → Pull Requests): the open pull requests (merge requests on GitLab), all of them, yours, or those that ask for your review. Select one for its description, its review comments (with the file and line; click to open it) and its conversation. **Check Out** fetches its branch (a fork's pull request too) and switches to it. **Open in Browser** opens it on the host.
- **Review comments in the editor**: when the current branch has an open pull request, its unresolved review comments show at the end of their lines (◆ who: what). Click one for the whole thread. Settings: `hosting.reviewComments`, `hosting.showResolvedComments`.
- **Create Pull Request** (Git menu, or the Pull Requests window's toolbar): a title (from the branch name), a description in Markdown, the branch to merge into (the default branch first), draft. The branch is pushed first if it needs to be.
- **CI status**: the current branch's checks (GitHub check runs and statuses, Azure Pipelines builds, GitLab's latest pipeline) in the status bar and Git Changes; click for the list, and pick one to open it. It's read again when you switch branches, push, and every `hosting.statusRefreshSeconds` (120). `hosting.ciStatus` turns it off.
- **Work Items** (Git → Work Items): the issues or work items assigned to you, or found by number or words. **Mention in Commit Message** adds its reference to the commit message.
- **`#` in the commit message** completes issues and work items: type `#` and a number or words, then pick one (Up, Down, Enter).

`hosting.enabled` turns all of this off: no call is made to any host.

## Credentials and SSH passphrases

Git asks your credential helper first (Git Credential Manager, the macOS Keychain helper, …). When none answers, or an SSH key needs its passphrase, or ssh asks whether to trust a new host, Codalus IDE asks in a dialog (passwords and passphrases are masked). The answer goes to git once and isn't stored; your credential helper may store it, as it would in a terminal.

## Diffs

A file's diff opens in a document, laid out like the classic IDE's:
- **The toolbar**: **Next Change** and **Previous Change** (`F8` and `Shift+F8` while the diff has the focus), the count ("Change 3 of 4"; a change is a run of changed lines) and the number of lines removed and added. The gear menu (**Diff Options**) switches between **Side by Side** and **Inline** (remembered in the `git.diff.layout` setting), refreshes the diff, and opens the file at the caret's line.
- **Side by side**, the two versions are aligned and scroll together; drag the splitter between them to resize them, or double-click it for half and half. **Inline**, each change shows its removed lines, then its added lines, with two columns of line numbers.
- **Each side** has a header with its file and what it is ("(Index)", "(Working tree)", "(HEAD)", a revision), line numbers that count its own file's lines, its own caret and current line, and a status bar with the caret's line and character, the indentation, the line endings and the encoding. The zoom box in the left one (or `Ctrl+wheel`) sizes both sides. The side that is the open document has the editor's navigation bar.
- **In the editor's colors**: each side is colored by its language's grammar, and the side whose text is the open document's (usually the working tree) by the language service too, as the editor colors it.
- **Changed words**: where a line was changed, the words that changed are marked on both sides, with a deeper fill and a thin border. Rows where only the other side has lines are hatched.
- **Staging**: a change is a run of changed lines, as drawn. The code's context menu has **Stage Change** and **Discard Change…** for the change at the caret, and **Stage Selected Lines** and **Discard Selected Lines…** for just the changed lines among the selected ones (or the caret's line); in a staged diff, **Unstage Change** and **Unstage Selected Lines**. The toolbar has **Stage All** and **Discard All…** (**Unstage All** in a staged diff). All of them are in the Command Palette too; none has a default key.
- **Show Staging Controls** (on the toolbar and in the gear menu; the `git.diff.showStagingControls` setting, off by default) adds a column between the line numbers and the signs with each change's **Stage** and **Discard** buttons (**Unstage** in a staged diff), at the change's first line in view. With a selection inside the change, they act on the selected lines. Hovering a button outlines the lines it acts on.
- **The working-tree side is the file's open buffer**, unsaved edits included (its header says so). Stage, Discard, Stage All and Discard All… save the file first, then check that the saved text is the text drawn: if saving changed it (format on save, for example), nothing is applied and you're told, so you can check the diff and try again.
- **Summary** (on the toolbar and in the gear menu, off by default) folds every unchanged run to three lines of context, on both sides in step. Click a folded run, or press `Enter` on it, to open it.
- **When there are no lines to compare**, the diff says why: the file has no changes; the working tree matches the index but the file has staged changes (**Open Staged Changes**); or only the line endings, the encoding, the file mode or the last newline differ, which Stage All still records. A binary file shows each side's size. A file over 10 MB or 100,000 lines waits for **Compare Anyway**.
- **Discarding asks first, every time**, naming the lines, with Cancel the default, and says when unsaved edits are saved first (or, for Discard All…, lost).

The diff refreshes by itself after edits, saves and changes to the index, and keeps its caret, scroll position and splitter when you switch tabs. Git → Compare with Unmodified opens the active file's diff.

## In the editor

- **Change markers** in the gutter, as you type, against the staged version: green for added lines, blue for modified lines, a red wedge for deleted lines.
- **Blame** (Git → Toggle Blame, `Ctrl+Shift+Alt+B`; `⌘⌥B` on macOS): the commit, author, date and summary of each block of lines. Lines you haven't committed say so.

## Branches and remotes

- **The status bar's branch item** (or Git → Switch Branch) lists local and remote branches, and offers to create a new one. Switching to a remote branch creates a local branch that tracks it.
- **Git → Merge Branch** merges another branch into the current one.
- **Git → Fetch, Pull, Push and Sync** (pull, then push). The first push of a new branch publishes it to `origin` (or your only remote) and sets its upstream.
- **Credentials** come from your git credential helper (Git Credential Manager, the macOS keychain, …), or from you, in a dialog, when no helper answers ([above](#credentials-and-ssh-passphrases)).

Errors from git are shown as notifications. The full log of git commands is in View → Output → Git.
