# 0009. Packaging and release

- **Status:** accepted (M7, continuing autonomously toward the MVP); superseded in part by [ADR 0028](0028-release-cycle-and-installers.md) since M31 (2026-10-11): the packaging table, `release.yml` and the private-repository rules below describe M7, not what ships now (Velopack installers, `pipeline.yml`, self-update; roadmap D16–D21). The crash reports and the performance gates stand.
- **Date:** 2026-10-09
- **Deciders:** maintainer (goal: "keep going until MVP is reached")

## Context

The project brief's M7 asks for installers or packages per OS: MSI or winget, a signed-ready .dmg, and AppImage, .deb or Flatpak. It also asks for performance regression gates in CI, local opt-in crash reporting, an accessibility pass, user documentation, and the preparation for going public.

The application itself is:
- the shell (`daedalus`, Native AOT)
- three helper hosts: `buildhost/`, `lshost/` and `pluginhost/`. These are framework-dependent .NET apps; the .NET SDK is a prerequisite anyway, because MSBuild comes from it.
- the built-in plugins
- netcoredbg

## Decision

**One script, `eng/package.sh [version]`**, builds the package for the OS it runs on:
1. It publishes the shell for the machine's RID, stamped with the version and without symbols.
2. It adds netcoredbg (pinned and checksummed, through `eng/get-netcoredbg.sh`) as `debugger/`, plus `LICENSE`, `THIRD-PARTY-NOTICES.md` and the README.
3. It wraps the result per OS:

| OS | Packages | Notes |
|---|---|---|
| Windows | A portable `.zip` and **winget** manifests (`eng/packaging/winget`) for that zip | A zip/portable winget package needs no MSI tooling and no admin rights. `TODO(issue)`: an MSI, if users ask for one (WiX is MS-RL, OSI-approved). |
| macOS | `Daedalus Studio.app` (generated `Info.plist` and `.icns`) inside a `.dmg` made with `hdiutil` | Signed when `DAEDALUS_CODESIGN_IDENTITY` is set (hardened runtime). Notarization needs an Apple Developer account: `TODO(issue)`. |
| Linux | A `.tar.gz`, a **.deb** (when `dpkg-deb` is available: `/opt/daedalus-studio`, `/usr/bin/daedalus`, a desktop entry and an icon) and an **AppImage** (with `appimagetool` from the `PATH`, or the pinned, checksummed appimagetool 1.9.1 and type2 runtime that `eng/get-appimagetool.sh` downloads on x64) | A **Flatpak** manifest is provided (`eng/packaging/flatpak`) but not built: an IDE in a sandbox has to reach the host's SDK and git, which needs testing first. `TODO(issue)`. |

**`release.yml`** (manual) runs the script on Windows, macOS and Linux and uploads the packages as artifacts. Publishing a GitHub release from them is a manual step in the release checklist.

**Performance gates**: `benchmarks.yml` already fails on budget regressions (`compare` against earlier runs on the same runner OS, ADR 0002).
- While the repository is private, it runs on manual dispatch.
- Once the repository is public, it also runs on every push to `master`; job-level conditions switch this on, with no edit needed.
- Absolute budgets are checked on reference hardware before a release (`benchmarks check`).

**CI**: pushes and pull requests run the full 3-OS matrix once the repository is public (`github.event.repository.private` decides). While it is private, pushes stay Linux-only.

**Crash reports**: a local, opt-in setting (`diagnostics.crashReports`, off by default).
- A report is a text file in the logs folder: version, OS, runtime, exception and log tail.
- Helper-process crashes and recovered UI errors are reported too.
- The next start says a report was saved; Help → Open Crash Reports Folder shows them.
- Nothing is uploaded.

## Consequences

- The Windows package is portable rather than an installer. winget gives it an install and update path once the manifest is accepted.
- macOS builds are signed-ready but not notarized: Gatekeeper warns until a Developer ID and notarization are set up.
- Packages need the .NET SDK and git on the machine. The .deb recommends `dotnet-sdk-10.0` and depends on `git`; the winget manifest lists both as dependencies.
