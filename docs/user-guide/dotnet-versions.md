# Older .NET versions

Codalus IDE opens, builds, runs, debugs and tests projects of every .NET (Core) version from .NET Core 2.0 to .NET 10, and .NET Standard 2.0 and 2.1 libraries, with the current .NET SDK (.NET 11's since Codalus moved to .NET 11; `net11.0` itself is the current framework, covered everywhere else in this guide). `netcoreapp1.x` is out of scope; .NET Framework has a page of its own, [.NET Framework](net-framework.md) (summarized below). This page says what works where, and what each gap needs.

## With the current SDK

Install the current .NET SDK and the runtimes your projects target (the SDK builds every framework; a program runs on its own runtime). The tables below come from the test suite (`tests/Codalus.TargetFrameworks.Tests` on `samples/TargetFrameworks`), run on each operating system with the .NET 10 SDK; its run with the .NET 11 SDK is pending.

- **Open:** the project loads and evaluates; the Dependencies node shows the framework (the `Microsoft.NETCore.App` package before .NET Core 3.0) and the project and package references; the property pages read the framework and its C# version.
- **Editor:** references and the C# version each framework defaults to (7.3 for .NET Core 2.x and .NET Standard 2.0, 8.0 for 3.x and .NET Standard 2.1, 9.0 for .NET 5, then one per release), so the editor reports what the compiler reports.
- **Tests:** VSTest with the test SDK and adapters that framework supports; **Coverage:** Coverlet (its netstandard2.0 collector before .NET 8).
- **NuGet:** the package manager marks the versions that don't support the project's frameworks and opens on the newest one that does.

### Windows x64

| Framework | Open | Editor | Build | Run | Debug | Tests | Coverage | Hot Reload | NuGet |
|---|---|---|---|---|---|---|---|---|---|
| `netcoreapp2.0` – `netcoreapp3.1` | yes | yes | yes | yes | yes | yes | yes | no [1] | yes |
| `net5.0` | yes | yes | yes | yes | yes | yes | yes | no [1] | yes |
| `net6.0` – `net10.0` | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| `netstandard2.0`, `2.1` | yes | yes | yes | – | – | – | – | – | yes |

ASP.NET Core 2.1 (the web host and a `Startup` class) and 3.1 (the generic host) run and answer requests.

### Linux x64

Tested on Ubuntu 24.04 (WSL), and in containers of Ubuntu 18.04 and 20.04 for the runtimes of their time (`eng/compat-containers.sh`).

| Framework | Open | Editor | Build | Run | Debug | Tests | Coverage | Hot Reload | NuGet |
|---|---|---|---|---|---|---|---|---|---|
| `netcoreapp2.0` – `netcoreapp2.2` | yes | yes | yes | yes [2] [3] | yes [2] | yes [2] [3] | yes [3] | no [1] | yes |
| `netcoreapp3.0`, `netcoreapp3.1` | yes | yes | yes | yes [2] [3] | yes [2] | yes [2] [3] | yes [3] | no [1] | yes |
| `net5.0` | yes | yes | yes | yes [3] | yes | yes [3] | yes [3] | no [1] | yes |
| `net6.0` – `net10.0` | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| `netstandard2.0`, `2.1` | yes | yes | yes | – | – | – | – | – | yes |

### macOS on Apple silicon

.NET 6 and later run natively. .NET 5 and earlier have no Arm64 runtime: they run as x64 programs under Rosetta 2, on the x64 runtime (`/usr/local/share/dotnet/x64`, where the x64 installers put it). Codalus starts them with the x64 `dotnet` and debugs them with the x64 debugger it ships (`debugger/x64/`).

| Framework | Open | Editor | Build | Run | Debug | Tests | Coverage | Hot Reload | NuGet |
|---|---|---|---|---|---|---|---|---|---|
| `netcoreapp2.0` – `net5.0` | yes | yes | yes | yes [4] | yes [4] [5] | yes [4] | yes [4] | no [1] | yes |
| `net6.0` – `net10.0` | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| `netstandard2.0`, `2.1` | yes | yes | yes | – | – | – | – | – | yes |

Validated on an Apple M4 (macOS 27.0.1). Every framework from `netcoreapp2.0` to `net10.0` evaluates, builds, runs and debugs to a breakpoint, the ones before `net6.0` as x64 under Rosetta. The ASP.NET Core 2.1 and 3.1 apps answer, and tests run with coverage on every framework's test host. The SDKs before 6.0 have no Arm64 build, so a `global.json` that pins one counts as not installed (note 6).

### .NET Framework

[.NET Framework](net-framework.md) projects (M43a), SDK-style and non-SDK-style, with the current SDK. Building needs no targeting pack: the reference assemblies come from NuGet.

| Framework | OS | Open | Editor | Build | Run | Debug | Tests | Hot Reload | NuGet |
|---|---|---|---|---|---|---|---|---|---|
| `net462` – `net481` | Windows | yes | yes (C# 7.3) | yes | yes | not yet [8] | not yet [8] | no [1] | yes (`packages.config` too) |
| `net462` – `net481` | Linux, macOS | yes | yes (C# 7.3) | yes | Windows only | Windows only | Windows only | no [1] | yes (`packages.config` too) |
| `net20` – `net461` | every OS | yes [9] | yes [9] | yes [9] | Windows only, where that .NET Framework is installed | – | – | no [1] | yes |

### Notes

1. **Hot Reload needs .NET 6.** `dotnet watch` applies changes to programs of .NET 6 and later. For an older startup project, Start with Hot Reload is disabled and its menu item says "(needs .NET 6 or later)"; with Hot Reload on by the setting, Ctrl+F5 runs the program without it and says so in its output. A limit of .NET itself.
2. **ICU on current Linux distributions.** .NET Core 2.x to 3.1 look for the ICU versions that existed when they shipped, and stop at start on a newer distribution ("Couldn't find a valid ICU package"). Codalus sets `CLR_ICU_VERSION_OVERRIDE` to the system's ICU when it runs, debugs or tests them, and says so in the output. The debugger gets the same setting: netcoredbg reads symbols on the program's own runtime. To choose otherwise, set `CLR_ICU_VERSION_OVERRIDE` or `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` in the launch profile. Outside Codalus (`dotnet run`, a terminal), set the same variable yourself.
3. **OpenSSL 1.x for .NET Core 2.x to .NET 5 on Linux.** These runtimes use OpenSSL 1.0 or 1.1 for cryptography and HTTPS; .NET 6 added OpenSSL 3, the only one current distributions have (Ubuntu 22.04 and later, Debian 12, Fedora 36 and later). Programs that hash, encrypt or call HTTPS fail ("No usable version of libssl was found"); the rest runs. Test hosts hash test names, so tests of these versions don't run without it (the Test Explorer still lists them from the sources). To use those features, install OpenSSL 1.1 next to OpenSSL 3 (for example `libssl1.1` from the distribution's previous release, or OpenSSL 1.1.1 built into `/usr/local/lib` with `LD_LIBRARY_PATH` set in the launch profile), or develop in a container of a distribution of that time. A documented gap: Codalus can't supply a library the distribution lacks. .NET Core 2.0 also needs the system's `libunwind` (`libunwind8` on Debian and Ubuntu), which later runtimes bring with them.
4. **x64 under Rosetta 2.** Install Rosetta 2 (`softwareupdate --install-rosetta`) and the x64 runtime of each version (the x64 installer, or `dotnet-install.sh --architecture x64 --install-dir /usr/local/share/dotnet/x64`). Without it, Run says which runtime to install. The .NET test plugin runs these test hosts as x64 too.
5. **Attaching to an x64 process** (Debug → Attach to Process) on Apple silicon uses the Arm64 debugger, which can't debug it. Start such programs with F5 instead.
8. **Debugging and tests on .NET Framework** come in a later release (M43b): netcoredbg debugs CoreCLR only, and the .NET Framework's runtime needs a debugger of its own. F5 offers Start Without Debugging.
9. **.NET Framework 2.0 to 4.6.1** are best effort: their reference assemblies packages load and build (the test suite builds a `v3.5` library); the newer versions are the ones validated.

## With an older SDK pinned in global.json

A solution whose `global.json` pins an older SDK opens and builds with that SDK, as `dotnet build` does in its folder, from the .NET SDK 2.1.200 on:

| Pinned SDK | Open | Build | Notes |
|---|---|---|---|
| 6.0 and later | yes | yes | |
| 2.1.200 – 5.0 | yes | yes | Built in the build host's own process: these SDKs' MSBuild can't start build nodes for a host on a newer .NET. Their reference cache isn't saved (warnings MSB3088 and MSB3101), so incremental builds take a little longer. |
| 2.0 and 2.1.1xx | uses the newest SDK [6] | uses the newest SDK [6] | Below the floor. |
| not installed | uses the newest SDK [7] | uses the newest SDK [7] | |

6. Below the floor, the build host loads the newest installed SDK instead, which also builds the older frameworks, and says which SDK the solution asked for and why (the .NET SDKs panel and the Output window). The 2.0 SDKs' compiler task can't run on a newer .NET.
7. A `global.json` that asks for an SDK that isn't installed (taking its `rollForward` policy into account) is named in the same places, with the SDK used instead. `dotnet build` fails in that folder until the SDK is installed or `global.json` changes.

On Linux, the SDKs before .NET 6 run on their own runtimes for restores: Codalus gives the ones before .NET 5 the system's ICU as above, and all of them need OpenSSL 1.x to reach nuget.org (note 3), so on a current distribution they open but don't restore. On Apple silicon, the SDKs before .NET 6 are x64 programs that the Arm64 `dotnet` doesn't see, so a `global.json` that pins one counts as not installed.

## Checking it yourself

`eng/get-runtimes.sh` installs the runtimes and SDKs of the matrix into `.work/dotnet-compat`, and `dotnet test --project tests/Codalus.TargetFrameworks.Tests` runs the suite against it. `eng/compat-containers.sh` (Linux, with docker or podman) runs the programs in containers of the distributions .NET Core 2.x to .NET 5 supported, and on a current one.
