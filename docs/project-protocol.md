# Shell ↔ build host protocol

The build host (`daedalus-buildhost`, [ADR 0004](adr/0004-project-system.md)) owns MSBuild: solution files, project evaluation, file watching, restore and project-file edits. The shell starts one host per open solution (`--solution-dir <dir>`, so the solution's `global.json` picks the SDK) and talks to it over **stdio** with **JSON-RPC 2.0 and LSP framing**, like the other hosts. Contracts are in `src/DaedalusStudio.Projects.Protocol`; the shell side is `SolutionService` in `src/DaedalusStudio.Workbench/Projects`.

**Protocol version 0.1.** Parameters are single objects with camelCase names. Paths are absolute file system paths.

## Lifecycle

1. `initialize` `{processId, clientName, protocolVersion}` → `{serverName, serverVersion, protocolVersion, msbuildVersion, sdkPath, sdkWarning?}`.
   - The host loads the SDK that `dotnet` resolves for `--solution-dir`, `global.json` included ([ADR 0025](adr/0025-hosts-and-the-users-sdk.md)).
   - The shell starts the host with `DOTNET_ROLL_FORWARD=LatestMajor` when that SDK belongs to a newer .NET than the host's, so the host can load it. It adds `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` for a preview SDK.
   - `sdkWarning` says why the resolved SDK couldn't be loaded and which one is used instead.
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
| `solution/designTimeProgress` | `{built, total, completed}`; `completed` once nothing more is queued |
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

The tree (nesting, Dependencies grouping) is derived in the shell (`SolutionTree`), not sent by the host.

## Packages and .NET tools

NuGet package management ([ADR 0012](adr/0012-package-management.md)) uses the NuGet client libraries of the SDK the host loaded MSBuild from; all feed access happens in the host. Every request needs a trusted solution. Contracts are in `PackageMessages.cs`; the shell side is `PackageService` in `src/DaedalusStudio.Workbench/Packages`.

| Method | Params | Result | Notes |
|---|---|---|---|
| `packages/search` | `{query, prerelease, skip, take, source?, offline, refresh}` | `{packages, hasMore, errors}` | Every enabled source (or the one named), merged by id, highest version wins. `skip`/`take` page each source. A failing source is reported in `errors`; the others still answer. |
| `packages/details` | `{id, prerelease, version?, source?, offline}` | `PackageDetails` | The chosen (or latest) version's metadata, every version for the picker, the README (global packages folder, the feed's `ReadmeUriTemplate`, or the `.nupkg` of a local source). |
| `packages/installed` | `{project?}` | `{projects: ProjectPackages[]}` | From each target framework's evaluation and `project.assets.json`. All loaded projects when `project` is null. |
| `packages/status` | `{packages: [{id, version?}], prerelease, offline, refresh}` | `{packages: [{id, latest?, deprecated, vulnerabilitySeverity, iconUrl?}], errors}` | The latest version across sources, and the deprecation and vulnerability badges of the given version. |
| `packages/why` | `{project, id}` | `{paths: [{framework, chain}]}` | Every chain (`id/version` steps) from a top-level dependency to the package. |
| `packages/install` | `{projects: [{project, framework?}], id, version, versionOverride, restore}` | `{updatedFiles}` | Adds or updates the reference as `dotnet add package` does. Under CPM the version goes to the central `PackageVersion` (or, with `versionOverride`, the project's `VersionOverride`). A restore follows. |
| `packages/uninstall` | `{projects, id, restore}` | `{updatedFiles}` | Removes the project's references, as `dotnet remove package` does; a central `PackageVersion` stays. |
| `packages/sources` | `{}` | `{sources, mappings, configFiles, globalPackagesFolder}` | Effective sources in priority order (`{name, url, enabled, isMachineWide, configFile?}`), package source mapping (`{source, patterns}`), and the config files (most specific first). |
| `packages/setSources` | `{sources, mappings}` | `{}` | Saves the whole list. Existing sources change in the file that defines them; new sources and mappings go to the most specific config file under the solution folder (created when there is none); machine-wide files are never written. |
| `tools/list` | `{}` | `{manifest?, local, global}` | The nearest `.config/dotnet-tools.json` and `dotnet tool list --global`. |
| `tools/run` | `{action: install \| update \| uninstall \| restore \| createManifest, id?, version?, global}` | `{succeeded, output}` | Runs `dotnet tool` (or `dotnet new tool-manifest`) in the solution folder. |

- **`PackageDetails`** `{id, version, versions: [{version, downloads?, deprecated, vulnerabilitySeverity}], title?, description?, authors?, licenseExpression?, licenseUrl?, projectUrl?, readme?, requireLicenseAcceptance, published?, downloads?, tags?, dependencyGroups: [{targetFramework, dependencies: [{id, range}]}], deprecation?: {reasons, message?, alternateId?, alternateRange?}, vulnerabilities: [{severity, advisoryUrl}], source}`. Severities are 0 low, 1 moderate, 2 high, 3 critical, -1 none.
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

## Errors

| Code | Meaning |
|---|---|
| -33100 | No solution is open. |
| -33101 | The solution is not trusted. |
| -33102 | Invalid operation (e.g. no such solution folder; editing the solution of a project opened on its own). |
| -33103 | File system error (exists, not found, access denied); the message is meant for the user. |
