# Contributing

Thanks for your interest in Daedalus Studio. The project has reached its first feature-complete milestone (MVP, M7). These guidelines describe how changes get in.

## Ground rules

- **License.** Contributions are accepted under the MIT license. You certify that you have the right to submit them (the [Developer Certificate of Origin](https://developercertificate.org/)); sign off your commits with `git commit -s`.
- **Dependencies.** Every new dependency must use an OSI-approved license compatible with MIT. Check the license before adding a package, and record it in `THIRD-PARTY-NOTICES.md` in the same change. If a license is ambiguous, ask in the issue before opening a pull request.
- **No Microsoft-proprietary components.** That rules out vsdbg, the C# Dev Kit, and Visual Studio branding or logos in the product. Windows-only UI technologies (WinForms, WPF, WinUI) are out of scope.
- **No telemetry that is on by default.** Crash reports stay on the user's machine and are opt-in.
- **Performance budgets are enforced** (`benchmarks/budgets.json`, ADR 0002). A change that regresses a budget needs a justification in its description.
- **Conduct.** Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as described in [SECURITY.md](SECURITY.md).

## Workflow

1. Open an issue first for anything bigger than a small fix, so the approach can be agreed on before you write code.
2. For significant decisions, write an ADR (`docs/adr/NNNN-title.md`, from `0000-template.md`) before the code.
3. Fork, branch, and open a pull request against `master`. CI builds and tests on Windows, macOS and Linux for every pull request; it must be green.
4. Keep changes covered by tests: unit tests, integration tests against the `samples/` fixtures, and headless UI tests where the UI stack allows them.
5. Never block the UI thread. All I/O and analysis is asynchronous and cancellable, and long operations report progress.
6. No silent stubs. If you defer something, leave a `TODO(issue)` that names the tracking issue.
7. The plugin API (`Daedalus.Plugins.Abstractions`) is a public contract: changes follow ADR 0003's versioning policy and go into the changelog in `docs/plugins.md`.

## Building

See the README and `CLAUDE.md` for commands. `eng/get-netcoredbg.sh` fetches the debugger for the debugging tests, and git must be on the `PATH` for the Git tests.

## Code style

`.editorconfig` is authoritative, and builds treat warnings as errors. C# uses file-scoped namespaces, `_camelCase` private fields, `s_camelCase` private statics, and `CancellationToken` on every async API. The shell must stay Native-AOT clean: use compiled bindings and source-generated JSON.
