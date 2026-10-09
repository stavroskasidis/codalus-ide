# Name conflicts to check: "Daedalus Studio"

A first pass on 2026-10-09. It is not legal advice: before announcing, have the trademark questions checked in the jurisdictions that matter to you. In the US that is the USPTO's trademark search, classes 9 (software) and 42 (software services); also check EUIPO and UKIPO.

## Software with the same or a similar name

| Name | What it is | Risk |
|---|---|---|
| **Daedalus** (Cardano wallet) | The official full-node desktop wallet for the Cardano blockchain, by Input Output Global (IOG, formerly IOHK). Open source, well known in the crypto world. Their documentation is at docs.daedaluswallet.io. | **Highest.** It is desktop software with the same core name, and IOG may hold or claim marks on it. The domain differs (crypto vs. developer tools), and "Daedalus Studio" is a distinct full name, but check IOG's registrations. |
| **DEDALUS / DEDALUS LABS** | US trademark applications filed in October 2025 (serials 99428659 and 99428660) for software development tools: an API gateway for AI agents, LLMs and the Model Context Protocol. They were still new applications when found. | **Medium**: the field (developer tools) overlaps, and the spelling is close. |
| **DAEDALUS** (Daedalus Technology Group) | A US filing (serial 98366405, January 2024) for R&D services in software development: software assessment and prototypes. | **Medium**: same spelling, an adjacent field (services, not an IDE). The ownership records conflict (company vs. an individual), so check the USPTO directly. |
| **Daedalus** NuGet packages | `Daedalus.Events.*` and `Daedalus.Commands.*` (CQRS/event-sourcing libraries) and `DaedalusCompiler` on nuget.org. | **Low** for the name, but they block the `Daedalus.*` package prefix. See [nuget-prefix-reservation.md](nuget-prefix-reservation.md). |
| **DAEDALUS** (Daedalus Technologies) | A 2022 US filing for stock-trading software. | Low (different field). |
| Daedalus Group (1990s) | Old US marks for educational writing software. | Low (different field, old). |
| Others | "Daedalus" is a common name: GitHub has many unrelated repositories (game engines, compilers, robotics), and there are tabletop-game and hardware companies. | Low, but check search-result confusion on GitHub and in search engines. |

## Domains and handles to check

- `daedalusstudio.dev`, `daedalus-studio.dev`, `.io`, `.org`. Is any of them registered, and to whom?
- GitHub organization `daedalus-studio`; nuget.org account names; winget ID `DaedalusStudio.DaedalusStudio`; Flathub ID `io.github.stavroskasidis.DaedalusStudio`; the Homebrew cask name `daedalus-studio`.
- Social and community handles, if you plan to use them.

## If the name has to change

The name appears in these places, so a rename touches a known, limited set of files:
- the product name: the shell's title, the About box, `AppPaths`' folder names, packaging metadata and docs
- the CLI (`daedalus`)
- the `Daedalus.*` namespaces

Namespaces don't need to change for a product rename.
