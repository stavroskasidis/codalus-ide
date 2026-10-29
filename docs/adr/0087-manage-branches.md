# 0087. Manage Branches: the Git Repository document

- **Status:** accepted (2026-10-29)
- **Date:** 2026-10-29
- **Deciders:** maintainer (the decisions of 2026-10-29: a document as the classic IDE's Git Repository window; branches, remotes, tags, pull requests and submodules in its tree; a root per repository; the links in the Git menu, Git Changes' ⋯ and the branch drop-down; the classic IDE's behavior on a double-click and in the menus)
- **Builds on:** [ADR 0008](0008-git.md), [ADR 0020](0020-git-depth.md) (the history), [ADR 0031](0031-git-hosting-and-repository-features.md) (hosting), [ADR 0085](0085-all-repositories.md) (All Repositories)

## Context

Codalus has a branch drop-down in Git Changes (D38) and a History document with a graph (M13), but nowhere to see every branch at once with its history beside it. The classic IDE's Git Repository window (Git → Manage Branches, `Ctrl+0 Ctrl+R`) does that: a tree of the branches in folders, the remotes, the tags, the pull requests and the submodules on the left, and the selected branch's history on the right, with its incoming commits above it and its outgoing count, Fetch, Pull, Push and Sync.

Constraints: git is the user's CLI, one process per operation (ADR 0008); nothing reaches a Git host until the user signs in to it (ADR 0031); with several repositories every command runs in the repository it comes from (ADR 0085, `ShellHost.Repo`).

## Options

| Question | Options | Verdict |
|---|---|---|
| Shape | A document as the classic IDE's window; the same as a tool window; a tree-only tool window that opens the History document | **A document.** The history needs the width. The right side is the History document's view (`GitHistoryView`) with its branch box hidden, so the graph, details and commit menu are the same. |
| The tree | Branches, remotes and tags; also pull requests; also pull requests and submodules | **All of them**, as the classic IDE's. Pull requests come from the host the repository's remotes are on, only once the user is signed in to it; until then the node offers Sign In. |
| Several repositories | A root per repository; the repository Git Changes shows | **A root per repository**, each with its Branches / Tags, Pull Requests and Submodules; with one repository the tree is the classic IDE's (Branches / Tags with the repository under it). Fetch, Pull, Push and Sync from the document run in that repository alone. |
| The links | Git menu only; Git Changes' ⋯; the branch drop-down; both | **Both**, and Git → Manage Branches, Git → Branches → Manage Branches, `Ctrl+0 Ctrl+R`. |
| Behavior | The classic IDE's; no incoming section; a double-click that only shows the history | **The classic IDE's.** A double-click (Enter) checks a branch out; a branch's menu is the drop-down's (`gitChanges/branch`, so plugins' entries show too, with the same when-clauses); a tag, a pull request, a submodule and the repository have menus of the document's own. |

## Decision

`GitRepositoryViewModel` reads each repository's branches (`BranchDetailsAsync`), tags, remotes and submodules (only when `.gitmodules` exists), builds the tree, and keeps the expanded nodes and the selection by key across reloads. The shell reloads the refs when a repository's status changes (the watcher sees `HEAD` and `refs/`), debounced, and the pull requests on open and Refresh. A local branch with an upstream lists its incoming commits (`branch..upstream`, at most 100); a pull request whose source branch is fetched shows `target..source`; a submodule's history is read through a `GitService` of its own, not watched, while it's selected.

The branch menu's context (`GitBranchScope`) and titles (`GitBranchTitle`) now take the repository, its local branch of a remote branch's name, and whether it has a remote, so the document builds them from its own data; the drop-down passes its own. Checkout on a remote branch finds the local branch of its name in the branches it reads, not the drop-down's last list (which was empty when the drop-down had never opened).

## Consequences

- No plugin API change: the document reuses `gitChanges/branch`; tags, pull requests and submodules have no menu location yet. `TODO(issue)`: a `gitRepository/*` menu location per node kind, if plugins need one.
- `TODO(issue)`: the website's screenshot of the document (`smoke-test-git-repository.png`, the `git.repository` smoke step) once a CI run has it.
- `TODO(issue)`: the pull request filters of the Pull Requests window (Mine, Review requested) in the tree; it lists all open ones.
