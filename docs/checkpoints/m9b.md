# M9b checkpoint: project properties and solution management

Date: 2026-10-09. Decisions: [ADR 0013](../adr/0013-project-properties-and-solution-management.md). Continues [M9a](m9a.md); M9 is complete with this checkpoint.

## Built
- **Build host** (`projectProperties/*`, `references/*`, `solution/configurations` and friends, `project/unload|reload`, `solution/saveFilter`, `sdk/*`; [project-protocol.md](../project-protocol.md#project-properties-and-solution-management)):
  - properties with **provenance**: evaluated value, the text as written, the file and line that set it, its condition, and its origin (project file, an imported repository file such as `Directory.Build.props`, the SDK, a global property, or unset)
  - the **minimal edit** for every configuration or one: update in place, else the scope's first property group (created after the last one); reset removes the project's definitions; Visual Studio's `Configuration|Platform` groups count as the configuration's scope; imports are never edited
  - **references**: assembly (`Reference` + relative `HintPath`, as Visual Studio writes it), analyzer and project references
  - **Configuration Manager** data and edits through SolutionPersistence (`.sln` and `.slnx`): per-project configuration, platform and build flag per solution configuration and platform; new configurations and platforms copy an existing one's mappings
  - **build dependencies** of the solution file
  - **unload/reload**: unloaded projects aren't evaluated, design-time built or built (solution builds go through a generated `.daedalus/build.slnf`); remembered in `.daedalus/solution.json`
  - **solution filters**: opening a `.slnf` loads only its projects; Save As Solution Filter
  - **SDKs**: installed SDKs and workloads, the `global.json` in effect and the SDK in use; pinning one writes `global.json`
- **Shell**:
  - **Project properties** document, generated from a page schema (Application, Build, Package, Code Analysis, Signing): an editor per property (text, list, file, yes/no, choice), the origin as a link to the defining file, Reset, a configuration scope (per-configuration properties only), the target frameworks switch between `TargetFramework` and `TargetFrameworks`; **Alt+Enter** in the Solution Explorer
  - **Debug page**: the launch profile editor (new, rename, delete; launch kind, executable, arguments, working directory, environment variables, app URL, launch browser and URL), which rewrites only `profiles` and keeps unknown properties
  - **Reference Manager**, **Configuration Manager** (with Make Active), **Project Dependencies** with the build order, **.NET SDKs**
  - **Unload Project**/**Reload Project** (unloaded projects show dimmed), **Save As Solution Filter**, `.slnf` in Open Solution
- **Plugin API 0.2.2** (proposed, additive): `IWorkspace.GetPackagesAsync` and `IWorkspace.GetProjectPropertiesAsync` (`workspace/packages`, `workspace/properties`).
- **Tests**: 9 build-host integration tests (provenance, configuration-scoped evaluation, minimal edits with a byte-exact project file, reset, Visual Studio-style conditions, assembly/analyzer/project references, Configuration Manager edits and copied configurations, dependencies and build order, unload/reload with a build that skips the unloaded project, solution filters, SDK info and `global.json`), the proposed workspace queries, and 2 headless UI tests (property pages end to end with the launch profile editor; Reference Manager, Configuration Manager, unload/reload and SDKs). The smoke test gained `projects.properties`.
- **Benchmarks**: `properties_open_ms` (command → the Application page rendered with its values) in the `packages` scenario; proposed budget p50 ≤ 300 ms.
- **Docs**: [user guide: Solutions and projects](../user-guide/solutions.md#project-properties), [project-protocol.md](../project-protocol.md), [plugins.md](../plugins.md#022-m9), [plugin-protocol.md](../plugin-protocol.md), ADR 0013.

## Not done, and deviations
- **Shared projects** (`.shproj`/`.projitems`): not shown or edited (low priority in the roadmap). `TODO(issue)`.
- **Properties outside the schema** are reachable only by editing the project file; M26's Properties window adds a generic view. Deep import chains show only the definition that wins.
- **Configuration Manager**: new solution configurations map to existing project configurations; creating *project* configurations (`<Configurations>`) from it isn't offered. `TODO(issue)`.
- **Launch profile editor**: Docker, IIS Express and WSL launch kinds are left to the JSON (M15/M17/M21).
- **Workload management** (install, update, repair) is not offered; the list is read only. `TODO(issue)`.
- Visual Basic and F# property page schemas: the machinery is ready; their pages come with M35/M36 (numbered M24/M25 when this was written).

## Validation
- **Windows 11** (development machine, 2026-10-10): `dotnet test --solution DaedalusStudio.slnx`: 387 passed, 1 skipped (symbolic links need extra privileges). Native AOT publish without warnings, and `eng/smoke-test.sh` on it passed every step (`projects.properties`).
- **Linux** (WSL 2, Ubuntu 24.04, `eng/wsl-test.sh --smoke`, 2026-10-10): 388 of 388 tests passed. Native AOT publish and smoke test (WSLg) passed every step.
  - Failures found and fixed on the way: a race between the installed-package reload and the project's re-evaluation after an update (now reloaded on re-evaluation); `MSB0001` when saving project files; orphaned `dotnet restore` processes; the NuGet UI test hung once in an earlier run and hasn't since.
- **macOS: pending.** The Mac's Remote Control session (`daedalus-mac`) was offline during this checkpoint. The maintainer asked for a note so it can be run later: `bash eng/push-snapshot.sh`, then `bash eng/validate.sh --smoke` in a worktree on the Mac. The hosted macOS runner wasn't used (CI minutes).
