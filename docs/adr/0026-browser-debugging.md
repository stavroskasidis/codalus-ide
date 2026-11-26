# 0026. Browser debugging through vscode-js-debug

- **Status:** accepted (M16c plan, 2026-10-10)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M16c asks for Blazor WebAssembly debugging and JavaScript debugging in the browser. netcoredbg (ADR 0007) can't do either: Blazor WebAssembly runs .NET on the browser's WebAssembly runtime, and JavaScript runs in the browser's engine. Both are reached through the Chrome DevTools Protocol (CDP).
- The Blazor WebAssembly dev server (`Microsoft.AspNetCore.Components.WebAssembly.DevServer`, MIT) includes a debugging proxy. A CDP client that connects through it (the launch profile's `inspectUri`, `/_framework/debug/ws-proxy`) sees the app's C# code as sources, with breakpoints, stepping and variables.
- `vscode-js-debug` (Microsoft, MIT) is VS Code's JavaScript debugger. It speaks DAP on one side and CDP on the other. It ships as a standalone DAP server (`js-debug-dap`, about 1.2 MB) that runs on Node.js, which M16b already downloads (ADR 0024). VS Code's Blazor WebAssembly debugging is `js-debug` with `inspectUri`.

A spike on 2026-10-10 drove `js-debug-dap` 1.140.0 from a small DAP client:
- **JavaScript:** with headless Chrome on a static page, a breakpoint in a script bound and was hit, with the call stack and locals.
- **Blazor WebAssembly:** on the dev server of `samples/BlazorWasm`, a breakpoint in `Counter.razor` bound and was hit, with `BlazorWasm.Pages.Counter.OnInitialized` at `Counter.razor:16` as the top frame and `this` in the locals.
- **Edge:** headless Edge couldn't be attached to on the development machine, where Edge was already running. Chrome first, then.

## Decision

**The adapter.** `js-debug-dap` at a pinned version (1.140.0), downloaded from the GitHub release on first use, verified by SHA-256, and unpacked into the Node cache next to the web language servers (`DAEDALUS_NODE_CACHE`). It runs on the M16b Node runtime as `dapDebugServer.js <port> 127.0.0.1`, one server per debug session, and Daedalus connects to it over TCP. Starting a browser debug session is the consent to download it; the Debug Console shows the progress.

**Sessions.** `js-debug` runs one session per target: the launch session asks the client to start a child session (`startDebugging`) for each page and worker it attaches to. The debugger library gets:
- **Reverse requests in `DapConnection`:** `startDebugging` is answered; the rest are still refused.
- **`IDebugAdapter`**, which `DebugService` uses instead of the netcoredbg class. netcoredbg keeps its implementation.
- **`JsDebugAdapter`,** a composite with the same interface:
  - it owns the server process and the launch session;
  - it opens a connection for each child session, replays the breakpoints and exception settings to it, then sends `configurationDone`;
  - it routes requests (threads, stack, variables, stepping, evaluation) to the child that last stopped, or the first;
  - it hands out its own breakpoint ids and maps every child's ids onto them, so `DebugService` sees one session.

**Browsers.** Chromium browsers only: Chrome, then Edge, then Chromium, at their usual install locations on each OS, or the one the setting `debug.browser` names (`auto`, `chrome`, `edge` or a path). The browser runs with a temporary profile, so the user's own browser and profile are untouched. `DAEDALUS_BROWSER_HEADLESS=1` runs it headless, for the tests. Firefox and Safari are gaps: neither speaks CDP well enough for `js-debug`.

**What uses it in M16c.**
- **F5 on a Blazor WebAssembly app** (a project with the WebAssembly dev server whose launch profile has `inspectUri`): the dev server starts as with Run Without Debugging, then a browser session opens its URL through the proxy. C# breakpoints in `.razor` and `.cs` files and JavaScript breakpoints in the app's scripts work in the same session. Stopping the session stops the dev server.
- Everything else still debugs with netcoredbg.

## Consequences

- The first browser debug session needs network access: Node.js (if M16b hasn't downloaded it) and about 1.2 MB for `js-debug`. Updating `js-debug` means a new pinned version and checksum.
- There's one debugger UI for both kinds of session. Features netcoredbg has and `js-debug` doesn't (modules, function breakpoints and some exception filters) follow the capabilities `js-debug` reports.
- **Not in M16c** (`TODO(issue)`, M16d):
  - compound sessions, with the server under netcoredbg and the page under `js-debug` at once (a Blazor Web App's interactive WebAssembly, or an ASP.NET Core app's scripts), because `DebugService` runs one session at a time;
  - a "debug a URL" launch for sites without a .NET dev server.
- Headless Edge, and browsers on machines without a Chromium browser, are tested only where one exists. Tests skip without a browser locally, and require one in CI (`ubuntu-latest` has Chrome).

## Amendment (2026-10-21): the page opens after the proxy's handshake

Under load, F5 on a Blazor WebAssembly app sometimes never stopped at a C# breakpoint: the smoke step `blazor.wasmDebug` on linux-arm64, whose js-debug log shows the race below, and `BlazorWasmTests` on Linux CI, with the same symptoms. The page ran, but the debugging proxy never reported the app's runtime: no `Mono.runtimeReady`, none of the app's sources.
- **Why:** when the browser reports the page attached, the proxy (BrowserDebugProxy, .NET 10) sets `globalThis.dotnetDebugger` on the current document, then registers it for the page's next documents (`Page.addScriptToEvaluateOnNewDocument`). The runtime pauses for the proxy at its start (`mono_wasm_runtime_ready`) only where the flag is set, and that pause is how the proxy learns that the runtime is ready. js-debug opens the launch's URL as soon as it has attached to the page, without waiting for the proxy. In the failing trace, the app's document was created 17 ms before the proxy's handshake finished, so the runtime started without the pause, and the proxy never checked again.
- **Decision:** behind the proxy (a launch with `inspectUri`), `JsDebugAdapter` sends js-debug's `skipNavigateForTest`, so the page stays at `about:blank`. It opens the page itself (`location.replace` through the page session's `evaluate`) once js-debug has answered that session's start. js-debug answers it after the browser has answered the session's `Debugger.enable`, which comes after the proxy's first handshake step, and the proxy sends the second step as soon as it reads that answer. So the flag is registered before the navigation reaches the browser. The breakpoints, set while the page was blank, carry over to the app's document. If the page's session can't be attached, a session still attaching opens the page once it has started (each counts from js-debug's `startDebugging`, so a failure can't take the page while another session is connecting); with none left, the Debug Console says the page couldn't be opened.
- **Consequences:** `skipNavigateForTest` is an undocumented js-debug test hook, to be checked on every js-debug update (noted at `JsDebug.Version`). `BrowserDebugTests` puts a stand-in proxy between js-debug and the browser that does the handshake and answers it late; before this change its page opened without the flag on every run. `BrowserPageOpenTests` covers the failed attaches with a scripted js-debug (`Codalus.LspConformance.Server` started with js-debug's server arguments). Debug a Web Page (no `inspectUri`) is unchanged.

## Amendment (2026-11-26): browsers installed from Flatpak

On Linux, Chrome, Edge and Chromium are often installed from Flathub. `auto` didn't find them: Flatpak exports an application's launcher as its application ID (`/var/lib/flatpak/exports/bin/com.microsoft.Edge`), not as `microsoft-edge`. Setting `debug.browser` to that launcher wasn't enough either. The sandbox sees neither the home folder nor the host's `/tmp`, so the browser couldn't use the profile the IDE gives it in its state folder (ADR 0092), and the IDE couldn't read that profile's `DevToolsActivePort`.
- **Finding them:** after each kind's native names on the PATH, `Browsers` looks for Flathub's application (`com.google.Chrome`, `com.microsoft.Edge`, `org.chromium.Chromium`) in the user's exports (`$XDG_DATA_HOME/flatpak/exports/bin`), then the system's (`/var/lib/flatpak/exports/bin`), when `flatpak` is on the PATH. So the order is still Chrome, then Edge, then Chromium, and a native browser of a kind comes before a Flatpak one. A `debug.browser` path in a `flatpak/exports/bin` folder is a Flatpak too: its kind comes from the known IDs, else Edge if the ID says so, else Chrome-like.
- **Starting them:** `Browser.Executable(profile)` writes a launcher script into the profile folder, `exec flatpak run --filesystem=<profile> <app> "$@"`, and js-debug and the agent's browser start that instead of the exported launcher. A page Debug a Web Page opens from a file has its folder added read-only (`--filesystem=<folder>:ro`): the sandbox showed Edge's network error page for it otherwise. The grants last for that run only, so the user's Flatpak overrides are left alone. `flatpak run` becomes `bwrap` under the same process, so the process js-debug and `AgentBrowser` watch is the sandbox's. The browser shares the host's network, so the DevTools port on the loopback works as it does natively.
- **Stopping them:** a SIGTERM to the outer `bwrap` ends only that process, and the sandbox's inner `bwrap` and the browser keep running under a new parent (tried with Flathub's Edge 158). So stops have to close the browser over CDP (js-debug does) or kill the whole tree. `ProcessTree.Kill` stops each process before it walks its children, so it finds the inner `bwrap` while it is still a descendant.
- **Tests:** `BrowserDebugTests` start the browser the way the IDE does (a profile in the test's folder and a DevTools port), so on a machine whose only browser is a Flatpak the browser tests run against it. A stand-in `flatpak` checks the launcher's quoting.
