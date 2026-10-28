# Go-public checklist

> **Done: the repository went public on 2026-10-11 (D29).** Kept as the record. The workflows it names were later merged: `ci.yml`, `release.yml`, `packages.yml` and `benchmarks.yml` are now `pipeline.yml`, and `linux-repos.yml` is `pages.yml` (see CLAUDE.md, CI and releases). The open items still apply: code signing (D19), the package key's backup, and the NuGet prefix (section 5a).

Prepared at M7 (2026-10-09). **Changing the repository's visibility is the maintainer's call. Nothing here was done to the GitHub settings.** Work through the list, then flip the switch.

**When:** after M32's checkpoint (roadmap D29). M31 replaced the manual release with nightly releases from `develop`, stable releases from `master` and Velopack installers (ADR 0028); the sections below follow it.

## 1. Decide on the files that read as internal notes

The history audit (section 6) found no secrets. A few tracked files are working notes rather than project documentation:

| File | What it is | Options |
|---|---|---|
| ~~`ide-prompt.md`~~ | The original mission and milestone brief. **Deleted and purged from the history** (after M7): history rewritten, then the GitHub repository deleted and recreated, which also removed the closed M0 PR #1 and the old commits. | Done. |
| `CLAUDE.md` | Instructions for the AI coding assistant: build commands and conventions. | Harmless and useful to contributors who use assistants; keep, or fold the build commands into `CONTRIBUTING.md`. |
| `docs/roadmap.md` | The post-MVP roadmap. | Done: committed, and kept up to date at every checkpoint. |
| `docs/checkpoints/*.md` | Per-milestone engineering notes: deviations, gaps, `TODO(issue)` lists. | Keep (they double as a changelog), or turn the `TODO(issue)`s into GitHub issues first (item 4). |

## 2. Repository settings (after flipping to public)

- [ ] **Branch protection** (rulesets) for `develop` (the default branch) and `master`:
  - require a pull request, with 0 approvals (the maintainer, and a coding assistant working as them, can't approve their own pull requests)
  - require the status check `ci` (`pipeline.yml`'s one job that sums up gate, test and smoke; a docs-only pull request reports it too), without requiring branches to be up to date (each update would test again for about 45 minutes)
  - block force pushes and deletion
  - don't require linear history: releases and the merge back into `develop` are merge commits
  - bypass: the repository admin, **for pull requests only**, so nobody pushes to `develop` or `master` directly (GitHub offers no GitHub Actions bypass for a repository owned by a user)
  - **let the release workflow move `develop`**: after a stable release `pipeline.yml`'s `finish` job merges `master` back and bumps `VERSION` through a pull request it opens and merges itself, so turn on Settings → Actions → General → "Allow GitHub Actions to create and approve pull requests". The workflow token's pull request starts no CI run, so the job posts its `ci` check run itself (as GitHub Actions, the source the ruleset requires; it needs `checks: write`) and then merges it
- [ ] **GitHub Pages**: Settings → Pages → Source: **GitHub Actions**. `linux-repos.yml` then deploys the apt and dnf repositories after each release (it is skipped while the repository is private, because Pages needs a public repository on the free plan).
- [ ] **Private vulnerability reporting**: Settings → Code security → enable. `SECURITY.md` and the issue templates link to it.
- [ ] **Dependabot alerts and security updates**: on. Consider Dependabot version updates for NuGet and GitHub Actions.
- [ ] **Secret scanning and push protection**: on (free for public repositories).
- [ ] **Actions**: require approval for workflows from first-time contributors. Keep the workflow permissions read-only, as the workflows already declare.
- [ ] **Topics and description**: `dotnet`, `csharp`, `ide`, `avalonia`, `roslyn`, `msbuild`; website: the user guide.
- [ ] **Discussions** (optional), for questions that aren't issues.

## 3. CI

No workflow edit is needed. Once the repository is public: `ci.yml` runs the full Windows/macOS/Linux matrix on every push and pull request; `benchmarks.yml` runs its regression gate on every push to `master`; `release.yml` makes a nightly every night `develop` changed and a stable release on every push to `master`, and both kinds add Linux ARM64 (GitHub's ARM64 runners are free for public repositories).
- [ ] Dispatch `ci.yml` once after the flip and check that all three jobs are green.
- [ ] Dispatch `benchmarks.yml` a few times to build up a baseline for its `compare` gate (it compares with the median of earlier runs).

## 4. Issues

- [ ] Turn the `TODO(issue)` items into GitHub issues: `git grep -n "TODO(issue)"`, plus the "Deviations and gaps" sections of `docs/checkpoints/`. Labels: `bug`, `enhancement`, `good first issue`, `help wanted`, `area/*`.
- [ ] Optionally replace `TODO(issue)` in the code with `TODO(#123)`.

## 5. First public release

The workflows build, test and publish (ADR 0028); a dispatch without the dry run publishes.
- [ ] Check `VERSION` on `develop` (the first private releases were `v0.1.0` and its nightlies).
- [ ] Dispatch `release.yml` (kind `nightly`) once with the dry run off, and install the nightly on each platform.
- [ ] Merge `develop` into `master`: `release.yml` publishes `vX.Y.Z`, the apt and dnf repositories follow, and `develop` moves to the next minor version.
- [ ] Run `benchmarks check` on the reference machine (ADR 0002) and attach the summary.
- [ ] Submit the winget manifests (`winget-manifests-<version>.zip` in the release) to `microsoft/winget-pkgs`: they point at the release's `Setup.exe`.
- [ ] **Code signing** (D19): apply to SignPath Foundation (free for open source) or set up Azure Trusted Signing for Windows, and get an Apple Developer ID with a notary profile for macOS. The workflows pass `CODALUS_CODESIGN_IDENTITY` to `eng/package.sh` already; add the secrets and the Windows signing option then.
- [ ] **Package key**: the apt and dnf repositories are signed with `PACKAGES_GPG_PRIVATE_KEY` (a repository secret, made 2026-10-11, with no expiry). Keep a copy of it somewhere safe outside GitHub: secrets can't be read back.

## 5a. NuGet package prefix

See [nuget-prefix-reservation.md](nuget-prefix-reservation.md). Summary: reserve `Codalus.*` (nuget.org has no `Codalus` package); the plugin SDK ships as `Codalus.Plugins.Abstractions`. Every stable release pushes it, with `Codalus.Plugins.Testing` and `Codalus.Templates`, through nuget.org's trusted publishing; its one-time setup is in [releasing.md](releasing.md#nugetorg).

## 6. History audit (done at M7)

The full history (52 commits at the time of the audit, all branches) was scanned:

| Check | Result |
|---|---|
| Secrets | **None found.** Searched every patch for GitHub tokens (`ghp_`, `gho_`, `github_pat_`), AWS keys, private key blocks, Slack tokens, `sk-…` keys, `AccountKey=`, and password, secret or API-key assignments. |
| Authors | One author and committer identity throughout (the maintainer's GitHub address). |
| AI attribution | No `Co-Authored-By` or "Generated with" trailers in any commit message. |
| Machine-specific data | No local user paths (`C:\Users\…`, `/Users/…`) in any tracked file or patch. Benchmark results record only the CPU model and OS version. |
| Large or binary files | No blob over 500 KB. Binaries are the logo PNGs and `.ico` (original artwork in `assets/logo`). No DLLs, archives, keys or certificates. |
| Licensing | Every dependency is MIT, BSD-3-Clause, Apache-2.0 (test-only xUnit; Roslyn 2.x inside netcoredbg) or "Old MIT" (HarfBuzz). All are OSI-approved and recorded in `THIRD-PARTY-NOTICES.md`. Downloaded tools (netcoredbg, PresentMon) are not committed. The reference IDEs measured in M0 are proprietary; they were only run, never redistributed. |
| Removed code | The M0 GPUI spike (Rust) and the Avalonia spike remain in history. They are MIT-compatible and contain nothing sensitive. |

To check again before flipping:

```bash
git log -p --all | grep -nE "ghp_|github_pat_|AKIA[0-9A-Z]{16}|BEGIN [A-Z ]*PRIVATE KEY|xox[baprs]-"
```

A dedicated scanner (gitleaks, MIT) is a good second opinion.

## 7. Name

See [name-conflicts.md](name-conflicts.md) before announcing.
