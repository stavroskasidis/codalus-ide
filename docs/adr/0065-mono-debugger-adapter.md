# 0065. Mobile debugging through a Mono adapter

- **Status:** accepted (M20b, 2026-10-15)
- **Date:** 2026-10-15
- **Deciders:** maintainer (D6), with the standing go-ahead to complete every milestone

## Context

On Android and iOS, .NET MAUI apps on .NET 8 to 10 run on the Mono runtime. netcoredbg (ADR 0007) debugs CoreCLR only. Mono has its own protocol, the soft debugger protocol: the runtime's agent either listens on a port or connects to a debugger, configured by `--debugger-agent=…` options. The roadmap called for a Mono soft-debugger DAP adapter, as a supervised host started only when a mobile debug session starts.

The research for M20b found the following:
- **The runtime is changing.** In .NET 11, CoreCLR is the default on every MAUI platform, and the release notes say there's no debugger for CoreCLR on mobile yet, so apps that need one opt back into Mono (`UseMonoRuntime`). dotnet/android's main branch has removed the Mono host. A Mono adapter therefore covers .NET 8 to 10, and .NET 11 apps that opt back in.
- **The client library.** Mono's soft debugger client, `Mono.Debugger.Soft` in [mono/debugger-libs](https://github.com/mono/debugger-libs) (MIT, maintained, `net6.0`), is what dotnet/android's own debugger tests, vscode-mono-debug and DotRush build from source. It has no NuGet package; the ones on nuget.org are old, `net40` only, and unlisted. Mono.Debugging (its evaluator, on Roslyn) is much larger.
- **Licenses to avoid.** `Microsoft.VisualStudio.Shared.VSCodeDebugProtocol`, the DAP library several adapters use, is proprietary. mlaunch (iOS) is closed source, and has to come from the user's workload.
- **How each platform connects:**
  - **Android:** .NET 10's Android SDK has its own debugger properties on its `Run` target: `AndroidAttachDebugger`, `AndroidSdbTargetPort` and `AndroidSdbHostPort`. It forwards the port with adb and starts the app with its agent listening (`debug.mono.extra=debug=127.0.0.1:P,…,server=y`). We confirmed this in the installed pack.
  - **iOS simulators and Mac Catalyst:** the app connects to a debugger that listens, through `__XAMARIN_DEBUG_PORT__` and `__XAMARIN_DEBUG_HOSTS__`.
  - **iPhones:** these need mlaunch's TCP tunnel.
- **A device-free test target exists.** .NET 8's desktop Mono runtime pack (`Microsoft.NETCore.App.Runtime.Mono.<rid>`) runs a console app on Mono. With `MONO_ENV_OPTIONS=--debugger-agent=…` its agent answers the soft debugger handshake. It is the same agent as .NET 8 on mobile. .NET 9 and later ship no desktop Mono pack.

## Options

| Question | Options | Verdict |
|---|---|---|
| The protocol client | Our own client of the wire protocol; Mono.Debugger.Soft vendored; Mono.Debugging.Soft (with its evaluator) | **Mono.Debugger.Soft vendored** in `src/ThirdParty/Mono.Debugger.Soft`, at a pinned commit, with its license. It's built without Mono.Cecil, and the one type it used from Microsoft.FileFormats is replaced. It's tested and maintained, and the others build it from source too. The evaluator stays out: it's large, and simple paths cover the common case. |
| DAP | A DAP library; our own | **Our own,** on the contracts in `Codalus.Debugging` (source-generated JSON, already used by our client). The server side is a small framing loop (`DapServer`). |
| The process | A new host; the debug companion host in another mode | **`codalus-debughost --mono-dap`.** It's still a separate process per session, started only when a mobile session starts, but it adds no executable to package, sign, or bundle for remote windows. |
| Connecting | One mode; connect and listen | **Both.** `connect` (Android through adb's forward, and the desktop test target) retries until the handshake succeeds: adb accepts the connection before the app listens. `listen` waits for iOS simulators and Mac Catalyst. |
| What a session offers | Everything netcoredbg does; the core | **The core:** breakpoints bound when their types load (a type-load request filtered by the files with breakpoints, and the next line with code when a line has none); stepping with Just My Code (stepping into code without source steps out again); threads; stacks; locals, `this`, fields, auto-properties and other properties through their getters, and arrays; evaluation of names and member paths (`item.Title`, `items[0]`); exceptions (all, or unhandled) with exception info. Function breakpoints, conditions and setting values are refused with a message. |

## Decision

- **F5** on a mobile startup project:
  1. picks a free local port;
  2. starts the run with the platform's debugger properties (`MobileTargets.DebugArguments`; for Android, the SDK's own);
  3. starts a session through `DebugService.LaunchAdapterAsync` with `codalus-debughost --mono-dap` and `MonoDebugArguments`.

  A run that exits with an error before the app is up cancels the session. Stopping the session ends the app (`terminateDebuggee`).
- **Debugging on an iPhone or iPad** says it isn't available yet. Ctrl+F5 runs there, and simulators debug.
- **Tests:** they run the real adapter against `samples/Debugging/MonoDebuggee` on .NET 8's desktop Mono, at the protocol level and through `DebugService`, on every OS's CI runner (the runtime pack comes from NuGet). Android and iOS sessions are checked by hand on a device or emulator.

## Consequences

- Debugging works for .NET 8 to 10 MAUI apps, and for .NET 11 apps that set `UseMonoRuntime`. CoreCLR on mobile needs a CoreCLR remote debugger: JaneySprings/clrdbg (MIT) is one to watch. `TODO(issue)`
- The vendored client is a few thousand lines of third-party code in the tree. It's built without our analyzers, and it can be updated by copying a newer commit (its README lists the two edits).
- Without Mono.Debugging's evaluator, expressions beyond names and member paths (calls, operators, casts) are refused. A Roslyn-based evaluator over the soft debugger is a candidate. `TODO(issue)`
- The iOS and Mac Catalyst properties (`MlaunchAdditionalArguments`, `OpenArguments`) still have to be confirmed on the Mac.
