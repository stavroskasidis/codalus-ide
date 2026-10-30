# Releasing a version

A stable release goes from `develop` to `master` through a release branch, `release/X.Y.Z`, so `develop` never waits
on a release: work for the next version merges while the release is tested, fixed and published. The rules are
[ADR 0028](../adr/0028-release-cycle-and-installers.md) (its 2026-10-21 update), the pipeline is
`.github/workflows/pipeline.yml`, and the version rules are `eng/Codalus.Release`. Never open a pull request from
`develop` itself into `master`.

An open release branch changes only through pull requests, like `develop` and `master`: its creation is its one push,
and it is never force-pushed.

## Before

- The milestone's checkpoint is validated (`docs/checkpoints/`), and `CHANGELOG.md` has the release's section,
  `## X.Y.Z`: the release notes come from it.
- `develop`'s head is green: its own push run passed, or it differs from the newest commit whose run passed only in
  `docs/` and Markdown files (those pushes start no run).
- `VERSION` on `develop` is `X.Y.Z`, the version to release.

## 1. Cut the release branch

From `develop`'s head:

```bash
git fetch origin develop master
sha="$(git rev-parse origin/develop)"
version="$(git show "$sha:VERSION" | tr -d '[:space:]')"
gh run list --workflow pipeline.yml --branch develop --event push --commit "$sha" --json conclusion --jq '.[0].conclusion // "no run"'
```

- `success`: cut it.
- `no run`: the head is a docs-only merge. Find the newest commit whose run passed
  (`gh run list --workflow pipeline.yml --branch develop --event push --status success --limit 1 --json headSha`) and
  check that `git diff --name-only <that sha> "$sha" | grep -vE '^docs/|\.md$'` prints nothing; then cut the head.
- anything else: `develop` is red; fix it first.

`git merge-base --is-ancestor "$sha" origin/master` must fail (master doesn't have it yet). Then:

```bash
git push origin "$sha:refs/heads/release/$version"
```

## 2. Open the release pull request

Write the body to a file (`release.md`): what the release brings, as `CHANGELOG.md`'s section says, and how it was
tested. Then:

```bash
gh pr create --base master --head "release/$version" --title "Release $version" --body-file release.md
```

Its run's `gate` job checks that the branch's `VERSION` is `$version` and that its `.github/workflows` are `develop`'s,
and fails otherwise (see step 4). It skips test and smoke when `develop`'s run passed on the head and the head has all
of `master`; otherwise it runs them. Nothing is published from a pull request.

## 3. Move `develop` to the next version

Right after the cut, so the next merges to `develop` are the next version's nightlies:

```bash
git switch -c start-next origin/develop
next="$(dotnet run --project eng/Codalus.Release -- bump --after "$version")"   # VERSION := the next minor version
git branch -m "start/$next"
git commit -m "Start $next after cutting release/$version" VERSION
git push -u origin HEAD
gh pr create --base develop --title "Start $next" --body "Moves VERSION past release/$version, cut for the $version release."
```

Merge it once its run passes. The next version's `CHANGELOG.md` section starts with the first change that needs it.

## 4. Fix what the release needs

A failure or a fix found before the release is published goes into the release branch, never straight into `master`:
a branch off `release/X.Y.Z`, a pull request into `release/X.Y.Z` (its run is CI, as for `develop`), merged when it
passes. The release pull request then has a new head, and its run tests it. The fix reaches `develop` when the release
run merges `master` back (step 6). If `develop` needs it sooner (a test that fails there too), cherry-pick the same
commit into a pull request into `develop`: the identical change merges back cleanly.

**Workflows stay equal.** The release's tag is created by the workflow token, and GitHub lets it tag only a commit whose
`.github/workflows` match `develop`'s (M31b). So while a release branch is open, a change to `.github/workflows` goes
into both: one merged into `develop` is cherry-picked into the release branch by pull request, and a workflow fix the
release needs goes into `develop` too. The release pull request's `gate` job fails until they match.

A release that fails only a benchmark gate the maintainer accepts gets its `benchmarks/accepted.json` entry the same
way, through the release branch. The evidence is a same-runner A/B on CI (both versions on one runner, alternating
rounds; `benchmarks/README.md`), never numbers from a developer's machine.

## 5. Merge the release

Once the release pull request's run passes:

```bash
gh pr merge "release/$version" --merge     # a merge commit; never squash or rebase a release
```

The push to `master` is the release run: CI, the packages, the self-update, the benchmarks and the site run on its
commit, and `v<VERSION>` is published only if all of them pass. Otherwise nothing is published and the tag is removed
(ADR 0028). Then:

- **A failure that needs a code fix:** step 4, then a new release pull request from the branch.
- **A failure that needs no change** (a flaky test, a runner that never started): re-run the master run with
  **Re-run all jobs**, not *Re-run failed jobs*: `plan` tags the commit again (the failed run removed the tag) and every
  gate runs again.
- **A fix that touches only `docs/` or Markdown** (a broken link in the user guide): such a push to `master` starts no
  pipeline run. After merging it through the release branch, start the release on `master`'s head by hand:
  `gh workflow run pipeline.yml --ref master -f dry_run=false`.
- **`plan` fails with HTTP 403 creating the tag:** `master`'s workflow files differ from `develop`'s (a workflow change
  merged into `develop` after the release pull request's checks). Bring them together (step 4) and release again.

## 6. After the release

Once the release is out, the run deploys the site with the apt and dnf repositories, pushes the AUR package and pushes
the plugin SDK to nuget.org (the `nuget` job; see [nuget.org](#nugetorg)). The release run's last job merges `master` into `develop` through a pull request of its own (`develop-after/X.Y.Z`,
merged by the run; the workflow token's pull request starts no run, so the job posts its `ci` check itself, since `master`
passed the release run's gates): it brings in the release branch's fixes and keeps
`develop`'s `VERSION` (already past the release since step 3). If anything else conflicts, it stops with an error; merge `master` into `develop` in a pull request
(a branch off `develop` that merges `master`, the conflicts resolved). Then delete `release/X.Y.Z`.

## Hotfixes

A branch off `master` with `VERSION` set to the patch version (`X.Y.(Z+1)`, such as 0.5.1), a pull request into
`master`; the release run publishes it and merges `master` back into `develop`. If a release branch is open meanwhile,
it takes the hotfix too: a branch off `release/X.Y.Z` that merges `master`, keeping the release branch's `VERSION` and
putting the hotfix's `CHANGELOG.md` section below the release's, in a pull request into `release/X.Y.Z`.

## nuget.org

Every stable release packs the plugin SDK and pushes it to nuget.org: `Codalus.Plugins.Abstractions`,
`Codalus.Plugins.Testing` and `Codalus.Templates` (`pipeline.yml`'s `nuget` job). Each package has a version of its
own, its project's `<Version>`, not the IDE's `VERSION`; a version already on nuget.org is skipped (`--skip-duplicate`).
So a release ships a package only when its version changed:

- `Codalus.Plugins.Abstractions` and `Codalus.Plugins.Testing` carry the plugin API's version ([plugins.md](../plugins.md)):
  bump both with an API change, and the template's `codalusVersion` default
  (`src/Codalus.Templates/content/CodalusPlugin/.template.config/template.json`) with them.
- `Codalus.Templates`: bump it with any change to the template, including a new `codalusVersion` default.

The job keeps no API key. nuget.org's [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
trades the job's GitHub OIDC token (`id-token: write`) for a key valid for an hour, through `NuGet/login`. The
one-time setup, the maintainer's:

1. On nuget.org (the account or organization that owns the packages, with 2FA): your name → **Trusted Publishing** →
   add a policy: **Repository Owner** `stavroskasidis`, **Repository** `codalus-ide`, **Workflow File** `pipeline.yml`
   (the file name only), **Environment** `nuget`. Its scope must allow pushing new packages for the first release.
2. On GitHub, **Settings → Environments → New environment** `nuget`; under **Deployment branches and tags**, choose
   **Selected branches and tags** and add `master`. Only `master`'s run can then use the policy: a branch that edits
   `pipeline.yml` can't publish. (The first run that names the environment creates it without that rule.)
3. **Settings → Secrets and variables → Actions → New repository secret**: `NUGET_USER`, the nuget.org profile name
   (not the email address) that owns the policy. Or `gh secret set NUGET_USER --repo stavroskasidis/codalus-ide`.

Without `NUGET_USER` the job packs the packages and pushes nothing, with a notice. When the push fails (the policy
inactive, nuget.org down), the release is already out and only `finish` waits: fix the cause and use **Re-run failed
jobs**. Reserve the `Codalus.*` prefix too ([nuget-prefix-reservation.md](nuget-prefix-reservation.md)).
