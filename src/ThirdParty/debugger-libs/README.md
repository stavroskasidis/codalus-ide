# debugger-libs (vendored)

The Mono Soft Debugger client libraries, MIT (see `LICENSE`).
- **Source:** [JaneySprings/Mono.Debugger](https://github.com/JaneySprings/Mono.Debugger) at commit `9edc6120533e818e593cb73b0910e9c7990cffb2`. It's the fork of [mono/debugger-libs](https://github.com/mono/debugger-libs) that .NET Meteor and DotRush maintain for .NET apps on Mono. Compared with upstream, it drops the symbol-server packages that aren't on nuget.org, and it has fixes for .NET for Android and iOS.
- **Used by:** M20b's Mono debug adapter (`codalus-monodbg`), to debug .NET apps on the Mono runtime: .NET for Android and iOS, as MAUI apps run.

The libraries:
- `Mono.Debugger.Soft`: the soft debugger's wire protocol.
- `Mono.Debugging`: the session API and the C# expression evaluator (on Roslyn).
- `Mono.Debugging.Soft`: `Mono.Debugging`'s backend for the soft debugger.

Only these three are taken. The repository's `CorApi`, `CorApi2` and `Mono.Debugging.Win32` (with Microsoft's sample EULA) are not.

## Changes from the fork

Each is marked `[Codalus]` in the code.
- **Project files.** They are ours: `net10.0`, central package versions, no strong-name keys. They build without the repository's analyzers (`../Directory.Build.props`), and with C# 12 because the code names variables `field`.
- **Assembly loads.** `SoftDebuggerSession` requests them with suspend policy None. Suspending on them stalls a .NET 10 Android app's runtime during startup, until the debugger is dropped (M20b's checkpoint).
- **Events in the connection log.** With `MONO_SDB_LOG`, `Connection` logs the events it receives, next to the commands.

To update: copy the three folders from a newer commit, keep these project files, and apply the changes above again.
