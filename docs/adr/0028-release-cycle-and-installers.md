# 0028. Release cycle and installers

- **Status:** accepted (M31, 2026-10-11; the maintainer's go-ahead to complete every milestone). It replaces ADR 0009's packaging table; ADR 0009's crash reports and performance gates stand.
- **Date:** 2026-10-11
- **Deciders:** maintainer (D16–D21, D27 in the roadmap's decision register)

## Context

M7 shipped hand-run packages (ADR 0009): a portable zip, a `.dmg`, a `.tar.gz`, a `.deb` and an AppImage, built by `eng/package.sh` and uploaded by a manual `release.yml`. Publishing a release was a checklist step. M31 makes releases automatic and gives every platform a setup installer, with update channels that M32's self-update reads. The decisions were settled on 2026-10-10:
- D17: Velopack for the installers
- D18: an AppImage, plus `.deb` and `.rpm` from our own apt and dnf repositories, and the `.tar.gz`
- D19: no code signing for now; checksums and a signed update feed instead
- D20: while the repository is private, the workflows run on manual dispatch only
- D21: `develop` for daily work and nightlies, `master` for stable releases, the version in a file
- D27: Windows x64, macOS on Apple silicon, Linux x64 and Linux ARM64

## Decision

**Branches and versions** (D21).
- `develop` is the default branch, and day-to-day commits land there. `master` only receives merges from `develop` (each one a stable release) and hotfixes.
- `VERSION` holds the next release's version. `Directory.Build.props` reads it, and the shell's `VersionPrefix` comes from it. Local builds are `X.Y.Z-dev`.
- `eng/DaedalusStudio.Release` (`daedalus-release`) is the one place for the rules, with unit tests:
  - `version stable|nightly|dev`: `X.Y.Z`, `X.Y.Z-nightly.YYYYMMDD.N` (the run number) or `X.Y.Z-dev`. A nightly sorts before its stable release (SemVer 2 precedence, which Velopack follows).
  - `check-tag`: a stable release fails when `vX.Y.Z` exists.
  - `bump --after X.Y.Z`: after a release, `develop` moves to the next minor version, unless it is already further (a hotfix).
  - `notes`: release notes from the commits since the previous release of the same kind, grouped by the milestone their subject names, with links to its checkpoint summaries. A hand-written `CHANGELOG.md` section for the version goes first.

**Installers** (D17, D18). `eng/package.sh <version> <kind>` builds one platform's packages, locally or in CI:

| Platform | Packages | Notes |
|---|---|---|
| Windows x64 | Velopack `Setup.exe`; the portable zip; winget manifests | Per user in `%LocalAppData%\DaedalusStudio`, no administrator rights, a Start menu entry and an uninstall entry. The `daedalus` command: see below. |
| macOS arm64 | Velopack `.pkg`; a zip of `Daedalus Studio.app` | The `.app` is built by vpk from the publish folder, with our `Info.plist` (bundle versions are the numeric `X.Y.Z`). The `.pkg` replaces the `.dmg`. |
| Linux x64 and arm64 | Velopack AppImage; `.deb`; `.rpm`; `.tar.gz` | The AppImage updates itself (M32). The `.deb` and `.rpm` (`/opt/daedalus-studio`, `/usr/bin/daedalus`) update with the system. |

- **Update channels** are `<rid>-<kind>`, for example `win-x64-stable` or `linux-arm64-nightly`: Velopack needs one channel per platform. Each release carries every platform's `releases.<channel>.json` feed and full packages, plus delta packages against the previous release of the channel.
- **The shell's hook.** `Installation.Run()` calls `VelopackApp.Build()…Run()` first thing in `Main`. On Windows Velopack's programs start the app with `--veloapp-*` arguments while installing, updating and uninstalling; `Run()` handles those and exits. Anything Velopack didn't install carries on at once. Applying a downloaded update at startup is off: M32 decides when.
- **The `daedalus` command on Windows.** Velopack's launcher (the root `daedalus.exe`) returns at once and passes no output or exit code, which `daedalus --wait` and `daedalus merge` need. The install and update hooks write `bin\daedalus.cmd`, which runs `current\daedalus.exe` and waits for it, and add `bin` to the user's PATH; the uninstall hook takes it out. The PATH is edited in the registry with its value kind kept (`Environment.SetEnvironmentVariable` would turn an expandable PATH into a plain string), then broadcast.
- **The `.deb` and `.rpm` versions** turn the prerelease dash into a tilde (`0.2.0~nightly.20261011.1`), so a nightly sorts before its release in both formats.
- **Prerequisites.** The installers don't install the .NET SDK or git; the `.deb` and `.rpm` declare them (git required, the SDK recommended), and winget lists them as dependencies.

**Workflows** (D20).
- `packages.yml` (reusable): the platform matrix builds the packages, installs them the way users would, runs the smoke test on the installed app, uninstalls them and checks that nothing is left (`eng/smoke-test.sh --packages`). Linux ARM64 joins when the repository is public: GitHub's ARM64 Linux runners are free for public repositories only.
- `nightly.yml` runs from `develop` at night when it changed since the last nightly, and on manual dispatch. It publishes a prerelease and keeps the newest 14.
- `release.yml` runs on every push to `master` and on manual dispatch. It publishes `vX.Y.Z` as the latest release, then merges `master` into `develop` (a hotfix) and moves `develop`'s `VERSION` past the release.
- Both publish through `eng/publish-release.sh`: every platform's files, one `SHA256SUMS.txt`, the winget manifests (stable) and the generated notes.
- Both have a dry run (the dispatch default): build and test everything, publish nothing.
- While the repository is private, the schedule and the push trigger do nothing (a job condition on `github.event.repository.private`), so they switch on by themselves when it goes public.

**apt and dnf repositories** (D18). `linux-repos.yml` rebuilds them after each release, from the newest stable release and the newest nightly:
- `eng/linux-repos.sh` writes the site: an apt archive with the suites `stable` and `nightly` (signed `InRelease` and `Release.gpg`), a dnf repository per channel (signed packages and a signed `repomd.xml`), `.repo` files, the public key and an index page with the instructions.
- The signing key is the project's package key. Its private half is the `PACKAGES_GPG_PRIVATE_KEY` secret (generated 2026-10-11, fingerprint `765B8B725B3FD42A8EAA508F59C7590683C2B9B3`); the public half is `eng/packaging/linux/daedalus-studio-packages.asc`.
- `eng/test-linux-repos.sh` installs from the site in Ubuntu and Fedora containers through `file:` URLs, with the signatures checked, then removes the package.
- The site holds only the newest package of each channel and architecture, which keeps it far below Pages' size limit. Older versions stay on the releases page.
- GitHub Pages needs a public repository on the free plan, so this job runs once the repository is public.

**Signing** (D19). The installers aren't code-signed. Every release has `SHA256SUMS.txt`, and M32 signs the update feed. The macOS signing step waits behind `DAEDALUS_CODESIGN_IDENTITY` (and a notary profile); Windows signing is a Velopack `--signParams`/Trusted Signing option to add then.

## Consequences

- A release is a dispatch (while private) or a merge into `master` (once public); nothing is uploaded by hand.
- The installed Windows app runs from `current\`, which Velopack replaces on every update: nothing may be written next to the executable. User data was already in the platform folders (ADR 0001's `AppPaths`).
- The `daedalus` command from a terminal on Windows goes through `cmd`, which gives a GUI program no output handles to capture: `daedalus --version` prints to the console window, not to a pipe. Waiting and the exit code work, which is what `--wait` and `merge` need.
- The previous dmg and appimagetool are gone; vpk brings its own AppImage runtime (THIRD-PARTY-NOTICES).
- `TODO(issue)`: Flatpak (the manifest stays in `eng/packaging/flatpak`); Windows ARM64 and Intel Mac builds (D27); code signing when the repository goes public (D19); the winget submission to `microsoft/winget-pkgs` after the first public release.
