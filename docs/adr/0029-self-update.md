# 0029. Self-update

- **Status:** accepted (M32, 2026-10-11; the maintainer's go-ahead to complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer (D16, D19, D20 in the roadmap's decision register)

## Context

M31 (ADR 0028) publishes every release with Velopack installers and, for each platform, an update channel `<rid>-<kind>`: a feed (`releases.<channel>.json`) and full and delta packages. D16 settled the behaviour: Velopack, stable (`master`) and nightly (`develop`) channels, a silent background download applied at the next restart, a "Restart to update" status bar item, and a setting for notify-only or off; apt and dnf installs update through the package manager. D19: the installers stay unsigned for now, so the update feed carries its own signature. D20: while the repository is private, the feed is read with a token.

Velopack checks each package it downloads against the size and SHA-256 that the feed lists. It doesn't check the feed itself. Its `GithubSource` only sees the newest 10 releases, so a run of nightlies would hide the stable releases from it.

## Decision

**Where the code runs.**
- **The workbench** holds the policy, with no Velopack types (`DaedalusStudio.Workbench.Updates`):
  - `UpdateService`: the schedule, the modes, the channels and the state;
  - `InstallInfo`: the kind of install;
  - `ReleaseVersions`: SemVer 2 precedence;
  - the release stores (`GitHubReleaseStore`, `FolderReleaseStore`) and `FeedSignature`.
- **The shell** (`Infrastructure/Updates.cs`) adapts the stores to Velopack's `IUpdateSource` and drives `UpdateManager`. `ShellHost.Updates.cs` is the UI. The Velopack library is AOT-compatible, and the Native AOT publish is warning-free with it.

**Kinds of install** (`InstallInfo.KindOf`):

| Kind | How it is recognized | What updating means |
|---|---|---|
| Development | the version ends with `-dev` (`dotnet run`, local builds) | never checks |
| Self-updating | Velopack's locator says it installed the app (Setup.exe, .pkg, AppImage, winget) | checks, downloads in the background, applies at the next restart |
| System package | Linux, running from `/opt/daedalus-studio` (the .deb and .rpm) | says a release is out: "update it with apt or dnf" |
| Portable | anything else (the zip, the .app zip, the tar.gz) | says a release is out, with a link to its page |

**Behaviour** (D16).
- **The first check** runs once the window is interactive, off the UI thread. Later checks follow every four hours, and Help → Check for Updates checks right away.
- **`update.mode`:**
  - `auto` (the default) downloads in the background;
  - `notify` says a release is available and downloads only when asked;
  - `off` means no update traffic, though a check the user asks for still runs.
- **When a download finishes**, the status bar offers "Restart to update (X)", which is hidden while debugging or building.
  - Restarting closes the window the usual way, with the unsaved-files prompt, then restarts with the open solution, whose editor session comes back.
  - Closing the IDE any other way applies the update without a restart, so the next start runs the new version.
  - Velopack's `WaitExitThenApplyUpdates` does the applying once the process has exited.
- **`update.channel`:** empty means the installed build's own channel (Velopack's locator knows it), which a nightly install keeps.
  - Changing the channel checks the new one at once.
  - When the newest release there is older (nightly → stable), a confirmation comes first, and then that release is installed (`AllowVersionDowngrade`).
- **What's New:** the release notes of every version since the running one go into the state folder when a download finishes. The updated IDE opens them once, as a Markdown preview, and Help → What's New opens them again.
- **Pulled releases:** a release whose title ends with "(pulled)" is skipped, so clients that haven't installed it never see it. The next release fixes the ones that have.

**The feed and its signature** (D19).
- `GitHubReleaseStore` lists the repository's releases itself (three pages of 100, newest first). A channel's feed comes from the newest release of its kind that carries it: `vX.Y.Z` for stable, `vX.Y.Z-nightly.…` for nightly. A package the feed names is found by its file name in any release, so delta chains across releases work.
- **Signature:** each `releases.<channel>.json` has a `.sig`, the base64 of an ECDSA P-256 / SHA-256 signature (DER) over the file's bytes. `eng/package.sh` makes it with `openssl` from the `FEED_SIGNING_KEY` secret (`DAEDALUS_FEED_KEY`).
  - The public key is built in (`FeedSignature.ProjectKey`, also `eng/packaging/update-feed-key.pub.pem`).
  - A feed without a valid signature, or with a package that has no SHA-256, is refused. The hashes in the signed feed are what Velopack checks every download against, so the signature covers the packages too.
  - The roadmap said ed25519. .NET 10 has no Ed25519, and ML-DSA isn't available on every platform yet; ECDSA P-256 is built in on all three OSes and safe under Native AOT.
- **Private repository** (D20): a token from `DAEDALUS_UPDATE_TOKEN`, or the GitHub CLI's (`gh auth token`). With a token, assets are read through the API; without one, through the public download URLs.
- **Tests and the update test:** `DAEDALUS_UPDATE_SOURCE=<folder>` reads feeds and packages from a folder. `DAEDALUS_UPDATE_TEST_KEY` is the key to trust for that folder only.

**Tests.**
- **Workbench:** the policy (modes, kinds, channels, the schedule, downgrades), SemVer precedence, the signature (tampered, foreign key, missing hashes) and the GitHub store (kinds, pulled releases, packages from older releases, the token and the public URLs), against an in-memory GitHub.
- **Headless UI:** the status bar item and the busy states, What's New, a portable build's notification, development builds, and the settings and Help menu.
- **`eng/test-update.sh`**, the end-to-end test, on Windows, Linux and macOS:
  1. it builds N and N+1 of the real installer, with a delta;
  2. it installs N as a user would;
  3. it checks that a tampered feed is refused (`--update-now` exits with 2);
  4. it updates through the signed feed and checks that N+1 runs.
- `--update-now` checks, downloads and has the update applied without a window. It exists for this test.

## Consequences

- An installed IDE talks to `api.github.com` and GitHub's download hosts every four hours unless `update.mode` is `off`. The call is listed with the other network calls.
- The feed signing key is a single point of trust. Losing it means a release with a new key that older clients refuse, so keep a copy outside GitHub (go-public checklist).
- Applying happens after exit, so a crash before exit leaves the download for the next run's check, which finds it in Velopack's packages folder.
- `TODO(issue)`:
  - wait for the last window when the single instance has several;
  - download at low network priority (Velopack's downloader has no priority);
  - a "skip this version" choice in notify mode.
