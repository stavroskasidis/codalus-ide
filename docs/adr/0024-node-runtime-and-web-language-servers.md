# 0024. The Node.js runtime and the web language servers

- **Status:** accepted (M16 plan, 2026-10-10; settles decision D3, foundational decision F4)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

The best open language servers for HTML, CSS and JavaScript/TypeScript run on Node.js. They are VS Code's HTML and CSS servers (MIT) and `typescript-language-server` with TypeScript (Apache-2.0). So does `vscode-js-debug`, which M16c needs for browser and Blazor WebAssembly debugging. The roadmap's F4 recommends:
- downloading a pinned, checksum-verified Node runtime on demand, the first time a feature needs it;
- not bundling it;
- not requiring a user install.

D3 asked for the details. The M11 LSP bridge already runs any stdio LSP server (ADR 0016), so the servers need no new protocol.

## Decision

**The runtime** (`NodeRuntime`)
- One pinned Node.js LTS release, 24.21.0, downloaded from `nodejs.org/dist` into the cache folder. The archive is the `.zip` on Windows and the `.tar.gz` elsewhere (x64 and arm64), verified against the SHA-256 from that release's `SHASUMS256.txt` (pinned in code), then unpacked. A mismatch deletes the download and fails.
- The user's own Node is never used: the runtime must not depend on what is installed.

**The servers** (`WebLanguageServers`)
- The shell ships a `package.json` and `package-lock.json` for the pinned servers: `vscode-langservers-extracted` 4.10.0, `typescript-language-server` 6.0.2 and TypeScript 5.9.3. TypeScript 7 is the native rewrite, which no longer ships the `tsserver` the server drives.
- They are installed with that runtime's npm (`npm ci --ignore-scripts`), so every package matches its lockfile integrity hash and no install scripts run. The install folder is named after the lockfile's hash, so an updated lockfile installs afresh.
- **Registered as language services:**
  - `html` (HTML, with embedded CSS and JavaScript);
  - `css` (CSS, SCSS and Less);
  - `typescript` (JavaScript, TypeScript and their JSX variants).
- **Launch:** while the servers aren't installed, their launch returns nothing. After the install, `LanguageService.RetryAsync` starts them and opens the web files that are already open.

**Consent**
- The first time a web file is opened without the servers, a notification explains the download (about 70 MB, once) and offers Install, Not Now and Never.
- The setting `web.languageServers` makes this `ask` (default), `install` (no question) or `off`. There is also a command, Install the Web Language Servers.
- `DAEDALUS_NODE_CACHE` points the downloads at another folder; tests and the smoke test share one so only the first run downloads.

**Found while wiring it:** VS Code's servers write file URIs with an escaped drive colon (`file:///c%3A/…`), which .NET's `Uri` turns into `/c:/…`. `DocumentUris.ToPath` now maps them to the same Windows path as ours. Before, diagnostics and locations from such servers didn't reach Windows files.

## Consequences

- **The first use needs network access and about 70 MB.** Offline machines keep TextMate coloring for these languages. The download comes from nodejs.org and the npm registry; a proxy or mirror setting is `TODO(issue)`.
- **Updating Node or a server** means changing the pinned version and checksums, or the lockfile, in a commit. Security updates of Node.js are a maintenance task per release.
- **Not covered:** HTML and CSS inside Razor files (`.razor`, `.cshtml`); they keep the Razor grammar. `TODO(issue)`. Also missing: ESLint and Prettier, the JSON server (the markup host serves JSON, ADR 0016), and npm scripts as tasks (M16c).
- **M16c** reuses the runtime for `vscode-js-debug`.
