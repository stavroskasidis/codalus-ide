# Blazor WebAssembly fixture

A standalone Blazor WebAssembly app (`dotnet new blazorwasm --empty`) for the M16c tests: it runs on the WebAssembly dev server, takes Hot Reload, and is debugged in a Chromium browser through the dev server's debugging proxy (`inspectUri`). `Counter.razor` has `// [name]` markers on the lines tests break on. `launchBrowser` is off so tests don't open a browser; the `http` profile's `launchUrl` is the counter page, so F5 lands where the breakpoints are.
