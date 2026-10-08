# Contributing

Thanks for your interest in Daedalus Studio. The project is pre-alpha (Milestone 1), and the guidelines below will grow with it.

## Ground rules

- **License.** Contributions are accepted under the MIT license. Every new dependency must use an OSI-approved license compatible with MIT. Check the license before adding a package, and record it in `THIRD-PARTY-NOTICES.md` in the same change. If a license is ambiguous, ask the maintainer before committing.
- **No Microsoft-proprietary components.** That rules out vsdbg, the C# Dev Kit, and Visual Studio branding or logos in the product. Windows-only UI technologies (WinForms, WPF, WinUI) are out of scope.
- **No telemetry that is on by default.**
- **Performance budgets are enforced.** Benchmarks run in CI. A change that regresses a budget needs a justification in its commit message.

## Workflow

1. Commit directly to `master` (no pull requests while the project is pre-public). Commit messages carry no AI co-author trailers.
2. For significant decisions, write an ADR (`docs/adr/NNNN-title.md`, from `0000-template.md`) before the code.
3. Keep changes covered by tests: unit, integration against `samples/` fixtures, and headless UI tests where the UI stack allows them.
4. Never block the UI thread. All I/O and analysis is asynchronous and cancellable, and long operations report progress.
5. No silent stubs. If you defer something, leave a `TODO(issue)` that names the tracking issue.
6. Push to `master`. CI builds and tests on Linux for every push, and runs the full Windows/macOS/Linux matrix and the benchmarks nightly (or on manual dispatch).

## Code style

`.editorconfig` is authoritative, and builds treat warnings as errors. C# uses file-scoped namespaces, `_camelCase` private fields, `s_camelCase` private statics, and `CancellationToken` on every async API.
