# 0034. Build depth: settings, batch builds, the fast up-to-date check, timings, binary logs and build events

- **Status:** accepted (M26b, 2026-10-11; the maintainer's goal of 2026-10-11: complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M26b is the build half of M26 (ADR 0033 covers M26a). It asks for Batch Build; build settings (MSBuild verbosity for the Output window and a log, parallel project builds, what Run does when projects are out of date, code analysis on build); a fast up-to-date check verified against real MSBuild runs; build timings with the critical path and the slowest targets and tasks; binary logs with a structured log viewer built on MSBuild Structured Log Viewer's `StructuredLogger` library; and pre-build and post-build events in the property pages, stored as the classic IDE stores them.

Constraints: MSBuild runs only in the build host (ADR 0004) and only for trusted folders; a wrong skip is a bug, not a trade-off; the shell is Native AOT.

## Decisions

**Build options travel with each request**
- `build/run` and `build/batch` take `BuildOptions`: the Output window's verbosity (`minimal` by default; `normal` adds the projects, command lines and copies, `detailed` the targets and low-importance messages), a log file (MSBuild's `FileLogger`) and its verbosity, the most projects built in parallel (`MaxNodeCount`; 0 is one per processor), a binary log file, whether the up-to-date check may skip MSBuild, and extra global properties. The shell fills them from settings: `build.verbosity`, `build.logFileVerbosity` (`off` by default; the log is `build.log` in the logs folder, Build → Open Build Log), `build.maxParallelProjects`, `build.binaryLog`, `build.fastUpToDateCheck`, and `build.runAnalyzersDuringBuild` (off sets `RunAnalyzersDuringBuild=false`). Without options a build runs as before (M4), so older callers are unchanged. The host keeps no build settings of its own.
- **Before running:** `build.beforeRun` is `build` (the default: F5 and Ctrl+F5 build the startup project and what it references, which the up-to-date check makes quick), `prompt` (ask only when `build/upToDate` says it is out of date: Build, Run Anyway or cancel) or `never`.

**The fast up-to-date check: state recorded at the last build**
- After a successful build with options, each project it built (and each project those reference) records its inputs and outputs with their sizes and write times in `obj/<configuration>/<framework>/daedalus.uptodate.json`, keyed by its global properties, target framework and SDK. Inputs: the project file and every import; `Compile`, `EmbeddedResource`, `AdditionalFiles`, `Analyzer`, `EditorConfigFiles`, XAML and `UpToDateCheckInput` items; `Reference` hint paths; the assets file, icon, manifest and key file; items it copies to the output folder; and the assemblies of the projects it references. Outputs: the assembly, reference assembly, deps and runtime config files, each copied file at its destination, and `UpToDateCheckBuilt` items. The state is recorded with a fresh evaluation after the build (its restore may change the imports), and an input written while the build ran (other than in the project's `obj` folder or a referenced project's assembly) is recorded as stale, so an edit made during a build is never lost.
- A build skips MSBuild only when every project in scope has its recorded inputs, as the same files with the same stamps, every output still as recorded, and every project it references up to date. Comparing with the recorded state, not timestamps against each other, means clock skew, restored backups and files copied with old dates can't cause a skip. A clean, a failed or cancelled build, `DisableFastUpToDateCheck`, or a project that can't be evaluated means no skip.
- A solution build is skipped when every project the active configuration builds is up to date; otherwise MSBuild builds the solution (its incrementality decides what runs). A project build is skipped when the project and its references are up to date. The Build output says why the first out-of-date project isn't up to date, or that MSBuild didn't run; Build → Check Whether Projects Are Up to Date lists every project's verdict.
- Tests compare each decision with MSBuild (`BuildDepthIntegrationTests`): after an edit (a source file in a referenced project, a new file under the glob, the project file, a copied content file, a deleted output) the check says "out of date" with the reason and an MSBuild build then writes the output; without one it says "up to date" and an MSBuild build writes nothing. Clean forgets the state and Rebuild records it.

**Timings from a logger**
- Every build attaches a timing logger, registered with a forwarding logger that passes only build, project, target and task start and finish events from the nodes. `build/timings` returns the last build's: each project build's span and node, the 50 slowest targets, the tasks summed by name, and the **critical path** over each project's real build (per project and target framework, its longest build; the others are the reference protocol's short calls): from the build that finished last, back through the referenced project whose build finished last. The shell shows them in a Build Timings document: a timeline row per project build (the short ones hidden unless asked for), the critical path, and the slowest targets and tasks.

**Binary logs and the structured log viewer**
- `build.binaryLog` (every build), or Build → Build Solution with Binary Log (one build, which then runs MSBuild even when everything is up to date), attaches MSBuild's `BinaryLogger` (with the project imports embedded) and writes `logs/binlogs/<solution>-<time>.binlog` in the user's data folder; the newest 20 are kept.
- The viewer reads a `.binlog` in the build host with `MSBuild.StructuredLogger` (MIT): `binlog/open` reads it and returns its root and a summary; `binlog/children` returns a node's children (the tree goes to the shell a level at a time, so a large log shows its root at once); `binlog/search` matches node text and diagnostics' files and returns each result with its path from the root, so the viewer can reveal it. Build → Open Binary Log… opens any `.binlog`; a node with a file opens it at its line.

**Batch Build**
- A document lists every project × configuration × platform the projects declare (from the Configuration Manager's data) with checkboxes remembered for the session, and builds, rebuilds or cleans the checked ones in turn through `build/batch` (one exclusive build: one restore per project, the up-to-date check per entry), with a result per entry.

**Build events as targets**
- `buildEvents/get` and `buildEvents/set` read and write `<Target Name="PreBuild" BeforeTargets="PreBuildEvent">` and `<Target Name="PostBuild" AfterTargets="PostBuildEvent">`, each with an `Exec Command`, and the `RunPostBuildEvent` property (absent for the default, `OnBuildSuccess`): exactly what the classic IDE writes in SDK-style projects. An empty command removes its target. The property pages gain a Build Events page.

## Consequences

- No-op builds of an unchanged solution take an evaluation per project instead of an MSBuild build.
- `MSBuild.StructuredLogger` loads in the build host only, against the SDK's MSBuild; binary logs are read through the open solution's build host.
- `TODO(issue)`: per-project skipping inside a solution build that has out-of-date projects (MSBuild's own incrementality handles them today); files a custom target reads without declaring them aren't tracked (declare them as `UpToDateCheckInput`, as in the classic IDE); the binary log viewer has no timeline or project-graph view yet (the tree, search and summary are there).
