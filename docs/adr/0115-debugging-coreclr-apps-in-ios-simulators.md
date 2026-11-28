# 0115. Debugging CoreCLR apps in iOS simulators

- **Status:** accepted (2026-11-24; the maintainer's answer of 2026-11-24 on the scope)
- **Date:** 2026-11-24
- **Deciders:** maintainer
- **Amends:** [ADR 0065](0065-mono-debugger-adapter.md) (mobile debugging through the Mono adapter: its gap "CoreCLR on mobile"), [ADR 0064](0064-maui-devices-and-mobile-runs.md) (the run command on iOS and Mac Catalyst)
- **Amended by:** [ADR 0116](0116-debugging-coreclr-apps-on-android.md) (2026-11-28: Android apps run on CoreCLR too, found by `_AndroidRuntime`, and the Codalus debugger debugs them on the device or emulator)
- **Builds on:** [ADR 0086](0086-a-managed-coreclr-debugger.md) and [ADR 0094](0094-the-codalus-debugger-by-default.md) (the Codalus debugger, its startup hook and held start)

## Context

The maintainer's report: F5 on a new .NET 11 MAUI app in an iOS simulator ran the app, and a breakpoint in a button's click handler never stopped. The Debug Console said "Waiting for the Mono runtime to connect to 127.0.0.1:…" and kept saying it.

.NET 11's iOS and Mac Catalyst workloads run apps on CoreCLR by default (`UseMonoRuntime` evaluates to `false`). F5 started the Mono debugger adapter (ADR 0065), which listens for a Mono runtime's debugger agent. A CoreCLR app has none, so the adapter waited for its whole 15 minutes while the app ran undebugged. ADR 0065 recorded CoreCLR on mobile as a gap, and the user guide said to set `UseMonoRuntime`; the product itself said nothing.

What a spike on the Mac found (macOS 27.2, Xcode 27.2, .NET 11.0.0, the `ios` workload 27.2):

- **A simulator app is a process of this Mac,** and its CoreCLR is the desktop one's commit, with the debugger's transport: it makes the `clr-debug-pipe-<pid>-…` pipes in its temporary folder. dbgshim's `EnumerateCLRs` finds the runtime in it.
- **Its debugging libraries don't load here as they are.** `libmscordbi.dylib` and `libmscordaccore.dylib` are in the app's bundle, built for the platform `IOSSIMULATOR`, and dyld refuses to load them into a macOS process (`CORDBG_E_DEBUG_COMPONENT_MISSING`). The desktop runtime's libraries of the same commit load, and then can't read the app's runtime (`CORDBG_E_READVIRTUAL_FAILURE` from `DebugActiveProcess`). Copies of the bundle's own libraries marked for macOS (`vtool -set-build-version macos`), with their reference to each other made relative to their folder (`install_name_tool`; the originals' is an `@rpath` whose only entry is `$ORIGIN`, which dyld doesn't know) and signed again ad hoc (`codesign`), load and work: dbgshim takes them through a library provider (`CreateDebuggingInterfaceFromVersion3`).
- **The pipes are in the app's temporary folder,** its simulator container's `tmp`, not the debugger's. With the adapter's `TMPDIR` set to it before the debugging library loads, the attach connects.
- **The startup hook holds the app.** A simulator app reads this Mac's files, and its Debug build isn't trimmed, so `DOTNET_STARTUP_HOOKS` with the debugger's hook and `CODALUS_DEBUG_WAIT` (ADR 0039, ADR 0086) holds it before `Main`: a breakpoint in the first page's constructor stops. Without a hold the app is attached to as it runs, and its first lines are missed.
- **The iOS SDK's `Run` target takes the variables** as mlaunch's `--setenv`, through the property `MlaunchAdditionalArgumentsProperty`, a `;`-separated list. On MSBuild's command line a `;` ends a property, unless the value is in quotes; `%3B` reaches the SDK as one argument.
- **.NET 11's `Run` target doesn't build.** `dotnet build -t:Run` on an app that isn't built fails with "The app must be built before the arguments to launch the app using mlaunch can be computed". It affected Start Without Debugging too, on a clean tree.
- **Mac Catalyst is different.** Its Debug build on Apple silicon is trimmed (`SdkOnly`), so the bundle has no `netstandard.dll` and the hook (netstandard2.0) doesn't load: the app ends at its start. Its template's app is sandboxed, so the hook and the folder it reports in have to be in the app's container (the SDK itself does that for `dotnet watch`). And building once with `MtouchLink=None` after a trimmed build fails (the incremental build's `R2R` files are missing). With an untrimmed app, the hook in the container and the retargeted libraries the mechanism is the same, but it wasn't seen working.
- **Android** on .NET 11 evaluates `UseMonoRuntime` to nothing, and it was taken here for Mono. That was wrong: the Android SDK says its runtime in `_AndroidRuntime`, which is `CoreCLR` unless `UseMonoRuntime` is `true`, so Android apps got the Mono adapter and its endless wait too. [ADR 0116](0116-debugging-coreclr-apps-on-android.md) (2026-11-28) finds them and debugs them on the device.

## Decision

1. **A project says which frameworks run on CoreCLR.** `ProjectInfo.CoreClrFrameworks`: the target frameworks whose evaluation has `UseMonoRuntime` equal to `false`. Desktop projects don't set the property and list none.
2. **F5 on such a framework never starts the Mono adapter.**
   - **In an iOS simulator** the Codalus debugger debugs the app (below).
   - **On Android** the Codalus debugger runs on the device ([ADR 0116](0116-debugging-coreclr-apps-on-android.md)).
   - **Elsewhere** (Mac Catalyst, an iPhone) a message says at once that the app runs on CoreCLR, which can't be debugged there yet, and to set `<UseMonoRuntime>true</UseMonoRuntime>` or run without debugging. The maintainer's choice (2026-11-24): simulators, and Mac Catalyst if the same approach worked there. It didn't (the last finding but one), so Mac Catalyst is a follow-up. `TODO(issue)`
3. **The run starts the app held.** The run command gets `-p:MlaunchAdditionalArgumentsProperty="--setenv=DOTNET_STARTUP_HOOKS=<hook>;--setenv=CODALUS_DEBUG_WAIT=<folder>"` (`MobileTargets.CoreClrDebugArguments`). The hook is a copy under a new temporary folder with the wait folder next to it (`MobileService.CoreClrDebugPlan`), removed when the run ends: the IDE's own folder may have spaces in its path.
4. **The Codalus debugger waits for it.** A `launch` with `waitFolder` (`NetFxDebugArguments.WaitFolder`; `DebugService.LaunchWaitingAsync`) waits for the process id the hook writes there (15 minutes: a build and a deploy), attaches, and lets the hook go once the program is stopped for it, as the held start does (`DbgShimDebugger.AttachWaiting`). A folder that is removed is the start given up; a run that ends before the app reported cancels the session, with "didn't start" in a notification. Stopping the session ends the app, which ends the run.
5. **Attaching to an app of another Apple platform** (`AppleRuntime`, in the debug host), for the wait above and for Attach to Process alike:
   - the runtime's platform is read from its Mach-O header (`LC_BUILD_VERSION`);
   - its debugging libraries are copied, marked for macOS, relinked to each other and signed with the user's own tools (`xcrun vtool`, `xcrun install_name_tool`, `codesign`), once per build of the libraries, under the temporary folder (`codalus-debug-libraries/`);
   - the process's `TMPDIR` is read from its environment (`sysctl kern.procargs2`) and made the adapter's (libc's `setenv`) before the debugging library loads. That holds for any process whose temporary folder differs, a sandboxed desktop app included. An adapter process serves one session, so nothing else moves with it.
6. **The run command on iOS and Mac Catalyst is `dotnet build -t:Build;Run`.** With the SDKs whose `Run` builds, the first target leaves the second nothing to build. Android keeps `-t:Run`.

## Consequences

- A .NET 11 MAUI app debugs in an iOS simulator with everything the Codalus debugger has (conditions, hit counts, logpoints, calls and operators in expressions, changing values, Set Next Statement), which the Mono adapter lacks.
- Debugging needs Xcode's command line tools, which building for iOS needs already.
- The retargeted libraries rest on the simulator's and macOS's ABI being the same for what those libraries use. They are the app's own runtime's build, so their data layouts match by construction; a future runtime could break the trick, and the attach would then fail with dbgshim's error, not hang.
- **Not available yet:** Hot Reload and Edit and Continue in such a session (the app would need `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and the agent's pipe; untried); x64 simulator apps on Apple silicon (they run under Rosetta 2 and need the x64 debugger); Mac Catalyst and iPhones on CoreCLR (Android: [ADR 0116](0116-debugging-coreclr-apps-on-android.md)). `TODO(issue)`
- A Release build, or one that turns startup hooks off, never reports: the wait ends with a message that says so.
- **Tests.** The wait runs on every OS against a console program the test starts with the hook (`WaitingLaunchTests`); the Mach-O reader, the environment reader and the library preparation (with stand-in tools) in `AppleRuntimeTests`; the evaluation in `CoreClrFrameworksTests`; the arguments and the plan in `MobileTests`. The simulator session itself was checked by hand on the Mac, as ADR 0065's sessions are: F5 on a new .NET 11 MAUI app from a clean tree stopped in its first page's constructor, stepped, and Stop ended the app and the run.
