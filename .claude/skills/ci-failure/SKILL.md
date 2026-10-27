---
name: ci-failure
description: What to do when a CI run fails (a pull request's checks, a nightly or a release run of pipeline.yml), before anything is re-run. Use whenever a pipeline job is red or cancelled by another job's failure, when asked to "re-run CI", "retry the job", babysit or fix a PR whose CI failed, or when a test is suspected to be flaky.
---

A failed run is a bug report. Re-running it until it passes hides the bug, costs a 40 to 70 minute cycle, and the
same flake fails the next pull request, the nightly and the release. So a failure is investigated first, and a re-run
is the exception, not the fix.

## 1. What failed, in every attempt

```bash
bash eng/ci-failures.sh <run id>          # every failed test, smoke step, hang and lost runner, in every attempt
bash eng/ci-failures.sh --recent 100      # the failures of the last 100 runs, most frequent first
```

A run stops at its first failure and cancels the rest (ADR 0028), so the red job is the one whose error says "failed:
the run's other jobs are cancelled"; the others show as cancelled. The failed tests are also annotations on the run's
page and in the job's summary (`eng/ci-test.sh`). Smoke steps say `FAILED` in the log and in `smoke-test.md` (the
`smoke-test-<OS>` artifact); a test shard's results, dumps, MSBuild failure files and (macOS) memory samples are in
the `test-results-<os>-<shard>` artifact.

## 2. Whose failure is it

- **This change's**: the test covers what the diff touches, or it fails locally too. Fix the change.
- **A known flake**: it is in `--recent` or in `docs/ci-flakes.md`. If the ledger says it was fixed, the fix didn't
  hold: that is new information, so investigate again with it.
- **A new flake**: it passed before on the same code, and nothing in the diff explains it. Investigate (step 3).
- **Infrastructure**: the runner was lost ("lost communication with the server"), a download or `apt-get` failed, a
  GitHub service answered 5xx, or the job died before any test ran (checkout, setup). Look at what the job was doing
  when it died; a lost runner in the middle of a test step is usually the test starving it (memory, a dump of a big
  process tree), not GitHub.

## 3. Find the root cause

Read the failure's assertion and stack, the test, and the code under test; then the log around it (the `[host]`
and `[restore]` lines, the watchdog's stacks, the `[memory]` samples). Ask what had to happen in which order for the
test to pass, and what else could run in between on a slow or loaded runner. The causes found so far
(`docs/ci-flakes.md` has each with its fix):

- An awaited load that a newer one supersedes returns before the newer one lands (a refresh timer, a watcher).
- A view rebuilt under the focused element drops the keyboard focus.
- A file watcher (FSEvents on macOS) reports writes made just before it started.
- A fixed short limit (1 s) for something that waits on a starved thread pool, on Windows' four cores.
- Attaching to, or talking to, a process before it is ready (a runtime still starting, a test host not connected).
- A failed or aborted result cached as a valid one, so every later step sees nothing.
- A timeout reported as a cancellation, so the real error is never shown.
- A read that writes after all: `git diff` refreshes the index under `index.lock` even with `GIT_OPTIONAL_LOCKS=0`, so a write next to it failed (`index.lock: File exists`).
- A process tree ended with .NET's own walk, which blocks for minutes on macOS (`ProcessTree.Kill` instead).
- A cancellation and an answer racing: the answer that lands first wins unless the code checks the cancellation again.
- A file polled while the IDE writes it: on Windows the read throws (a sharing violation) instead of returning old text.

Reproduce it when you can: run the test in a loop, under CPU load, or force the interleaving (start the second load
before awaiting the first, delay the process, fire the event early). A fix is proven by a test that fails without it.

## 4. Fix it, don't hide it

- Fix the race in the product when users can hit it too (most of the list above); fix the test when only the test
  is racy (asserting before the state it waits for, a shared temp path, a fixed sleep).
- Never: a retry loop around a test or an assertion, `Skip`, a quarantine list, a longer timeout for the whole test,
  or a re-run to get green. A longer wait for one condition is fine only when the log proves that condition was just
  slow (say so in the comment, with the run id).
- A failure the log can't explain gets the diagnostic that would explain it next time: a failure message that says
  what state it saw, a log line, an artifact. Say in the pull request that the cause isn't known yet.
- Comments cite the run id (`(macOS CI, run 40547948961)`), as the rest of the codebase does.
- Platform-specific causes are validated on that platform (CLAUDE.md, Platform validation).

## 5. Record it

Add a row to `docs/ci-flakes.md` (or update the row: another run, a fix that didn't hold): what failed, where, the
runs, the root cause, the fix. Then the next failure of the same test starts from what is already known.

## When a re-run is right

Only after steps 1 to 5, and only to let a run finish that the failure doesn't concern:

- an infrastructure failure from step 2 that the workflow can't prevent (a lost runner with nothing of ours running,
  a GitHub outage), named in the pull request; or
- a flake that is recorded and has a fix or a diagnostic on its way in its own pull request.

Re-run the failed jobs once (`gh run rerun <run id> --failed`). A second failure is real. Never re-run a release
run's tests to get a release out: a release is atomic (CLAUDE.md, CI and releases).
