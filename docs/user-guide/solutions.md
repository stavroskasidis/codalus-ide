# Solutions and projects

Daedalus Studio opens `.sln` and `.slnx` solutions and SDK-style projects (`.csproj`, and the structure of `.fsproj` and `.vbproj`). Legacy, non-SDK-style project files are shown but not loaded.

## Solution Explorer

The tree shows the solution, its solution folders, projects, project folders and files (nested files, like `Form.Designer.cs`, sit under their parents). Each project also has its **Dependencies**:
- frameworks
- packages
- projects
- assemblies
- analyzers

| To | Do |
|---|---|
| Open a file in a preview tab (replaced by the next preview) | Single-click it. |
| Keep a file open | Double-click it, or edit it. |
| Create a file or folder | Use the context menu. New C# files get the right namespace. |
| Rename | Press `F2`. Renaming a project folder updates the solution and project references. |
| Delete | Press `Delete`. Files go to the OS trash. |
| Move or copy | Cut, Copy and Paste, or drag and drop. |
| Change the solution | Add Existing Project, New Solution Folder, Remove from Solution. |
| Add a project reference | Add Project Reference on a project. Remove one by deleting it under Dependencies. |
| Make a project the one F5 and Ctrl+F5 start | **Set as Startup Project** (the startup project is bold). |

Daedalus Studio watches the files: changes made outside the IDE (a `git pull`, another editor) are picked up, and the affected projects are evaluated again.

## Configurations and target frameworks

- The status bar shows the solution configuration (`Debug | Any CPU`). Click it to switch. Projects are evaluated again with the configuration they map to.
- For a project that targets several frameworks, the editor's project-context picker (top of the editor) chooses which framework the language service uses.

## Restore

Packages are restored in the background when a trusted solution opens (setting **Projects → Restore on Open**). Project → Restore Packages restores again; the output is in View → Output → Restore. To add, update and remove packages, see [NuGet packages and .NET tools](packages.md).

## Project properties

**Properties** (a project's context menu, **Alt+Enter** in the Solution Explorer, or Project → Project Properties) opens the project's property pages:
- **Application**: target frameworks, output type, assembly name, default namespace, nullable, implicit usings, language version
- **Build**: conditional compilation symbols, platform target, optimization, warnings and errors, debug symbols
- **Package**: what `dotnet pack` writes (id, version, authors, license, README, icon, symbols)
- **Code Analysis**: analysis level and mode, code style enforcement
- **Signing**: strong naming
- **Debug**: the launch profiles of `Properties/launchSettings.json` (what F5 and Ctrl+F5 start): arguments, working directory, environment variables, app URL and browser

Each property shows **where its value comes from**: set in the project file (with the line), inherited from an imported file such as `Directory.Build.props`, or the SDK's default. Click it to open that file. Changing a value writes the smallest possible edit to the project file; **Reset** removes the project's value, so the inherited or default one applies again. Choose a configuration (Debug, Release) at the top to change per-configuration properties (symbols, optimization, debug symbols, platform target) for that configuration only.

## References

**Reference Manager…** (a project's context menu) adds and removes:
- project references (tick the projects; a filter narrows the list)
- assembly references (a `.dll`, written with a relative `HintPath`)
- analyzers (an analyzer or source generator `.dll`)

Packages are managed in the [NuGet Package Manager](packages.md).

## Configurations, dependencies and build order

- **Configuration Manager…** (Build menu, or the solution's context menu): for each solution configuration and platform, which configuration and platform of each project builds, and whether it builds at all. Add and remove solution configurations and platforms; **Make Active** switches to the shown one.
- **Project Dependencies…**: projects a project waits for, besides those it references, and the resulting build order.

## Unloading projects and solution filters

- **Unload Project** (a project's context menu) stops loading, analyzing and building it; it shows dimmed as *(unloaded)*. **Reload Project** brings it back. Unloaded projects are remembered per user in `.daedalus/solution.json`.
- **Save As Solution Filter…** (File menu, or the solution's context menu) writes the loaded projects to a `.slnf`. Opening a `.slnf` opens its solution with only those projects loaded.

## .NET SDKs

**.NET SDKs…** (Project menu) shows the SDK the solution builds with, the `global.json` that chose it, the installed SDKs and the installed workloads. **Use This SDK** pins the selected SDK in `global.json` (with the roll-forward policy you choose) and reloads the solution with it; **Remove Pin** takes the version out again.

## New projects and solutions

The built-in **Templates** plugin creates projects and solutions with `dotnet new` (File → New Project or New Solution, or the command palette).
