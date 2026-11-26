# 0111. A version manager's Node.js, and the distributions' npm

- **Status:** accepted (2026-11-26; the maintainer's answers of 2026-11-26)
- **Date:** 2026-11-26
- **Deciders:** maintainer
- **Amends:** [ADR 0083](0083-connecting-external-agents-from-templates.md) (Connect's Node.js requirement and its install options, the entry's `node`)
- **Builds on:** M56 and [ADR 0107](0107-the-environment-read-again-and-the-terminal-card.md) (the login shell's PATH, read again before each check)

## Context

On an Arch-based machine, Connect said npx was missing although the user had installed Node.js and npm with nvm and restarted the IDE. Two gaps showed up together:

1. **nvm loaded only by `~/.bashrc`.** The IDE takes its PATH from the user's login shell (`LoginShellPath`). Their login shell was fish, which doesn't read `~/.bashrc`, so nvm's folder was never on the IDE's PATH. Checking again or restarting asked the same shell. The same holds for any version manager that one shell's startup files load and the login shell's don't, on macOS as on Linux.
2. **A distribution's Node.js without npx.** Arch's `nodejs` package has no npm. Arch packages npm apart, as do Debian, Ubuntu, Fedora, openSUSE and Alpine. Connect found `/usr/bin/node` and no npx, and could offer only nodejs.org's page or Codalus's own Node.js. Its install options knew apt, dnf and snap only.

## Decision

### A version manager's Node.js is offered first

- `NodeVersionManagers` (`Codalus.Workbench.Node`) finds Node.js in the folders of **nvm, fnm, Volta, asdf and mise**, on macOS and Linux (Windows reads its PATH from the registry, so this gap doesn't occur there). It reads files only and runs nothing: `$NVM_DIR/versions/node/v*/bin`, fnm's `node-versions/v*/installation/bin`, Volta's `tools/image/node/*/bin`, asdf's `installs/nodejs/*/bin`, and mise's `installs/node/*/bin`, leaving out its links. It also honors the variables that move these folders.
- Only a folder with both `node` and `npx` counts. **The manager's default is taken when it's new enough** for the agent: nvm's `alias/default` (followed through `lts/*`), the version fnm's `aliases/default` link points to, Volta's `platform.json`, `~/.tool-versions` for asdf, and `[tools] node` in mise's global `config.toml`. Otherwise its newest version that is new enough is taken.
- When Node.js or npx is missing or too old on the PATH, Connect **offers each manager's Node.js first, preselected** (the maintainer's choice, over using it silently or only hinting at it): *nvm's Node.js 26.13.1*, with its folder and why the PATH lacks it. **Use It** installs nothing: the check runs again on that folder, and the test runs with that folder first on the PATH, as for Codalus's own Node.js.
- **The entry names the manager**, `"node": "nvm"` (the maintainer's choice, over the folder): `AcpAgents.ResolveAsync` chooses the manager's Node.js again at each start, for the template's Node.js major. So `nvm install 28` and removing the old version need no reconnect. When the manager has no Node.js left, the start fails, saying to install one or connect again. `"node"` now takes `builtIn` or one of the five managers' ids.
- Tests never read the machine's managers. While the connector's `SearchPath` holds stand-ins, no manager is looked for unless the test sets `VersionManagerEnvironment`. `AcpAgents.ManagedNodeFolder` is a function that tests replace.

### Every distribution's packages

- Node.js missing or too old: besides apt and dnf, Connect offers **pacman** (Arch and its derivatives), **zypper** (openSUSE, with the newest `nodejs<major>` it has) and **apk** (Alpine), each with npm, under the same rule as apt and dnf: only when its version is new enough, with a note saying why when it isn't. The versions are read from the machine's package metadata, with no refresh and in the C locale: `pacman -Si nodejs`, `zypper --no-refresh search nodejs*` and `apk policy nodejs`.
- The distribution's Node.js (`/usr/bin/node`) found without npx: Connect offers **the distribution's npm package**, using `InstallPlans.Package`'s command for apt, dnf, pacman, zypper (`npm<major>`, matching the Node.js) or apk. It runs in the Terminal, then Connect checks again.

## Consequences

- An nvm, fnm, Volta, asdf or mise Node.js is found whichever shell loads it, without changing the user's shell files.
- The pairs that stay manual shrink: Arch, openSUSE and Alpine join the coverage table (`AcpAgentTemplates.Coverage`, the user guide's table, checked by `AcpAgentCatalogTests`).
- A manager that keeps Node.js somewhere else (nodenv, n's own prefix, a custom Volta layout) isn't found. Such a Node.js is still found when the login shell puts it on the PATH.
