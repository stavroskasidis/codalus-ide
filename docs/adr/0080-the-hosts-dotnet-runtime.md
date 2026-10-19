# 0080. The hosts' .NET runtime: checked before they start and before an update

- **Status:** proposed (M49, 2026-10-18), implemented on `claude/m49-host-runtime`; the maintainer accepts it, with D35, by merging the pull request
- **Date:** 2026-10-18
- **Deciders:** maintainer
- **Amends:** [ADR 0025](0025-hosts-and-the-users-sdk.md) (the hosts' prerequisite is now checked and offered), [ADR 0029](0029-self-update.md) (the signed feed names each release's runtime)

## Context

`develop` moved to .NET 11 RC2 (M30b, `d0aa2f13`). Every out-of-process host in the bundle (buildhost, datahost, debughost, diaghost, lshost, markuphost, pluginhost, replhost, t4host) is a framework-dependent app host whose `runtimeconfig.json` asks for `Microsoft.NETCore.App 11.0.0-rc.2.26504.105` with `rollForward` Major. The shell is Native AOT and needs no runtime.

On the maintainer's Mac, the self-updater installed `0.4.0-nightly.20261018.224` over a working 0.3.0:
- `/usr/local/share/dotnet` had the runtimes 8.0.20, 9.0.9 and 10.0.0–10.0.9, and `~/.dotnet` had 10.0.12 and 11.0.0-rc.1. The hosts used `/usr/local/share/dotnet`.
- No root had a runtime that rc.2 rolls forward to: an earlier release candidate doesn't count. Every host exited at once. hostfxr printed a clear message to standard error ("You must install or update .NET to run this application. Framework: 'Microsoft.NETCore.App', version '11.0.0-rc.2.26504.105' (arm64) … found 8.0.20 … 10.0.9"), but nobody read it.
- The user saw "[codalus.welcome] failed: The connection was lost." and nothing worked: no plugins, builds, language services or debugging.
- Check Environment's .NET row (M30b) did compare `dotnet --list-runtimes` with the hosts' version, but with plain version order rather than the host's rules. It was one notification among the per-plugin failures, and it read like an SDK problem ("Codalus IDE uses another SDK for now").
- Nothing stopped the update: the feed didn't say what the release needs.

The maintainer asked for four things:
1. a runtime check before a host starts, by the host's own rules;
2. one actionable notification that installs the runtime and restarts the hosts;
3. a host's standard error in place of "The connection was lost", without restart loops;
4. an update that can't leave the IDE in this state, decided in an ADR.

## Decision

### 1. Each host's runtime is checked before it starts

- **Where:** `LocalProcessLauncher` checks before `Process.Start` (`HostRuntimeCheck`, `Codalus.Rpc`). It refuses to start a host whose runtime is missing and throws `HostRuntimeMissingException`, which names the framework, the version, the policy, the root and what's installed there.
  - The exception is a `JsonRpcConnectionLostException` (now unsealed), so code that already handled a lost host handles this too.
  - `ProcessLaunchInfo.CheckRuntime` turns the check off for one launch, and `CODALUS_SKIP_RUNTIME_CHECK=1` for every launch.
  - Every supervised process gets the check without a change, including M48's agents.
- **The rules are hostfxr's**, as a pure component, tested against the cases of dotnet/runtime's `framework-version-resolution.md` and the code of `fx_resolver.cpp` and `fx_reference.cpp`:
  - **Versions:** SemVer 2 (`FrameworkVersion`).
  - **Policies:** Disable, LatestPatch, Minor (the default), LatestMinor, Major and LatestMajor.
    - A version of another major counts only for Major and LatestMajor, and one of another minor only from Minor on.
    - The policy takes the lowest version that counts, or the highest for LatestMinor and LatestMajor. A release it takes then rolls to its major.minor's latest patch.
  - **Releases and pre-releases:**
    - A release reference looks at releases first, and at pre-releases only when no release fits.
    - A pre-release reference looks at both. So rc.2 runs on rc.2, a later release candidate, or 11.0.0 and later, but never on rc.1.
  - **Which policy applies**, in the host's order of precedence: the muxer's `--roll-forward`, then `DOTNET_ROLL_FORWARD`, then the framework's own `rollForward`, then `runtimeOptions.rollForward`.
  - **Prereleases preferred or not:** `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` drops the preference for releases.
  - **`--fx-version`** asks for that exact version.
  - **What counts as installed:** a version folder needs its `Microsoft.NETCore.App.deps.json`, and `DOTNET_DISABLE_RUNTIME_VERSIONS` removes versions.
- **The root is the one the host itself would use** (`DotnetRootResolver`):
  - For `dotnet app.dll`: the folder of the `dotnet` on the `PATH`, with its links resolved, when that folder has `host/fxr`. Homebrew's wrapper script and snap's launcher run the real muxer from elsewhere, so the check stands aside for them.
  - For an app host: `DOTNET_ROOT_<ARCH>`, `DOTNET_ROOT`, then the registered location (`/etc/dotnet/install_location[_<arch>]`, or Windows' registry), then the default one (`/usr/local/share/dotnet`, `/usr/share/dotnet`, `%ProgramFiles%\dotnet`; its `x64` folder for an x64 process on Arm64, which is the win-x64 build on Windows on Arm). A variable whose folder doesn't exist is passed over, as hostfxr does.
- **It is conservative.** Some programs aren't checked: one without a `runtimeconfig.json`, a self-contained app, a root that doesn't exist, and a setting the host itself would refuse. For them, starting the program tells, and item 3 makes that readable.

### 2. One notification, Install .NET, and the hosts again

- **The IDE's own requirement** is what its hosts' `runtimeconfig.json` files ask for (`IdeRuntime`). It has its own row in Check Environment: ".NET runtime for Codalus IDE", resolved under the hosts' root, with what each .NET folder has when it's missing. The SDK row is about SDKs again.
- **The root.** `FindSdkRoot` prefers a root that has both SDKs and that runtime. A `~/.dotnet` with rc.2 beats a `/usr/local/share/dotnet` with only .NET 10, even when the latter is on the `PATH`.
  - `EnvironmentCheck.ChooseDotnetRoot` decides what `UseUserDotnet` sets. A root that isn't the one in use becomes `DOTNET_ROOT` and goes on the `PATH`, as since M37.
  - When the chosen root is already in use, the app hosts (which don't look on the `PATH`) are pointed at it only when that helps: it has their runtime, or the root they'd use otherwise doesn't. A runtime-only install where the hosts look (a distribution's `dotnet-runtime-11.0` in `/usr/lib/dotnet`, Microsoft's runtime package in `/usr/local/share/dotnet`) keeps its hosts.
  - Pointing the hosts sets `DOTNET_ROOT`, and also `DOTNET_ROOT_<ARCH>` when that one is set elsewhere, since the host reads it first.
  - After an install of .NET, the root is chosen again: winget installs into Program Files, not where `DOTNET_ROOT` points.
- **One notification**, whatever reports the problem: the startup check, a solution that can't open, plugin hosts (`PluginManager.HostRuntimeMissing`, once per host group, with no per-plugin errors), a language host (`LanguageService.StartFailed`).
  - It shows once while it shows. Once it is answered or closed, a new failure (a solution opened later) shows it again, rather than nothing.
  - With no SDK at all, the missing SDK's notification stands for it, since its install brings the runtime.
  - A report that nothing shows (not one of the IDE's hosts) falls back to its caller's own error.
  - The text: "Codalus IDE needs the .NET 11.0.0-rc.2.26504.105 runtime, which isn't installed (found: 8.0.20, 9.0.9, 10.0.0–10.0.9 in /usr/local/share/dotnet; 10.0.12, 11.0.0-rc.1… in ~/.dotnet)".
  - Its actions: **Install .NET 11.0** and **Check Environment**.
  - A program that isn't one of the IDE's hosts (a designer on the app's runtime) isn't the IDE's runtime: its own caller reports it. The IDE's hosts are known by their names (`codalus-pluginhost`…), wherever they run from.
- **Install .NET** is M37's flow: the plan is shown first, then the SDK of the runtime's channel is installed (it brings the runtime) with `dotnet-install` into `~/.dotnet`, with no password, or with winget on Windows. The new root becomes `DOTNET_ROOT`.
- **Then `RestartHostsAsync` starts again what failed:** the plugins whose activation failed with their host (`RetryFailedHostsAsync`; the others of the group are just usable again), the language services that couldn't start (`RetryFailedAsync`), and the solution that couldn't open. The IDE doesn't restart. An install from an update's warning does this too, then checks the update again.

### 3. A host that exits says why, and isn't restarted in a loop

- **What it wrote.** `SupervisedProcess` keeps the first 50 lines of each start's standard error, and a ring of the last 20.
  - When the connection drops and the process has ended, pending and later requests fail with `HostExitedException`. It carries the exit code, the lines, and whether the host was still starting.
  - A failed start quotes its first lines: "plugin host 'builtin' exited during startup with exit code 150: You must install or update .NET…". A crash of a running host quotes its last ones: the hosts log to standard error, so the first lines are its startup log.
  - The hook is `JsonRpcConnection.ExplainLoss`. It runs once, when reading stops or a write finds the peer gone, and waits briefly for the exit and the end of standard error.
  - The lines also go to the host's Output channel, as before, followed by a summary line.
- **No restart after an exit during startup**, which lasts until `Initialize` ends, however it ends: a host whose handshake failed but kept running is a running host. Without `Initialize`, an exit counts as one during startup only if it has a failure code and standard error output, comes before the first message, and comes within two seconds. The decision is made once, before the pending requests fail, so the end of `Initialize` can't change it.
  - Starting such a host again would fail the same way. `SupervisionPolicy.RestartAfterStartupExit` brings back the old behaviour.
  - The plugin manager reports such a host once per group.
- **Unchanged:** crashes after startup restart as before, and `FinalExitCodes` stays.

### 4. Updates: the runtime in the signed feed, compared before download

**Chosen: option A, the runtime requirement in the signed feed.**
- `eng/package.sh` runs `codalus-release feed-runtime` before it signs `releases.<channel>.json`. That adds `CodalusFrameworks` (name, version, rollForward, from the hosts' `runtimeconfig.json` files) to the release's entries. The signature covers it, and Velopack ignores the property.
- The IDE reads it into `UpdateOffer.Requires` and checks it with `EnvironmentCheck.RuntimeStatusFor` under the roots the updated IDE could give its hosts: their own root, and the roots with SDKs. A runtime-only folder that isn't the hosts' doesn't count, since nothing would point the hosts at it.
- On Windows, Install .NET installs the winget package of the runtime wanted: the GA SDK for 11.0.0 even on a release candidate, and the preview package for a preview.
- When the runtime is missing, the state is `NeedsRuntime` and nothing is downloaded. A warning names the runtime, with **Install .NET 11.0** (which then checks again) and **Update Anyway**. The same applies to a channel switch, and to portable and system-package installs, which only notify.

**Rejected: option B, self-contained hosts.**
- A self-contained host carries its own runtime: about 70 MB, times nine hosts. Native AOT can't take their place, because the hosts load Roslyn, MSBuild, analyzers and plugins by reflection.
- The build host can't be self-contained anyway. It must run on the runtime of the SDK whose MSBuild it loads (ADR 0025): a .NET 12 preview SDK needs .NET 12.

**Rejected: option C, one runtime bundled in the .app and the installers, as the hosts' `DOTNET_ROOT`.**
- **Size:** about 70 MB on disk and 30 MB compressed in every package. `shell_zip_mb` is 91–130 MB, so that's a 25–33% increase against ADR 0002's +5% size gate, on every OS. Delta updates would hide it after the first install, not before.
- **What still needs the SDK:** the build host (above), and every build. The SDK brings the runtime, so the prerequisite stays.
- **Security fixes** for a bundled release-candidate runtime would ship only with IDE releases.
- **Linux packages:** the `.deb` and `.rpm` would carry a copy of what the distribution's `dotnet-runtime-11.0` provides. They already recommend `dotnet-sdk-11.0`.
- **Windows:** the winget manifest depends on the SDK package, which installs the runtime anyway.
- ADR 0025 already rejected a private runtime for the build host. A bundled runtime for the other hosts only would make two classes of host, and wouldn't avoid the SDK prerequisite.

**Why A:** it costs nothing in size, keeps one runtime for builds and hosts, and turns the failure into an offer to install, before the update and after it. The cost is that a nightly built on a newer release candidate asks for that release candidate once.

## Consequences

- **The Mac's case:**
  - **A broken install can update.** The shell runs on the broken nightly, since it needs no runtime, so the self-updater can install the first M49 nightly. That nightly names the runtime and offers it.
  - **Later updates are protected.** A nightly that needs a runtime that isn't installed isn't downloaded.
  - **Installs from before M49 can't tell.** They don't read `CodalusFrameworks`, so their next update installs as before. The first start after it then offers the runtime.
- **A wrong check is the risk.** If the check misjudges a machine, it blocks hosts that would have started. The resolution follows hostfxr's code and is tested against its documented cases, the check stands aside when it can't tell, and `CODALUS_SKIP_RUNTIME_CHECK=1` turns it off.
- **Behaviour changes in `SupervisedProcess`:**
  - A host that exits during startup is no longer restarted.
  - Errors are more specific subclasses of `JsonRpcConnectionLostException`.
  - Both changes stay compatible: the policy flag restores restarts, and existing catches still catch the subclasses.
- **Velopack and old entries.** Velopack rewrites the previous feed's entries at the next release and may drop their `CodalusFrameworks`. Only the newest entry is offered, so that's harmless.
- `TODO(issue)`:
  - frameworks referenced by other frameworks (ASP.NET Core's reference to `Microsoft.NETCore.App`) and the legacy `rollForwardOnNoCandidateFx`/`applyPatches` aren't followed (no host uses them);
  - a host started through a remote window's server isn't checked (the server is self-contained, and its hosts' runtime is the remote machine's);
  - the GitHub release page doesn't name the runtime yet (the feed does);
  - an installer-level check (the `.pkg`'s and the Setup.exe's) before a first install.
