# Git

When the open solution is in a git repository, Codalus IDE shows its state and lets you work with it. Git operations run the `git` you have installed, so your configuration, credential helper, SSH keys, hooks and commit signing all apply.

## The Git Changes window

Git → Git Changes (`Ctrl+0 Ctrl+G`).

- **At the top**: the [branch drop-down](#the-branch-drop-down), then **Fetch**, **Pull**, **Push**, **Sync** (pull, then push) and **⋯** (new branch, merge, rebase, update a branch, stash, history, [manage branches](#managing-branches), remotes, tags, refresh).
- **While Fetch, Pull, Push or Sync runs** (from the window, the Git menu or the palette): the window says what it's doing under the branch ("Pulling…") over a running bar, and **Cancel** takes the place of the four buttons; the status bar shows it too, with its own cancel button. Cancel stops git, and the status bar says it was cancelled.
- **Outgoing / incoming**: the commits to push and to pull (⇅ 2 / 1); click it, or **View all commits**, for the history. A branch with no upstream offers **Publish branch**; a repository with no remote offers **Publish to a Git Host…** instead (its tooltip says where to add a remote), and neither shows with a detached HEAD or before the first commit. The pull request button creates a pull request on GitHub, Azure DevOps (or an Azure DevOps Server) or GitLab; before you're signed in to the repository's host, it opens [the sign-in in Git Changes](#signing-in-from-git-changes) first, and then the form.
- **Commit**: a message is required. The button commits the staged changes when there are some (**Commit Staged**), otherwise every change, new files included (**Commit All**); its menu also commits **and Push** or **and Sync**. `Ctrl+Enter` in the message box commits. **Amend** fills in the last commit's message and rewrites that commit. The **#** button (or typing `#`) mentions an issue or work item.
- **Related Items**: the issues and work items the message mentions.
- **Staged Changes** and **Changes**: a tree of the repository, its folders (a folder holding a single folder is shown with it, as `src\Data`) and files, each with its status letter. Hover a file or folder for the buttons that stage (`+`), unstage (`−`) and discard it. **Stage All** (`+`) and **Unstage All** (`−`) act on the whole section. Double-click a file to see its diff.
- **Right-click a folder, a file or the repository** (or press `Shift+F10` or the Menu key) for its menu, as in the classic IDE: **Open in Merge Editor** (a conflict), **Compare with Unmodified** and **Open** (a file), **Stage** or **Unstage**, **Discard Changes…**, **Stash Changes…**, **Add to .gitignore** (a folder or file whose files are all new), **View History**, **Reveal in File Explorer** (Finder, File Manager), **Open in Terminal**, **Copy Path**, **Copy Relative Path**, and on a folder **Expand All** and **Collapse All**. Entries that don't apply aren't shown: a folder that's gone has no Reveal, a staged folder no Discard, a conflict no Stash while the merge is in progress. A folder's commands act on the files the window shows under it: with a filter on, only those, and the folder says "2 of 5 files" and the section header "Changes (2 of 10)". **Discard Changes…** always asks, lists the files it acts on, says how many new files it deletes and how many the filter hides, and **Cancel** is the default, so `Enter` never discards. **Stash Changes…** stashes only that folder's files (new files included unless you uncheck it); the rest stays. Plugins can add to this menu (`gitChanges/context`).
- **The Changes menu (⋯)**: **Open File as Default Action** (double-click opens the file instead of its diff), **Switch to List View** (Name and Path columns; click a column to sort) or back to the tree, and **Show Filter** (only the files whose path contains the text).
- **Stashes**: double-click one to apply it; right-click to apply, pop or drop it. **Drop All** drops every stash.
- **An operation in progress** (a merge, rebase, cherry-pick or revert) shows a banner with **Continue**, **Skip** (to leave out the commit that stopped it) and **Abort**.
- **Merge Conflicts**: double-click one to open it in the merge editor.

## The branch drop-down

The branch button at the top of the Git Changes window opens a list of the branches (**Manage Branches** at its bottom opens [the Git Repository document](#managing-branches)): **Local branches**, the current one first (with a check), then each remote's branches, without the remote's name. A chip on a local branch says how many commits it has to push and to pull (↑2 ↓1, as of the last fetch), and a **worktree** chip that it's checked out in another worktree. Type to filter; `Down` moves into the list.

- **Click a branch, or press `Enter`, to check it out**, as in the classic IDE. Checking out a remote branch switches to your local branch of that name if you have one (the entry says how far behind it is), or makes one that tracks it. A branch checked out in another worktree says where instead, with **Open in New Window**.
- **Right-click a branch, or click its ⋯** (or `Shift+F10`, the Menu key), for its menu, over the list: **Checkout**, **New Local Branch From…**, **Merge 'feature' into 'main'**, **Rebase 'main' onto 'feature'**, **Update from 'origin/feature'**, **Push**, **Rename…** (`F2`), **Delete** (`Delete`; **Delete from origin…** on a remote branch), **Compare with Current Branch**, **View History** and **Copy Branch Name**. Entries that don't apply aren't shown, and the list says why: during a merge or rebase it says which are hidden (a new branch is still made, without being checked out), and with a detached HEAD the titles name HEAD.
- **Update** brings the branch up to date from its upstream without checking it out ([below](#branches-and-remotes)). The list stays open, the row says **Updating…** while it runs, and its chip changes when it's done; a branch that has diverged, or one checked out in another worktree, is explained above the list with what to do.
- **New Local Branch From…** asks for the name, says what it's based on, and checks the new branch out unless you uncheck it. **New Branch…** at the bottom of the list starts one from the current branch; when nothing matches what you typed, it offers **New Branch 'name'…**.
- **Rename…** checks the name as you type, with git's rules and the names other branches have.
- **Delete** always asks, and **Cancel** is the default. When the branch has an upstream, you can also delete it on the server, for everyone; the box lists the commits only the server has. When the branch isn't merged (into its upstream, or into the current branch when the upstream goes too, as git decides), a second confirmation lists the commits that would be lost.
- **Compare with Current Branch** opens a document named "main → feature" with the files the two branches' latest commits differ in, as a tree or a list with their status letters; double-click one for its diff, the current branch on the left. When they have the same files, it says so and offers **View History**.

With all of a solution's repositories shown, the list has **Common branches** and each repository's branches instead ([Several repositories](#several-repositories)).

Plugins can add to a branch's menu (`gitChanges/branch`).

## Managing branches

Git → Manage Branches (`Ctrl+0 Ctrl+R`; also Git → Branches, the Git Changes window's **⋯** and the bottom of the branch drop-down) opens the **Git Repository** document, as the classic IDE's Git Repository window:
- **The tree, on the left**: **Branches / Tags** with the repository, "Codalus (main)", under it: the local branches, the current one in bold with how many commits it has to push and to pull (↑2 ↓1); a branch named with `/` sits in folders (`feature/sign-in` under **feature**); then each remote's branches (**remotes/origin**) and the **tags**, collapsed at first. **Pull Requests** lists the open pull requests of the host the remotes are on once you're signed in to it, in the Pull Requests window's filters as folders: **Mine**, **Review requested** and **All open**, each with its count (until then it offers to sign in, and nothing is sent); **Submodules** the repository's submodules and their state. Type in **Filter** to show only the branches, tags, pull requests and submodules whose names contain the text.
- **Select a branch or a tag** for its [history](#history) on the right. A local branch with an upstream shows **Incoming (n)** first, the commits it doesn't have yet, with **Fetch** and **Pull** (**Update** for a branch that isn't checked out), then **Local History (n Outgoing)** with **Push** and **Sync**; the chevron beside each collapses it. A pull request shows the commits of its branch that its target branch doesn't have, once its branch is fetched; a submodule shows its own history.
- **Double-click a branch, or press `Enter`, to check it out.** A remote branch switches to your local branch of its name, or makes one that tracks it. Double-clicking a pull request opens it in the browser.
- **Right-click a branch** (or `Shift+F10`, the Menu key) for the same menu as in the [branch drop-down](#the-branch-drop-down): checkout, new branch, merge, rebase, update, push, rename (`F2`), delete (`Delete`), compare, history, copy the name. A **tag** has Checkout (Detached), New Local Branch From…, Push, Delete (`Delete`), Delete from Remote…, View History and Copy Tag Name; deleting asks first. A **pull request** has Check Out, Open in Browser, Show in Pull Requests and Copy Link; a **submodule** Update Submodules, Open in New Window, reveal it, Open in Terminal and Copy Path; the **repository** New Branch…, New Tag…, Fetch, Manage Remotes… and Refresh.

The tree follows git: a branch you make, switch to, pull or delete anywhere (here, in Git Changes or in a terminal) shows at once. With [several repositories](#several-repositories) shown, each repository is a root of its own, with its Branches / Tags, Pull Requests and Submodules, and everything runs in that repository: Fetch, Pull, Push and Sync here act on that repository alone.

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

Git → View History (`Ctrl+0 Ctrl+H`), **View all commits** in Git Changes and a branch's **View History** open the [Git Repository](#managing-branches) document with the branch selected (the current one by default), its history beside the tree. The history has the classic IDE's columns: **Branch / Tag** (the current branch highlighted), **Graph**, **Message**, **Author**, **Date** and **ID**.
- **The toolbar**: **Refresh**; **Show All Branches**, the commits of every branch in one graph; whether the Branch / Tag column shows remote branches and tags. The toggles are remembered (`git.history.showAllBranches`, `git.history.showRemoteBranches`, `git.history.showTags`).
- **Filter History** shows the commits whose message or author contains the text, or the commit whose ID starts with it (`98aa44a1`), even one that isn't on the branch.
- **Double-click a commit, or press `Enter`** (or **View Commit Details** on its menu) to open it in a tab of its own, "Commit 98aa44a1": the diff of the selected file against the commit's parent on the left, and on the right its ID with **Revert**, **Reset** (keeping changes staged, keeping changes, or discarding them) and **⋯** (cherry-pick, checkout, new branch, tag, copy the ID), its message, its author and committer with their dates, and **Changes (n)**, the files it changed as a tree; select a file for its diff. With AI on, **Explain** asks AI Chat about the commit (its message and diff, the AI exclusions applied).
- **On a commit** (right-click): View Commit Details, Compare with Previous, Checkout, New Branch Here, Create Tag, Cherry-Pick, Revert, Reset (keeping changes staged, keeping changes, or discarding them, after a confirmation), Interactive Rebase from Here, Copy Commit Hash.
- **Compare commits**: `Ctrl`+click or `Shift`+click selects several commits; with two selected, **Compare Commits** on the menu opens a document named "1a2b3c4d → 5e6f7a8b" with the files they differ in, the older first, as a tree or a list with their status letters; double-click one for its diff, the older commit on the left. **Compare with Previous** does the same for one commit and its parent (the first, for a merge). With several commits selected, the commands that act on one commit are disabled.
- **File history**: Git → View File History, or the Solution Explorer's context menu, for the active file or a folder, in a tab of its own with the same columns, filter and commit details. Renames are followed.
- **Line history**: select lines and choose View Line History (editor context menu): only the commits that changed them.
- **Show Commit for Line**: the tab of the commit that last changed the caret's line, from blame.
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
- **Several repositories**: when the solution's projects are in more than one repository (a project in a repository of its own, a submodule), Git Changes shows all of them at once. See [Several repositories](#several-repositories).

## Several repositories

When the solution's projects are in more than one repository (a project in a repository of its own, a submodule, a clone beside the solution), Git Changes has a **repository picker** at the top. It starts on **All Repositories**, as the classic IDE does:
- **Changes** and **Staged Changes** have a tree for each repository, the solution's own first. Staging, discarding and a folder's or file's menu act in that file's repository.
- **Commit All Repos** commits with the one message in each repository that has changes: its staged changes when any repository has some (**Commit Staged**), otherwise every change. **Amend** belongs to one repository, so it's hidden here.
- **Fetch**, **Pull**, **Push**, **Sync**, **Stash All…** and **Drop All** run in each repository. Pull skips a branch with no upstream. Push skips a repository with nothing to push. If a repository fails, the rest still run, and one notification lists each failure with git's message (the Git output has them too). Nothing is undone in the repositories that succeeded.
- The branch button shows the branch every repository is on, or "3 branches", and its tooltip has each repository's. The outgoing and incoming counts are added up across repositories.
- The [branch drop-down](#the-branch-drop-down) lists **Common branches**, the branches every repository has, checked when every repository is on that branch. Under **All branches** it lists each repository's branches, under its name and current branch. Clicking a common branch checks it out in every repository. Its menu has **New Local Branch From…** (one name, a branch in each repository), **Merge**, **Rebase** and **Delete in Each Repository…**. A repository's own rows act on that repository only. **New Branch…** creates a branch in every repository.
- A merge, rebase, cherry-pick or revert in progress in any repository shows its banner, named for the repository. **Continue**, **Skip** and **Abort** act there.
- **View all commits** shows the open repository's current branch in the Git Repository document, where each repository is a root of its own. A file's history is its own repository's.
- **Write the Commit Message with AI** describes the staged changes of every repository.

Pick a repository in the picker (or Git → Switch Repository) to see that one alone, as with a single repository. The picker's filter box narrows the list. **Add Repository…** adds a repository that has no project in the solution (docs, build scripts). **Remove from the List** (the × beside a repository) leaves one out; the solution's own repository can't be removed. Your choice, and the repositories you added or removed, are kept with the solution.

Change markers and blame always use each file's own repository.

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

Built-in plugins connect to GitHub (and GitHub Enterprise Server), Azure DevOps (and [Azure DevOps Server](#azure-devops-server)) and GitLab (and self-managed GitLab). **Nothing is sent to a host until you sign in to it**: before that, the plugins don't even load.

### Signing in from Git Changes

When the repository's remote is on GitHub, Azure DevOps or GitLab (or a server of theirs) and you aren't signed in there, Git Changes says so under the outgoing and incoming commits: *This repository is on GitHub (github.com). Sign in to create pull requests and see CI status and work items.*
- **Sign In** opens that host's ways in, right there: **Sign in with your Windows account** on an [Azure DevOps Server](#azure-devops-server), **Sign In with Your Browser** where the host's OAuth app is set up, and a personal access token. **Create a token** opens the host's page for a new token (on GitHub, with the scopes Codalus IDE needs already ticked); paste it and **Sign In with Token**.
- **Create a pull request**, and **Sign in** in the Pull Requests and Work Items windows, open the same panel. Once you're signed in from the pull request button, the pull request form opens.
- **×** hides the suggestion and never shows it again for this repository. The pull request button still offers to sign in.
- A remote on a host Codalus IDE doesn't know (no provider for it) opens Git Hosting Accounts instead, where `github.serverUrl`, `gitlab.serverUrl` and `azureDevOps.serverUrl` name your servers.

### Git Hosting Accounts

**Signing in** (Git → Git Hosting Accounts, every host at once):
- **Sign In** shows a code: **Copy Code and Open Browser**, enter it on the host's page and approve. The page shows *Waiting for approval…* until you do; **Cancel** stops.
- **Or paste a personal access token**: GitHub needs the `repo`, `read:org` and `workflow` scopes; GitLab the `api` scope; Azure DevOps Code (read and write), Build (read) and Work Items (read).
- Tokens are kept in the operating system's credential store: Windows Credential Manager, the macOS Keychain, or the Secret Service (GNOME Keyring, KWallet…) through libsecret on Linux. Codalus IDE never writes them to a file. Where no credential store is available, a sign-in lasts until Codalus IDE closes; the environment variable `CODALUS_CREDENTIAL_STORE=memory` does that on purpose (tests). A `--user-data-dir` profile keeps its sign-ins apart from the default profile's.
- **Servers**: `github.serverUrl`, `gitlab.serverUrl`, and `azureDevOps.serverUrl` (with `azureDevOps.authorityUrl` and `azureDevOps.profileUrl`) point at an enterprise or self-managed server. Device sign-in needs the server's OAuth app. On github.com, gitlab.com and Azure DevOps Services (dev.azure.com) it's Codalus IDE's own (the defaults of `github.oauthClientId`, `gitlab.oauthClientId` and `azureDevOps.oauthClientId`), so **Sign In** works there out of the box. Azure DevOps' asks Microsoft for your work or school account; an organization that only lets its users approve apps from verified publishers asks its admin to approve Codalus IDE once. A GitHub Enterprise Server or a self-managed GitLab (17.2 or later) needs its own app's client id in `github.oauthClientId` or `gitlab.oauthClientId`; an Azure DevOps Server signs in with your Windows account or a token. Without one, there's no **Sign In** button: sign in with a token.
- **Sign Out** deletes the token. A session that expires or is revoked asks you to sign in again.

### Azure DevOps Server

Codalus IDE recognizes a remote on an Azure DevOps Server (or Team Foundation Server) by its path, `https://tfs.contoso.com/tfs/DefaultCollection/Project/_git/Repository`, so you don't need to set anything first:
- Git Changes [suggests signing in](#signing-in-from-git-changes) there: *This repository is on Azure DevOps (tfs.contoso.com/tfs)*. **Create a pull request**, and **Sign in** in the Pull Requests or Work Items window, open the same sign-in.
- **Sign in with your Windows account** signs in as you, with no token: Windows authentication, as the browser does. On macOS and Linux the button is **Sign in with Kerberos** and needs a ticket for your domain account (`kinit ada@CONTOSO.COM`).
- **Or paste a personal access token**: **Create a token** opens the server's page for one (your profile → Personal access tokens, in the repository's collection), with Code (read and write), Build (read) and Work Items (read).
- Signing in sets `azureDevOps.serverUrl` to the server. You can also set it yourself: the server's address without the collection (`https://tfs.contoso.com/tfs`, or `https://ado.contoso.com` on a server installed without the `/tfs` path).
- Codalus IDE is signed in to one Azure DevOps server at a time. Signed in to another (dev.azure.com, say), the sign-in says *This repository is on … Sign out, then sign in there.*
- Azure DevOps Server 2019 and later work. An older server's REST API version is used when the server says it doesn't take the latest.

The repository's host is found from its remotes (`origin` first). Then:
- **Pull Requests** (Git → Pull Requests): the open pull requests (merge requests on GitLab), all of them, yours, or those that ask for your review. Select one for its description, its review comments (with the file and line; click to open it) and its conversation. **Check Out** fetches its branch (a fork's pull request too) and switches to it. **Open in Browser** opens it on the host.
- **Review comments in the editor**: when the current branch has an open pull request, its unresolved review comments show at the end of their lines (◆ who: what). Click one for the whole thread. Settings: `hosting.reviewComments`, `hosting.showResolvedComments`.
- **Create Pull Request** (Git Changes' pull request button, the Git menu, or the Pull Requests window's toolbar): a title (from the branch name), a description in Markdown, the branch to merge into (the default branch first), draft. The branch is pushed first if it needs to be.
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
- **Git → Branches → Update Branch…** (also in the Git Changes window's ⋯ menu) brings a branch up to date from its remote without checking it out and pulling. It lists the local branches that have an upstream, the current one first. It fetches only the branch you pick (its remote-tracking branch, `origin/feature`, moves too) and moves the branch only by a fast-forward, so the branch you're on, your files and your uncommitted changes don't change. Picking the current branch fast-forwards it with its files, as a pull would, without ever making a merge commit; if that would overwrite a file you've changed, nothing moves and git's message says which. The status bar shows it running, with a cancel button, then how many commits came, or that the branch was up to date. A branch with only commits of its own has nothing to update: Push sends them. When the branch and its upstream both have commits the other doesn't, nothing changes and a notification says how many on each side, with **Checkout** to merge or rebase there (for the current branch, merge or rebase from its upstream). A branch checked out in another worktree, or being rebased there, is left alone, and the notification says where, with **Open in New Window**; a pull in that window updates it. While a merge or rebase is in progress, the current branch (or the branch being rebased) isn't offered. Each local branch with an upstream also has **Update from 'origin/…'** on its menu in the [branch drop-down](#the-branch-drop-down), where these messages show above the list instead.
- **Git → Fetch, Pull, Push and Sync** (pull, then push). The first push of a new branch publishes it to `origin` (or your only remote) and sets its upstream.
- **Credentials** come from your git credential helper (Git Credential Manager, the macOS keychain, …), or from you, in a dialog, when no helper answers ([above](#credentials-and-ssh-passphrases)).

Errors from git are shown as notifications. The full log of git commands is in View → Output → Git.
