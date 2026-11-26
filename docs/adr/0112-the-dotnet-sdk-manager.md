# 0112. The .NET SDK manager

- **Status:** accepted (2026-11-26; the maintainer's answers of 2026-11-26)
- **Date:** 2026-11-26
- **Deciders:** maintainer
- **Builds on:** [ADR 0025](0025-hosts-and-the-users-sdk.md) (the hosts and the user's SDK), [ADR 0080](0080-the-hosts-dotnet-runtime.md) (the hosts' runtime), M37 (Check Environment and its installs), M9b (Project → .NET SDKs…), [ADR 0030](0030-older-dotnet.md) (older .NET)

## Context

The maintainer, after Check Environment had installed the .NET 11 SDK with `dotnet-install.sh` into `~/.dotnet`, wanted the .NET 10 SDK next to it and didn't know how to add it properly. Codalus offers an SDK only when one is missing or a `global.json` asks for one (M37). Nothing in it installs another channel, says that an installed SDK has a newer patch or a security fix, or removes an old one. By hand that means knowing that SDKs sit side by side in one folder, running the script again with the right `--channel` and `--install-dir`, and deleting `sdk/<version>` and the runtimes it brought from `shared/` and `packs/` one by one.

What there is:

- **Project → .NET SDKs…** (M9b, `SdkViewModel`) shows the SDKs the build host sees, their support status, the workloads, and pins one in `global.json`. It needs an open solution.
- **Check Environment** (M37) finds `dotnet`, installs the SDK the IDE needs in the way that fits the machine (`InstallPlans.DotnetOptions`: `dotnet-install` into a folder of the user's, a distribution's package, the snap, Microsoft's `.pkg`, winget), and moves the IDE to the new .NET without a restart.
- **The support dates** (`DotnetSupport`) are pinned in the code from Microsoft's `releases-index.json`: no network.

## Decision

### Where it lives

**Tools → .NET SDKs…** (`dotnetSdks.show`), a document of its own, for the whole machine: it works with no solution open. Check Environment's .NET SDK row gets **Manage…**, which opens it. Project → .NET SDKs… keeps the solution's own view and its `global.json` pinning, and links to the manager.

### The prompts open it

The prompts that offer .NET (a solution's `global.json` asks for an SDK that isn't installed, no SDK at all, the IDE's own runtime missing) open the manager instead of installing directly (the maintainer's choice, over keeping the direct install with a Manage button beside it): **Install…** opens Tools → .NET SDKs with what's needed first in Available and selected, its reason in the row (the pinned SDK exactly; else the newest SDK of the channel the IDE needs), and the user installs it there.

- After the install the IDE checks again as Check Environment does (`RecheckEnvironmentAsync`): the hosts that couldn't start for want of their runtime start again. For a `global.json`'s SDK, a notification offers **Reload Solution**, since the build host chose another SDK when the solution opened.
- Without release metadata to name the channel's newest SDK (offline with no cache), the prompt installs as Check Environment does (M37's options), so a machine without the network is still unblocked. A pinned version needs no metadata.
- With no .NET at all, the manager's options are M37's order: the distribution's package on Linux or Microsoft's `.pkg` on macOS first, then `dotnet-install` into `~/.dotnet`; winget on Windows.
- Check Environment's rows and the first-run setup's Tools step keep their direct Install…; the .NET rows have Manage… beside it.

### Microsoft's release metadata

- The manager reads Microsoft's release metadata: `releases-index.json` (every channel: its newest release, runtime and SDK, support phase, end-of-support date) and the `releases.json` of each channel it shows in detail (every release: its date, whether it fixes security issues, its runtime, ASP.NET Core and Windows Desktop versions, and its SDKs). They come from `https://builds.dotnet.microsoft.com/dotnet/release-metadata/`.
- **When:** when the window opens, and on **Refresh**. They're cached for a day in the cache folder (`dotnet-releases/`), and a failed fetch falls back on the cache whatever its age, saying when it's from. Nothing checks in the background (the maintainer's choice, over a daily check with a notification).
- **Which channels' files:** the channels installed on the machine, and one the user opens in the Available list. A channel's `releases.json` is several MB; the index alone lists what can be installed.
- `CODALUS_DOTNET_RELEASES` points the manager at another location: a URL, or a folder with the same files (the tests and the smoke test).
- `DotnetSupport`'s pinned dates stay for New Project's labels and the SDK status, which must not wait for the network.

### What's installed, and who owns it

- **The .NET folders:** the one the IDE uses (`EnvironmentCheck.FindSdkRoot`), and every other folder where an installer puts .NET (`~/.dotnet`, `/usr/lib/dotnet`, `/usr/share/dotnet`, `/usr/lib64/dotnet`, `/usr/local/share/dotnet`, the snap, `%ProgramFiles%\dotnet`, `%LOCALAPPDATA%\Microsoft\dotnet`). Each lists its SDKs (`sdk/<version>`) and runtimes (`shared/<framework>/<version>`) from the folders themselves, without running `dotnet`. The folder the IDE uses is marked.
- **Who owns a folder** decides how it changes (the maintainer's choice, over managing only the user's folders and over moving everything into `~/.dotnet`):
  - **The user's** (in the home folder, or one the user can write to that no package manager owns): the IDE changes it directly, with no password: `dotnet-install` installs and updates, and removing deletes the folders.
  - **A package manager's** (a distribution's package, the snap, Microsoft's `.pkg` on macOS, winget or the installer on Windows): the IDE shows that tool's exact command and runs it in the integrated terminal after the user agrees, where `sudo` or Windows asks itself. Which package holds an SDK is asked when it's needed (`dpkg -S`, `rpm -qf`, `pacman -Qoq`). The `.pkg` has no uninstaller, so removing from `/usr/local/share/dotnet` is `sudo rm -rf` of the folders listed, shown first.

### Install

- **Available** lists, for each channel: its newest SDK, and its runtimes alone (.NET, ASP.NET Core; Windows Desktop on Windows), for running projects that target an older .NET (the maintainer's choice, over SDKs only). Previews and channels out of support show only with **Show previews** and **Show out of support**.
- An install goes into the folder the IDE uses, in that folder's way. When that's a package manager's and the package manager has no package of that version, `dotnet-install` into `~/.dotnet` is offered instead, with the note M37's options already have when the terminal's `dotnet` won't see it.
- After an install the IDE does what it does after M37's: reads the environment again, chooses the .NET folder again, checks again.

### Update

- An installed SDK's **feature band** (`10.0.1xx`, `10.0.4xx`) is the unit of updates (the maintainer's choice, over the channel's newest): **Update** installs the newest SDK of the same band (10.0.102 to 10.0.104). A `global.json` with the default roll-forward moves only within its band, so this keeps pinned solutions working. A newer band of the channel shows as its own row under Available.
- The row says when the newer patch fixes security issues (any release since the installed one with `security: true`), and when its channel is out of support.
- Once the newer patch is in, the manager offers to remove the old one, checked, unless a `global.json` of a recently opened solution pins that exact version.
- A runtime updates the same way, to the newest patch of its channel.

### Remove

- **Removing an SDK** removes `sdk/<version>`, and the runtimes only it brought, with their reference and app host packs (`shared/Microsoft.NETCore.App/<v>`, `packs/Microsoft.NETCore.App.Ref/<v>`, `packs/Microsoft.NETCore.App.Host.<rid>/<v>`, `templates/<v>`, and the same for ASP.NET Core and Windows Desktop), from that SDK's release in `releases.json` (the maintainer's choice, over the SDK alone and over a checkbox per runtime).
- **A runtime stays** when another SDK left in the folder brought the same version, when it's the newest patch of its channel in that folder (removing a channel's last SDK doesn't leave its projects unable to run), or when the IDE's own hosts would lose their runtime (`EnvironmentCheck.HasHostRuntime` on what would be left). A runtime can still be removed from the Runtimes list, unless the IDE's hosts run on it or an SDK left in the folder does (an SDK runs on its own channel's runtime, at least the one it brought); its packs and templates stay while an SDK there brought it.
- **Refused, with the reason:** the SDK the open solution builds with (the build host loaded it), and the last SDK of the folder the IDE uses when the IDE's hosts need it.
- **Remove Old Patches** (per channel) removes every SDK of the channel that has a newer one in its band, under the same guards.
- `host/fxr`, the workload manifests and the workloads' packs stay: `dotnet` uses the newest `hostfxr`, and `dotnet workload clean` removes the packs no SDK uses.
- **The confirmation lists every folder** (or the command), and nothing is deleted before the user agrees. A folder is deleted only when it's inside the .NET folder and named as the layout names it.

### Workloads

Workloads are per feature band. After installing an SDK of a band that has none, when an older band of the same channel had some (`metadata/workloads/<band>/InstalledWorkloads/`), the manager offers to install the same ones for the new band (`dotnet workload install …`, in the terminal). It never runs that on its own: a MAUI install is gigabytes.

### Other machines

The .NET in WSL and on a remote machine (Connect, ADR 0050) is managed from the same window, with a machine picker (the maintainer's choice). The remote server's protocol already has what it needs (`process/spawn`, `fs/readDirectory`, `fs/delete`, and `server/hello`'s OS and architecture), so no new messages; WSL goes through `wsl.exe` as `WslDotnet` does. This comes in a second pull request, on the same abstraction (`IDotnetMachine`); the first covers this machine. `TODO(issue)`: WSL and remote machines in the SDK manager.

## Consequences

- Adding another channel, updating a patch, and removing old SDKs and their runtimes are each one action, for every way .NET is installed, and nothing runs without the exact command or folder list shown first.
- The manager is the IDE's first feature that reads Microsoft's release metadata at run time. It sends nothing but the request, and only when the window opens or Refresh is pressed.
- The release metadata is the source of truth for which runtimes an SDK brought; an SDK not in it (a daily build) removes only `sdk/<version>`.
- Tests: `DotnetReleasesTests` (the index and a channel's file, recorded and trimmed; the cache and its fallback), `DotnetInstallationsTests` (folders and owners on described machines), `DotnetSdkPlannerTests` (bands, updates, security, the removal set and every guard, package commands), `DotnetSdkManagerShellTests` (the window, Manage…, confirmations, runs through stand-ins), and the smoke step `setup.dotnetSdks` on a local copy of the metadata.
