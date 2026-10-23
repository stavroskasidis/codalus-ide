# 0043. Aspire integration: the IDE execution protocol and the resource service

- **Status:** accepted (M17b, 2026-10-11; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

.NET Aspire apps are orchestrated by an AppHost project. Run from the command line, the AppHost starts its orchestrator (DCP, from the `Aspire.Hosting.Orchestration.<rid>` package of the user's restore), which starts the projects, containers and executables, and the dashboard. An IDE wants three things from it: to start (and debug) the projects itself, to show the resources, and to open the dashboard. D12 settled that we integrate only through the IDE execution protocol and never redistribute DCP.

**D12, DCP's licence (checked):** the orchestration package's nuspec declares MIT but requires licence acceptance, and its `tools/` folder carries Microsoft's `EULA.rtf` for the DCP binaries. We never ship, download or start DCP: the user's AppHost does, from the user's own restore. The dashboard (`Aspire.Dashboard.Sdk`) and `Aspire.Hosting` are MIT.

## Decisions

**Run and debug: the AppHost is the startup project; its projects come back to the IDE**
- F5 or Ctrl+F5 on a project whose `IsAspireHost` is true (evaluated by the build host, `ProjectInfo.IsAspireHost`) builds it and starts it with its launch profile, plus `DEBUG_SESSION_PORT` (`localhost:<port>`), `DEBUG_SESSION_TOKEN`, `DEBUG_SESSION_RUN_MODE` (`Debug` or `NoDebug`) and `DEBUG_SESSION_INFO` (protocol `2024-03-03`, launch configuration `project`).
- `IdeExecutionServer` (workbench `Aspire/`) is the IDE side: an `HttpListener` on the loopback interface with the bearer token, serving `GET /info`, `PUT /run_session` (one project: its path, mode, launch profile or none, the AppHost's environment and arguments), `DELETE /run_session/{id}`, and the `/run_session/notify` WebSocket with `processRestarted`, `sessionTerminated` and `serviceLogs`.
- A run session's project starts like a startup project: its launch profile (the one named, none when disabled), then the AppHost's environment over it (its ports, service discovery, OpenTelemetry), and the AppHost's arguments. Ctrl+F5: a program with its own Run channel, its lines also sent to the dashboard's console logs. F5: a program of the AppHost's **compound debug session** (`DebugService.LaunchAsync(…, compound: true)` and `AddLaunch`, M14c's independent `CompoundDebugAdapter`), so the AppHost and every project debug in one session; the process id and exit come from the Processes list. The request is answered once the program has its place in the session, not once it started: a start under netcoredbg can take minutes on a loaded machine, and the orchestrator gives the IDE 120 seconds to answer (`DCP_IDE_REQUEST_TIMEOUT_SECONDS`), past which it gives up on the project; the process id follows in `processRestarted`, or a failed start's end in `sessionTerminated`.

**Resources: the AppHost's resource service, read by our own gRPC client**
- The dashboard reads the AppHost's resource service (`aspire.v1.DashboardService`, gRPC). We read it too: the AppHost gets `DOTNET_RESOURCE_SERVICE_ENDPOINT_URL` on a free loopback port (plain HTTP/2, `ASPIRE_ALLOW_UNSECURED_TRANSPORT`), and `ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY` with a key of ours, which the dashboard is given too.
- `AspireResourceClient` speaks gRPC directly over `HttpClient` (HTTP/2 prior knowledge, gRPC framing, trailers) with a small protocol buffers reader of our own and the field numbers of `dashboard_service.proto` (MIT): `WatchResources`, `WatchResourceConsoleLogs` and `ExecuteResourceCommand`. No Grpc.Net.Client or Google.Protobuf in the Native AOT shell, no generated code; unknown fields are skipped, so newer AppHosts still read.
- The **Aspire window** lists the resources (projects, containers, executables; hidden ones and parameters left out) with state and endpoints; for the selected one its console output, environment, endpoints and properties; Start, Stop and Restart are its commands; Open Dashboard opens the login URL the AppHost prints (and opens it on start when the profile has `launchBrowser`).

## Consequences

- Only `project` launch configurations come to the IDE; executables, containers and other resource types stay with DCP, as in other IDEs.
- In a debug session, a project's output goes to the Debug Console, not to the dashboard's console logs (`TODO(issue)`).
- A debug session holds at most eight programs (the AppHost and seven projects; `CompoundDebugAdapter.MaxParts`); more projects are reported as not started (`TODO(issue)`).
- The Database Explorer's Aspire connections still come from the AppHost's source (M27b); live connection strings from the resource service are a `TODO(issue)`.
- The protocol version is `2024-03-03`; newer ones (`2024-04-23`, `2025-10-01`) add messages and launch configurations we don't use yet.
