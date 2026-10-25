# 0012. Package management

- **Status:** accepted (M9 plan, 2026-10-09; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

M9 asks for the classic IDE's NuGet Package Manager: Browse, Installed, Updates and Consolidate, at project and solution level; package details with dependencies, licence, README, vulnerabilities and deprecation; licence acceptance; transitive packages with "why is this here"; Central Package Management (CPM); a package sources UI with source mapping; credential providers; offline mode. Edits must match what `dotnet add package` and `dotnet remove package` write.

Constraints:
- The shell is Native AOT and must not do network or MSBuild work (ADR 0004). The build host already owns MSBuild, restore and project-file edits.
- NuGet's client libraries (`NuGet.Protocol`, `NuGet.Configuration`, `NuGet.Versioning`, `NuGet.Packaging`, `NuGet.Commands`; Apache-2.0) **ship inside every .NET SDK**, next to MSBuild. The SDK's own `NuGetSdkResolver` runs inside our MSBuild evaluations and loads them.
- Rich plugin UI (F3, decision D2) is still open, so the manager can't be a plugin on a webview surface yet.

## Options

**Where NuGet runs**
1. **In the build host, using the SDK's NuGet assemblies.** Reference the packages for compilation only (`ExcludeAssets="runtime"`, like `Microsoft.Build`); `MSBuildLocator` resolves them from the SDK folder at run time. One copy of NuGet per process, the same version that restore uses, so the same `NuGet.Config` semantics, credential plugins and caches. We compile against an old NuGet (6.11, the SDK 8.0.4xx line that also matches our MSBuild 17.11 reference) and run on whatever newer one the SDK has.
2. In the build host with our own NuGet copies. Two NuGet versions in one process: the SDK resolver would bind to ours. Rejected.
3. A new "package host". A third .NET process for something the build host already has loaded. Rejected.

**How edits are made**
1. Run `dotnet add package --no-restore`. Exactly the CLI's output, but one process per edit (≈ 1 s), no `VersionOverride`, and no batch edits across projects.
2. **Edit through MSBuild's construction model (`ProjectRootElement`)** with the same calls the CLI makes (`AddItemGroup`, `AddItem`, `AddMetadata(expressAsAttribute: true)`, and removing an item group once it is empty). Golden tests compare the result with `dotnet add package` / `dotnet remove package` on copies of the fixtures.

## Decision

- **NuGet in the build host, from the SDK** (option 1 above). All network access is there: cancellable, through NuGet's own HTTP stack (system proxy, `NuGet.Config` proxy settings, `HttpSourceCacheContext` caching), and with `nuget.offline` only local sources (folders and the global packages folder) are queried.
- **Edits through `ProjectRootElement`** (option 2), then `dotnet restore` of the edited project (the existing restore path), whose progress and errors the manager shows.
- **CPM:** a project with `ManagePackageVersionsCentrally` gets a version-less `PackageReference`; the version goes into the `PackageVersion` of the `Directory.Packages.props` that MSBuild evaluated (`DirectoryPackagesPropsPath`). Updating an existing central version changes it for every project, and the manager says which projects that affects. "Override in this project" writes `VersionOverride` instead. `GlobalPackageReference` items are listed as installed everywhere and are read-only in the per-project view.
- **Protocol:** `packages/*` requests on the build host connection (`docs/project-protocol.md`): `search`, `details`, `installed`, `status`, `why`, `install`, `uninstall`, `sources`, `setSources`. Search fans out to every enabled source (or one), merges by id (highest version wins) and pages per source.
- **Credentials:** NuGet's cross-platform credential-provider plugin protocol (the providers NuGet discovers, such as the Azure Artifacts provider), non-interactive from the host; a source that needs interactive sign-in reports that, and the user signs in with the provider's own device flow (`dotnet restore --interactive` in the terminal). We don't bundle any provider.
- **UI:** a built-in shell document ("NuGet: Project" or "NuGet: Solution"), not a plugin, because D2 is open. Package Sources is a second document. README Markdown uses the M1 renderer.
- **Licences:** a package with `requireLicenseAcceptance` asks before install, listing the licence expression or link.

## Consequences

- The NuGet version is the SDK's, so behaviour follows the user's SDK. A NuGet API that changes between 6.11 and the SDK's version would fail at run time; the build host catches `MissingMethodException`/`TypeLoadException` per request and reports "this SDK's NuGet is not supported" instead of crashing. Integration tests run on the pinned SDK on three OSes.
- Golden tests pin our edits to the CLI's; when the CLI changes its output, the tests show it.
- Vulnerability and deprecation data come from each source's metadata (search results and registrations, as nuget.org publishes them); sources without them show no badges.
- The SDK's NuGet (7.x) doesn't search plain-HTTP feeds, even with `allowInsecureConnections` (`dotnet package search` behaves the same); their metadata and restore still work. Real feeds are HTTPS.
- Interactive credential prompts inside the IDE (device-code UI): done on 2026-10-15 as `packages/signIn` (see the roadmap's change log).
- When D2 lands, the manager can move to a plugin; the `packages/*` protocol doesn't change.
