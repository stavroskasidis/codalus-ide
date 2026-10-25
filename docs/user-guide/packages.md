# NuGet packages and .NET tools

Codalus IDE manages NuGet packages the way the classic IDE's NuGet Package Manager does. It works in trusted solutions. Feeds are searched by the build host with the NuGet that ships in your .NET SDK, so it uses the same `NuGet.Config` files, credentials and caches as `dotnet restore`.

## The NuGet Package Manager

Open it for a project with **Manage NuGet Packages…** (the project's or its Dependencies node's context menu, or the Project menu), or for the whole solution with **Manage NuGet Packages for Solution…** (the solution's context menu). It opens as a tab.

| Tab | Shows |
|---|---|
| **Browse** | Packages from your sources. Type to search; press `Enter` to search at once. Choose a source or *All sources*, and tick **Include prerelease** for previews. |
| **Installed** | The project's top-level packages and, below them, its **transitive packages** (restored as dependencies of others). Select a transitive package to see **why it is there**: every chain of dependencies that brings it in. |
| **Updates** | Packages with a newer version. Tick the ones to update and click **Update**. |
| **Consolidate** | (Solution only) Packages used at different versions in different projects. |

Each package in the list shows its version as a badge: the installed version with a check (green), then an available update in the accent color (**↑ 1.1.0**); a package you haven't installed shows its latest version. **prerelease**, **deprecated** and **vulnerable** badges sit under the version.

### Verified owners

A blue badge with a check after a package's ID means **nuget.org reserved the ID's prefix for one of the package's owners** (nuget.org's [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation)): only they can publish packages whose IDs start with it. Its tooltip says so; screen readers read it as "verified owner". It vouches for who holds the prefix, not for the **Authors** shown, which are free text, and it stays on a deprecated or vulnerable version (the owner is still verified).

The badge shows only when the package can be nobody else's copy:
- it comes from nuget.org itself (`https://api.nuget.org`). A mirror, a private feed or a feed with nuget.org as its upstream never shows it, even when it passes nuget.org's mark on, so if you reach nuget.org only through such a feed you won't see the badge;
- nuget.org is the only source that could provide it: you chose nuget.org in the source box (or it is your only enabled source), or **package source mapping** maps the ID to nuget.org alone. In *All sources* with several sources and no mapping, restore could take the same ID and version from any of them, so no badge shows.

Browse shows it in the list and in the details. Installed, Updates and Consolidate show no badge in their lists; their details ask nuget.org once, beside the details, and show it in the heading when the answer comes. **Offline**, or when nuget.org doesn't answer, no badge shows.

The **details** pane on the right shows the selected package:
- the version picker (the installed, latest, deprecated and vulnerable versions are marked, and for a project the versions that don't support its target frameworks, from the package's dependency groups; the details open on the newest version that does)
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
- changing a version changes the central `PackageVersion`, which every project using the package follows. Codalus IDE lists those projects and asks first; **Override in This Project** writes a `VersionOverride` instead (also available as a checkbox in the details)
- uninstalling removes the project's reference and keeps the central version, as the CLI does
- `GlobalPackageReference` packages are listed as *global* and are edited in `Directory.Packages.props`

### Offline

Setting **NuGet → Offline** limits the package manager to local sources (folders) and the global packages folder. Restore is not affected.

## Package sources

**Package Sources…** (Project menu, or the gear in the package manager) lists the sources of the solution's `NuGet.Config` files in priority order. You can:
- add, remove, rename, enable and disable sources, and change their order
- map package patterns to a source (**package source mapping**: `Contoso.*` restores only from that source; `*` matches everything)

With package source mapping, the package manager's search and the details' versions leave out copies from sources an ID isn't mapped to, since restore would refuse them: with `Contoso.*` mapped to nuget.org, a local feed's `Contoso.Verified` build isn't offered.

Saving writes each change to the file that defines the source. New sources and mappings go into the solution folder's `nuget.config`, which is created when there is none, so your user-wide settings and other solutions don't change. Machine-wide sources are read-only.

**Private feeds:** credentials come from your `NuGet.Config` and from NuGet credential providers you installed (for example the Azure Artifacts credential provider).
- **Signing in:** when a feed refuses for want of a sign-in, the package manager shows **Sign In…** next to its error. You can also use Project → Packages and Tools → **Sign In to a Package Source…**.
- **Device codes:** the credential provider runs interactively. When it gives a device code, a notification shows it, and **Copy Code and Open Browser** takes you to the page.
- **After signing in:** the feed answers for the rest of the session. Most providers also keep the sign-in for later sessions and for restore.
- **Restore:** if a restore still can't reach the feed, run `dotnet restore --interactive` once in a terminal.

## Pack

**Pack** (the project's context menu, or Build → Pack Project) runs `dotnet pack` with the active configuration. The output goes to View → Output → Build, and a notification opens the folder with the new `.nupkg`.

## .NET tools

**.NET Tools…** (Project menu) lists:
- the **local tools** of the nearest `.config/dotnet-tools.json` (from the solution folder upwards)
- your **global tools**

You can install (`id` or `id@version`), update, uninstall and restore tools, create a manifest, and open the manifest to edit it.

The classic IDE's Package Manager Console is not reproduced: packages are managed here, Entity Framework commands come with the data tooling, and `dotnet` commands run in a terminal.
