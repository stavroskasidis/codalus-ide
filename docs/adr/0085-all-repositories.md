# 0085. All Repositories: a solution's repositories in Git Changes at once

- **Status:** accepted (0.9.0, 2026-10-26)
- **Date:** 2026-10-26
- **Deciders:** maintainer (the decisions of 2026-10-26: the full mode, with the found repositories and the ones the user adds; carry on past a failure and report it; the history asks which repository; additive plugin keys; Common branches offer Checkout, New Local Branch From, Merge, Rebase and Delete)

## Context

Since M13b (ADR 0031) a solution whose projects are in several repositories (a project in a repository of its own, a submodule, a clone beside the solution) gets a repository picker in Git Changes. Git Changes, the history and the status bar show one repository at a time; gutter markers and blame use each file's own. Committing a change that spans two repositories means switching, committing, switching back and committing again, and nothing shows that the other repository has changes at all.

The classic IDE's Git Changes has a multi-repository mode that the maintainer uses every day:
- the picker's first entry is **All Repositories (N)**, with a filter box;
- **Changes** lists every repository's files, grouped by repository;
- **Commit All Repos** commits with one message in each repository;
- fetch, pull, push and sync act on all of them;
- the branch drop-down has **Common branches** (the branches every repository has) and **All branches** (each repository's, under its name and current branch).

Constraints: git is the user's CLI, one process per operation (ADR 0008). Nothing blocks the UI thread. The plugin API is a public contract (ADR 0003, 0058). Per-solution state lives next to the solution (ADR 0070).

## Options

| Question | Options | Verdict |
|---|---|---|
| Scope | A full All Repositories mode; changes and commit only; or a better picker only | **The full mode.** Changes, staging, Commit All Repos, fetch, pull, push, sync and stash in each repository, and Common branches in the branch drop-down. |
| Which repositories | Only the ones found; the found ones plus the ones the user adds or leaves out; a scan of the solution's folder | **Found, plus added, less left out.** The picker's Add Repository… takes a repository with no project in the solution (docs, build scripts). Each repository but the solution's own has Remove from the List (×) in the picker. Both are kept with the solution. A scan would cost a folder walk on every open. |
| One repository fails | Carry on and report; check every repository first and then run all or none; stop at the first failure | **Carry on and report.** Each repository runs (one after another, so askpass prompts come one at a time). Then one notification lists every failure with git's own message ("2 of 3 repositories failed: Library: …"), and the Git output has each line. Nothing is undone: git can't roll back across repositories, so checking first would only narrow the window. |
| The history | A repository selector; a merged timeline; one tab per repository | **Pick a repository first.** View All Commits asks which repository (a quick pick, the solution's own first). A file's or a line's history uses that file's repository. The title names the repository, and the history's commit actions run there. A merged timeline has no meaningful graph. |
| The plugin contract | Additive context keys with the arguments unchanged; hide plugin entries on Common branches; no change | **Additive keys.** `gitAllRepositories` (global), `gitChangeRepository` (`gitChanges/context`), and `gitBranchIsCommon` and `gitBranchRepository` (`gitChanges/branch`). The arguments don't change. A Common branches row runs a plugin's entry once per repository that has the branch, with that repository's ref. |
| A Common branch's menu | Checkout, New Local Branch From, Merge, Rebase and Delete; Checkout and New only; every entry | **Checkout, New Local Branch From, Merge, Rebase, Delete** (and plugin entries). Rename, Update, Push, Compare, History and Copy Name are one repository's, on its own rows (`!gitBranchIsCommon`). |

## Decision

**All Repositories is the default** when the solution has several repositories. The picker's choice is kept with the solution (`session.json`'s `gitRepositories`: `added`, `removed`, and `shown`, a root, or none for all).

**Siblings.** `GitService` keeps tracking one repository: the solution's own, which the status bar, hosting (pull requests, CI status) and LFS badges read. While all repositories are shown, the others are open as **siblings** (`SetSiblingsAsync`). Each is a `GitService` with its own status, watcher and askpass environment. Their status changes raise `SiblingStatusChanged`, and their index changes raise the owner's `IndexChanged`, so gutter markers in their files follow. `ServiceFor(path)` returns the repository that holds a path, the innermost. `GroupByRepository` splits a list of paths, and `ForEachAsync` runs an action in each repository and collects `GitRepositoryFailure`s. `CommonBranches` is the local branches every repository has, in the first one's order.

**Routing in the shell.** A command acts on `ShellHost.Repo`: the repository it runs from (an `AsyncLocal` set by `InRepositoryAsync`), else the open one. These set it:
- a node's menu and hover buttons in Git Changes;
- a branch row in the drop-down;
- the operation banner (with all shown, the first repository with a merge, rebase, cherry-pick or revert in progress; its name prefixes the banner);
- a stash row (stashes carry their repository);
- a history or Compare document.

A command given a path (stage, discard, the diff, the merge editor, Add to .gitignore, Stage Active File) goes to that file's repository directly.

**Git Changes with all shown** has:
- one tree per repository, named by its folder;
- Commit All Repos. It commits the staged changes in each repository that has some, or with nothing staged every change in each (untracked files included, as Commit All does). It keeps the message when a repository fails. Amend is hidden.
- the branch button: the branch every repository is on, or "3 branches", with a line per repository in its tooltip;
- the outgoing and incoming counts, added up across repositories;
- Fetch, Pull (repositories whose branch has an upstream), Push (repositories with commits to push, or a branch not yet published, as with one repository), Sync, Stash All and Drop All, each in every repository;
- Write the Commit Message with AI: every repository's staged diff, each filtered against its own repository's exclusions (`AiActions.CommitMessageAsync` with several diffs).

**The branch drop-down with all shown** lists:
- **Common branches**: checked when every repository is on that branch. Checkout switches each repository that isn't. New Local Branch From asks for one name and creates the branch in each. Merge and Rebase run in each repository whose branch differs. Delete asks once and lets git refuse a branch that isn't fully merged.
- **All branches**: each repository under "Name (branch)", with its local branches and, when it has a remote, its remote branches. These rows act on their own repository.

The footer's New Branch… creates a branch in every repository. With all shown, Git → Switch Branch and the status bar's branch item open this drop-down.

## Consequences

- One message and one click commit a change that spans repositories. Every repository's changes are always in sight.
- With all shown, a watcher runs per repository. That is the cost of every repository's status staying current, and it is what the classic IDE does.
- `TODO(issue)`, deferred:
  - the per-repository rows of the branch drop-down have no ahead/behind chips;
  - Git LFS badges, hosting (pull requests, CI status, `#` completion) and the commit template are the solution's own repository's only;
  - Update Branch, Publish Branch and Publish to a Git Host act on one repository: pick it first.
- Revisit if solutions with many repositories (tens) make a watcher per repository too costly. A status read on focus, as the classic IDE does for closed repositories, would replace them.
