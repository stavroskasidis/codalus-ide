# 0059. The plugin developer experience

- **Status:** accepted (M22b)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

With API 1.0 frozen (ADR 0058), M22 asks for the tools that make writing a plugin pleasant: a template, a way to test plugins without the IDE, running one in a development instance with a debugger, and an API reference. M22a also left a gap: icon themes made of SVG icons, which most VS Code icon themes are, drew only their raster icons.

Constraints:
- Plugins run out of process (ADR 0003), so a debugger has to attach to the right plugin host, and before the plugin's code runs.
- The shell never parses SVG at run time and stays Native AOT clean (ADR 0019, D24).
- The documentation must not drift from the frozen API.

## Decision

- **Testing: an in-memory context, not a host.** `Codalus.Plugins.Testing` implements `IPluginContext` with fakes that record what a plugin registers and shows, and drive it as the IDE would. Commands' arguments and results go through JSON as over the protocol. The fakes also cover trees, views and custom editors, tasks, and file system, authentication and source control providers. Tests are ordinary fast unit tests. A host-based harness would test the protocol, which our own integration tests already do.
- **The template** (`Codalus.Templates`, `dotnet new codalus-plugin`) makes a solution with the plugin and its tests. The plugin class is `Plugin` and the test class `PluginTests`, so a dotted name (`Contoso.Greeter`) stays a valid identifier. A test packs the API from the repository under a unique prerelease version, instantiates the template, builds it, runs its tests and runs the plugin in the real host.
- **Development instance: a debug host of its own.** Run and Debug Plugin in Development Instance build the startup plugin project and start a second IDE (`--plugin-dir`, user data in the solution's `.codalus/plugin-dev`). With `--debug-plugin-host <file>`, the `--plugin-dir` plugins run in a host group of their own (`debug`), activated at startup. That host writes its process id to the file and holds activations until a debugger attaches (up to a minute), and their activation has no time limit. The first IDE attaches netcoredbg to that process. Only the plugin under development is in the debugged process, and a breakpoint in `ActivateAsync` is hit.
- **The API reference is generated and checked.** A test renders `docs/user-guide/plugin-api.md` from the assembly (reflection, nullability) and its XML documentation, and fails when the page is stale or a public member is undocumented. The site serves it with the guide.
- **SVG icons are converted outside the shell.** The build-time icon compiler (`eng/icons/CompileIcons.cs`) is also compiled into `codalus-pluginhost` (`CODALUS_SVG_CONVERTER`), whose `--compile-icons` mode turns a theme's SVG icons into the same drawing operations as our own icons. The result is cached under the cache folder by the files' paths, sizes and times. The shell replays the operations into geometries (`CompiledIcons.Build`), so icon themes' SVG icons are vectors, colored as their files say. An SVG the parser doesn't support (anything but paths and groups with fills) falls back to our icon and is named in the log.

## Consequences

- The packages (`Codalus.Plugins.Abstractions`, `Codalus.Plugins.Testing`, `Codalus.Templates`) aren't on nuget.org until M22c, after the maintainer reserves the prefix. Until then the guide explains packing them from a clone.
- The SVG converter accepts the subset our icons use. Icon themes that use gradients, strokes, `<rect>` or `<circle>` draw those icons with ours until the converter grows; the log says which.
- Debugging needs netcoredbg, like every debug session.
