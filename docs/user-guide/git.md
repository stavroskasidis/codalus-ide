# Git

When the open solution is in a git repository, Daedalus Studio shows its state and lets you work with it. Git operations run the `git` you have installed, so your configuration, credential helper, SSH keys, hooks and commit signing all apply.

## The Git Changes window

Git → Git Changes (`Ctrl+0 Ctrl+G`).

- **At the top**: the branch, with commits ahead ↑ and behind ↓ its upstream; Fetch, Pull and Push.
- **Commit**: write a message, then **Commit Staged**, or **Commit All** (every change to tracked files). `Ctrl+Enter` in the message box commits. **Amend** fills in the last commit's message and rewrites that commit.
- **Staged Changes** and **Changes**: each file has buttons to stage (`+`), unstage (`−`) and discard. **Stage All** and **Unstage All** act on the whole list. Double-click a file to see its diff.
- **Merge Conflicts**: listed during a merge. Resolve them in the file, stage it, and commit, or **Abort Merge**.

## Diffs

A file's diff opens in a document:
- **Side by side**, with the two versions aligned and scrolling together, or **inline**.
- **Previous Change** and **Next Change** move between changes.
- **Stage Hunk**, **Unstage Hunk** and **Discard Hunk** act on the change at the caret. That's how you stage part of a file.

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
