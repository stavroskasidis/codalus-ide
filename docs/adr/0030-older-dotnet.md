# 0030. Older .NET: .NET Core 2.0 to .NET 10

- **Status:** accepted (M33, 2026-10-11; D23)
- **Date:** 2026-10-11
- **Deciders:** maintainer (D23, decided 2026-10-10); the details below were decided during M33 and recorded here

## Context

D23 asks Daedalus to prove that projects of every .NET (Core) version from 2.0 to 10.0 work, with the current SDK, and that solutions whose `global.json` pins an older SDK open and build down to the oldest SDK the build host can load. ADR 0025 made the hosts roll forward onto any newer runtime and the build host take the SDK that `dotnet` resolves for the solution's folder.

Before deciding anything, M33 ran each framework and each older SDK through the product (the M33 checkpoint has the runs). With the current SDK, every target framework already opened, built, ran, debugged and tested. What failed:

1. **Older SDKs' builds hung.** The build host builds with MSBuild's out-of-process worker nodes (ADR 0006). The MSBuild of the .NET 5 SDK and older (MSBuild 16 and 15) starts its nodes but never connects to them from a host on .NET 10, and the build waits forever. Their node protocol serializes with `BinaryFormatter`, which .NET 9 removed; the same SDKs' design-time builds, which run in the host's own node, worked. The .NET 6 SDK (MSBuild 17.3) and later build out of process as before.
2. **Their builds also stopped on the first warning or error.** The build logger read `BuildErrorEventArgs.HelpLink`, which MSBuild 16.8 added: "an unexpected logger failure".
3. **Their restores failed.** `dotnet restore --nologo` reached the MSBuild of SDKs before 5.0, which rejects the switch (MSB1001).
4. **The 2.0 SDKs can't build at all.** Their compiler task starts `csc.exe` directly when MSBuild runs on a newer .NET, and that isn't an executable (Win32 error 193). The 2.1.2xx SDKs (MSBuild 15.7) and later run `dotnet csc.dll`. The 1.x SDKs are out of scope with `netcoreapp1.x`.
5. **A `global.json` that asked for a missing SDK was silent, or crashed the host.** `Microsoft.Build.Locator` either offered the newest SDK without saying so or threw, depending on how hostfxr reported it.
6. **The test process's own environment hid a problem:** `dotnet test` starts a test apphost with `DOTNET_ROOT_X64` pointing at its own `dotnet`, which apphosts of .NET 6 and later read before `DOTNET_ROOT`. A program started from such a process runs on that `dotnet`, whatever the solution uses.
7. **On Linux,** .NET Core 2.x to 3.1 stop at start on a current distribution ("Couldn't find a valid ICU package": they look for the ICU versions that existed when they shipped). `CLR_ICU_VERSION_OVERRIDE` lets them load the system's ICU. .NET Core 2.x to .NET 5 also need OpenSSL 1.0 or 1.1 for cryptography and HTTPS (.NET 6 added OpenSSL 3), which current distributions don't have: their programs fail when they hash or call HTTPS, and their SDKs can't restore from nuget.org.
8. **On Apple silicon,** .NET 5 and earlier have no Arm64 runtime; they run as x64 under Rosetta 2, from the x64 `dotnet` (`/usr/local/share/dotnet/x64`). A debugger debugs processes of its own architecture, and netcoredbg 3.2.0, our pinned release, dropped its macOS x64 build.
9. **Coverage:** Coverlet 10's VSTest collector is built for .NET 8 and doesn't load into older test hosts.
10. **Hot Reload** (`dotnet watch`) applies changes from .NET 6 on; before, it only restarts.

## Options

- **Older SDKs' builds:** (a) an out-of-process build that runs the SDK's own `dotnet msbuild` (a second build path, with its own output parsing and no structured diagnostics), (b) the build host's own node, as design-time builds already do, for MSBuild before 17, or (c) declare the floor at the .NET 6 SDK. (b) keeps one build path and moves the floor down to 2.1.200; its cost is that task assemblies load into the build host for those builds, which ADR 0006 avoided for file locks.
- **Linux on current distributions:** (a) only document what users need, (b) set invariant globalization for old runtimes, which changes how the program behaves, or (c) set `CLR_ICU_VERSION_OVERRIDE` to the system's ICU for runtimes that don't find it, unless the launch already chooses. (c) keeps globalization and is what users do by hand.
- **The x64 debugger on Apple silicon:** (a) record a gap, (b) download it on demand, (c) ship it. netcoredbg's last macOS x64 build is 3.1.3-1062 (about 3.5 MB); shipping it is the simplest that works offline.

## Decision

**What is validated (D23).** Every target framework from `netcoreapp2.0` to `net10.0`, and `netstandard2.0` and `2.1` libraries, with the current SDK: opening, the Dependencies node, the property pages, the editor's C# version, build, run, debug, tests, coverage, Hot Reload from .NET 6 and the package manager's versions. Solutions that pin older SDKs: opening and building.

**The SDK floor is 2.1.200** (MSBuild 15.7, the 2.1.2xx SDKs that build `netcoreapp2.0`). Below it, the build host takes the newest SDK it can load, as for an SDK it can't run (ADR 0025), and says which SDK the solution asked for and why (`sdkWarning`): in the Output window and the .NET SDKs panel. The newer SDK builds the older frameworks too.

**MSBuild before 17 builds in the build host's own node** (`SolutionBuilder.InProcess`: the .NET 5 SDK and older). `DAEDALUS_BUILD_IN_PROCESS=1` does the same for any SDK. Their `ResolveAssemblyReference` cache also needs `BinaryFormatter`, so it isn't saved (warnings MSB3088 and MSB3101): incremental builds re-resolve references.

**A `global.json` that asks for a missing SDK is named:** the build host reads `global.json` itself (`GlobalJsonSdk`, dotnet's roll-forward rules), uses the newest SDK, and warns that `dotnet build` fails in that folder.

**Programs start on the runtime the SDK would give them** (`RuntimeSelection`, for Run, Debug and the integrated terminal's plan):
- an apphost gets the SDK's root in `DOTNET_ROOT` (`DOTNET_ROOT_<ARCH>` from .NET 6) unless the variable is set, as `dotnet run` does;
- on Arm64, frameworks without an Arm64 runtime (macOS before .NET 6, Windows before .NET 5) run as x64: `dotnet exec` becomes the x64 `dotnet`, and an apphost gets the x64 root. The x64 root is `<root>/x64`, the registered location (`/etc/dotnet/install_location_x64`), or the default one; without it the launch fails with what to install;
- on Linux, .NET Core 2.x to 3.1 get `CLR_ICU_VERSION_OVERRIDE` with the system's ICU, unless the launch sets it or invariant globalization; the Run and Debug output says so. The build host does the same for the CLI of SDKs before 5.0 (restores, tools), and the .NET test plugin for test hosts.

**The x64 debugger ships on macOS:** netcoredbg 3.1.3-1062's macOS x64 build in `debugger/x64/` (`eng/get-netcoredbg.sh --x64`, `DAEDALUS_NETCOREDBG_X64`). `DebugService` uses it for a plan that runs as x64 and for any x64-only Mach-O program (the test host VSTest starts with the x64 `dotnet`).

**Tests on older frameworks** use the test SDK and adapter versions those frameworks support (VSTest from Microsoft.NET.Test.Sdk 15.9 to 18.x, xUnit 2.4 to 2.9) through the current SDK's `vstest.console`. The .NET test plugin also ships Coverlet's last netstandard2.0 collector (6.0.4, MIT) for test hosts before .NET 8, and runs the test hosts of .NET 5 and earlier as x64 on an Arm64 Mac.

**Hot Reload needs .NET 6:** for an older startup project, Start with Hot Reload is disabled and its menu item says "(needs .NET 6 or later)"; invoked anyway, it says why; with Hot Reload on by the setting, Ctrl+F5 runs without it and the output says so.

**The package manager offers compatible versions:** with a project's frameworks, each version says whether its dependency groups cover them, and the details open on the newest version that does. It is an estimate: packages without groups, or with a framework-less one, say nothing.

**The Dependencies node** shows the implicit `Microsoft.NETCore.App` and `NETStandard.Library` packages of .NET Core 2.x and .NET Standard under Frameworks, with their version.

**Keeping it checked:** `eng/get-runtimes.sh` installs the pinned runtimes and SDKs into one dotnet root (`.work/dotnet-compat`; on Apple silicon the x64 ones into `x64/`). `tests/DaedalusStudio.TargetFrameworks.Tests` points its own process at that root and runs `samples/TargetFrameworks` through the product. Without the root it skips; with `DAEDALUS_DOTNET_COMPAT` set it must be there. `eng/validate.sh` installs it, so WSL and Mac validation run the suite; hosted CI runs it only in the manual matrix (`ci.yml`'s `dispatch` jobs). `eng/compat-containers.sh` runs the programs in containers of the distributions .NET Core 2.x to .NET 5 supported, and on a current one to show what it lacks.

## Consequences

- Every target framework from .NET Core 2.0 works with the current SDK on Windows and Linux; on Linux, programs of .NET Core 2.x to .NET 5 that use cryptography or HTTPS need OpenSSL 1.x, and so do those SDKs' restores (documented in the user guide, with what to install).
- Older SDKs down to 2.1.200 open and build, those before .NET 6 in the build host's node. Builds with them don't persist the reference cache, and their task assemblies stay loaded in the host until the solution closes.
- The build host's `DOTNET_HOST_PATH` and the apphost's `DOTNET_ROOT` now follow the SDK in use, including a private one (`~/.dotnet`).
- The macOS package grows by the x64 netcoredbg. TODO(issue): attaching to an x64 process on Apple silicon still uses the Arm64 debugger, because a process's architecture isn't known from its id.
- The support table in the user guide is the contract. A new .NET release adds a row (the M30 checklist); dropping a framework from the matrix needs a decision.
- Worth revisiting when: netcoredbg ships a macOS x64 build again, or MSBuild 16's node protocol is needed for something (it isn't fixable from our side).
