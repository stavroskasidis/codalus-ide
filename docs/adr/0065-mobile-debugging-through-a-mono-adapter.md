# 0065. Mobile debugging through a Mono adapter

- **Status:** accepted; Android sessions experimental (M20b, see Findings)
- **Date:** 2026-10-14
- **Deciders:** maintainer (D6; the roadmap's M20: "MAUI apps run on the Mono runtime on Android and iOS, and netcoredbg doesn't debug Mono")

## Context

.NET for Android and .NET for iOS run apps on the **Mono runtime**. Its debugger is the Mono Soft Debugger:
- an agent inside the runtime;
- a TCP wire protocol that a client drives.

netcoredbg (ADR 0007) speaks ICorDebug, which Mono doesn't have.

The open-source clients of the soft debugger are:
- Mono's `debugger-libs` (MIT, still maintained): `Mono.Debugger.Soft` for the wire protocol, `Mono.Debugging` for a session API with a C# evaluator on Roslyn, and `Mono.Debugging.Soft` joining them;
- the DAP adapters built on them: `vscode-mono-debug` (MIT), and .NET Meteor up to 6.x (MIT).

Visual Studio's own Xamarin debugger is proprietary.

How the runtime is told to connect:
- On **Android**, the app's runtime reads the system property `debug.mono.connect` (`port=N`) when `am set-debug-app` marks the app. Its agent then listens on that port on the device, and `adb forward` makes it local.
- On **iOS** (M20c), the launcher gets the debugger's host and port.

## Decision

**debugger-libs, vendored.**
- `Mono.Debugger.Soft`, `Mono.Debugging` and `Mono.Debugging.Soft` are copied into `src/ThirdParty/debugger-libs` (MIT) at a recorded commit. Their NuGet packages are from 2017 and target .NET Framework.
- The source is the fork that .NET Meteor and DotRush maintain (JaneySprings/Mono.Debugger), not mono/debugger-libs. It has fixes for .NET for Android and iOS, and doesn't need `Microsoft.SymbolStore`, which isn't on nuget.org.
- They build on `net10.0` without our analyzers. The folder's README lists our changes.

**A new supervised host, `codalus-monodbg`** (`src/Codalus.Debugging.Mono`).
- A Debug Adapter Protocol server on its stdio, like netcoredbg, so the shell's `DebugService` drives it like any adapter. It covers breakpoints (with conditions and hit counts), stepping, threads, stack, variables, evaluation, exceptions and output.
- It runs on the JIT, like the other hosts, because Roslyn's evaluator needs it.
- It starts only when a mobile debug session starts. `CODALUS_MONODBG` overrides it; installed builds have it in `monodbg/`.

**An Android debug session** (`AndroidRunner.DebugAsync`):
1. Builds and installs as Start Without Debugging does (ADR 0064), in Debug.
2. Runs `adb forward tcp:P tcp:P` on a free port.
3. Sets `setprop debug.mono.connect port=P`, then `am set-debug-app <ApplicationId>`.
4. Starts the adapter's attach first. The attach is answered at once, and the connection is retried every half second in the background, so the breakpoints that follow are in the session before the app's code runs.
5. Starts the app. Its runtime listens for the debugger for only two seconds as it starts, and expects `start debugger: sdb` (a length byte, then ASCII) before the soft debugger's handshake.
6. On the end of the session: stops the app, removes the forward, and clears the property and the debug-app mark.

## Findings (M20b, an Android 15 x86_64 emulator, .NET 10's Android workload)

These were established step by step with probes against the runtime: a raw socket, Mono.Debugger.Soft alone, and Mono.Debugging alone, with the agent's own log (`debug.mono.extra` with `debug.mono.log=debugger-log-level=4`).
- **Idle connections drop.** The agent drops a connection that sends it nothing for about eleven seconds, in both of the runtime's debugging modes (`debug.mono.connect` and `debug.mono.extra`). The agent's own keepalive events don't prevent it. A version request every couple of seconds does, and the agent answers those even while the app runs. The adapter's transport sends one.
- **Suspending on assembly loads stalls the app.** With assembly-load events at suspend policy All, the agent sends one event that never reaches the client (`Xamarin.AndroidX.Loader`, about the 56th), after which it answers nothing. The app then waits until the connection drops. The thread-suspend mode (`MONO_THREADS_SUSPEND`) changes nothing. With policy None the app runs normally, so the vendored session requests assembly loads that way. The agent doesn't implement suspend policy EventThread; it asserts.
- **Filtered type loads don't fire.** Type-load events filtered by source file (by name or full path) or by type name never fire, although unfiltered ones do. The runtime has the app's symbols: types report their source files, and methods their lines.
- **Breakpoints can hit.** With an unfiltered type-load request, a breakpoint set on the type's load was hit, and the stopped thread could be inspected.
- **The app stalls after a hit.** After resuming from that stop, the app stalled again until the connection dropped.

Until these are understood, F5 on a mobile app says debugging isn't available yet. `CODALUS_MOBILE_DEBUG=1` turns the session on, for working on it. Next to try:
- a physical device and an arm64 emulator image, to rule out the x86_64 emulator;
- Visual Studio's own sequence, captured with `MONO_SDB_LOG`-style logging on its side, to compare.

## Consequences

- When it works, breakpoints, stepping and evaluation on Android devices and emulators use the same windows as desktop debugging. For now that's behind `CODALUS_MOBILE_DEBUG=1`.
- `[DebuggerDisplay]`, visualizers and the companion host (ADR 0021, 0032) are netcoredbg-side features. They don't apply to Mono sessions yet.
- The vendored libraries are ours to update; the README says how.
- Hot Reload on Mono is M20d's.
