## windows

Machine: 13th Gen Intel(R) Core(TM) i5-13600KF, 20 logical cores, 32 GB RAM, GPU NVIDIA GeForce RTX 4060, display 165 Hz, Microsoft Windows 10.0.26300.

### cold-start

| metric | avalonia | gpui | vscode | vs |
|---|---|---|---|---|
| memory_idle_private_mb | 193 / 194 / 194 (5) | 184 / 185 / 185 (5) | 1293 / 1313 / 1313 (5) | 716 / 740 / 740 (5) |
| memory_idle_private_mb_first | 193 | 1219 | 1299 | 748 |
| memory_idle_shell_private_mb | 95.8 / 96.5 / 96.5 (5) | 87.1 / 87.9 / 87.9 (5) | 148 / 151 / 151 (5) | 663 / 692 / 692 (5) |
| memory_idle_shell_private_mb_first | 95.6 | 87.3 | 146 | 693 |
| memory_idle_ws_mb | 233 / 233 / 233 (5) | 206 / 206 / 206 (5) | 1496 / 1524 / 1524 (5) | 811 / 819 / 819 (5) |
| memory_idle_ws_mb_first | 233 | 2438 | 1492 | 846 |
| process_count | 3.0 / 3.0 / 3.0 (5) | 3.0 / 3.0 / 3.0 (5) | 8.0 / 8.0 / 8.0 (5) | 2.0 / 2.0 / 2.0 (5) |
| process_count_first | 3.0 | 41.0 | 8.0 | 2.0 |
| startup_interactive_ms | 262 / 266 / 266 (5) | 288 / 311 / 311 (5) | – | – |
| startup_interactive_ms_first | 295 | 292 | – | – |
| startup_ready_ms | 346 / 404 / 404 (5) | 373 / 400 / 400 (5) | 1517 / 1573 / 1573 (5) | 921 / 1683 / 1683 (5) |
| startup_ready_ms_first | 404 | 374 | 1548 | 1368 |
| startup_selfreported_ms | – | – | 1072 / 1126 / 1126 (5) | – |
| startup_selfreported_ms_first | – | – | 1095 | – |

- avalonia: Run 0 is the first launch of the session (reported separately as *_first); distributions use runs 1..N-1. OS file caches are not flushed, so runs 1..N-1 are warm starts.
- gpui: Run 0 is the first launch of the session (reported separately as *_first); distributions use runs 1..N-1. OS file caches are not flushed, so runs 1..N-1 are warm starts.
- vscode: No extensions: TextMate highlighting only, no C# language service.
- vscode: Run 0 is the first launch of the session (reported separately as *_first); distributions use runs 1..N-1. OS file caches are not flushed, so runs 1..N-1 are warm starts.
- vscode: startup_selfreported_ms: VS Code's own --prof-append-timers measurement (process start to workbench restored).
- vs: Experimental instance (/rootsuffix CodalusBench); first launch needs a one-time manual setup.
- vs: Run 0 is the first launch of the session (reported separately as *_first); distributions use runs 1..N-1. OS file caches are not flushed, so runs 1..N-1 are warm starts.

### editor-100k

| metric | avalonia | gpui | vscode | vs |
|---|---|---|---|---|
| file_highlight_ms | 1304 / 1333 / 1333 (3) | 1495 / 1567 / 1567 (3) | – | – |
| file_ready_ms | 1396 / 1402 / 1402 (3) | 1570 / 1655 / 1655 (3) | 1821 / 2379 / 2379 (3) | 2060 / 2636 / 2636 (3) |
| memory_file_private_mb | 390 / 391 / 391 (3) | 382 / 382 / 382 (3) | 1608 / 1611 / 1611 (3) | 1394 / 1413 / 1413 (3) |
| memory_file_shell_private_mb | 227 / 230 / 230 (3) | 220 / 220 / 220 (3) | 143 / 144 / 144 (3) | 1009 / 1028 / 1028 (3) |
| memory_file_ws_mb | 380 / 381 / 381 (3) | 432 / 432 / 432 (3) | 1777 / 1778 / 1778 (3) | 1471 / 1489 / 1489 (3) |
| scroll_input_to_display_ms | 11.8 / 22.7 / 25.4 (450) | 12.2 / 18.7 / 19.7 (450) | 10.9 / 25.4 / 27.3 (450) | 25.2 / 53.4 / 178 (450) |
| scroll_responses_missing | 0.0 / 0.0 / 0.0 (3) | 0.0 / 0.0 / 0.0 (3) | 0.0 / 0.0 / 0.0 (3) | 0.0 / 0.0 / 0.0 (3) |
| scroll_responses_over_50ms | 0.0 / 0.0 / 0.0 (3) | 0.0 / 0.0 / 0.0 (3) | 0.0 / 0.0 / 0.0 (3) | 2.0 / 3.0 / 3.0 (3) |
| typing_in_app_ms | 6.4 / 18.0 / 23.9 (603) | 3.2 / 5.6 / 6.0 (459) | – | – |
| typing_input_to_display_ms | 13.6 / 24.9 / 31.7 (600) | 11.3 / 18.8 / 19.4 (600) | 13.2 / 20.0 / 23.4 (600) | 17.0 / 201 / 288 (600) |

- vscode: No extensions: TextMate highlighting only, no C# language service.
- vs: Experimental instance (/rootsuffix CodalusBench); first launch needs a one-time manual setup.

### open-solution-50

| metric | avalonia | gpui | vs |
|---|---|---|---|
| memory_solution_private_mb | 333 / 334 / 334 (3) | 226 / 228 / 228 (3) | 2458 / 3265 / 3265 (3) |
| solution_loaded_ms | 8508 / 8590 / 8590 (3) | 8104 / 8250 / 8250 (3) | – |
| solution_ready_ms | 9037 / 9099 / 9099 (3) | 8198 / 8326 / 8326 (3) | 1022 / 1070 / 1070 (3) |

- avalonia: The fixture copy is restored (dotnet restore) before each launch; restore time is not measured.
- gpui: The fixture copy is restored (dotnet restore) before each launch; restore time is not measured.
- vs: Experimental instance (/rootsuffix CodalusBench); first launch needs a one-time manual setup.
- vs: The fixture copy is restored (dotnet restore) before each launch; restore time is not measured.

### self-driven

| metric | avalonia | gpui |
|---|---|---|
| file_highlight_ms | 1343 / 1431 / 1431 (3) | 1576 / 1579 / 1579 (3) |
| memory_file_private_mb | 559 / 560 / 560 (3) | 408 / 411 / 411 (3) |
| selfdriven_jump_ms | 4.2 / 13.7 / 13.7 (15) | 6.4 / 8.4 / 8.4 (15) |
| selfdriven_scroll_frame_ms | 6.0 / 7.0 / 8.3 (4950) | 6.1 / 7.0 / 8.1 (4950) |
| selfdriven_scroll_frames_over_50ms | 0.0 / 0.0 / 0.0 (3) | 0.0 / 0.0 / 0.0 (3) |
| selfdriven_typing_ms | 7.2 / 19.5 / 28.1 (900) | 5.5 / 8.7 / 15.7 (900) |
| startup_interactive_ms | 256 / 264 / 264 (3) | 298 / 305 / 305 (3) |

- avalonia: In-app measurements: the prototype drives the scenario itself and reports input→rendered and frame times. Comparable between prototypes, not with black-box numbers.
- gpui: In-app measurements: the prototype drives the scenario itself and reports input→rendered and frame times. Comparable between prototypes, not with black-box numbers.

### binary-size

| metric | avalonia | gpui | vscode | rider | vs |
|---|---|---|---|---|---|
| install_mb | – | – | 1052 | 7313 | 5218 |
| lshost_mb | 60.9 | 60.9 | – | – | – |
| shell_mb | 45.5 | 27.5 | – | – | – |
| shell_zip_mb | 18.7 | 8.8 | – | – | – |

- avalonia: Debug symbols (*.pdb) excluded. The language service host is shared by both prototypes and requires the .NET 10 runtime.
- gpui: Debug symbols (*.pdb) excluded. The language service host is shared by both prototypes and requires the .NET 10 runtime.
- vscode: No extensions: TextMate highlighting only, no C# language service.
- vscode: Informative: whole install directory, including bundled runtimes and plugins.
- rider: User's configuration; 'Reopen projects on startup' must be off (see benchmarks/README.md).
- rider: Informative: whole install directory, including bundled runtimes and plugins.
- vs: Experimental instance (/rootsuffix CodalusBench); first launch needs a one-time manual setup.
- vs: Informative: whole install directory, including bundled runtimes and plugins.

Cells: p50 / p99 / max (n). Single-sample metrics show the value only. Times in ms, memory in MB.
