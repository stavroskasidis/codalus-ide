# 0027. Web front-end depth: compound debugging, HTML in Razor, npm, ESLint and Prettier

- **Status:** accepted (M16d plan, 2026-10-10; the maintainer's standing go-ahead for M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M16a–M16c (ADRs 0023, 0024, 0026) gave Razor editing, the web language servers on an on-demand Node.js, and browser debugging for Blazor WebAssembly. M16c's checkpoint left M16d to finish M16:
- **Compound sessions:** a Blazor Web App's WebAssembly components run in the browser, its server under netcoredbg. An ASP.NET Core app's scripts run in the browser too. `DebugService` runs one session, on one adapter.
- **Debugging a URL** without a .NET dev server (a static site).
- **npm scripts** as tasks, and npm restore.
- **Scoped CSS** (`.razor.css`), and **HTML and CSS inside Razor files**, which keep only the Razor grammar and the C# host's component completion.
- **ESLint and Prettier.**

## Decision

**Compound sessions are one session on a composite adapter.** `CompoundDebugAdapter` (in `DaedalusStudio.Debugging`) is an `IDebugAdapter` over several adapters, the way `JsDebugAdapter` already is over `js-debug`'s child sessions. So `DebugService`, the debugger windows and the editor keep their single-session model.
- **Joining:** the first adapter (netcoredbg, the server) is launched and configured with the session. Others join later (`AddAsync`), here `js-debug` once the server prints "Now listening on". The session's breakpoints, function breakpoints and exception settings are replayed to each adapter that joins.
- **Ids:** thread and frame ids and variable references are the adapter's own times four plus its index. A request goes to the adapter its id came from. A joined adapter's threads are named after it (`Browser: …`).
- **Breakpoints:** the session's breakpoints have the composite's own ids. A breakpoint is sent to every adapter, and it is bound when any adapter bound it. A `.razor` breakpoint in a WebAssembly component binds in both: the server prerenders the component, then the browser runs it.
- **Stops:** one shows at a time. A stop in another adapter while one shows waits, and shows when the shown one resumes, instead of pausing everything. Break All pauses every adapter, and Continue from a pause continues every paused one.
- **End:** the session ends when any adapter ends. Closing the browser stops the server, and stopping the server closes the browser.

**Which runs compound:**
- A web project whose launch profile has `inspectUri` (the Blazor Web App template's, with `UseWebAssemblyDebugging`) always debugs its page with the server, as M16c's WebAssembly apps do.
- Other web projects whose profile launches a browser do so when the setting `debug.javaScript` is on (the default, as in other IDEs).
- Without a Chromium browser, the server debugs alone and the profile's browser opens as before. A WebAssembly app says why its components won't stop.

**Debug a Web Page** (`debug.webPage`, and F5 on an open HTML file with no startup project): any http, https or file URL, or an HTML file, opens in the browser under `js-debug` alone. The web root is the file's folder, or the solution's.

**HTML in Razor through a projection, not a Razor language server.** `RazorHtmlProjection` turns a Razor file into its HTML: the same text with the C# and the Razor syntax replaced by spaces, line breaks kept, so offsets and positions are the same. It is a small Razor scanner:
- `@expressions`, `@(…)`, comments, directives, `@code` blocks and directive attributes (`@onclick="…"`) are blanked;
- in `@{…}`, `@if`, `@foreach` and the other statements, the C# is blanked and the elements inside (and `@:` lines) stay.

The shell sends the HTML to the HTML server as `Counter.razor__virtual.html`, next to the file. At a markup position the editor asks the HTML server too, and merges its completion (elements, attributes, CSS in `<style>`) and hover with the C# host's. The projection's diagnostics are dropped, because blanked C# would make false ones. This is how VS Code's Razor support feeds HTML to the HTML language service. The scanner is an approximation of Razor's parser, as `RazorContext` already is (ADR 0023).

**Scoped CSS.** `X.razor.css` nests under `X.razor` by the usual rule (an added extension). In its selectors the shell adds completion items next to the CSS server's: the class names and ids of the component's markup (its projection, so C# expressions don't count), and Blazor's `::deep`. While wiring this, completion commits were found to ignore an item's edit range. They now honor it, so a CSS item like `::after` replaces the `::` typed before it.

**npm: the Task Runner Explorer.**
- **Finding scripts:** `package.json` files are found under the solution's folder and its projects' folders (not in `node_modules`, `bin` or `obj`). The Task Runner Explorer lists their scripts.
- **Running and restoring:** Run starts `npm run <script>` in a terminal. Restore npm Packages runs `npm install`, with its output in the npm output.
- **Bindings:** `"-vs-binding"` in `package.json` (the Task Runner format other IDEs write) binds scripts to Before Build (a failure stops the build), After Build, Clean and Project Open.
- **Which npm:** the user's npm on PATH runs them. Without one, or with `npm.runtime` set to `builtIn`, it's the built-in Node.js's (ADR 0024).
- **Restore on open:** when a trusted solution opens with packages not installed, `npm.restoreOnOpen` asks (default), restores, or does nothing.

**ESLint is a linter service; diagnostics may be pulled.**
- **Linters:** `LanguageServiceDescriptor.IsLinter` registers a server that gets its languages' documents next to their language service, only those it `Accepts` (files under an ESLint configuration). It contributes diagnostics and quick fixes, never completion or navigation.
- **The server:** ESLint is vscode-eslint's server, shipped in `vscode-langservers-extracted` with the HTML and CSS servers, running the project's own `eslint`. Its settings are answered per file (`workspace/configuration` with the file as `scopeUri`), and its own requests (`eslint/noLibrary`, …) are acknowledged and logged.
- **Pull diagnostics:** it reports diagnostics only when asked (LSP 3.17 pull diagnostics). So the language service asks every server that declares a `diagnosticProvider` after a document opens, changes or saves, and again on `workspace/diagnostic/refresh`.
- **Several sources per file:** `DiagnosticsStore` keeps each server's diagnostics of a file separately, so ESLint's don't replace TypeScript's.
- **Quick fixes:** the lightbulb merges the linters' quick fixes. ESLint's are commands (`workspace/executeCommand`), whose edits come back as `workspace/applyEdit`, which the client now handles.

**Prettier is a formatter, not a server.** Format Document and format on save run the project's own Prettier, when it is installed in `node_modules` above the file and a Prettier configuration is found (`.prettierrc*`, `prettier.config.*`, or `"prettier"` in `package.json`). It runs as `prettier --stdin-filepath <file>` on Node.js, and only the changed middle of the text is replaced. The setting `web.prettier` turns it off.

## Consequences

- **Not covered:**
  - **Fingerprinted scripts:** `@Assets["app.js"]` serves `app.<hash>.js`, which `js-debug` can't map back to `wwwroot/app.js`, so breakpoints there don't bind. Plain URLs work. `TODO(issue)`.
  - **.NET 11's Blazor templates** (release candidate) have no `inspectUri` and no `UseWebAssemblyDebugging`: how their WebAssembly components debug is for M30b.
  - **Other browsers:** Firefox and Safari, as `js-debug` needs the Chrome DevTools Protocol.
- **The projection is approximate:** markup the scanner misreads gets plain-text HTML features, and HTML diagnostics in Razor files are dropped.
- **Code and network the user didn't write:** npm scripts, `npm install` and the project's ESLint and Prettier run the project's code, so restore is limited to trusted folders. They need network access the first time.
- **Pull diagnostics and linters are general:** any server contributed later can use them.
