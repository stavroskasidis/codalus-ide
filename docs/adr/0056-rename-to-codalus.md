# 0056. Daedalus Studio becomes Codalus IDE

- **Status:** accepted (M39, 2026-10-13)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

"Daedalus" is a crowded name (`docs/release/name-conflicts.md`): trademarks in software, the `Daedalus.*` prefix taken on nuget.org, and many unrelated repositories. The maintainer decided on 2026-10-13 to rename the project, weighed Codalus IDE, Akmon IDE (Akmωn), Akmonide and Einion, and chose **Codalus IDE**: *code* and *Daedalus*. The project had one preview release (0.1.0) and no users to move.

## Decision

**The names.**

| What | Before | After |
|---|---|---|
| Product | Daedalus Studio | Codalus IDE ("Codalus" where it's short) |
| CLI and executable | `daedalus` | `codalus` |
| Assemblies, namespaces, projects, solution | `DaedalusStudio.*`, `DaedalusStudio.slnx` | `Codalus.*`, `Codalus.slnx` |
| Hosts | `daedalus-pluginhost`, `-buildhost`, `-lshost`, … | `codalus-pluginhost`, … |
| Environment variables | `DAEDALUS_*` | `CODALUS_*` |
| Protocol extensions | `daedalus/…` | `codalus/…` |
| Plugin manifest, ids, API | `daedalus-plugin.json`, `daedalus.welcome`, API 0.2.9 | `codalus-plugin.json`, `codalus.welcome`, API 0.3.0 |
| Per-solution folder | `.daedalus/` | `.codalus/` |
| User data folders, credential service | `Daedalus Studio`, `daedalus-studio` | `Codalus`, `codalus` |
| Labels, bundle ids | `dev.daedalus.*` | `dev.codalus.*` |
| Packages | Velopack `DaedalusStudio`, `daedalus-studio`, AUR `daedalus-studio-bin`, winget `DaedalusStudio.DaedalusStudio` | Velopack `Codalus`, `codalus`, AUR `codalus-bin`, winget `Codalus.Codalus` |
| Repository and site | `stavroskasidis/daedalus-studio`, `/daedalus-studio/` | `stavroskasidis/codalus-ide`, `/codalus-ide/` |

**A clean break.** There's no compatibility with the old names: no migration of settings or data, no bridge release, and no aliases for the old CLI, variables or protocol methods. 0.1.0 doesn't update to 0.2.0; the changelog says to uninstall it.

**Mechanical, scripted and kept.** `eng/rename.py` moves every path with the name and applies ordered text replacements to every tracked text file:
- the specific forms first: the repository, the site and the AUR package;
- then `DaedalusStudio`, `Daedalus Studio`, `daedalus-studio`, `DAEDALUS`, `Daedalus`, `daedalus`.

Each rule has a test of what it must and mustn't touch. It reads Latin-1 for the few test files that aren't UTF-8. `--check` lists what still says daedalus. The manual steps (the plugin API's version, the folder and credential names, the changelog, these docs) followed.

**History stays as written.** ADRs, checkpoints, the changelog's 0.1.0 entry, the name research and the roadmap's M39 section keep the old name. The maintainer's clones keep their folder names (`Projects/daedalus-studio`) until renamed by hand.

**The mark** is new too. The maintainer chose it on 2026-10-13 from seven rounds of proposals: the old mark's wings, now two feathers each, with the left wing violet (`#A47BFF` to `#5B2EC7`) and the right wing ember (`#FFC24A` to `#F0662A`), each shaded from tip to base, and the inner feathers at 82%. It's code and Daedalus, two colors for two halves of the name.
- `eng/logo.py` writes every logo file from that one geometry: the SVG mark, the single-colour variant and the wordmarks ("Codalus" and "IDE"), the PNGs from 16 to 1024 px (drawn with Pillow, 4x supersampled), the Windows `.ico` and the site's icons.
- The old alternatives and the unused tile are gone.

## Consequences

- Everything outside the repository follows the merge, with the maintainer's go-ahead: the GitHub repository's rename (GitHub redirects the repository's URLs but not Pages), the Pages site's new path, the `Codalus` prefix on nuget.org, and `codalus-bin` on the AUR.
- The update feeds and channels are new (`Codalus`): 0.1.0 installs find no update.
- Local environments move too: the WSL mirror (`~/codalus`), the worktrees, and the scripts and notes that name the old folders.
