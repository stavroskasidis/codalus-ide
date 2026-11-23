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

- **The result shows the merge base, not markers.** The markers are parsed as before, but each conflict's block in the result starts as the merge base's text, highlighted, and counts as unresolved until a side is taken or the block is edited. The base comes from diff3's section when git wrote one; otherwise from stage 1, through a line diff: the current block's lines found in stage 2 (by diffing the result with every current block taken against it), then those lines mapped to stage 1 and widened to the changes they touch, and the same for the incoming block in stage 3; the base is the stretch of stage 1 both cover, as diff3's section is (so a conflict where one side deleted what the other changed shows the deleted lines; fixed 2026-11-18). It takes the file's line endings, since the index's text can differ from the work tree's (`core.autocrlf`). With no stage 1 (both added), the block is empty.
- **The panes are the versions themselves.** Incoming (stage 3) and Current (stage 2) as they are, each block found by the same line diff; when a block can't be found (a filter changed the text), that pane falls back to the result with its side's blocks taken. Headers name the branches: the current branch during a merge, else git's marker labels (`HEAD`, `master`, a commit for a rebase).
- **Check boxes resolve.** A box beside each block in each source pane: one side checked takes its block, both take both (current first, whatever order they were checked in), none puts the base back. A header's box checks or unchecks its side on every conflict (mixed when only some). Typing in a block resolves it with the typed text; text equal to the base, a side or both reads as that state again, so undoing an edit unresolves it. Each check is one undo step that also restores the blocks' places and states, so undo and redo keep the text and the boxes together.
- **Accept Merge** is enabled once no conflict is unresolved, writes the result in the file's own encoding (its byte order mark kept) and stages it, as before.
- **Layout:** Incoming on the left, Current on the right, the result below, as in the classic IDE. The gear menu switches to three columns (result in the middle) and swaps the sides, kept in two user settings, `git.mergeEditor.layout` and `git.mergeEditor.incomingOnLeft`, so a user used to the old current-on-the-left order can have it back. The panes scroll together: sideways by the same offset, and up and down by line, mapped stretch by stretch between the blocks so that each conflict's blocks stay level when the versions differ in length elsewhere.
- **Colors:** new tokens in every theme, `Shell.Merge.Conflict` (a muted red fill, opaque so the code on it is checked for contrast; none in High Contrast), `Shell.Merge.Conflict.Border` (the dashed outline) and `Shell.Merge.Resolved.Border` (a solid outline once resolved), with their contrast pairs.
- **Commands:** `git.mergeEditor.nextConflict` (`F8`), `.previousConflict` (`Shift+F8`), `.takeIncoming`, `.takeCurrent`, `.takeBoth`, `.takeAllIncoming`, `.takeAllCurrent` and `.acceptMerge`, enabled while a merge editor is the active document.

## Amendment (2026-11-18): the merge editor's own re-merge

The maintainer compared a conflict in a Blazor component in the merge editor and in the classic IDE (2026-11-18, with screenshots of both): one branch had moved a toolbar into a new container (re-indenting its buttons and changing two attributes) and the other had changed another attribute of the same buttons. git's merge (merge-ort, the histogram diff on exact lines) matched none of the re-indented lines, so it wrote one 41-line conflict whose current side was empty; the merge editor showed it as such, with an empty result (the base-text bug fixed above). The classic IDE showed a small conflict on the button itself. Working from git's markers stays the rule; this amendment adds a second opinion for files git's merge left untouched.

- **When:** the file is still exactly git's merge of the three stages: `git merge-file -p --object-id --diff-algorithm=histogram` with the file's marker labels and conflict style (plain, `--diff3` or `--zdiff3`) gives the work tree's text, apart from the line endings checkout gave it. Otherwise (an edit, rerere, a merge driver) git's markers stay, since a re-merge would lose what changed them. `merge-file` takes `--diff-algorithm` from git 2.44 (and `--object-id` from 2.43); with older git the check fails and git's markers stay. `codalus merge` (mergetool) checks git's LOCAL, BASE and REMOTE files the same way.
- **How:** `ThreeWayMerge` (Workbench), on the thread pool. diff3's chunks: each side aligned with the base by the Myers line diff (`LineDiff`) on lines with their whitespace removed, so a re-indented line lines up with its old self; between the stretches all three share, the stretches either side changed. Whether a side changed a stretch is decided on the exact text, so a re-indent still counts as a change: a stretch only one side changed (or both alike) takes that side, and the rest are conflicts, written with diff3 markers (the base section included, so the result's base text needs no line mapping). Consecutive conflicting stretches are one conflict, as are changes to neighboring lines, as in git. The output takes the work tree's labels and line endings.
- **Rejected:** `git merge-file --diff-algorithm=myers` on exact lines. On the maintainer's file it found two conflicts, but paired master's buttons with develop's unrelated blocks (the re-indented buttons matched nothing, so the diff anchored on braces). git's merge-file has no whitespace option, hence the merge of our own. Auto-merging a stretch where one side only re-indented and the other edited a line was rejected too: it stays a (one-line) conflict, as no change is merged on a guess.
- **Shown:** when the re-merge's conflicts differ from git's, the editor shows the re-merge's, and the count's tooltip says so: "Conflicts recomputed with a line-by-line merge (git found 1)". When they're the same, git's file is used as it is.
- **Changed words:** in each source pane, the words a conflict's block changed from the other side's block are boxed, in `Shell.Diff.Removed.Word` with its border, the diff view's tokens, which are already checked for contrast on red fills. The blocks' lines are paired by a line diff without whitespace, and each pair is compared token by token (`WordDiff.TokenRanges`: words and single other characters, whitespace left out). The diff view's character diff was tried first, on the maintainer's file: it boxed the re-indent, and it matched every letter of one side's short value inside the other side's longer one (as `@SendText` inside `@Resources.ChatSend`), so it boxed only the longer value. Tokens box neither a re-indent nor scattered letters, and mark both values whole.
- **The panes line up without whitespace too** (after trying the re-merge on the maintainer's file): between conflicts, `MergeAlignment` diffs each pane with the result on lines without their whitespace. On exact lines, the incoming side's lines in a block the current side re-indented matched none of the result's, and stood opposite hatched filler.
- **The base text is indented as the code around it:** a conflict's base text in the result (unresolved, or kept as the base) takes a side's indentation where that side re-indented the same line (the current side first, as the merge takes a re-indent), and a line neither side still has moves as its nearest such line did (`MergeEditorViewModel.Reindent`). Shown exactly as the base had it, an unresolved line in a block a branch moved into a new element sat a level to the left of the lines around it.
