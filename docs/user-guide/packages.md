# NuGet packages and .NET tools

Daedalus Studio manages NuGet packages the way Visual Studio's NuGet Package Manager does. It works in trusted solutions. Feeds are searched by the build host with the NuGet that ships in your .NET SDK, so it uses the same `NuGet.Config` files, credentials and caches as `dotnet restore`.

## The NuGet Package Manager

Open it for a project with **Manage NuGet Packages…** (the project's or its Dependencies node's context menu, or the Project menu), or for the whole solution with **Manage NuGet Packages for Solution…** (the solution's context menu). It opens as a tab.

| Tab | Shows |
|---|---|
| **Browse** | Packages from your sources. Type to search; press `Enter` to search at once. Choose a source or *All sources*, and tick **Include prerelease** for previews. |
| **Installed** | The project's top-level packages and, below them, its **transitive packages** (restored as dependencies of others). Select a transitive package to see **why it is there**: every chain of dependencies that brings it in. |
| **Updates** | Packages with a newer version. Tick the ones to update and click **Update**. |
| **Consolidate** | (Solution only) Packages used at different versions in different projects. |

The **details** pane on the right shows the selected package:
- the version picker (the installed, latest, deprecated and vulnerable versions are marked)
- **Install**, **Update** and **Uninstall** (at solution level, tick the projects first)
- description, authors, licence, publish date, downloads, project link and source
- dependencies per target framework
- deprecation (with the alternative package) and known vulnerabilities (with links to the advisories)
- the package's README

Packages that require **licence acceptance** ask before they are installed. After every change the edited projects are restored; progress and errors show at the top of the tab (**Show Output** opens View → Output → Restore).

The project file is edited exactly as `dotnet add package` and `dotnet remove package` would edit it, with its formatting kept.

### Central Package Management

When a project manages versions centrally (`ManagePackageVersionsCentrally` in `Directory.Packages.props`):
- installing adds a reference without a version to the project, and the version to `Directory.Packages.props`
- changing a version changes the central `PackageVersion`, which every project using the package follows. Daedalus Studio lists those projects and asks first; **Override in This Project** writes a `VersionOverride` instead (also available as a checkbox in the details)
- uninstalling removes the project's reference and keeps the central version, as the CLI does
- `GlobalPackageReference` packages are listed as *global* and are edited in `Directory.Packages.props`

### Offline

Setting **NuGet → Offline** limits the package manager to local sources (folders) and the global packages folder. Restore is not affected.

## Package sources

**Package Sources…** (Project menu, or the gear in the package manager) lists the sources of the solution's `NuGet.Config` files in priority order. You can:
- add, remove, rename, enable and disable sources, and change their order
- map package patterns to a source (**package source mapping**: `Contoso.*` restores only from that source; `*` matches everything)

Saving writes each change to the file that defines the source. New sources and mappings go into the solution folder's `nuget.config`, which is created when there is none, so your user-wide settings and other solutions don't change. Machine-wide sources are read-only.

**Private feeds:** credentials come from your `NuGet.Config` and from NuGet credential providers you installed (for example the Azure Artifacts credential provider). If a feed asks you to sign in interactively, run `dotnet restore --interactive` once in a terminal.

## Pack

**Pack** (the project's context menu, or Build → Pack Project) runs `dotnet pack` with the active configuration. The output goes to View → Output → Build, and a notification opens the folder with the new `.nupkg`.

## .NET tools

**.NET Tools…** (Project menu) lists:
- the **local tools** of the nearest `.config/dotnet-tools.json` (from the solution folder upwards)
- your **global tools**

You can install (`id` or `id@version`), update, uninstall and restore tools, create a manifest, and open the manifest to edit it.

Visual Studio's Package Manager Console is not reproduced: packages are managed here, Entity Framework commands come with the data tooling, and `dotnet` commands run in a terminal.
