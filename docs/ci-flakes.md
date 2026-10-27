# CI flakes

Every failure of `pipeline.yml` that passed on a re-run, with its root cause and its fix, so the next failure of the
same test starts from what is known. How to investigate one: skill `ci-failure` (`.claude/skills/ci-failure/`), with
`eng/ci-failures.sh <run id>` and `eng/ci-failures.sh --recent 100`. A row whose fix didn't hold gets the new run and
what it showed; it isn't replaced.

The first sweep (2026-10-27) read every failed job of the last 100 runs, 2026-10-23 to 2026-10-27, in every attempt:
about 45 failed jobs, 30 distinct failures, of which only a handful were the pull request's own.

## Tests

| Failure | Where | Runs | Root cause | Fix |
|---|---|---|---|---|
| `GitChangesMenuTests.The_branch_drop_down_lists_the_branches_each_with_its_menu` | macOS shell-1 | 40480055094, 40538692111, 40547948961 | Since ADR 0085 the repositories' refresh, 750 ms after git changes `.git`, reloads an open branch drop-down. A load it replaced returned without applying anything, so its caller read the rows from before the change (users too, after Update). | `GitBranchPickerViewModel.LoadAsync` waits for the newest load once its own is replaced; `A_load_that_a_newer_one_replaces_returns_with_the_newer_ones_branches` forces the interleaving. |
| `VariableWindowsTests.A_watch_that_doesnt_evaluate_shows_the_error_icon_and_values_are_edited_in_place` | Windows and macOS shell-1 | 39959543059, 39959550864, 40346124678 | Every evaluation of the watches rebuilds the rows; the focused row's container went with them and the keyboard focus with it, so Delete (and F2, the arrows) went nowhere until a click. The test's earlier fix was a three-attempt retry loop. | `VariableRowList.Rebuild` gives the focus back to the selected row; the retry loop is gone, and the test rebuilds under the focused row on purpose. |
| `ProjectWatcherTests.A_restore_that_fails_hands_what_it_wrote_back_to_the_watcher` | macOS others | 40425680553, 40447979346 | FSEvents delivers writes made just before a stream starts (inotify doesn't): the fixture's own `App.csproj` write was reported as a change. | The tests start from a fence: they create a folder after the watch starts, wait for it, then clear what was reported. |
| `RestoreRaceTests.Completion_asked_before_the_framework_arrives_works_once_it_does` | macOS others | 40117668370 | `FrameworkGuard` checked only the document's own project for its framework; a referenced project still without one made Roslyn's completion provider throw, and Roslyn disabled it for the session. A user opening a solution before its restore could lose completion until a restart. | The guard also checks every project the document's project depends on; `FrameworkGuardTests`. |
| `WinFormsDesignerServiceTests.A_target_starts_on_the_copys_runtime_loads_the_copy_and_takes_the_copy_with_it` | Windows others | 40401940980 | A 1-second limit on an answer that every read of the fake host's pipe takes from the thread pool, which the log shows starved (7 threads, 6 items queued). | Requests that are answered get 30 s; only the tests of a timeout keep 1 s. |
| `DotNetTestRunControlTests.Cancelling_stops_a_hanging_test` and five more Plugins tests (coverage, a class's outcomes, debugging a test, net48 debugging) | Windows others | 39979261891 | On a loaded runner vstest.console didn't connect within its 90 s. The plugin kept the unconnected wrapper, cached the aborted discovery as "no tests" until the assembly changed, and reported MTP's own connect timeout as a cancellation. The cancel test then waited 4 minutes for a test that never started. | The wrapper waits again until it connects once; an aborted discovery shows what it found but isn't the build's; a connect timeout is a `TimeoutException`; the test fails at once when the run ends without starting. |
| `DotNetTestRunControlTests.Cancelling_stops_a_hanging_test` (MSTestMtpTests) | macOS others | 40375629247 | The fixture's build failed: an MSBuild node died writing its pipe with ENOBUFS (MSB4166), the runner's socket buffers. | Not fixed (once in 100 runs). The test now fails at once and names the build failure instead of waiting 4 minutes. |
| `DebugServiceTests.Detach_All_from_an_attached_apphost_stopped_at_a_breakpoint_leaves_it_running` | macOS others | 40401940980 | The test attached right after `Process.Start`; on a loaded runner the runtime was still starting, and the attach timed out (CORDBG_E_TIMEOUT to configurationDone). | The attach tests wait for the Debuggee's "waiting" from `Main` first. |
| `DataTipDebuggerTests.The_collection_views_read_their_elements_and_set_them_on_netcoredbg` | Windows others | 40375629247 | netcoredbg refused the first property-getter func-eval after a stop (CORDBG_E_FUNC_EVAL_BAD_START_POINT); the log has no netcoredbg trace to say why. | Not root-caused. Next time: `CODALUS_NETCOREDBG_LOG` for the job, uploaded with the results. |
| `AspireShellTests.F5_on_the_apphost_debugs_its_projects_in_one_session` | Windows shell-1 | 40346124678 | The AppHost sat stopped on a `TimeoutRejectedException` while DCP started; the log can't say whether netcoredbg stopped on an exception Aspire handles. | Diagnostic: a timed-out wait now says where and why the debugger stopped. |
| `BlazorWebAppHotReloadDebugTests.A_rude_edit_while_stopped_on_the_server_restarts_the_app_and_debugging_follows_it` | macOS shell-1 | 39933634808 | The old server, held by the debugger, kept the port the restarted one needed. | Already fixed by 7a35b4e3 (the held server is ended once dotnet watch says it exited). |
| `ProcessTreeTests.Kill_ends_a_child_started_as_its_parent_is_stopped`, `HostRuntimeTests.A_host_killed_in_its_first_seconds_after_it_logged_is_restarted` | macOS others | 40322617496, 39916009422 | A child forked while its parent was being stopped was missed. | Already fixed by 1f0c4cae and 5385f364. Still a timing heuristic (five looks 10 ms apart): waiting until each stopped process reads as stopped would make it exact. |
| HangDump in `Codalus.Workbench.Tests` (`TemplateServiceTests.A_command_that_runs_past_its_limit_fails_with_what_it_printed` among those running) | macOS others | 39902231604, 39908085950 | `Process.Kill(entireProcessTree: true)` blocked for minutes while ReportCrash held a crashed process. | Already fixed by 7af040af (`ProcessTree.Kill`, checked by `ProcessTreeTests`). |
| `WebsiteTests.The_sitemap_dates_each_page_by_its_sources_last_commit_with_the_whole_history_only` | macOS and Windows others | 40117668370, 40120328282 | Not a flake: the new test's own bugs on its pull request (`/var` against `/private/var`, git's read-only objects on Windows). | Fixed on that pull request (a42df772, a9aecb37). |

## Runners

| Failure | Where | Runs | Root cause | Fix |
|---|---|---|---|---|
| "The hosted runner lost communication with the server" | macOS shell-2 | 40255384457, 40453091722 (and 40241261003 hit the job's limit) | Not certain: a lost runner keeps neither its log nor its artifacts. Each time it was lost about 46 minutes into the test step: a hang (the shard usually takes 20 to 30), then HangDump's 15 minutes, with it dumping the Shell tests' process tree on a 7 GB runner. | `eng/ci-test.sh` ends a hang in the macOS Shell shards without a dump (the watchdog's stacks are in the log), so the job fails with the hung test's name instead of losing the runner. If one is lost again, this guess was wrong: note it here. |

## Not flakes, but red runs

- **The benchmark regression gate** (39953872764, 39979261891, 40423773696, 40461519855): one metric just past +10% on
  a hosted runner, once with VS Code, the reference app, moving as much in the same run. That is the gate's policy on
  noisy machines (ADR 0002), a decision for the maintainer, not a test to fix here.
- **`finish`** (40307688648): merging master into develop conflicted beyond `VERSION`; it says so and stops.
