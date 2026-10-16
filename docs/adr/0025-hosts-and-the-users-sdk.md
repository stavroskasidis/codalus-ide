# 0025. The hosts and the user's SDK

- **Status:** accepted (M30 plan, 2026-10-10; D22), amended 2026-10-16 for M30b ([see below](#amendment-2026-10-16-the-hosts-on-net-11)): the hosts target .NET 11 and still roll forward.
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

D22 makes Daedalus follow every new .NET release, starting with .NET 11. M30 asks the hosts to start with whatever SDK the user has, from the oldest one M33 supports to the newest.

The hosts (build, language, plugin, markup and interactive) are framework-dependent .NET 10 programs. The build host loads MSBuild from the user's SDK (ADR 0004), and an SDK's MSBuild needs that SDK's runtime or a newer one. Today:
- `Microsoft.Build.Locator` skips SDKs that are newer than the runtime it runs on. With the .NET 11 SDK installed, a `net11.0` project is evaluated by the .NET 10 SDK, which fails with NETSDK1045.
- The build host took the newest SDK it could load, not the one `dotnet` resolves for the solution's folder. That ignored `global.json`, unlike `dotnet build` in the same folder.
- With only a newer runtime installed (a machine with just .NET 11), the hosts don't start at all: the default roll-forward policy stays within .NET 10.

The roadmap names two options:
1. The hosts carry the runtime they target: one private runtime in the package, shared by all hosts.
2. The hosts roll forward onto a newer installed runtime.

## Decision

**Option 2: the hosts roll forward.** A private runtime fixes the second problem but not the first: a host on its own .NET 10 still can't load the .NET 11 SDK's MSBuild. Rolling forward always works, because every SDK installs its own runtime.

**Every host** has `RollForward=Major`. It runs on .NET 10 when that is installed, and otherwise on the next newer major version.

**The build host uses the SDK that `dotnet` resolves for the solution's folder**, `global.json` included: the same SDK as the command line.
- The shell checks before starting it. When an SDK newer than the hosts' .NET is installed, the shell runs `dotnet --version` in the solution's folder (about 100 ms, only on such machines).
- When the resolved SDK is newer, the shell starts the build host with `DOTNET_ROLL_FORWARD=LatestMajor`. For a preview or release candidate SDK, it also sets `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`, because rolling forward skips prerelease runtimes otherwise.
- The build host takes the resolved SDK. If it still can't load it (its runtime is missing, say), it falls back to the newest SDK it can load. It names the SDK the solution asked for in the SDK status and the Output window, so the problem is never silent.

**Generators from a newer SDK.** Analyzers and source generators that ship in the SDK (the Razor generator) are built against that SDK's compiler, which can be newer than the language host's Roslyn. The language host then loads the same folder from the newest other installed SDK whose assemblies it can load, and says so in the Language Services output. Without such an SDK, they don't load, and the output says why.

**Prerequisites.** A .NET runtime of the hosts' version or later, which any .NET 10 or later SDK brings. The installers (M31) declare this.

## Consequences

- A `net11.0` solution opens with the .NET 11 SDK, release candidates included, while Daedalus itself still runs on .NET 10.
- Design-time builds and the command line use the same SDK. A `global.json` that pins an older SDK now gets that SDK, where it used to get the newest one.
- The other hosts don't load SDK code, so they run on .NET 10 whenever it is installed. When Daedalus moves to .NET 11 (M30b), the hosts' target moves with it, and this check only matters for previews of the next version.
- Older SDKs' MSBuild runs on a newer runtime. M33 finds the oldest one that still loads and records it.
- The language host's Roslyn is separate from the SDK's compiler. The .NET 11 SDK's Razor generator references Roslyn 5.11, and nuget.org has 5.9 at most. Until the host catches up at .NET 11 GA (M30b):
  - Razor files get the Razor generator of an older installed SDK (10.0), also in `net11.0` projects. With only the .NET 11 SDK installed, they get no C# features.
  - C# 15 syntax shows errors in the editor.
- This matters beyond .NET 11 projects: once the .NET 11 SDK is installed, every solution without a `global.json` uses it. Without the fallback, installing it would have turned off Razor in .NET 10 projects too.

## Amendment (2026-10-16): the hosts on .NET 11

M30b moved Codalus onto .NET 11 on its second release candidate (the maintainer's decision of 2026-10-16), ahead of GA. The question was whether the hosts, now `net11.0` programs, should change how they start. **They don't: option 2 stands.**

- **Every host still has `RollForward=Major`.** It runs on .NET 11 and on any newer .NET. On a release candidate, the hosts' `runtimeconfig.json` asks for that release candidate (`11.0.0-rc.2.26504.105`): that one, a later one, or GA (11.0.0 and its patches) starts them; an earlier release candidate doesn't.
- **A private runtime still doesn't help.** It would let a machine with only .NET 10 start the hosts, but the build host must run on the runtime of the SDK it loads: a .NET 12 preview SDK needs the .NET 12 runtime, which a private .NET 11 doesn't have. It would also add tens of MB to every package. The SDK Codalus needs installs the runtime the hosts need.
- **Older SDKs.** The build host loads the MSBuild of the SDK a solution's `global.json` pins, such as 10.0.401, on .NET 11: an older SDK's MSBuild runs on a newer runtime, as M33 found on .NET 10 down to 2.1.200. `DotnetVersionsTests` pins the oldest installed SDK from 8.0 on (10.0.401 in the M30b run); the target framework matrix's run on .NET 11 is still to come.
- **The newer-SDK check** (`BuildHostRuntime`) now compares with 11, so it only acts for a .NET 12 preview SDK.
- **Prerequisites.** The .NET 11 SDK (release candidate 2 or later until GA), which brings the runtime. SDKs of older versions alone are no longer enough. Help → Check Environment (M37) says so when no runtime is new enough, and installs the .NET 11 SDK. The `.deb` and `.rpm` packages recommend `dotnet-sdk-11.0`, and the winget manifest depends on `Microsoft.DotNet.SDK.Preview` until GA.
- **The plugin API stays on .NET 10.** `Codalus.Plugins.Abstractions` and `Codalus.Plugins.Testing` target `net10.0`, as do the `codalus-plugin` template and the sample plugins: plugins built with the .NET 10 SDK keep building, and load into the .NET 11 plugin host.
- **The language host's Roslyn is the .NET 11 SDK's compiler** (5.12.0-1.26504.105 at RC2, from the .NET team's public `dotnet-tools` feed until it's on nuget.org). The .NET 11 SDK's Razor generator loads as it is, and C# 15 has no false errors. The fallback for generators built for a newer compiler stays, for the next .NET's previews.
- `TODO(issue)`: at GA, the GA SDK in `global.json` without `allowPrerelease`, Roslyn from nuget.org, and `Microsoft.DotNet.SDK.11` in the winget manifest.
