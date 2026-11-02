# 0090. A Blazor Web App's edits under the Codalus debugger: the server and the browser

- **Status:** accepted (2026-10-30)
- **Date:** 2026-10-30
- **Deciders:** maintainer (the decisions of 2026-10-30, after the maintainer's report on WPoseidon: the IDE as the browser refresh server with the SDK's middleware; every Blazor Web App launched under the Codalus debugger, without `dotnet watch`; .NET 10 WebAssembly apps; the session's capabilities limited to the browser's; the program's update handlers called after each edit; the status saying each side, with Restart when the browser can't take an edit). SDK 11's different middleware was found while testing the change.
- **Amends:** [ADR 0040](0040-hot-reload-while-debugging.md) (a Blazor Web App under `dotnet watch` while debugging), [ADR 0086](0086-a-managed-coreclr-debugger.md) (phase 2, M55)

## Context

The maintainer debugged WPoseidon's DemoApp (a Blazor Web App whose components run as WebAssembly) under the Codalus debugger, edited the Counter page's text and saw *Hot Reload: applied*, but the page didn't change.

WPoseidon's `global.json` pins SDK 10.0.303. `F5` runs a Blazor Web App's server under `dotnet watch` only from SDK 10.0.400 (ADR 0040's update), so it launched the server under the debugger. M55's Hot Reload then applied the delta to `DemoApp.Client.dll` in the server process, which loads it for routing and prerendering, and reported it applied. The component runs in the browser, which nothing told. Two more gaps showed:

- The debugging API applies deltas without calling the program's `[MetadataUpdateHandler]`s. `dotnet watch`'s agent calls them after `MetadataUpdater.ApplyUpdate`, and Blazor re-renders its components from them. Components the server runs (InteractiveServer, or InteractiveAuto before WebAssembly loads) took edits under the Codalus debugger but didn't show them until a navigation.
- Under `dotnet watch` (SDK 10.0.400+), M55b's server took edits from the debugger while `dotnet watch`'s copy was refused and it stopped sending that process updates. Two mechanisms, with the server's startup code never stopping at breakpoints (the debugger attaches after it ran).

What the SDK does, found on SDK 10.0.303 and confirmed in a spike without `dotnet watch`:

- Every Debug build of a .NET 10 WebAssembly app has the browser's Hot Reload agent (`Microsoft.DotNet.HotReload.WebAssembly.Browser`, added by the WebAssembly SDK). It turns on, and sets the runtime's `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, when the page has a script whose `src` contains `aspnetcore-browser-refresh`.
- `dotnet watch`'s middleware, `Microsoft.AspNetCore.Watch.BrowserRefresh.dll` (in every SDK, under `DotnetTools/dotnet-watch/…/hotreload/` in 10), injects that script when loaded as a startup hook and a hosting startup. The script connects to the WebSocket in `ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT` and takes `ApplyManagedCodeUpdates`, `RefreshBrowser` and `GetApplyUpdateCapabilities`. SDK 10.0.1xx to 10.0.4xx share it.
- SDK 11 (RC3) changed the design. Its middleware reads only `ASPNETCORE_AUTO_RELOAD_PROVIDER_ADDRESS` and forwards `/_framework/dotnet-browser-tools/…` requests and WebSockets to a "browser tools provider" that `dotnet watch` hosts and that serves the scripts itself. Loaded without one, it threw at the server's start (found by the shell test, which resolved SDK 11).
- The page's runtime reported `Baseline AddMethodToExistingType AddStaticFieldToExistingType NewTypeDefinition ChangeCustomAttributes AddInstanceFieldToExistingType GenericAddMethodToExistingType GenericUpdateMethod UpdateParameters GenericAddFieldToExistingType`: CoreCLR's .NET 10 set without `AddFieldRva`.

## Options

| Question | Options | Verdict |
|---|---|---|
| How deltas reach the page | The IDE as the refresh server, with the SDK's own middleware; through the page debugger (vscode-js-debug's CDP proxy); a middleware of our own | **The IDE as the refresh server.** The same code path the SDK uses, in any browser, with or without the page debugger. The page proves the script came from the server with a secret it encrypts with the server's RSA key (`ASPNETCORE_AUTO_RELOAD_WS_KEY`), which every update carries back, as `dotnet watch` does. |
| Which `F5` paths | Every Blazor Web App under the Codalus debugger, launched; only where `dotnet watch` isn't used | **Every one, launched.** No SDK 10.0.400 gate, no detach and attach around restarts, and startup code stops at breakpoints. An edit that needs a restart asks (Restart, or go on with the old code), as for other programs. netcoredbg keeps `dotnet watch` (ADR 0040). |
| Which runtimes | .NET 10+ WebAssembly; also 8 and 9 | **.NET 10+.** Older apps use Blazor's built-in `applyHotReload` and an older middleware protocol. Such an app debugs launched, and its WebAssembly edits say they need a restart instead of *applied* (`TODO(issue)`: .NET 8 and 9). |
| Which SDKs | SDK 10; also SDK 11's browser tools provider | **SDK 10.** SDK 11's provider is a protocol of its own, still in preview. Under SDK 11 the app debugs launched and its WebAssembly edits say they need a restart (`TODO(issue)`: the provider, once SDK 11 is out of preview). |
| Capabilities | The server's and the browser's intersection, from a table; wait for the page's answer; the server's only | **The intersection, from a table** (`BrowserHotReload.WebAssemblyCapabilities`), checked against a real page by `BlazorWebAppHotReloadDebugTests`. The session starts at once; an edit the browser can't take is a rude edit before anything applies. |
| The program's handlers | Called after each edit; a follow-up | **Called**, for every program the Codalus debugger launches. The startup hook (`Codalus.Debugging.StartupHook`, ADR 0039) connects to the IDE's pipe (`CODALUS_HOTRELOAD_PIPE`). After each edit it is told the updated types and calls every handler's `ClearCache`, then every `UpdateApplication`, lower-level assemblies first, as `dotnet watch`'s agent does. A func-eval from the debugger was the other way: it needs a thread at a safe point, which a program stopped for an apply often hasn't. |
| Reporting | Each side, with Restart when the browser fails; one *applied* | **Each side.** The status bar says *applied at … (server and browser)*, *(no page open)*, *the browser needs a restart*, or *the browser couldn't apply it*. In the last case the Debug Console has the page's log and a warning offers Restart, since the two now run different code. A page opened later gets the edits from the middleware, which the script posts them to. |

## Decision

Under the Codalus debugger, `F5` on a Blazor Web App's server (`ShellHost.BrowserHotReload.cs`):

1. Checks that the browser can take edits: `debug.hotReload` on, every WebAssembly client targets .NET 10 or later, and the solution's SDK is 10 with the middleware (`BrowserHotReload.UnsupportedReason`).
2. If it can, starts a `BrowserRefreshServer` on a loopback port. The server is launched under the debugger, with the page in the same session (ADR 0027). Its environment adds the middleware to `DOTNET_STARTUP_HOOKS` and `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`, with the endpoint, the key, `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and `__ASPNETCORE_BROWSER_TOOLS=true`. The session's capabilities are limited to the browser's.
3. Then each apply (`DebugHotReload`), after the debugger took the deltas:
   - tells the program's agent the updated types;
   - sends the pages the deltas of projects that run in the browser (the clients and what they reference, `BlazorProjects.BrowserProjectsOf`);
   - sends `RefreshBrowser`, so statically rendered content shows the server's new code.

   A program stopped at a breakpoint runs its handlers when it goes on; the apply doesn't wait for them, and the refresh waits for the next apply.
4. If the browser can't take edits, the app debugs the same way, and an edit to code that runs in the browser says it needs a restart (once in the Debug Console, and in the status bar).

## Consequences

- WPoseidon on SDK 10.0.303, and every .NET 10 Blazor Web App on SDK 10, takes edits in the server and the page while both are debugged, and the server's startup code stops at breakpoints. Under SDK 11 the server takes them, and the browser after a restart.
- Every program the Codalus debugger launches gets the startup hook and an agent thread ("Codalus Hot Reload agent"). Found while testing it: the debugger's launcher (`codalus-debughost --launch`), which runs with the program's environment, ran `DOTNET_STARTUP_HOOKS` although its runtimeconfig turns startup hooks off (.NET 11 RC3). Its agent took the pipe, and the variable it cleared never reached the program. The hook now does nothing in that host, which also keeps a user's own startup hooks out of it. A program with startup hooks turned off (`System.StartupHookProvider.IsSupported=false`) takes edits without its handlers, as before.
- A page connects before its WebAssembly runtime has started. The script skips an edit that comes before the runtime's agent is ready, and still answers that it applied it. The agent fetches the edits kept on the server as it starts, so an edit that came after that fetch and before the agent was ready was lost, and the status said "(server and browser)" (Ubuntu CI, run 41157169266). The server now asks each new page what its runtime can apply until it answers, and holds the page's edits until then; an edit that waits longer than the apply's wait says "sent to the browser" and updates when the page answers. A page whose runtime never starts (rendered on the server only) gets edits once 60 seconds have passed. The same window remains for a runtime that starts later on a page that has been open longer than that, on an enhanced navigation to an interactive page.
- The SDK's middleware and its script's protocol are internal to the SDK. A change would show in `BlazorWebAppHotReloadDebugTests` on the SDKs CI runs; `UnsupportedReason` is where a version without the protocol is turned away.
- The M55b code for the Codalus debugger beside `dotnet watch` is gone; the `dotnet watch` path is netcoredbg's.
- `TODO(issue)`: WebAssembly edits for .NET 8 and 9 apps (Blazor's built-in `applyHotReload`).
- `TODO(issue)`: SDK 11's browser tools provider.
- `TODO(issue)`: CSS and other static files under the Codalus debugger (`UpdateStaticFile`, which `dotnet watch` sends).
- `TODO(issue)`: a standalone Blazor WebAssembly app keeps its dev server under `dotnet watch`.

## Amendment (2026-11-02): AddExplicitInterfaceImplementation, which no runtime reports

The maintainer's report on WPoseidon: under the Codalus debugger, `currentCount++` → `currentCount += 2` in a WebAssembly component's `async Task IncrementCount()`, whose `await`s were commented out, came back as CS9346 ("Update requires emitting explicit interface implementation"), a restart.
- **Why.** Roslyn's analyzer gives the compiler a syntax map for an async method only when it has suspension points. Without one, the compiler can't map the old state machine and emits a new one, a new type whose `MoveNext` and `SetStateMachine` implement `IAsyncStateMachine` explicitly: MethodImpl rows, which need `AddExplicitInterfaceImplementation`. Any edit to an async method without an `await` does this, in a `.cs` file as in a `.razor` one.
- **What the runtimes say.** CoreCLR's `MetadataUpdater.GetCapabilities` and the browser's agent (Mono) both leave it out, though both apply it. `dotnet watch` adds it to every .NET and Mono runtime's set ("All other runtimes (.NET and Mono) support it", `HotReloadClient.AddImplicitCapabilities`, from SDK 10.0.4xx; 10.0.303's didn't), and Mono's soft debugger client adds it to what the runtime reports (mono/debugger-libs#407).
- **Decision.** The Codalus debugger's `codalus/hotReloadCapabilities` (.NET 6 on) and `BrowserHotReload.WebAssemblyCapabilities` add it, so the session's intersection keeps it. `BlazorWebAppHotReloadDebugTests` allows it beside what the page reports. The BlazorHosted sample's tick now adds what an async method without an `await` returns, and the browser's edit in that test, the `dotnet watch` one and the smoke test changes that method, so each gives the page a new state machine. `HotReloadDebuggingTests` does the same on CoreCLR.
- **Found while testing it:** the browser's debugger doesn't stop at a breakpoint in the edited method's new state machine (its tick, under `dotnet watch` and under the Codalus debugger alike), so the tests stop in the tick that calls it. `TODO(issue)`: breakpoints in a state machine an edit added, in the browser.
