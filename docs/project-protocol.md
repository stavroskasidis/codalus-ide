# Shell ↔ build host protocol

The build host (`daedalus-buildhost`, [ADR 0004](adr/0004-project-system.md)) owns MSBuild: solution files, project evaluation, file watching, restore and project-file edits. The shell starts one host per open solution (`--solution-dir <dir>`, so the solution's `global.json` picks the SDK) and talks to it over **stdio** with **JSON-RPC 2.0 and LSP framing**, like the other hosts. Contracts are in `src/DaedalusStudio.Projects.Protocol`; the shell side is `SolutionService` in `src/DaedalusStudio.Workbench/Projects`.

**Protocol version 0.1.** Parameters are single objects with camelCase names. Paths are absolute file system paths.

## Lifecycle

1. `initialize` `{processId, clientName, protocolVersion}` → `{serverName, serverVersion, protocolVersion, msbuildVersion, sdkPath}`.
2. `solution/open` `{path, trusted, configuration?, platform?, restore, activeFrameworks?}` → `SolutionInfo`, read from the solution file only. A `.csproj` (or `.fsproj`, `.vbproj`) opened on its own becomes an in-memory solution with format `project`.
3. When trusted, projects are evaluated in parallel and each arrives as a `project/evaluated` notification, with `solution/evaluationProgress` `{evaluated, total, completed}` after each one. Design-time builds follow (below). With `restore`, a restore follows; afterwards only projects whose assets file changed get a new design-time build.
4. `shutdown` / `exit`. If the host crashes, the shell restarts it and repeats `initialize` and `solution/open` with the user's latest choices (trust, configuration, active frameworks), without restoring again.

## Requests (shell → host)

| Method | Params | Result | Notes |
|---|---|---|---|
| `solution/open` | `OpenSolutionParams` | `SolutionInfo` | Untrusted: structure only, no evaluation, no restore, no watching. |
| `solution/close` | `{}` | `{}` | |
| `solution/trust` | `{restore}` | `{}` | The user trusted the folder: evaluate (and restore). |
| `solution/setConfiguration` | `{configuration, platform}` | `{}` | Re-evaluates every project with the mapped project configuration. |
| `project/reevaluate` | `{path}` | `{}` | |
| `restore/run` | `{path?}` | `{}` | Starts `dotnet restore` (solution when `path` is null); trusted only. |
| `fs/list` | `{path}` | `{entries: [{path, isDirectory}]}` | Non-hidden entries of a folder. |
| `fs/createFile` | `{path, contents?}` | `FileOperationResult` | Fails if the file exists. |
| `fs/createFolder` | `{path}` | `FileOperationResult` | |
| `fs/rename` | `{oldPath, newPath}` | `FileOperationResult` | Files, folders and project files; updates explicit items, `DependentUpon`, project references and solution entries. |
| `fs/move` | `{sources, targetFolder, copy}` | `FileOperationResult` | Same updates as rename (not for copies). |
| `fs/delete` | `{paths}` | `FileOperationResult` | Moves to the OS trash; removes explicit items and, for project files, the solution entry. |
| `solution/addProject` | `{projectPath, solutionFolder?}` | `{}` | `solutionFolder` is a folder id such as `/src/`; created if missing. |
| `solution/removeProject` | `{path}` | `{}` | |
| `solution/addFolder` | `{name, parent?}` | `{}` | |
| `solution/renameFolder` | `{id, newName}` | `{}` | |
| `solution/removeFolder` | `{id}` | `{}` | |
| `solution/move` | `{item, targetFolder?}` | `{}` | `item` is a project path or a folder id; null target = solution root. |
| `project/addReference`, `project/removeReference` | `{projectPath, referencePath}` | `{}` | `ProjectReference` items, relative paths. |
| `project/setActiveFramework` | `{path, targetFramework}` | `{}` | The framework of a multi-targeted project that design-time builds (and so the language service) use. Default: the first one. |
| `project/designTimeBuild` | `{path?}` | `{}` | Runs design-time builds again (all projects when `path` is null); trusted only. |
| `build/run` | `{target: build \| rebuild \| clean, project?, restore}` | `{succeeded, canceled, errors, warnings, elapsedMilliseconds}` | Builds the solution (`project` null) or a project with the active configuration (ADR 0006); restore first unless `restore` is false (never for clean). Completes after the build's last `build/output` and `build/diagnostic`. One at a time; trusted only. |
| `build/cancel` | `{}` | `{}` | Stops the running build; its `build/run` result has `canceled: true`. |
| `project/runInfo` | `{path}` | `RunInfo` | How to start a project, for the active configuration and target framework; trusted only. |

`FileOperationResult` is `{updatedFiles, createdPaths}`: the project and solution files that were edited to keep up, and what was created.

## Notifications (host → shell)

| Method | Params |
|---|---|
| `solution/changed` | `SolutionInfo` (the solution file changed, by us or outside Daedalus) |
| `project/evaluated` | `ProjectInfo` |
| `solution/evaluationProgress` | `{evaluated, total, completed}` |
| `restore/output` | `{line}` |
| `restore/state` | `{state: running \| succeeded \| failed, message?}` |
| `project/compilerInputs` | `CompilerInputsInfo` (a design-time build finished) |
| `solution/designTimeProgress` | `{built, total, completed}`; `completed` once nothing more is queued |
| `build/output` | `{line}`: a line for the Build output channel |
| `build/diagnostic` | `BuildDiagnosticInfo` |
| `log` | `{level, message}` |

## Model

- **`SolutionInfo`** `{path, format (sln | slnx | project), folders, projects, configurations, active, trusted}`. Folders are `{id, name, parent?, files}` where `id` is the folder's path in the solution (`/src/libs/`) and `files` are solution items. Projects are `{path, name, parent?, typeId?}`. Configurations are `{configuration, platform}` (solution platforms keep their spaces, e.g. `Any CPU`).
- **`ProjectInfo`** `{path, name, state, error?, targetFrameworks, outputType?, items, references, diagnostics, restored}`. `state` is `loading`, `loaded`, `failed`, `untrusted`, `unsupported` (not SDK-style) or `missing`.
- **Items** `{kind, path, link?, dependentUpon?}`: the item type (`Compile`, `None`, `Content`, `EmbeddedResource`, `Folder`, `Page`… and anything in `AvailableItemName`), with `Visible="false"` items left out. Multi-targeted projects report the union of their per-framework items.
- **References** `{kind (framework | package | project | assembly), name, version?, path?, targetFramework, resolved}`. Package versions are the resolved ones from `obj/project.assets.json` when restored, else the requested ones with `resolved: false`.

- **`CompilerInputsInfo`** `{path, targetFramework, language (C# | VB | F#), state (ok | failed), error?, arguments, projectReferences, outputPath?, outputRefPath?}`: the compiler command line of a design-time build ([ADR 0005](adr/0005-editor-and-language-services.md)). `arguments` are `csc`/`vbc`/`fsc` arguments as MSBuild passes them, with source files relative to the project directory. References to another project's `outputPath` or `outputRefPath` stand for a project reference.

- **`BuildDiagnosticInfo`** `{severity (error | warning), code?, message, file?, line, column, endLine, endColumn, project?, helpLink?}`: positions are 1-based as MSBuild reports them (0 when unknown); `file` is absolute.
- **`RunInfo`** `{path, targetFramework?, outputType?, targetPath?, runCommand?, runArguments?, runWorkingDirectory?, launchSettings?}`: the SDK's `RunCommand`, `RunArguments` and `RunWorkingDirectory` properties (what `dotnet run` starts) and the path of `Properties/launchSettings.json` when it exists.

## Builds

Builds use a `BuildManager` of their own with MSBuild's out-of-process worker nodes only (no in-process node, no node reuse) and a fresh `ProjectCollection`. A build cancels the design-time builds in flight and queues them again for after it; projects whose assets file its restore changed are evaluated again.

## Design-time builds

After evaluation the host builds `Compile` and `CoreCompile` with `DesignTimeBuild`, `SkipCompilerExecution`, `ProvideCommandLineArgs` and `BuildingInsideVisualStudio` set (referenced projects are asked for their target path, not built), for each project's active target framework, and reports `project/compilerInputs`. Builds are queued and coalesced: changes during a batch are built right after it. They re-run after evaluations caused by project-file or folder changes, configuration and framework switches, and after a restore for projects whose assets file changed. Unrestored projects fail with MSBuild's error (for example NETSDK1004).

The tree (nesting, Dependencies grouping) is derived in the shell (`SolutionTree`), not sent by the host.

## Errors

| Code | Meaning |
|---|---|
| -33100 | No solution is open. |
| -33101 | The solution is not trusted. |
| -33102 | Invalid operation (e.g. no such solution folder; editing the solution of a project opened on its own). |
| -33103 | File system error (exists, not found, access denied); the message is meant for the user. |
