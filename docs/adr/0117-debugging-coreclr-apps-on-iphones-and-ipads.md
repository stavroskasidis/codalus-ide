# 0117. Debugging CoreCLR apps on iPhones and iPads

- **Status:** accepted (2026-11-28; the maintainer's ask of 2026-11-28 for real devices too, and their answers: research first, a spike on their iPhone, then a pull request of its own; F5's build kept apart from the user's)
- **Date:** 2026-11-28
- **Deciders:** maintainer
- **Amends:** [ADR 0115](0115-debugging-coreclr-apps-in-ios-simulators.md) (iPhones and iPads, which it left saying they can't be debugged yet), [ADR 0065](0065-mono-debugger-adapter.md) (mobile debugging)
- **Builds on:** [ADR 0086](0086-a-managed-coreclr-debugger.md) and [ADR 0094](0094-the-codalus-debugger-by-default.md) (the Codalus debugger), [ADR 0116](0116-debugging-coreclr-apps-on-android.md) (the debugger beside a mobile app's runtime, and its two missing steps)

## Context

After ADR 0115 and 0116, F5 on a .NET 11 app on an iPhone or iPad said at once that it can't be debugged there yet. The maintainer asked for it to work, first asking how JetBrains Rider does it.

**What others do (2026-11-28):**
- **Rider** doesn't debug .NET 11 iOS apps on a device: its issue (RIDER-142102) is open.
- **Microsoft's** tools (VS Code's .NET MAUI extension, vsdbg) load a "CoreCLR remote debugger profiler" into the app, which listens on device port 10000 and is reached over a USB tunnel (the logs in dotnet/maui#35929). That proxy and its protocol aren't public, and its license doesn't allow redistributing it (dotnet/docs#56485). The issue, "breakpoint never binds on device", was open and "still WIP" at its last update (2026-09-15).
- **Microsoft's guide for other debuggers** ("How to initialize a custom debugger on mobile platforms", .NET 11) says the target-side component must be loaded into the app: a profiler library with `DllGetClassObject`, loaded through `DOTNET_ENABLE_PROFILING`, `DOTNET_PROFILER` and `DOTNET_PROFILER_PATH`, which calls `libmscordbi`'s `CoreCLRCreateCordbObjectEx`, `ICorDebug::Initialize` and `DebugActiveProcess` on its own process. iOS doesn't let an app start another process, and the debugging library reads the debuggee's memory through `task_for_pid`, so ICorDebug has to run inside the app.

**What a spike on the maintainer's iPhone found** (an iPhone with iOS 27.2.1, Xcode 27.2, .NET 11.0.0, a new .NET 11 MAUI app's Debug build, signed with the maintainer's team):
- A Debug build's bundle already has `libmscordbi` and `libmscordaccore`, each a framework of its own (`Frameworks/libmscordbi.framework/libmscordbi`). The runtime pack (`Microsoft.NETCore.App.Runtime.ios-arm64`) has the debugger built in, with interpreter frames (no JIT on a device).
- **The Codalus debugger compiles ahead of time for iOS.** The whole debug companion host's CoreCLR adapter (its sources; ClrDebug's COM wrappers are source-generated) builds with NativeAOT for `ios-arm64` as a shared library, about 12 MB, with no warnings beyond the launcher's own path and Mono.Cecil's. The iOS SDK compiles only apps ahead of time; a library takes NativeAOT from the runtime pack (`PublishAotUsingRuntimePack`).
- **Two runtimes in one process work:** CoreCLR loads the NativeAOT library as a profiler, calls its `Initialize` before any managed code, and the library's own runtime runs beside the app's.
- **Two steps the guide doesn't name**, the same as on Android (ADR 0116): the debugging library's platform layer has to be set up (its `DllMain`, which dbgshim's loader calls; without it the first environment lookup crashed on a null pointer), and the data access library's path has to be given (`CoreCLRCreateCordbObject3`'s `dacModulePath`): the library looks for `libmscordaccore.dylib` next to itself, and on a device it is a framework of its own (`CORDBG_E_DEBUG_COMPONENT_MISSING`).
- Then `DebugActiveProcess` succeeds in `Initialize`, at once. The callbacks run on the debugging library's threads, which call into the NativeAOT library; nothing deadlocks, as the debugger's threads aren't the app runtime's managed threads. Breakpoints in the app's code (interpreted) stop, stepping works, and values read (a field before and after `_count++`).
- **The process is held while the debugger has it synchronized**, and iOS ends an app that hasn't launched in 20 seconds (`0x8BADF00D`, "process-launch watchdog transgression"). The hold before `Main` has to stay under that.
- **The Mac reaches a port in the app directly**: CoreDevice's tunnel gives each paired device an address (devicectl's `tunnelIPAddress`, an IPv6 address on `utun`), and a TCP listener in the app answered on it, over USB, with no tool in between.
- `DOTNET_PROFILER_PATH` takes `@executable_path/Frameworks/…`: the path is known before the app is installed.
- The iOS SDK's `Run` target takes the launch environment for a device as for a simulator (mlaunch's `--setenv`, through `MlaunchAdditionalArgumentsProperty`).

## Decision

1. **The in-app debugger.** `src/Codalus.Debugging.Device` builds the Codalus debugger's CoreCLR adapter (the debug companion host's sources, not a copy) with NativeAOT for `ios-arm64` into `CodalusDebugger.framework`:
   - `ProfilerEntry`: `DllGetClassObject`, a class factory and a profiler whose `Initialize` starts the server; every other callback answers S_OK, and the event mask stays empty. Plain vtables of unmanaged function pointers.
   - `InAppServer`: the adapter on the TCP port `CODALUS_DEBUG_PORT`, one IDE at a time (another may connect after a detach). The app's start is held in `Initialize` until the IDE's `configurationDone`, or `CODALUS_DEBUG_HOLD` seconds (12 by default, below iOS's 20), after which breakpoints bind as their code loads.
   - `IosDeviceRuntime` (in the host, compiled into both): the session's `launch` with `inProcess` attaches to its own process, with the two steps above.
2. **F5 on a paired iPhone or iPad**, for a CoreCLR framework (`ShellHost.DebugCoreClrOnIosDeviceAsync`):
   - The run is the device's (`-t:Build;Run`, `RuntimeIdentifier=ios-arm64`, `_DeviceName`) with F5's own build folders (`obj/<configuration>/codalus-device/`, `bin/<configuration>/codalus-device/`): the maintainer's choice, so the app the user's own builds and Ctrl+F5 make stays as it was. Nothing is written into the project: a targets file of the IDE's (`CustomAfterMicrosoftCommonTargets`) adds the framework as a `NativeReference`, and the app's build signs it with the app.
   - The launch's environment (`MlaunchAdditionalArgumentsProperty`): the profiler's three settings (`InAppDebugger.Environment`) and a port in the dynamic range.
   - The IDE connects to the port at devicectl's tunnel address, asked again on each try (`InAppConnection`), until the app answers or the run ends (15 minutes: a build, an install and the launch). The connection is the adapter's "process" (`DebugAdapterStartInfo.Launcher`), so the session is the Codalus debugger's as anywhere, with `sourcesOnClient` (the PDB's documents are this Mac's).
   - Stopping the session ends the app, which ends the run.
3. **Shipping.** A published install for macOS has `remote/ios-arm64/debugger.tar.gz` and `debugger.id` (`CodalusIosDebugger`), unpacked into the temporary folder on first use, one folder per build. It ships archived: an iOS library is no part of the Mac app that is signed and notarized. A source checkout builds it with `eng/get-codalus-debugger.sh ios-arm64` into `.work/codalus-debugger-ios-arm64`; `CODALUS_DEBUGGER_IOS_ARM64` names another folder. It builds only on macOS with Xcode, so it isn't in `Codalus.slnx`.

## Consequences

- A .NET 11 app debugs on an iPhone or iPad with the Codalus debugger: breakpoints (in the first page too), stepping with the PDB's lines, Locals, Watch, the Call Stack and the debugger's evaluator. Microsoft's own device debugging was still being finished at its last report, and Rider has none.
- F5's build is a second build of the app (its own folders), and its bundle is about 12 MB larger.
- **Not available yet:** Hot Reload and Edit and Continue on a device (`DOTNET_MODIFIABLE_ASSEMBLIES` and the agent's pipe, untried); a device paired over Wi-Fi only (its tunnel is the same kind, but untried); tvOS and Mac Catalyst; Mono apps on a device (ADR 0065: mlaunch's tunnel). A Release build has no debugging libraries and isn't debugged. `TODO(issue)`
- The hold is shorter than the IDE might need on a slow Mac; then the app starts, and breakpoints in its first lines are passed (the app's console, in the run's output, says so).
- Microsoft says the profiler mechanism "doesn't establish general-purpose support for third-party profilers on mobile platforms": it is a documented path for debuggers, which this is.
- **Tests:** device-free, on every OS: the IDE connecting, trying until the port answers, and launching in the app's process with the sources on its side, against the real adapter on a TCP port with a stand-in ICorDebug (`InAppDebuggerTests`); the run's arguments, the targets file, the tunnel address, the framework's lookup and unpacking (`MobileTests`). The device session was checked by hand on the maintainer's iPhone, as ADR 0065's are: F5 through the shell on a new .NET 11 MAUI app's copy, from a clean tree, stopped at the first page's constructor with its source, read `_count`, stepped over to the next line, and Stop ended the app and the run (37 s in all).
