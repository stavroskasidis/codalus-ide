# 0070. Per-solution, per-user state in `.codalus/state/` next to the solution

- **Status:** accepted (2026-10-16, the maintainer's decision)
- **Date:** 2026-10-16
- **Deciders:** maintainer

## Context

Roadmap F7 settled where per-solution state lives: "`.codalus/` next to the solution (git-ignored, like `.vs/`)". As built, only part of it went there. The shell kept a solution's session (open documents and carets, breakpoints, exception settings, Watch, pinned data tips, startup projects, launch profiles, mobile devices, the ASP.NET Core environment) in the user's state folder, as `sessions/<SHA-256 of the solution path>.json`. Bookmarks were one global `bookmarks.json`. Solution Explorer's expansion and the active configuration weren't kept at all. Only the build host's unloaded projects were next to the solution, directly in `.codalus/solution.json` (ADR 0013). The maintainer opened a solution, found no `.codalus` folder, and lost the Solution Explorer's expansion when they reopened it.

`.codalus/` also holds files people share and commit: `settings.json`, `tasks.json`, `launch.json`, `playlists/`, and files they may keep, such as `profiler/` and `benchmarks/`. So the per-user state needs a place of its own inside it, one that never shows in Git.

## Options

1. **Keep the session in the user's state folder** and key it by a hash of the solution's path. Nothing is written into the repository, but the state doesn't move with the folder, there's nothing to inspect next to the solution, and it isn't what F7 says.
2. **`.codalus/state/` next to the solution, ignored by the repository's own `.gitignore`.** A repository without the line shows the state as untracked files.
3. **`.codalus/state/` with its own `.gitignore` holding `*`**, as `.pytest_cache` and `.mypy_cache` do: the folder ignores itself, whatever the repository's `.gitignore` says. Generated `.gitignore` files (Add `.gitignore`, ADR 0031) list `.codalus/state/` too.

## Decision

Option 3. Per-solution, per-user state lives in `<solution folder>/.codalus/state/`, one folder per solution file, so several solutions in one folder don't collide:

```
.codalus/
  settings.json, tasks.json, launch.json, playlists/, profiler/, benchmarks/, folder.json   (unchanged)
  state/
    .gitignore                  "*": the folder ignores itself
    App.slnx/                   one folder per solution file name (App.sln, App.slnf and App.csproj have their own)
      session.json              the editor session, below
      bookmarks.json            the solution's bookmarks
      solution.json             the unloaded projects (written by the build host)
    folder/                     a folder opened without a solution (folder mode, ADR 0033)
```

`session.json` keeps the existing session's JSON shape (source-generated, `EditorSession`) with new optional fields: the active configuration and platform (reopened with them while the solution still has them, else the default: Debug, then the first), the target framework chosen per multi-targeted project (kept only while the project still targets it), Solution Explorer's expanded nodes and the solution folders the user collapsed, the Error List's filters (errors, warnings, messages, build or IntelliSense, current document only) and the Test Explorer's grouping.

- **Writing.** `SolutionStateStore` (workbench) serializes on the caller's thread and writes in the background, one write after another, through a temporary file and a rename. The app waits up to 2 s for pending writes when it exits. The first write creates `.codalus/state/` and its `.gitignore`. Opening a solution writes nothing (`SettingsService` still doesn't create `.codalus/`), except for the migration below. The session is saved when the solution closes, when the app exits, and on the events that saved it before (builds, Watch and exception-setting changes, the startup projects page). Bookmarks are saved on every change.
- **A folder that can't be written** (read-only, a share that refuses it, a file named `.codalus`): the state goes to the user's state folder, `<state>/solution-state/<hash of the solution path>/`, and the failure is logged. Reads take the newer of the two. The next write that succeeds next to the solution deletes the fallback copy. The build host has no fallback for the unloaded projects: a failed save is logged, and the project stays unloaded for the session.
- **Migration.** When a solution has no `session.json` yet, its old `<state>/sessions/<hash>.json` is moved in, then deleted. When it has no `solution.json` yet, the build host moves `.codalus/solution.json` in (a project opened alone never takes it). If the move fails, the old file is read where it is. When a solution has no `bookmarks.json` yet, the global `bookmarks.json`'s bookmarks in the solution's folder (or below) move to it. If two solutions share the folder, the first one opened takes the old session-less bookmarks and unloaded projects.
- **Bookmarks outside any solution.** With no solution open, bookmarks stay in the global `<state>/bookmarks.json`, as before, for files opened on their own. A solution's bookmarks are only its own: the global ones come back when it closes.
- **Hidden folder.** On Windows, the store hides the `state` folder when it creates it, as Visual Studio hides `.vs`. `.codalus` itself stays visible, because people edit and commit the shared files in it, and tools read `.codalus/settings.json`. The hidden attribute only affects how Explorer shows the folder: reading and writing the files inside it isn't affected.
- **Remote windows** (M21) keep no solution state, as before (their session was never restored).
- **Paths.** Paths in the files stay absolute, as before. Solution Explorer's node ids hold paths (`p:`, `d:`, `f:`) or the solution file's folder ids (`sf:`), so they're the same in every session. Moving the solution folder keeps the configuration, the views and the solution folders' expansion, but not path-keyed entries.

## Consequences

- What a solution reopens with is next to it, git-ignored without help, and can be looked at or deleted like `.vs`.
- A solution in a read-only checkout still keeps its state, in the user's folder.
- Expanded node ids aren't pruned, so ids of deleted items stay in `session.json`. They do no harm, because nothing expands that doesn't exist.
- Not kept (yet): per-document folding, Solution Explorer's selection and scroll position, Show All Files, the Breakpoints window's columns, and the layout of tool windows per solution. The window layout stays per user, for all solutions. `folder.json` (folder mode's loaded projects) stays in `.codalus/`: it defines the folder's solution view, like a solution file, and moving it would change the build host's contract for no gain.
- Revisit if a shared, committed part of the session is ever wanted (VS has none): it would go in `.codalus/` next to `settings.json`, not in `state/`.
