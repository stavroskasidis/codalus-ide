# 0115. Debugging CoreCLR apps on Android

- **Status:** accepted (2026-11-28; the maintainer's report of 2026-11-24, and their ask for the debugger itself rather than a message)
- **Date:** 2026-11-28
- **Deciders:** maintainer
- **Amends:** [ADR 0114](0114-debugging-coreclr-apps-in-ios-simulators.md) (its Android finding, and the platforms F5 debugs on CoreCLR), [ADR 0065](0065-mono-debugger-adapter.md) (mobile debugging through the Mono adapter)
- **Builds on:** [ADR 0086](0086-a-managed-coreclr-debugger.md) and [ADR 0094](0094-the-codalus-debugger-by-default.md) (the Codalus debugger, its startup hook, its bundles for other machines)

## Context

The maintainer's report: F5 on a new .NET 11 MAUI app on an Android emulator ran the app, and no breakpoint ever stopped. The Debug Console said "Waiting for the Mono runtime to connect to 127.0.0.1:…" and kept saying it, as on iOS before ADR 0114.

ADR 0114 found .NET 11's iOS apps on CoreCLR through `UseMonoRuntime`, and thought Android kept Mono because that property evaluates to nothing there. It doesn't keep Mono. The Android SDK says its runtime in `_AndroidRuntime`, and `UseMonoRuntime` only chooses Mono when it is `true`.

What a spike on the Mac found (Apple silicon, macOS 27.2, .NET 11.0.0, the Android SDK 37.2.31, an arm64 Android 16 emulator, a new .NET 11 MAUI app's Debug build):

- **The runtime.** `Microsoft.Android.Sdk.DefaultProperties.targets` sets `_AndroidRuntime` to `NativeAOT` for an AOT publish, to `MonoVM` when `UseMonoRuntime` is `true`, and to `CoreCLR` otherwise. That is .NET 11's default.
- **What the app has for a debugger.**
  - The app's CoreCLR is the runtime's own build for Android, in the app's process, under the app's user.
  - It makes the debugger's transport, the `clr-debug-pipe-<pid>-<disambiguation>-in` and `-out` pipes, in the app's temporary folder (its `cache`).
  - A Debug build's package has `libmscordbi.so` and `libmscordaccore.so` next to `libcoreclr.so`. The SDK leaves them out only when `AndroidIncludeDebugSymbols` isn't `true`.
  - There is no remote transport, and there won't be one. The runtime's maintainers said remote debugging is the debugger's to provide (dotnet/runtime#136350).
- **What the SDK's debugger properties do.**
  - `AndroidAttachDebugger`, `AndroidSdbTargetPort` and `AndroidSdbHostPort` set up Mono's soft debugger (an adb forward and `debug.mono.*`). On CoreCLR nothing listens, which is the endless wait.
  - On CoreCLR the only property the SDK uses is `debug.dotnet.log`, for logging.
- **How Microsoft's tooling debugs it.**
  - Microsoft's guide for debuggers on mobile platforms ("Initialize a custom debugger on mobile platforms", .NET 11) loads a target-side component into the app as a profiler (`DOTNET_PROFILER`…). That component calls `libmscordbi`'s `CoreCLRCreateCordbObjectEx` and attaches from inside the app.
  - The component Microsoft's own mobile debugger proxy uses, and that proxy's protocol, aren't public, and can't be redistributed (dotnet/docs#56485).
- **The debugger can run on the device, as the app's user.**
  - `adb shell run-as <package>` runs a command as a debuggable app's user (a Debug build is debuggable), in SELinux's `runas_app` domain.
  - That user may read the app's `/proc/<pid>/maps` and `/proc/<pid>/mem`, use its pipes, stop it and end it.
  - Two limits:
    - It can't execute files outside the app's own storage. `/data/local/tmp` is refused.
    - It can't ask whether the app's process exists with the null signal: `kill(pid, 0)` is `EACCES`. The debugging libraries ask that way, and take the refusal for a failure ("E_FAIL" from `DebugActiveProcess`).
- **Which .NET runs there.**
  - The `linux-bionic-arm64` runtime pack (Android's libc) builds a self-contained app with an app host for Android.
  - Its CoreCLR 11.0.0 crashed at its start on the emulator, in its first call into JIT-compiled code (a call through a null pointer).
  - The Android runtime pack's (`Microsoft.NETCore.App.Runtime.android-arm64`), under the bionic pack's app host and host libraries, runs.
  - That pack's `System.Console` is the system log (`logcat`) and has no standard input. The program's own file descriptors still work.
  - There is no dbgshim for Android.
- **What attaching takes.**
  - The runtime's base address is in the process's `maps`.
  - The debugging library loaded by its path doesn't start until its `DllMain` is called (dbgshim's platform layer does that). Before then, its first call waits forever.
  - It needs `libmscordaccore.so`, by its bare name, loaded first.
  - With that done, the Codalus debugger attached to the running app: a breakpoint in a button's handler stopped, the stack and the locals were right, and a step and an evaluation worked.
  - .NET's `Process.GetProcessById` finds no processes on Android, but `/proc` is readable.
- **Holding the app at its start.**
  - `dotnet run` (the .NET 11 SDK) takes `-e NAME=VALUE`, which the Android SDK writes into the app's environment (`@(RuntimeEnvironmentVariable)`, `__environment__.txt`).
  - A `DOTNET_STARTUP_HOOKS` that names a file here is put into the app and renamed to its assembly's name, as for `dotnet watch`'s Hot Reload agent.
  - `dotnet build -t:Run` has no way to give the app an environment.
  - Debug builds keep startup hooks on. A Release build (`Optimize`) turns them off.
  - With the debugger's startup hook and `CODALUS_DEBUG_WAIT`, the app waits before its first page: a breakpoint in the page's constructor stopped, and a step and an evaluation worked. Stopping the session ended the app, and `dotnet run` with it.
- **Android empties `code_cache` at each install** of the app. `cache` survives it. The app's storage doesn't exist before the first install, and the app starts right after it.

## Decision

1. **Android apps on CoreCLR are found.** `ProjectInfo.CoreClrFrameworks` lists a framework whose evaluation has `_AndroidRuntime` equal to `CoreCLR`, as well as one whose `UseMonoRuntime` is `false` (ADR 0114). An Android app on Mono (`MonoVM`) or NativeAOT isn't listed.
2. **F5 debugs it with the Codalus debugger on the device or emulator,** never the Mono adapter. The device has to be running (F5 starts an emulator first, as for a run) and 64-bit (arm64 or x86_64). Elsewhere a message says why at once.
3. **The debugger's bundle for Android.**
   - `android-arm64` and `android-x64` are built for `linux-bionic-<arch>`, on the Android runtime pack. The host's project swaps the runtime pack before trimming, so what is trimmed is what runs, and adds the bionic pack's `libhostfxr.so` and `libhostpolicy.so`.
   - A published install ships them as `remote/android-<arch>/debugger.tar.gz`, about 16 MB each (`CodalusAndroidDebuggerRids`).
   - A source checkout builds them with `eng/get-codalus-debugger.sh android-arm64`, and `CODALUS_DEBUGGER_ANDROID_ARM64` names a folder, as for the Linux bundles.
4. **The debugger in the app** (`AndroidDebugging`, in the Workbench):
   - The bundle is pushed to `/data/local/tmp/codalus-debugger-<build>`, once per build of it. Other builds' copies there are removed.
   - It is copied into the app's `code_cache/codalus-debugger-<build>` once per install.
   - It starts as `adb -s <serial> shell -T run-as <package> env TMPDIR=<the app's cache> …/codalus-debughost --clr-dap`. Its DAP is adb's standard streams (`-T`: no terminal, the bytes pass as they are).
   - The package is the project's `ApplicationId`.
5. **The debug host on Android** (`AndroidRuntime`, `ElfImports`):
   - No dbgshim. The runtime is the `libcoreclr.so` the process maps.
   - `libmscordaccore.so` and then `libmscordbi.so` are loaded from its folder, the latter's `DllMain` is called, and `CoreCLRCreateCordbObjectEx` (ICorDebug 4.0, the process, the runtime's base address) gives the ICorDebug.
   - The two libraries' import of `kill` is redirected (their import slots rewritten) to one that answers the null signal from `/proc`.
   - The process is attached to by its id alone, with no `Process` object to hold.
   - The adapter's streams are its file descriptors.
6. **The run holds the app.**
   - The run is `dotnet run --project … -f … -c … --device <serial>` with the IDE's Android SDK and JDK, plus:
     - `-e DOTNET_STARTUP_HOOKS=<the hook>`
     - `-e CODALUS_DEBUG_WAIT=<the app's cache>/codalus-debug-<run>`, a new folder each run
     - `-e CODALUS_DEBUG_WAIT_CREATE=1`, so that the hook makes the folder (the IDE can't before the install)
     - `-e CODALUS_DEBUG_WAIT_SECONDS=900`
   - The IDE asks the device for the folder's `pid` each second (`run-as … cat`). Once the app has reported, it copies the debugger in, starts it, and launches with `waitFolder` (ADR 0114's wait). The hook goes on once the breakpoints are set.
   - The sources are this machine's (`sourcesOnClient`, as for remote targets: the app's PDBs name this machine's paths). The symbol options stay here.
   - A run that ends before the app reported cancels the session. An app that has run for 30 seconds without reporting gets a message: a Release build, or one with startup hooks off, isn't debugged.
   - Stopping the session ends the app, which ends the run.

## Consequences

- A .NET 11 MAUI app on CoreCLR debugs on an Android emulator or device with what the Codalus debugger has elsewhere: conditions, hit counts, logpoints, calls and operators in expressions, changing values, Set Next Statement. The Mono adapter has none of those.
- Every published install is about 32 MB larger: the two Android bundles.
- The first F5 on a device pushes about 45 MB. That takes a moment on the emulator, and longer over USB. An F5 after a reinstall copies it into the app on the device.
- The redirected `kill` and the call to `DllMain` rest on how this build of the debugging libraries works. A future runtime could need neither, or something else. The attach would then fail with the runtime's error, not hang.
- **Not available yet:**
  - Hot Reload and Edit and Continue in such a session (untried; the SDK sets `DOTNET_MODIFIABLE_ASSEMBLIES=Debug` for Debug builds).
  - Attach to Process for an Android app.
  - Symbol servers from the device (the device's .NET has no HTTPS of its own).
  - 32-bit devices.
  - A work profile's or a secondary user's apps (`run-as --user`).
  - Devices whose vendor broke `run-as`.
  - `TODO(issue)`
- **Tests:**
  - The evaluation is in `CoreClrFrameworksTests`.
  - The run's arguments, the adapter's command, the ABIs, the copies to the device and into the app, and the wait (with a stand-in adb) are in `MobileTests`.
  - The hook making its folder is in `WaitingLaunchTests`.
  - The maps reader and the ELF import slots, on a library written by the test, are in `AndroidRuntimeTests`.
  - The session on the emulator was checked by hand, as ADR 0065's are. F5 in the shell on a new .NET 11 MAUI app's copy, from a clean tree, stopped in its first page's constructor at the right line, stepped, evaluated `Title`, and Stop ended the app and the run.
