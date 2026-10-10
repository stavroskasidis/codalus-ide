# Blazor Web App with WebAssembly components

A Blazor Web App with interactive WebAssembly rendering (`dotnet new blazor --interactivity WebAssembly --empty`), for the compound debugging tests (M16d, ADR 0027): the server debugs under netcoredbg and the page under vscode-js-debug in one session.

- `BlazorHosted.Client/Pages/Counter.razor` renders with `InteractiveWebAssembly`: it is prerendered on the server, then runs in the browser, so a breakpoint at `// [initialized]` stops twice, once in each.
- `BlazorHosted/wwwroot/app.js` is the page's script, with a `// [script]` marker for a JavaScript breakpoint.
- The launch profiles keep the template's `inspectUri` (the WebAssembly debugging proxy), with `launchUrl: counter`. `launchBrowser` is off so tests that only run the app don't open a browser; debugging opens the page anyway.
