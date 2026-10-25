# 0013. Project property pages and solution management

- **Status:** accepted (M9 plan, 2026-10-09; the maintainer's standing go-ahead for M9–M16), amended 2026-10-16 (ADR 0070: the unloaded projects moved to `.codalus/state/<solution file name>/solution.json`)
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

M9b asks for the classic IDE's project property pages (Application, Build, Package, Code Analysis, Signing, Debug), each showing the evaluated value, **where it comes from** and its conditions, and writing the minimal change; a launch profile editor; project, assembly and analyzer references; and solution management: Configuration Manager, build dependencies and order, load and unload, solution filters (`.slnf`), `global.json` SDK selection and SDK and workload status. F# and VB pages must be able to reuse the machinery.

Constraints: MSBuild only runs in the build host (ADR 0004); the shell is Native AOT; edits keep the files' formatting; solution files are read and written with `Microsoft.VisualStudio.SolutionPersistence`.

## Options

**Property pages**
1. Hand-written views per page. Fast to start, but every language and page is new UI code, and provenance logic is repeated.
2. **A page schema** (pages, properties, editors, options) interpreted by one shell view, with a generic host API that reads any property with provenance and writes it with a condition scope.
3. The classic IDE's XAML rule files (CPS). They describe the same thing, but they ship with the classic IDE (not redistributable) and assume CPS.

**Load and unload**
1. Remove unloaded projects from the solution file. Changes a shared file for a personal preference.
2. **Per-user state** in `.daedalus/` (like `.vs/`), applied by the build host (no evaluation, no design-time builds) and by builds (a solution filter of the loaded projects).

## Decision

- **Property pages: option 2.** The schema lives in the shell (`ProjectPropertyPages`): page → sections → properties `{name, label, description, editor (text, bool, choice, list, number, file), options, perConfiguration}`. One view renders any page.
- **`projectProperties/get`** `{project, names, configuration?}` evaluates the project (with the active or the given configuration) and returns, per property: the evaluated value, the unevaluated text, the file and line that set it, its condition (the property's or its group's), and its origin: `project` (the project file), `import` (an imported file of the repository, such as `Directory.Build.props`), `sdk` (the SDK, NuGet or MSBuild), `global` (a global property) or `unset`.
- **`projectProperties/set`** `{project, name, value?, configuration?}` writes the **minimal change** to the project file:
  - an existing definition in the project file with the same scope is updated in place
  - otherwise the property goes into the first `PropertyGroup` of that scope (no condition, or `'$(Configuration)' == 'Release'` for one configuration), and that group is created after the last property group when it doesn't exist
  - `value` null removes the project file's definitions in that scope (back to the inherited or SDK value)
  - a property set in an imported file is overridden in the project file, never edited in the import
  - `TargetFramework`/`TargetFrameworks` are switched together by the shell (one framework ↔ several)
- **Launch profiles** are edited in the shell (`Properties/launchSettings.json` is plain JSON) with the comment-preserving JSON editor used for settings, so unknown properties and formatting survive.
- **References**: `references/add` and `references/remove` with `kind` `project`, `assembly` (`Reference` with `HintPath`) or `analyzer` (`Analyzer` item). Shared projects are deferred (`TODO(issue)`, low priority in the roadmap).
- **Configuration Manager**: `solution/configurations` returns the solution's build types and platforms and, per project, the mapping of each solution configuration to a project configuration and platform, and whether it builds; `solution/setProjectConfiguration`, `solution/addConfiguration` and `solution/removeConfiguration` edit the solution file through SolutionPersistence (`.sln` and `.slnx`).
- **Build dependencies**: `solution/dependencies` and `solution/setDependencies` edit the solution's project dependencies; the shell shows the build order derived from project references plus these dependencies.
- **Load and unload: option 2.** `.daedalus/solution.json` keeps the unloaded projects; `solution/open` takes them, `project/unload` and `project/reload` change them; unloaded projects have the state `unloaded`. With unloaded projects, builds build a generated solution filter of the loaded ones.
- **Solution filters**: opening a `.slnf` opens its solution with only the listed projects loaded; **Save As Solution Filter** writes the loaded projects to a `.slnf`.
- **SDKs**: `sdk/info` lists installed SDKs and workloads (`dotnet --list-sdks`, `dotnet workload list`), the `global.json` in effect and the SDK the host resolved; `sdk/setGlobalJson` writes `global.json` (`sdk.version`, `rollForward`) next to the solution. Since the SDK is chosen when the host starts, the shell restarts the build host afterwards.
- **Plugin API** (proposed, additive, API 0.2.2): `IWorkspace.GetPackagesAsync` and `IWorkspace.GetProjectPropertiesAsync`, read only.

## Consequences

- New pages and languages only add schema entries. Provenance comes from MSBuild itself, so `Directory.Build.props` hierarchies show correctly, though deep import chains show only the last definition.
- Property writes never touch imports or SDK files; overriding an imported value is always explicit in the project file.
- `.daedalus/solution.json` is per user and git-ignored, like the rest of `.daedalus/` state (F7). Since ADR 0070 it is `.codalus/state/<solution file name>/solution.json`, in a folder that ignores itself in Git; the old file is moved there on open.
- `TODO(issue)`: properties that aren't in the schema are reachable only by editing the project file (M26 adds a generic Properties window).
