# 0020. Git depth: history, merge editor and operations

- **Status:** accepted (M13 plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M6 gave the MVP Git (ADR 0008: the git CLI, porcelain output): status, diffs, hunk staging, commit, branches, fetch, pull, push and blame. M13 covers the workflows people still leave the IDE for, starting with merge conflicts. It is split:
- **M13a:** history, conflicts and operations, in this ADR.
- **M13b:** hosting integration (sign-in, pull requests, CI status, issues), the IDE askpass prompt, worktrees and LFS.

The decisions to make:
- how the history graph is laid out and paged;
- what the merge editor works from;
- how operations that normally open an editor (rebase, cherry-pick, revert, continue) run without one;
- how per-line staging builds its patches;
- how `daedalus` serves as git's merge tool and editor.

## Decisions

**Still the git CLI**
- Every new operation is one `git` process, as in ADR 0008: log, show, stash, cherry-pick, revert, reset, rebase, tag, remote, submodule, clone, init.
- Signing, hooks, credential helpers and `commit.template` keep working because git applies them itself. The template prefills an empty commit message.

**The history graph: laid out in the workbench, page by page**
- `git log --topo-order -z` with a field separator gives pages of 200 commits: hash, parents, author, date, refs, subject. Filters map to git's own options: `--all` or a revision, `--author`, `--grep` (fixed string, case-insensitive), `--since`/`--until`, and a path (`--follow` for one file).
- `GitGraph` assigns lanes as the pages arrive. Each lane expects the next commit of one line of history. A commit takes its lane (or a free one); every lane that expected it ends at its dot; its first parent continues the lane; other parents join a lane that expects them or start one.
- The state carries over between pages, so a long history is laid out as it scrolls. Each row records only what to draw: lines from the top edge to the dot, from the dot to the bottom edge, and the lanes passing through. Rows stack into the graph without any whole-graph pass.
- **Line history:** `git log -L start,end:file`. Commits are found by a marker line in the format, because `-L` always prints patches. (`-s` with `-L` isn't available in every git we support.)

**The merge editor: git's markers in the work tree, plus the three stages**
- The result starts as the file git left, with its conflict markers (diff3's base section included when `merge.conflictStyle` asks for it). The current and incoming versions shown above come from the index's stages 2 and 3; the base comes from stage 1.
- Each conflict is resolved by Accept Current, Incoming or Both (one or all), or by editing. Accept Merge is enabled when no markers remain; it writes the file and stages it.
- Working from the markers keeps git's own merge (its rename handling, rerere, merge drivers) and shows exactly what git decided. Take Ours and Take Theirs for a whole file use `git checkout --ours/--theirs`.

**Operations without an editor**
- `GitStatus.Operation` says what's in progress (`merge`, `rebase`, `cherry-pick`, `revert`), from the git directory's state files. Git Changes shows a banner with Continue, Skip (not for a merge) and Abort.
- Continue runs with `GIT_EDITOR=true`, so prepared messages are kept as an IDE's Continue does.
- **Interactive rebase:** the todo list is edited in a document (reorder; pick, reword, squash, fixup, drop) and handed to git as `GIT_SEQUENCE_EDITOR="cp <our todo file>"`, through git's own shell, also on Windows.
  - **reword** becomes `pick` plus `exec git commit --amend --only --file=<message>`, so no editor opens for messages.
  - **squash** keeps the combined message.

**Per-line staging: sub-hunk patches**
- Choosing lines of a hunk builds a patch with only those changes. An unchosen removal becomes context and an unchosen addition is left out when the patch applies to the index; it is the other way around for unstaging and discarding, which apply in reverse.
- It's applied with `git apply --cached --unidiff-zero` (`-R` for the reverse cases), like hunk staging.

**`daedalus` as git's merge tool and editor**
- `daedalus merge LOCAL REMOTE BASE MERGED` and `daedalus --wait FILE` hand the request to the running shell over the single-instance pipe and wait for the answer. The pipe now serves each client on its own task, so a long merge doesn't hold up other requests.
- **The answers:** the merge succeeds when the result is accepted (written to MERGED) and fails when its tab is closed; the edit is done when the file's tab closes.
- With no shell running, a new one starts for the request and exits with its outcome (0 or 1), as git expects of `mergetool.trustExitCode`.

## Consequences

- Conflicts, history, stashes, tags, remotes and rebases no longer need a terminal.
- The history scales by pages. The proposed budget for 100k commits isn't measured yet: `TODO(issue)`, add the generated 100k-commit repository to the benchmark harness.
- `TODO(issue)`, M13b:
  - the hosting plugin: GitHub, GitLab and Azure DevOps sign-in through the OS credential store; pull requests with inline review comments; CI status; issues and work items;
  - publishing a repository to a host;
  - an askpass prompt for HTTPS and SSH passphrases (today the credential helper must be set up);
  - worktrees and Git LFS awareness;
  - solutions that span several repositories.

## Amendment (2026-10-19): the merge editor redesigned after a classic IDE's

The maintainer asked for the merge editor to look and work like a classic IDE's (2026-10-19, with screenshots of both). It replaces the text buttons and the result with git's markers; working from git's markers and the index's stages stays as decided above.

- **The result shows the merge base, not markers.** The markers are parsed as before, but each conflict's block in the result starts as the merge base's text, highlighted, and counts as unresolved until a side is taken or the block is edited. The base comes from diff3's section when git wrote one; otherwise from stage 1, through a line diff: the current block's lines found in stage 2 (by diffing the result with every current block taken against it), then those lines mapped to stage 1 and widened to the changes they touch (the incoming side when the current one can't be found). It takes the file's line endings, since the index's text can differ from the work tree's (`core.autocrlf`). With no stage 1 (both added), the block is empty.
- **The panes are the versions themselves.** Incoming (stage 3) and Current (stage 2) as they are, each block found by the same line diff; when a block can't be found (a filter changed the text), that pane falls back to the result with its side's blocks taken. Headers name the branches: the current branch during a merge, else git's marker labels (`HEAD`, `master`, a commit for a rebase).
- **Check boxes resolve.** A box beside each block in each source pane: one side checked takes its block, both take both (current first, whatever order they were checked in), none puts the base back. A header's box checks or unchecks its side on every conflict (mixed when only some). Typing in a block resolves it with the typed text; text equal to the base, a side or both reads as that state again, so undoing an edit unresolves it. Each check is one undo step that also restores the blocks' places and states, so undo and redo keep the text and the boxes together.
- **Accept Merge** is enabled once no conflict is unresolved, writes the result in the file's own encoding (its byte order mark kept) and stages it, as before.
- **Layout:** Incoming on the left, Current on the right, the result below, as in the classic IDE. The gear menu switches to three columns (result in the middle) and swaps the sides, kept in two user settings, `git.mergeEditor.layout` and `git.mergeEditor.incomingOnLeft`, so a user used to the old current-on-the-left order can have it back. The panes scroll together: sideways by the same offset, and up and down by line, mapped stretch by stretch between the blocks so that each conflict's blocks stay level when the versions differ in length elsewhere.
- **Colors:** new tokens in every theme, `Shell.Merge.Conflict` (a muted red fill, opaque so the code on it is checked for contrast; none in High Contrast), `Shell.Merge.Conflict.Border` (the dashed outline) and `Shell.Merge.Resolved.Border` (a solid outline once resolved), with their contrast pairs.
- **Commands:** `git.mergeEditor.nextConflict` (`F8`), `.previousConflict` (`Shift+F8`), `.takeIncoming`, `.takeCurrent`, `.takeBoth`, `.takeAllIncoming`, `.takeAllCurrent` and `.acceptMerge`, enabled while a merge editor is the active document.
