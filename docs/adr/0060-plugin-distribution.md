# 0060. Plugin distribution on NuGet

- **Status:** accepted (M22c)
- **Date:** 2026-10-13
- **Deciders:** maintainer (D5: NuGet is the transport)

## Context

D5 decided that plugins are distributed as NuGet packages, found and installed from a plugin browser in the IDE. M22 also asks for:
- updates, enabling and disabling per workspace, and a "bisect plugins" troubleshooting command;
- signature checks, a publisher identity, and declared permissions shown before install.

Constraints:
- Plugins install with no solution open, so the browser can't go through the build host, where NuGet.Protocol lives (ADR 0012).
- .NET plugins can't be sandboxed in-process (M22's scope says so). Permissions inform the user; they aren't enforced.
- Nothing installs or updates without the user's say, and tests never reach nuget.org.

## Decision

**The package.** A plugin is a NuGet package of type `CodalusPlugin` whose `plugin/` folder is the plugin's folder: its `codalus-plugin.json` and build output. The template's project packs that way (`dotnet pack`), and its id and version are the manifest's.

**The gallery** (`PluginGallery`, workbench `Plugins/`): a small NuGet v3 client on `HttpClient`, with no NuGet.Protocol in the shell.
- It reads the service index, then searches with `SearchQueryService` (`packageType=CodalusPlugin`). Versions come from `PackageBaseAddress`'s `index.json`, and downloads from its `.nupkg` addresses.
- Sources are the setting `plugins.sources`, nuget.org by default. Private feeds work when they need no credentials; authenticated feeds are a gap.

**Installing** (`PluginInstaller`):
- download the package to a staging folder;
- check its signature (below);
- read `plugin/codalus-plugin.json` from it: the id and version must match the package, and the engine must accept the running API;
- show the permissions;
- extract `plugin/` into `<data>/plugins/<id>/`.

A new plugin loads at once (`PluginManager.AddPlugins`). An update or an uninstall replaces or removes a folder whose assemblies a plugin host may hold open, so it is staged in `<data>/plugins/.pending.json` and applied at the next start, before plugins are discovered; the Plugins window says a restart applies it.

**Signatures.**
- `dotnet nuget verify --all` (the user's SDK) checks a package's signatures. nuget.org repository-signs every package, so a package from nuget.org must verify.
- The setting `plugins.requireSignature` (`nugetOrg` by default; `all` or `none`) says which sources must be signed.
- The publisher shown is the author signature's subject when there is one, else the package's `authors`.

**Permissions.**
- The manifest's `permissions` lists what the plugin does beyond its contributions: `network`, `processes`, `fileSystem` (outside the workspace) and `secrets`.
- They're shown before install and in the plugin's details, with a sentence saying they inform and are not enforced.
- A plugin that declares nothing is shown as "declares no permissions", not as safe.

**Updates.**
- At startup, at most once a day, and on demand, the installed gallery plugins are checked against their source.
- With `plugins.autoUpdate` (on by default) newer versions are downloaded, verified and staged for the next start, and the user is told. Otherwise the Plugins window lists them.

**Enabling per workspace.** `plugins.disabled` may be set in a workspace's settings as well. The two lists add up, and a change applies at the next start.

**Bisect** (`plugins.bisect`) finds the plugin behind a problem by halves:
- It turns half of the remaining third-party plugins off for the workspace and restarts.
- It asks whether the problem is still there, and repeats until one plugin is left, then offers to turn it off.
- The state is in `<state>/plugin-bisect.json`, so it survives restarts; built-in plugins are never bisected.

## Consequences

- Publishing needs the maintainer's nuget.org account and the `Codalus.*` prefix reservation (`docs/release/nuget-prefix-reservation.md`). Until then the browser works with any feed, and the tests use a mock NuGet server.
- `dotnet nuget verify` makes signature checks depend on the SDK, which a .NET IDE needs anyway. Without an SDK, packages from sources that must be signed don't install.
- The Components page, the offline layout and plugin performance attribution move to M22d.
