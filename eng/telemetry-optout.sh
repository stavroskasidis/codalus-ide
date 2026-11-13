#!/usr/bin/env bash
# The tools' telemetry opt-outs (M59, ADR 0098), sourced by the scripts that build, test, package or run Codalus:
# the .NET SDK's, Avalonia's build task's (Avalonia.BuildServices, in every Avalonia 12 package: its only switch is
# this variable), Microsoft.Testing.Platform's and dotnet-svcutil's. The same list as ToolTelemetry.Variables
# (src/Codalus.Platform/ToolTelemetry.cs) and .github/actions/setup; ToolTelemetryTests checks the three agree.
# A build outside these scripts sets them the same way (CONTRIBUTING.md).
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_SVCUTIL_TELEMETRY_OPTOUT=1
