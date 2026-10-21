# 0082. New Project: the classic IDE's three-page wizard, in the shell

- **Status:** proposed (0.6.0, 2026-10-21), implemented on `claude/new-project-wizard-06`; the maintainer accepts it by merging the pull request (decision D37 in the [roadmap](../roadmap.md#decision-register))
- **Date:** 2026-10-21
- **Deciders:** maintainer
- **Amends:** [ADR 0003](0003-plugin-api.md) (project templates are no longer a built-in plugin), [ADR 0004](0004-project-system.md) (`dotnet new` runs in a neutral folder with `--no-restore`; the Templates plugin is retired), [ADR 0033](0033-project-items-templates-and-folder-mode.md) (New Project's templates)

## Context

File → New → Project was the built-in Templates plugin (`codalus.templates`): a quick pick for the template, another for the language, an input box for the location and one for the name, then `dotnet new` in the location. The maintainer asked for the classic IDE's wizard instead: one window with Back, Next and Create over three pages, **Create a new project** (recent templates, search, language, platform and project type filters, rows with an icon, a description and tags), **Configure your new project** (the project name, the location with recent locations and Browse, the solution name, "Place solution and project in the same directory", where it will be created) and **Additional information** (the template's own options, the framework labeled with its support).

What stood in the way:
- The plugin API's windows (`Plugin.cs`, `Views.cs`) have notifications, quick picks, input boxes, progress and declarative views for tool windows and custom editors only: no modal or owned window, no rows with icons and chips, no path box, no enabled conditions. Roadmap D2 put richer plugin UI (webviews) after 1.0.
- `dotnet new` has no machine-readable output for a template's parameters, frameworks, IDE options or start files.
- The plugin ran `dotnet new` in the location, so a `global.json` there chose the SDK and the template's restore post-action ran MSBuild whether or not the folder was trusted (ADR 0004 keeps MSBuild in the build host, for trusted folders).
- No shell window uses `ShowDialog`, and the shell's prompts (the trust prompt among them) draw only in the main window's dialog layer.

## Options

1. **Extend the plugin API with a wizard**: about ten public types under ADRs 0003 and 0058, for one consumer, ahead of D2's decision on webviews.
2. **Move New Project into the shell**, beside Add New Item and `TemplateService` (ADR 0033), and retire the plugin.
3. **Read templates with the Template Engine API** (`Microsoft.TemplateEngine.Edge`) in a host process. Rejected: no host runs without a solution, the engine's version would drift from the SDK's templates, and the SDK is the user's CLI.

## Decision

**Option 2.** New Project is the shell's (`Views/NewProjectWindow`, `ViewModels/NewProjectViewModel`, `ShellHost.NewProject.cs`), its catalog and creation the Workbench's (`Projects/ProjectTemplateCatalog`, `NewProjectPlan`, `NewProjectService`, `NewProjectHistory`, `AspireEnlistment`), and its commands, menus, key and setting register through `BuiltIns.Templates.cs`, the registries plugins use.

**Where template data comes from.** `dotnet new list --type project` (the CLI refreshes its cache and says which short names and languages are available), then the template engine's cache (`templatecache.json`) and each template's own files in its package: `ide.host.json`, `template.json` and `localize/templatestrings.<culture>.json`. This is a recorded exception to the rule that the user's CLIs are read through their machine-readable output: `dotnet new` has none for this. The cache isn't a contract, so it's read case-insensitively and tolerantly, a template it can't read falls back to the list's columns and its `--help`, recorded fixtures for two SDKs and a real-SDK test under a temp hive check it, and `docs/release/dotnet-upgrade.md` has a line for it.

**Groups and members.** A row is a template group in one language; its members are the group's identities (one per SDK channel). The chosen framework picks the member, and that member gives page 3's options, its CLI names, its localized strings and the files to open, so xUnit for .NET 10.0 never shows options only its 11.0 template has. Frameworks are labeled from `DotnetSupport` (Long Term Support, Standard Term Support, Preview, Out of support); the default is the newest released one, else the template's, and a remembered choice wins.

**Running `dotnet`.** Every `dotnet new` runs in the neutral state folder with `--no-restore`, one process per step, cancellable; `TemplateService` alone adds the hive argument. A step that runs past `TemplateService`'s three-minute limit fails with what it printed; only the user's Cancel is a cancel. Restore is MSBuild, so a trusted solution restores through the build host once it's open (`projects.restoreOnOpen`). A trusted location whose `global.json` pins an SDK that can't build the chosen framework gets a warning on page 3.

**Trust on page 2.** "Trust this location: projects in it are built and their packages restored" is a check box, unchecked for a new location and checked and disabled under a trusted one. The location is trusted once Create has made the project, so a Create that fails or is cancelled trusts nothing, and the new solution opens with that choice and no prompt.

**An owned window that asks everything itself.** The wizard is `Show(owner)`, like the setup and start windows, not modal: installing a missing template pack (page 1's Install), trust (page 2) and installing the Aspire templates (page 3's check box description is the consent) are all asked on its pages. It closes once the files are written, before the main window closes or opens any solution. Add New Project closes when its solution closes or another workspace opens, and adds to the solution folder selected when it opened; New Project stops offering to add to a workspace that is no longer open, and a project made for one that closed during Create is reported, not added elsewhere. Opening another kind of New Project (Add New Project, New Solution, a template) replaces an open wizard that isn't creating.

**Adding through the build host.** The project joins the new or open solution (or folder) through the build host's AddProject, which works on trusted and untrusted solutions and on folders; Aspire's references go the same way. Container support waits for the build host's evaluation of the new project (the build host answers AddProject before it evaluates), since it reads the project's framework, web SDK and references. With the solution and project in the same directory, Aspire's AppHost and ServiceDefaults go beside the project's folder, not inside it.

**The command contract.** `templates.newProject` (optional `{ template, language, fromStartWindow }`) returns the created project's path once it's open, or null when cancelled; `templates.newSolution` (the wizard on Blank Solution) returns the solution's path; `solution.addNewProject` is the wizard in add mode. The ids and the setting `templates.defaultLocation` are the plugin's, so plugins that run the commands are unaffected. `solution.getInfo` and `solution.addProjectFile` stay for plugins.

**The retired plugin.** The plugin host never loads a copy of `codalus.templates` left behind (a user plugins folder, `--plugin-dir`, a portable install unpacked over an older one) and logs where it is, and no plugin can redeclare a command or setting the shell owns. HelloWorld's `hello.ask` keeps quick picks and input boxes with Browse tested through the real host. `PublicAPI.txt` doesn't change.

**Persistence.** `<state>/new-project.json` keeps recent templates (pinned first), recent locations, the filters, the framework per template and option values by their `ide.host.json` persistence keys.

## Consequences

- New Project looks and works like the classic IDE's, with the templates' own names, descriptions, tags and options, in every theme and language the shell has.
- Projects are created with the default .NET SDK; with `projects.restoreOnOpen` off, a trusted solution's packages stay unrestored until it builds or restores, as for any solution.
- A template that tests `HostIdentifier` in unusual ways could open a different start file; what's created is always `dotnet new`'s.
- The cache's format can change with an SDK; the fallback keeps New Project working, with fewer options, until the reader catches up.
- Deferred, in the roadmap's Deferred work table: TODO(issue) New Project in a remote window (create through the server's build host); TODO(issue) the classic IDE's Container OS and container build type choices; TODO(issue) provisioning for Microsoft identity platform (the project gets placeholder settings); TODO(issue) installing the MAUI workload from the wizard; TODO(issue) WinUI templates.
