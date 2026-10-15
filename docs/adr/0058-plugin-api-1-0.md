# 0058. Plugin API 1.0 and its contribution points

- **Status:** accepted (M22a)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

ADR 0003 set up the plugin API as 0.x: minor versions may break, and new APIs start as *proposed* (`context.Proposed()`, `enableProposedApi`) until built-in plugins prove them. By M21b the built-ins depended on every proposed API: the .NET test plugin (tests, workspace, build, debug, terminals), the GitHub, Azure DevOps and GitLab plugins (hosting, secrets), the dictionary visualizer (visualizers). Third-party plugins could only use them with `--enable-proposed-api`. The roadmap's M22 asks for a frozen 1.0 and the contribution points that were still missing: progress, themes and icon themes, debuggers, declarative views (D2) and custom editors, file system, authentication and source control providers.

Constraints:
- The shell is Native AOT, and plugins run out of process (ADR 0003). Every new surface has to cross the plugin protocol as data.
- Plugin UI is declarative (D2, and ADR 0003's promise of an ADR before richer plugin UI: this is it): the shell draws it with its own controls, so it follows the theme and stays accessible. Webviews come after 1.0, as a proposed API.
- The shell's icons are compiled at build time and SVG is never parsed at run time (D24, M34a).

## Decision

**1.0.0 freezes the API.**
- The proved proposed APIs become members of `IPluginContext`: `Tests`, `Workspace`, `Build`, `Debug` (with visualizers), `Terminals`, `Hosting` and `Secrets`. `IProposedApi` stays, empty, for future proposals.
- The members are default interface members that throw `NotSupportedException`. A test double of `IPluginContext` doesn't have to implement them all, and members added in 1.x don't break implementations.
- **The policy after 1.0** (ADR 0003's): minor versions only add. A member is removed only in a major version, after being marked `[Obsolete]` with the diagnostic id `CODALUS0001` (`PluginApi.DeprecationDiagnostic`) for at least two minor releases and listed in the changelog. ADR 0003 named the id `DAEDALUS0001` before the rename.
- **The frozen surface** is `src/Codalus.Plugins.Abstractions/PublicAPI.txt`. `PublicApiTests` fails when the public API changes and the file doesn't; `CODALUS_UPDATE_PUBLIC_API=1` rewrites it. A removal shows up in review as a deleted line.

**New in 1.0**, each proved by a sample plugin and tests:

| Surface | Shape | Sample |
|---|---|---|
| Progress | `IWindow.WithProgressAsync(title, work, cancellable)`: a task in the status bar (the workbench's `ProgressService`), cancellable from its ✕ | HelloWorld |
| Color themes | `contributes.themes`: VS Code's theme files (`colors`, `tokenColors`, `semanticTokenColors`) mapped onto our tokens (`ThemeFiles`) over a built-in base | MidnightTheme |
| File icon themes | `contributes.iconThemes`: VS Code's icon theme files, raster icons only (SVG icons fall back to ours, named in the log) | BlockIcons |
| Debuggers | `contributes.debuggers`: a DAP adapter over stdio per launch configuration `type`; `launch.json` configurations of that type start it | scripted adapter in the tests |
| Declarative views | `IWindow.SetView(viewId, ViewNode)` and `OnViewEvent`: stacks, texts, buttons, text boxes, check boxes, dropdowns, tables, toolbars; tool windows of kind `view` | TodoBoard |
| Custom editors | `contributes.customEditors` and `RegisterCustomEditor`: a file type opens in a declarative view; `SetCustomEditorText` makes the document dirty and Save writes it | TodoBoard |
| File systems | `IFileSystems.RegisterProvider(scheme, provider)`: mounted under `\\codalus-plugin.invalid\<scheme>` (Windows) or `/codalus-plugin/<scheme>`, read and written through the workbench's `FileSystems` like a remote's | HelloWorld `MemoryFiles` |
| Authentication | `contributes.authentication` and `IAuthentication`: providers offer sessions, any plugin asks for one; another plugin's provider asks the user's consent; the Accounts page lists the sessions | HelloWorld `MemoryAccounts` |
| Source control | `ISourceControl.RegisterProvider(id, label, root, provider)` and `Update(state)`: groups of files, a commit box and originals to compare with, in the Source Control Providers window | HelloWorld `FolderScm` |

Activation is implicit, as for 0.x's contributions: `onAuthenticationRequest:<id>` when a session is asked of a provider, and `onCustomEditor:<viewType>` when a file opens in the editor. A plugin with only themes or icon themes has no code at all (`entry` is optional for declarative plugins).

## Consequences

- Plugins that asked for `^0.3.0` don't load on 1.0. They move to `^1.0.0` and use `context.Tests` and the other members instead of `context.Proposed().Tests`. Every built-in plugin and sample did so in M22a. 0.3.0 was current for a few hours, so the break costs little.
- The declarative views are deliberately small. Forms, lists and tables cover the built-ins' and the samples' needs. Richer UI waits for webviews, which go in as a proposed API after 1.0.
- Icon themes made of SVG icons, which most VS Code icon themes are, drew ours for those icons in M22a. *Update (M22b, ADR 0059):* their SVG icons are converted outside the shell into vector drawings; the shell itself still never parses SVG.
- The Source Control Providers window is separate from the Git window. Git stays built in and isn't rewritten on this API, so the API doesn't constrain Git's depth (ADR 0008, 0020, 0031).
- The development experience (template, testing package, development instance, API reference) is M22b. Distribution (the NuGet marketplace, signatures, permissions, the Components page) is M22c.
