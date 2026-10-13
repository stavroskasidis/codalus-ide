# Shell ↔ build host protocol

The build host (`daedalus-buildhost`, [ADR 0004](adr/0004-project-system.md)) owns MSBuild: solution files, project evaluation, file watching, restore and project-file edits. The shell starts one host per open solution (`--solution-dir <dir>`, so the solution's `global.json` picks the SDK) and talks to it over **stdio** with **JSON-RPC 2.0 and LSP framing**, like the other hosts. Contracts are in `src/DaedalusStudio.Projects.Protocol`; the shell side is `SolutionService` in `src/DaedalusStudio.Workbench/Projects`.

**Protocol version 0.1.** Parameters are single objects with camelCase names. Paths are absolute file system paths.

## Lifecycle

1. `initialize` `{processId, clientName, protocolVersion}` → `{serverName, serverVersion, protocolVersion, msbuildVersion, sdkPath, sdkWarning?}`.
   - The host loads the SDK that `dotnet` resolves for `--solution-dir`, `global.json` included ([ADR 0025](adr/0025-hosts-and-the-users-sdk.md)).
   - The shell starts the host with `DOTNET_ROLL_FORWARD=LatestMajor` when that SDK belongs to a newer .NET than the host's, so the host can load it. It adds `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` for a preview SDK.
   - `sdkWarning` says why the resolved SDK couldn't be loaded and which one is used instead: its runtime is missing, it is below the floor (the .NET SDK 2.1.200, [ADR 0030](adr/0030-older-dotnet.md)), or `global.json` asks for an SDK that isn't installed. The newest SDK the host can load stands in.
   - The MSBuild of SDKs before 6.0 (MSBuild 16 and older) builds in the host's own node: its worker nodes don't connect to a host on a newer .NET (ADR 0030).
2. `solution/open` `{path, trusted, configuration?, platform?, restore, activeFrameworks?}` → `SolutionInfo`, read from the solution file only. A `.csproj` (or `.fsproj`, `.vbproj`) opened on its own becomes an in-memory solution with format `project`.
3. When trusted, projects are evaluated in parallel and each arrives as a `project/evaluated` notification, with `solution/evaluationProgress` `{evaluated, total, completed}` after each one. Design-time builds follow (below). With `restore`, a restore follows; afterwards only projects whose assets file changed get a new design-time build.
4. `shutdown` / `exit`. If the host crashes, the shell restarts it and repeats `initialize` and `solution/open` with the user's latest choices (trust, configuration, active frameworks), without restoring again.

## Requests (shell → host)

| Method | Params | Result | Notes |
|---|---|---|---|
| `solution/open` | `OpenSolutionParams` | `SolutionInfo` | Untrusted: structure only, no evaluation, no restore, no watching (a folder is still watched). `path` may be a folder (folder mode, [below](#project-items-show-all-files-folder-mode-and-item-templates)). |
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
| `build/run` | `{target: build \| rebuild \| clean \| pack, project?, restore}` | `{succeeded, canceled, errors, warnings, elapsedMilliseconds}` | Builds the solution (`project` null) or a project with the active configuration (ADR 0006); restore first unless `restore` is false (never for clean). Completes after the build's last `build/output` and `build/diagnostic`. One at a time; trusted only. |
| `build/cancel` | `{}` | `{}` | Stops the running build; its `build/run` result has `canceled: true`. |
| `project/runInfo` | `{path, targetFramework?}` | `RunInfo` | How to start a project, for the active configuration and the given (or active) target framework; trusted only. |

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
| `solution/designTimeProgress` | `{built, total, completed}`; `completed` once nothing more is queued; `{0, 0, true}` when an evaluation leaves nothing to build (M34b); a completed progress also carries `cacheHits` and `cacheMisses` (`"Name: reason"` strings), the design-time build cache's results since the last completed one (M34c) |
| `build/output` | `{line}`: a line for the Build output channel |
| `build/diagnostic` | `BuildDiagnosticInfo` |
| `log` | `{level, message}` |

## Model

- **`SolutionInfo`** `{path, format (sln | slnx | project), folders, projects, configurations, active, trusted}`. Folders are `{id, name, parent?, files}` where `id` is the folder's path in the solution (`/src/libs/`) and `files` are solution items. Projects are `{path, name, parent?, typeId?}`. Configurations are `{configuration, platform}` (solution platforms keep their spaces, e.g. `Any CPU`).
- **`ProjectInfo`** `{path, name, state, error?, targetFrameworks, outputType?, items, references, diagnostics, restored, isTestProject, testPlatform?}`. `state` is `loading`, `loaded`, `failed`, `untrusted`, `unsupported` (not SDK-style) or `missing`. `isTestProject` is the `IsTestProject` property or `IsTestingPlatformApplication`; `testPlatform` is `mtp` when `IsTestingPlatformApplication` is true, `vstest` for other test projects (ADR 0010).
- **Items** `{kind, path, link?, dependentUpon?}`: the item type (`Compile`, `None`, `Content`, `EmbeddedResource`, `Folder`, `Page`… and anything in `AvailableItemName`), with `Visible="false"` items left out. Multi-targeted projects report the union of their per-framework items.
- **References** `{kind (framework | package | project | assembly), name, version?, path?, targetFramework, resolved}`. Package versions are the resolved ones from `obj/project.assets.json` when restored, else the requested ones with `resolved: false`.

- **`CompilerInputsInfo`** `{path, targetFramework, language (C# | VB | F#), state (ok | failed), error?, arguments, projectReferences, outputPath?, outputRefPath?}`: the compiler command line of a design-time build ([ADR 0005](adr/0005-editor-and-language-services.md)). `arguments` are `csc`/`vbc`/`fsc` arguments as MSBuild passes them, with source files relative to the project directory. References to another project's `outputPath` or `outputRefPath` stand for a project reference.

- **`BuildDiagnosticInfo`** `{severity (error | warning), code?, message, file?, line, column, endLine, endColumn, project?, helpLink?}`: positions are 1-based as MSBuild reports them (0 when unknown); `file` is absolute.
- **`RunInfo`** `{path, targetFramework?, outputType?, targetPath?, runCommand?, runArguments?, runWorkingDirectory?, launchSettings?}`: the SDK's `RunCommand`, `RunArguments` and `RunWorkingDirectory` properties (what `dotnet run` starts) and the path of `Properties/launchSettings.json` when it exists.

## Builds

Builds use a `BuildManager` of their own with MSBuild's out-of-process worker nodes only (no in-process node, no node reuse) and a fresh `ProjectCollection`. A build cancels the design-time builds in flight and queues them again for after it; projects whose assets file its restore changed are evaluated again.

## Design-time builds

After evaluation the host builds `Compile` and `CoreCompile` with `DesignTimeBuild`, `SkipCompilerExecution`, `ProvideCommandLineArgs` and `BuildingInsideVisualStudio` set (referenced projects are asked for their target path, not built), for each project's active target framework, and reports `project/compilerInputs`. Builds are queued and coalesced: changes during a batch are built right after it. They re-run after evaluations caused by project-file or folder changes, configuration and framework switches, and after a restore for projects whose assets file changed. Unrestored projects fail with MSBuild's error (for example NETSDK1004).

Speed (M34b): a batch of six projects or more adds MSBuild worker nodes (half the processors at most; `DAEDALUS_DESIGNTIME_NODES` sets the most; MSBuild before 17 stays in process), and asks only for the items and properties it reads (`ProvideSubsetOfStateAfterBuild`). Successful results are cached in each project's `obj/daedalus.designtime.json`, per configuration, platform and target framework, under a key of the project's evaluation fingerprint (SDK, global properties, every evaluated item, the project file, its imports and the assets file with their timestamps and sizes), the solution's properties and the fingerprints of every project it references; an entry is used only while its sources, analyzers and references outside the solution exist. `build/designTime` (asked for by the user) never reads the cache; `DAEDALUS_DESIGNTIME_CACHE=0` turns it off. Environment variables are not part of the key.

Restore on open (M34b) is skipped when every project's packages are up to date: NuGet's last restore succeeded (`project.nuget.cache`), every package file it expects exists, and the assets file (or `obj/daedalus.restore.stamp`, written after each restore the host runs) is newer than the project file and every import outside `obj/`. The host then reports `succeeded` without `running`. After a restore only projects whose assets file changed are evaluated again, and an evaluation that changes nothing sends no `project/evaluated`.

The tree (nesting, Dependencies grouping) is derived in the shell (`SolutionTree`), not sent by the host.

## Packages and .NET tools

NuGet package management ([ADR 0012](adr/0012-package-management.md)) uses the NuGet client libraries of the SDK the host loaded MSBuild from; all feed access happens in the host. Every request needs a trusted solution. Contracts are in `PackageMessages.cs`; the shell side is `PackageService` in `src/DaedalusStudio.Workbench/Packages`.

| Method | Params | Result | Notes |
|---|---|---|---|
| `packages/search` | `{query, prerelease, skip, take, source?, offline, refresh}` | `{packages, hasMore, errors}` | Every enabled source (or the one named), merged by id, highest version wins. `skip`/`take` page each source. A failing source is reported in `errors`; the others still answer. |
| `packages/details` | `{id, prerelease, version?, source?, offline, targetFrameworks?}` | `PackageDetails` | The chosen (or latest) version's metadata, every version for the picker, the README (global packages folder, the feed's `ReadmeUriTemplate`, or the `.nupkg` of a local source). |
| `packages/installed` | `{project?}` | `{projects: ProjectPackages[]}` | From each target framework's evaluation and `project.assets.json`. All loaded projects when `project` is null. |
| `packages/status` | `{packages: [{id, version?}], prerelease, offline, refresh}` | `{packages: [{id, latest?, deprecated, vulnerabilitySeverity, iconUrl?}], errors}` | The latest version across sources, and the deprecation and vulnerability badges of the given version. |
| `packages/why` | `{project, id}` | `{paths: [{framework, chain}]}` | Every chain (`id/version` steps) from a top-level dependency to the package. |
| `packages/install` | `{projects: [{project, framework?}], id, version, versionOverride, restore}` | `{updatedFiles}` | Adds or updates the reference as `dotnet add package` does. Under CPM the version goes to the central `PackageVersion` (or, with `versionOverride`, the project's `VersionOverride`). A restore follows. |
| `packages/uninstall` | `{projects, id, restore}` | `{updatedFiles}` | Removes the project's references, as `dotnet remove package` does; a central `PackageVersion` stays. |
| `packages/sources` | `{}` | `{sources, mappings, configFiles, globalPackagesFolder}` | Effective sources in priority order (`{name, url, enabled, isMachineWide, configFile?}`), package source mapping (`{source, patterns}`), and the config files (most specific first). |
| `packages/setSources` | `{sources, mappings}` | `{}` | Saves the whole list. Existing sources change in the file that defines them; new sources and mappings go to the most specific config file under the solution folder (created when there is none); machine-wide files are never written. |
| `tools/list` | `{}` | `{manifest?, local, global}` | The nearest `.config/dotnet-tools.json` and `dotnet tool list --global`. |
| `tools/run` | `{action: install \| update \| uninstall \| restore \| createManifest, id?, version?, global}` | `{succeeded, output}` | Runs `dotnet tool` (or `dotnet new tool-manifest`) in the solution folder. |

- **`PackageDetails`** `{id, version, versions: [{version, downloads?, deprecated, vulnerabilitySeverity, compatible?}], title?, description?, authors?, licenseExpression?, licenseUrl?, projectUrl?, readme?, requireLicenseAcceptance, published?, downloads?, tags?, dependencyGroups: [{targetFramework, dependencies: [{id, range}]}], deprecation?: {reasons, message?, alternateId?, alternateRange?}, vulnerabilities: [{severity, advisoryUrl}], source}`. Severities are 0 low, 1 moderate, 2 high, 3 critical, -1 none. When `packages/details` gets `targetFrameworks` (a project's, M33), each version's `compatible` says whether its dependency groups cover them (absent: the package lists no frameworks), and `version` is the newest version that does, unless one was asked for.
- **`ProjectPackages`** `{project, name, targetFrameworks, centralPackageManagement, centralPackagesFile?, restored, packages, transitive}`. `packages` are `{id, requested?, resolved?, frameworks, kind (direct | global | implicit), versionOverride?, centralVersion?, definedIn?}`; `transitive` are `{id, resolved, frameworks}`.

## Project properties and solution management

Property pages, references, configurations, dependencies, load/unload, solution filters and SDKs ([ADR 0013](adr/0013-project-properties-and-solution-management.md)). Contracts are in `ProjectManagementMessages.cs`; the shell side is `ProjectManagementService` in `src/DaedalusStudio.Workbench/Projects`.

| Method | Params | Result | Notes |
|---|---|---|---|
| `projectProperties/get` | `{project, names, configuration?}` | `{project, configuration, platform, configurations, properties}` | Evaluated with the active configuration, or `configuration`. Each property: `{name, value, unevaluated?, origin (project \| import \| sdk \| global \| unset), definedIn?, line, condition?, projectValue?}`; `projectValue` is the project file's value in the asked scope. |
| `projectProperties/set` | `{project, name, value?, configuration?}` | `{changed}` | The minimal edit: an existing definition in the scope is updated, else the property goes into the scope's first property group (created after the last one, with `'$(Configuration)' == 'X'` for one configuration); `value` null removes the project's definitions in the scope. Visual Studio's `'$(Configuration)\|$(Platform)' == 'X\|AnyCPU'` groups count as X's scope. Imports are never edited. |
| `references/add`, `references/remove` | `{project, kind (project \| assembly \| analyzer), path}` | `{}` | `Reference` with a relative `HintPath` (named after the assembly), `Analyzer`, or `ProjectReference`. |
| `solution/configurations` | `{}` | `{buildTypes, platforms, projects: [{project, name, projectConfigurations, projectPlatforms, entries: [{solutionConfiguration, solutionPlatform, configuration, platform, build}]}]}` | The Configuration Manager's data. |
| `solution/setProjectConfiguration` | `{project, solutionConfiguration, solutionPlatform, configuration?, platform?, build?}` | `{}` | Writes the solution's project configuration rules. |
| `solution/addConfiguration`, `solution/removeConfiguration` | `{dimension (buildType \| platform), name, copyFrom?}` | `{}` | A new one copies `copyFrom`'s project mappings. A solution keeps at least one of each. |
| `solution/dependencies` | `{}` | `{projects: [{project, dependsOn}]}` | Explicit build dependencies of the solution file. |
| `solution/setDependencies` | `{project, dependsOn}` | `{}` | |
| `project/unload`, `project/reload` | `{path}` | `{}` | An unloaded project is reported with state `unloaded` and isn't evaluated, design-time built or built (solution builds use a generated `.daedalus/build.slnf`). Kept in `.daedalus/solution.json`. |
| `solution/saveFilter` | `{path, projects?}` | `{}` | Writes a `.slnf` (default: the loaded projects). |
| `sdk/info` | `{}` | `{resolvedSdk?, resolvedVersion?, globalJson?, requestedVersion?, rollForward?, sdks: [{version, path}], workloads: [{id, version, source}], error?}` | `dotnet --list-sdks`, `dotnet workload list`, the `global.json` in effect and the SDK this host runs. |
| `sdk/setGlobalJson` | `{version?, rollForward?}` | `{}` | Writes `sdk.version`/`sdk.rollForward` of the `global.json` in effect (or one next to the solution); `version` null removes the pin. The shell reopens the solution so a new host picks the SDK. |

`solution/open` also accepts a `.slnf`: the filter's solution opens with only its projects loaded, and `SolutionInfo` gains `filterPath`.

## Project items, Show All Files, folder mode and item templates

The Properties window, Include and Exclude, links, moves between projects, Show All Files, folder mode and `dotnet new` item templates ([ADR 0033](adr/0033-project-items-templates-and-folder-mode.md)). Contracts are in `ItemMessages.cs`; the shell side is `ProjectItemService` in `src/DaedalusStudio.Workbench/Projects`. Item requests need a trusted solution; `fs/listTree` and `folder/discover` don't.

| Method | Params | Result | Notes |
|---|---|---|---|
| `items/get` | `{project, path}` | `ItemInfoResult` | The file as the Properties window shows it, evaluated with the active configuration (a multi-targeted project with its first framework). |
| `items/set` | `{project, path, itemType?, metadata?: {name: value \| null}}` | `{changed, updatedFiles}` | Changes the Build Action and metadata with the minimal edit: an `Update` item for a globbed file (metadata as child elements), `<Old Remove>` + `<New Include>` when a glob gives another type, existing metadata changed in place; a null value, or the value the project would have without its own definition, removes the definition. Imports are never edited. |
| `items/exclude` | `{project, paths}` | `{changed, updatedFiles}` | Removes the file's explicit `Include` and `Update` items, then adds `<Type Remove>` for each type a glob still includes it as. A folder gets `Remove="Folder\**"` for each default glob type (`Compile`, `Content` in web projects, `EmbeddedResource`, `None`). A linked file loses its link. |
| `items/include` | `{project, paths}` | `{changed, updatedFiles}` | Removes the `Remove` items that name the file or folder; whatever is still excluded gets an `Include` of the type the globs would give it. |
| `items/addExisting` | `{project, targetFolder, files, asLink}` | `FileOperationResult` | Copies the files into the folder and makes sure they are items; with `asLink`, adds `<Type Include="..\x" Link="Folder\x" />` instead. |
| `fs/listTree` | `{path, maxEntries?}` | `{entries: [{path, isDirectory}], truncated}` | Everything under a folder (Show All Files, the folder view) except `.git`, `.vs`, `.idea` and `node_modules`; at most 20,000 entries by default. |
| `folder/discover` | `{}` | `{projects}` | Folder mode: the project files under the folder (not under `bin`, `obj` or those folders). |
| `folder/setProjects` | `{projects}` | `{}` | Folder mode: the projects of the folder's in-memory solution, kept in `.daedalus/folder.json`. New ones are evaluated (and restored when the solution opened with `restore`). |
| `templates/instantiate` | `{template, name, outputDirectory, project?, language?, arguments?}` | `{createdPaths, output}` | Runs `dotnet new <template>` with `--project`; a project that isn't restored is restored first (project-capability constraints read its assets). `DAEDALUS_TEMPLATE_HIVE` adds `--debug:custom-hive`. |

| Notification | Params |
|---|---|
| `fs/changed` | `{directories}`: folders whose set of files changed (debounced), for Show All Files and the folder view |

- **`ItemInfoResult`** `{project, path, itemType?, isLinked, link?, fromGlob, elements: [{operation (include \| update \| remove), itemType, value, line, condition?}], metadata: [{name, value, projectValue?}], itemTypes}`. `itemType` is null for a file that isn't an item. `metadata` has `CopyToOutputDirectory`, `Generator`, `CustomToolNamespace`, `Link` (linked files) and every metadata of the item; `projectValue` is what the project file sets. `itemTypes` are the Build Action choices (`None`, `Compile`, `Content`, `EmbeddedResource`, `AdditionalFiles`, then `AvailableItemName`).
- **`fs/move` across projects:** when a file moves (or is copied) from one project's folder into another's, the source project's explicit items that name it are carried to the target project with `items/set`'s edit, and removed from the source on a move.
- **Folder mode:** `solution/open` with a folder path answers `SolutionInfo` with `format: folder`, `path` the folder, and the loaded projects. `build/run` of the folder builds its one loaded project, or a generated `.daedalus/folder.slnx`; `solution/addProject` and `solution/removeProject` change the loaded projects; editing a solution file fails.

## Build depth: options, the up-to-date check, batch builds, timings, build events and binary logs

[ADR 0034](adr/0034-build-depth.md). Contracts are in `BuildDepthMessages.cs` (`BuildDepthJsonContext`); the shell side is `BuildService` and `BuildDepthService` in `src/DaedalusStudio.Workbench/Build`. Builds, the up-to-date check and build events need a trusted solution.

- **`build/run`** takes `options?: BuildOptions` `{verbosity?, logFile?, logFileVerbosity?, maxParallelProjects, binaryLog?, upToDateCheck (default true), properties?}`. Verbosities are `quiet`, `minimal` (the default), `normal`, `detailed` and `diagnostic`; `properties` are extra global properties. Without options the build runs as in M4. `BuildResultInfo` gains `upToDate` (the fast up-to-date check found nothing to build, so MSBuild didn't run) and `binaryLog` (the log the build wrote).
- **The fast up-to-date check:** after a build with options, each built project (and the projects it references) records its inputs and outputs, with sizes and write times, in `obj/<configuration>/<framework>/daedalus.uptodate.json` (or forgets them after a failure or a clean). A Build with `upToDateCheck` skips MSBuild when every project in scope (the project and its references, or every project the solution's active configuration builds) is unchanged since. The output names the first out-of-date project and why.

| Method | Params | Result | Notes |
|---|---|---|---|
| `build/batch` | `{target, entries: [{project, configuration, platform}], options?}` | `{succeeded, canceled, errors, warnings, elapsedMilliseconds, entries: [{project, configuration, platform, succeeded, skipped, errors, warnings, elapsedMilliseconds}]}` | Each entry in turn, in one exclusive build (one restore per project); `skipped` is the up-to-date check's. `build/output`, `build/diagnostic` and `build/cancel` work as for `build/run`. A binary log option is ignored. |
| `build/upToDate` | `{project?}` | `{upToDate, projects: [{project, upToDate, reason}]}` | The check alone, for a project or the solution. |
| `build/timings` | `{}` | `BuildTimingsResult` | The last build's: `{totalMilliseconds, projects: [{id, project, name, targetFramework?, node, start, duration, succeeded, targets, dependsOn}], targets: [{project, name, duration}], tasks: [{name, duration, count}], criticalPath: [id], binaryLog?}`; times in milliseconds from the build's start. Fails before the first build that ran MSBuild. |
| `buildEvents/get` | `{project}` | `{project, preBuild, postBuild, runPostBuildEvent}` | The `PreBuild` and `PostBuild` targets' `Exec` commands, and `RunPostBuildEvent` (`OnBuildSuccess` when unset). |
| `buildEvents/set` | `{project, preBuild?, postBuild?, runPostBuildEvent?}` | as `buildEvents/get` | A null field is left alone; an empty command removes its target; `OnBuildSuccess` removes the property. |
| `binlog/open` | `{path}` | `{id, path, root, succeeded, durationMilliseconds, errors, warnings, nodes}` | Reads a `.binlog` with `MSBuild.StructuredLogger`. |
| `binlog/children` | `{id, node}` | `{children: [BinaryLogNode]}` | A node's children, a level at a time. |
| `binlog/search` | `{id, query, maxResults = 500}` | `{results: [{node, path: [id]}], truncated}` | Case-insensitive search of the nodes' text and diagnostics' files; `path` runs from the root to the node. |
| `binlog/close` | `{id}` | `{}` | |

- **`BinaryLogNode`** `{id, kind, text, hasChildren, durationMilliseconds?, file?, line, isError, isWarning}`: `kind` is the StructuredLogger type (`Build`, `Project`, `Target`, `Task`, `Message`, `Error`, `Warning`, `Property`, `Item`, `Folder`…).

## Entity Framework Core

EF Core ([ADR 0036](adr/0036-ef-core-integration.md)) runs the user's own `dotnet ef` as a child process of the host, in the startup project's folder, with `--json --prefix-output` (one at a time: dotnet-ef copies its own build host into the project's output on every run). The model view's data comes from `daedalus-efagent` (`buildhost/efagent/`, net8.0), started like dotnet-ef starts its `ef.dll`: `dotnet exec --depsfile <app>.deps.json --runtimeconfig <app>.runtimeconfig.json`, so it runs on the app's runtime with the app's EF Core. Every request needs a trusted solution; cancelling one kills its process tree. Contracts are in `EntityFrameworkMessages.cs`; the shell side is `EntityFrameworkService` in `src/DaedalusStudio.Workbench/Data`, which builds the startup project first and passes `noBuild`.

**`EfTarget`** `{project, startupProject?, context?, connection?, noBuild}`: `--project`, `--startup-project` (default: the project), `--context` (full or short name), `--connection` (default: the app's configuration; only for the commands that take one, and never echoed in `ef/output`), `--no-build`. The startup project's configuration in the active solution configuration and its active target framework are passed too.

| Method | Params | Result | Notes |
|---|---|---|---|
| `ef/status` | `{project, startupProject?}` | `{toolVersion?, toolSource? (local \| global), manifest?, manifestListsTool, efCoreVersion?, providers, hasDesignPackage, message?}` | `dotnet ef --version` in the startup project's folder, the tool manifest, and the projects' EF Core package references. |
| `ef/contexts` | `EfTarget` | `{contexts: [{name, fullName, assemblyQualifiedName?}]}` | `dbcontext list`. |
| `ef/contextInfo` | `EfTarget` | `{type, providerName?, databaseName?, dataSource?, options?}` | `dbcontext info`. |
| `ef/migrations` | `EfTarget` | `{migrations: [{id, name, applied?}], connectionError?}` | `migrations list`; when the database can't be reached, again with `--no-connect` (`applied` null) and the error. |
| `ef/addMigration` | `{target, name, outputDir?, namespace?}` | `{files}` | `migrations add`; the name must be a C# identifier. |
| `ef/removeMigration` | `{target, force}` | `{files}` | `migrations remove` (`--force` reverts an applied one first). |
| `ef/script` | `{target, from?, to?, idempotent, noTransactions}` | `{sql}` | `migrations script` into a temporary file. `from` newer than `to` gives the Down script; the shell shows the script from the last applied migration to the target before every update. |
| `ef/updateDatabase` | `{target, migration?}` | `{}` | `database update [migration]` (`0` reverts every migration). |
| `ef/bundle` | `{target, output, selfContained, runtime?, force}` | `{files}` | `migrations bundle`. |
| `ef/scaffold` | `{project, startupProject?, connection, provider, outputDir?, contextDir?, contextName?, namespace?, contextNamespace?, tables?, schemas?, dataAnnotations, noOnConfiguring, useDatabaseNames, noPluralize, force, noBuild}` | `{files}` | `dbcontext scaffold`; empty `tables` and `schemas` mean everything. |
| `ef/optimize` | `{target, outputDir?, namespace?}` | `{files}` | `dbcontext optimize`; `files` are the files written to the output folder. |
| `ef/model` | `EfTarget` | `{context, provider?, productVersion?, entities, debugView?}` | The design-time model from `daedalus-efagent`. Each entity: `{name, clrType?, table?, schema?, view?, isOwned, owner?, baseType?, isKeyless, properties: [{name, clrType?, nullable, isKey, isForeignKey, isShadow, column?, columnType?, maxLength?}], keys: [{properties, isPrimary}], foreignKeys: [{properties, principal, principalProperties, deleteBehavior?, isUnique, isRequired, isOwnership, dependentToPrincipal?, principalToDependent?}], navigations: [{name, target?, isCollection, isOnDependent, inverse?, isSkip}], indexes: [{properties, isUnique, name?}]}`. |

Notification (host → shell): **`ef/output`** `{level: info | warn | error | verbose, line}`, the running command's messages (the command line comes first as `> dotnet ef …`).

## Scaffolding and service references

Scaffolding ([ADR 0041](adr/0041-scaffolding-and-service-references.md)) runs the user's own `dotnet aspnet-codegenerator` (a local or global tool) as a child process of the host, in the project's folder, one at a time; every request needs a trusted solution, and cancelling one kills its process tree. Scaffolders that need no tool go through `templates/instantiate` (the SDK's `apicontroller`, `mvccontroller`, `page`, `view` and `razorcomponent` item templates). Service references are project edits: the items `dotnet-openapi` writes, and the packages that generate the code at build time. Contracts are in `WebToolsMessages.cs`; the shell side is `ScaffoldingService` and `ServiceReferenceService` in `src/DaedalusStudio.Workbench/Web`, which also copies or downloads the documents into the project.

| Method | Params | Result | Notes |
|---|---|---|---|
| `scaffold/status` | `{project}` | `{toolAvailable, toolVersion?, toolSource? (local \| global), manifest?, manifestListsTool, designPackageVersion?, dotnetVersion?, efProviders, efToolsVersion?, message?}` | `dotnet aspnet-codegenerator --help` in the project's folder, the tool manifest, and the project's declared package references (`Microsoft.VisualStudio.Web.CodeGeneration.Design`, the EF Core providers, `Microsoft.EntityFrameworkCore.Tools`). |
| `scaffold/install` | `{project, version?}` | as `scaffold/status` | `dotnet tool restore` when the manifest lists the tool, otherwise `dotnet tool install dotnet-aspnet-codegenerator` (a manifest is created first); adds the design package and, in an EF Core project, `Microsoft.EntityFrameworkCore.Tools` (at its EF Core version), then restores. The version is the newest stable one of the project's .NET major version, from the global packages folder first, then the sources. |
| `scaffold/run` | `{project, generator, arguments}` | `{created, modified, output}` | `dotnet aspnet-codegenerator <generator> <arguments>`; `generator` is `controller`, `view`, `razorpage`, `area`, `identity`, `minimalapi`, `blazor` or `blazor-identity`. The created and modified files come from comparing the project's folder (without `bin` and `obj`) before and after. The generator exits with 0 after some errors (a missing package): an error on its standard error with nothing written fails the request, with the line before its stack trace as the message. |
| `serviceReferences/list` | `{project}` | `{references: [{kind (openapi \| grpc), path, include, sourceUrl?, namespace?, className?, codeGenerator?, options?, grpcServices?, access?}], packages: [{id, version?}]}` | The project's own `OpenApiReference` and `Protobuf` items (evaluated), and the generating packages it references. |
| `serviceReferences/add` | `{project, kind, file, sourceUrl?, namespace?, className?, codeGenerator?, options?, grpcServices?, access?, restore, versions?}` | `{changedFiles, addedPackages}` | Adds the packages the project doesn't reference yet (OpenAPI: `Microsoft.Extensions.ApiDescription.Client`, `NSwag.ApiDescription.Client`, `Newtonsoft.Json`; a gRPC client: `Grpc.Net.ClientFactory`, `Google.Protobuf`, `Grpc.Tools`; a server: `Grpc.AspNetCore`), central package management included, the generators with `PrivateAssets="all"`; then the item, its `Include` relative to the project. `versions` pins package versions; otherwise each is the newest stable one in the global packages folder, else from the sources, else a known one. `addedPackages` are `Id Version`. |
| `serviceReferences/update` | as `add` | `{changedFiles, addedPackages: []}` | Rewrites the item's metadata; null metadata is removed. |
| `serviceReferences/remove` | `{project, kind, file, deleteFile}` | `{changedFiles, addedPackages: []}` | Removes the item (and an item group left empty); `deleteFile` sends the document to the trash. The packages stay. |

Notification (host → shell): **`scaffold/output`** `{level: info | error, line}`, the code generator's output (the command line comes first as `> dotnet aspnet-codegenerator …`).

## Errors

| Code | Meaning |
|---|---|
| -33100 | No solution is open. |
| -33101 | The solution is not trusted. |
| -33102 | Invalid operation (e.g. no such solution folder; editing the solution of a project opened on its own). |
| -33103 | File system error (exists, not found, access denied); the message is meant for the user. |
| -33104 | A tool the request runs isn't installed (`dotnet-ef` for `ef/*`): the shell offers to install or restore it. |
