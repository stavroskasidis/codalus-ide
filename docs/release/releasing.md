# Releasing a version

A stable release goes from `develop` to `master` through a release branch, `release/X.Y.Z`, so `develop` never waits
on a release: work for the next version merges while the release is tested, fixed and published. The rules are
[ADR 0028](../adr/0028-release-cycle-and-installers.md) (its 2026-10-21 update), the pipeline is
`.github/workflows/pipeline.yml`, and the version rules are `eng/Codalus.Release`. Never open a pull request from
`develop` itself into `master`.

## Before

- The milestone's checkpoint is validated (`docs/checkpoints/`), and `CHANGELOG.md` has the release's section,
  `## X.Y.Z`: the release notes come from it.
- `develop`'s latest push run passed: the commit to release is the one whose run published the newest nightly.
- `VERSION` on that commit is `X.Y.Z`, the version to release.

## 1. Cut the release branch

From the `develop` commit whose run passed (normally `develop`'s head):

```bash
git fetch origin develop
sha="$(gh run list --workflow pipeline.yml --branch develop --event push --status success --limit 1 --json headSha --jq '.[0].headSha')"
version="$(git show "$sha:VERSION" | tr -d '[:space:]')"
git push origin "$sha:refs/heads/release/$version"
```

## 2. Open the release pull request

```bash
gh pr create --base master --head "release/$version" --title "Release $version" --body "…"
```

The body summarizes the release (what `CHANGELOG.md`'s section says) and how it was tested. The pull request's own run
skips test and smoke when `develop`'s run passed on its head and it has all of `master` (the `gate` job); otherwise it
runs them. Nothing is published from a pull request.

## 3. Move `develop` to the next version

Right after the cut, so the next merges to `develop` are the next version's nightlies:

```bash
git switch -c "start/<next>" origin/develop
dotnet run --project eng/Codalus.Release -- bump --after "$version"   # VERSION := the next minor version
git commit -m "Start <next> after cutting release/$version" VERSION
git push -u origin HEAD
gh pr create --base develop --title "Start <next>" --body "Moves VERSION past release/$version."
```

Merge it once its run passes. The next version's `CHANGELOG.md` section starts with the first change that needs it.

## 4. Fix what the release needs

A failure or a fix found before the release is published goes into the release branch, never straight into `master`:
a branch off `release/X.Y.Z`, a pull request into `release/X.Y.Z` (its run is CI, as for `develop`), merged when it
passes. The release pull request then has a new head, and its run tests it. The fix reaches `develop` when the release
run merges `master` back (step 6). A change `develop` needs sooner (a test that also fails there) is a second pull
request into `develop`.

A release that fails only a benchmark gate the maintainer accepts gets its `benchmarks/accepted.json` entry the same
way, through the release branch.

## 5. Merge the release

Merge the release pull request (a merge commit) once its run passes. The push to `master` is the release run: CI, the
packages, the self-update, the benchmarks and the site run on its commit, and `v<VERSION>` is published only if all of
them pass; otherwise nothing is published and the tag is removed (ADR 0028). If it fails, fix the cause on the release
branch (step 4) and open a new release pull request from it.

## 6. After the release

The release run's last job merges `master` into `develop` through a pull request of its own (`develop-after/X.Y.Z`,
merged by the run): it brings in the release branch's fixes, keeps `develop`'s `VERSION` (already past the release
since step 3) and stops with an error if anything else conflicts, for the maintainer to merge by hand. Then delete
`release/X.Y.Z`.

## Hotfixes

Unchanged: a branch off `master` with `VERSION` bumped to the patch version (`X.Y.Z+1`), a pull request into `master`,
and the release run merges `master` back into `develop`.
