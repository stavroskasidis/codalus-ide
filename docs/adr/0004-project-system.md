# 0004. Project system and build host

- **Status:** accepted (M2 plan approval, 2026-10-08)
- **Date:** 2026-10-08
- **Deciders:** maintainer

## Context

The project brief's M2 asks for `.sln`/`.slnx` and SDK-style projects opened **through MSBuild evaluation in a build host**, a Solution Explorer (nested projects, solution folders, a Dependencies node, file nesting, add/rename/delete/move with project-file updates, file watching), new projects from `dotnet new` templates, configurations and platforms, and multi-targeting. ADR 0002 sets a provisional budget: the tree from the solution file within 1.5 s and projects evaluated within 5 s on the 50-project fixture.

Constraints:
- The shell is Native AOT. MSBuild needs the JIT, reflection and the installed SDK, so it can't run in the shell.
- The M0 language host loads solutions with Roslyn's `MSBuildWorkspace`, which evaluates *and* compiles every project before returning anything (8–9 s on the fixture, ADR 0002).
- Opening a solution runs code: MSBuild evaluation imports `.props`/`.targets` files from the solution and from restored packages, and restore runs NuGet's tasks. The maintainer chose a **workspace trust prompt** (VS Code's model) with automatic restore for trusted folders.

## Options

1. **Evaluate in the language host** (extend `MSBuildWorkspace` use). One process fewer, but the tree waits for Roslyn, file operations and builds would live in the language service, and a Roslyn crash would take the project model down with it.
2. **A dedicated build host** that owns MSBuild: solution files, evaluation, file watching, restore, project-file edits, and later builds (M4) and the design-time data Roslyn needs (M3). The language host becomes a consumer of the project model.
3. **Parse project files ourselves** (no MSBuild). Fast, but wrong for anything beyond trivial projects: SDK imports, conditions, globs, `Directory.Build.props`, multi-targeting.

## Decision

**Option 2.** `daedalus-buildhost` is a .NET 10 process on the regular JIT runtime (ReadyToRun when published, framework-dependent like the plugin host). It finds MSBuild in the installed SDK with `Microsoft.Build.Locator` and speaks JSON-RPC (LSP framing, `DaedalusStudio.Rpc`) with the shell, which supervises it with `SupervisedProcess`: a crash restarts it and the solution is reopened. It starts when a solution opens; with no solution, no build host runs.

**Progressive loading.**
1. The solution file is read with `Microsoft.VisualStudio.SolutionPersistence` (MIT; the library the `dotnet` CLI uses; reads and writes `.sln` and `.slnx` and keeps their formatting). Solution folders, projects and solution configurations go to the shell at once, so the tree appears before any evaluation.
2. Projects are evaluated in parallel with a shared `EvaluationContext`, using the active solution configuration's `Configuration` and `Platform` and, for multi-targeted projects, each target framework. Each project is sent as soon as it is evaluated.
3. Restore runs in the background (`dotnet restore`). Dependencies are read from `obj/project.assets.json` and update when it changes.

**The wire model is data, not a tree.** The host sends projects as items with kinds and metadata (compile, content, none, embedded resource, folder; `Link`, `DependentUpon`), references (framework, package, project, analyzer, with resolved versions when restored), target frameworks and diagnostics. The shell derives the Solution Explorer tree (nesting rules, Dependencies grouping), so presentation changes need no protocol changes.

**Edits.** File operations happen on disk; with SDK globbing most need no project edit. Where an item is explicit (`Compile Remove`, `None Update`, `EmbeddedResource`, `Folder`, `Link`, `DependentUpon`) or a project reference names a moved or renamed project, the host updates the project file through MSBuild's construction model with formatting preserved, and the solution file through SolutionPersistence. Deleting moves files to the OS trash.

**Watching.** The host watches the solution and project directories (debounced). Project and imported-file changes trigger re-evaluation; file additions and removals refresh the affected projects.

**Trust.** Trust is stored per folder in the user's state (not in the repository). An untrusted solution shows only the structure from the solution file: no evaluation, no restore, no builds, until the user trusts it. The decision can be changed later (Trust command, or revoking in settings).

**Templates.** New Project and New Solution run `dotnet new` from a built-in plugin, using quick-pick and input-box APIs added to the plugin API. They shipped as 0.1.1, not 0.2.0: they are additive, and under the caret rules a 0.2.0 would have locked out `^0.1.0` plugins.

## Consequences

- The tree appears from the solution file alone, so budget 4's first step doesn't depend on MSBuild's speed.
- MSBuild is loaded once, in one process, and a crash in it never touches the UI or Roslyn.
- M3 must feed Roslyn from the build host's evaluation (design-time builds for references and compiler options) instead of `MSBuildWorkspace`. Decided in ADR 0005.
- Two .NET helper processes run while a solution is open (build host, and from M3 the language host), which costs memory. ADR 0002 sets no budget for an open solution yet; M3 records one.
- Non-SDK (legacy) projects are shown from the solution file with an "unsupported project" state. Legacy project support is a non-goal (the roadmap's Out of scope, from the project brief), so this state stays.
