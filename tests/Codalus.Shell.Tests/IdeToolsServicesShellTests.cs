using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Aspire;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The IDE's tools for agents, round 2 (M60): compose services and Aspire resources, listed, stopped, started and read
/// through the same paths as the Containers and Aspire windows. A stand-in docker CLI plays the engine (no real engine,
/// no image), and a stand-in resource service plays a running AppHost's.
/// </summary>
public sealed class IdeToolsServicesShellTests
{
    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    /// <summary>
    /// A stand-in docker in <paramref name="folder"/>: compose's services are web and db; each service's container exists
    /// while its state file does (web running at first, db not created); stop, start and restart write it, compose up
    /// creates db, and logs prints two lines. Every call goes to calls.txt.
    /// </summary>
    private static string StandInDocker(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "web.state"), "running\n");
        var path = Path.Combine(folder, OperatingSystem.IsWindows() ? "docker.cmd" : "docker");
        Codalus.Tests.StandInScript.Write(path, OperatingSystem.IsWindows()
            ? """
              @echo off
              setlocal
              set "dir=%~dp0"
              echo %*>>"%dir%calls.txt"
              if "%1"=="version" (echo linux& exit /b 0)
              if "%1"=="compose" goto compose
              if "%1"=="ps" goto ps
              if "%1"=="logs" (echo web says hello& echo web listens on 8080& exit /b 0)
              if "%1"=="stop" (call :state %4 exited& exit /b 0)
              if "%1"=="start" (call :state %2 running& exit /b 0)
              if "%1"=="restart" (call :state %4 running& exit /b 0)
              echo unexpected: %* 1>&2
              exit /b 1
              :state
              set "name=%~1"
              set "svc=%name:app-=%"
              set "svc=%svc:-1=%"
              >"%dir%%svc%.state" echo %2
              echo %~1
              exit /b 0
              :compose
              echo %* | findstr /c:"config --services" >nul && (echo web& echo db& exit /b 0)
              echo %* | findstr /c:"up -d db" >nul && (>"%dir%db.state" echo running& exit /b 0)
              echo unexpected: %* 1>&2
              exit /b 1
              :ps
              for %%s in (web db) do if exist "%dir%%%s.state" call :row %%s
              exit /b 0
              :row
              set /p st=<"%dir%%1.state"
              echo id%1	app-%1-1	%1:dev	%st%	Up	8080/tcp
              exit /b 0
              """.ReplaceLineEndings("\r\n")
            : """
              #!/bin/sh
              dir="$(dirname "$0")"
              echo "$*" >> "$dir/calls.txt"
              for last in "$@"; do :; done
              svc="${last#app-}"; svc="${svc%-1}"
              case "$1" in
                version) echo linux; exit 0;;
                compose)
                  case "$*" in
                    *"config --services"*) printf 'web\ndb\n'; exit 0;;
                    *"up -d db"*) echo running > "$dir/db.state"; echo "Container app-db-1 Started"; exit 0;;
                  esac
                  echo "unexpected: $*" >&2; exit 1;;
                ps)
                  for s in web db; do
                    if [ -f "$dir/$s.state" ]; then printf '%s\t%s\t%s\t%s\t%s\t%s\n' "id$s" "app-$s-1" "$s:dev" "$(cat "$dir/$s.state")" "Up" "8080/tcp"; fi
                  done
                  exit 0;;
                logs) printf 'web says hello\nweb listens on 8080\n'; exit 0;;
                stop) echo exited > "$dir/$svc.state"; echo "$last"; exit 0;;
                start|restart) echo running > "$dir/$svc.state"; echo "$last"; exit 0;;
              esac
              echo "unexpected: $*" >&2
              exit 1
              """);
        return path;
    }

    [AvaloniaFact]
    public async Task Agents_list_stop_start_and_read_the_compose_services_of_the_workspace()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "app");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "compose.yaml"), "services:\n  web:\n    build: .\n  db:\n    image: postgres\n");
        var engine = Path.Combine(f.UserData, "engine");
        var docker = StandInDocker(engine);
        await f.Shell.Workbench.Settings.SetAsync(Workbench.Containers.ContainerEngine.ToolSetting, $"\"{docker}\"", SettingScope.User, CancellationToken.None);
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution?.Directory is not null, seconds: 60);

        // The services with their containers' state, as the engine tells them.
        var listed = await CallAsync(f, "list_services");
        Assert.False(listed.IsError, listed.Text);
        Assert.Contains("Compose file compose.yaml (project app, docker):", listed.Text, StringComparison.Ordinal);
        Assert.Contains("- web: running (Up), container app-web-1, image web:dev, ports 8080/tcp", listed.Text, StringComparison.Ordinal);
        Assert.Contains("- db: not created (service_start creates and starts it)", listed.Text, StringComparison.Ordinal);

        // Stop: the engine's stop on its container, as the Containers window does, and the new state.
        var stopped = await CallAsync(f, "service_stop", """{"name":"web"}""");
        Assert.False(stopped.IsError, stopped.Text);
        Assert.StartsWith("Done (stop). web: exited", stopped.Text, StringComparison.Ordinal);
        Assert.Contains("stop -t 5 app-web-1", File.ReadAllText(Path.Combine(engine, "calls.txt")), StringComparison.Ordinal);
        Assert.Contains("stop app-web-1 (an AI agent)", f.Shell.ContainerLog.GetText(), StringComparison.Ordinal);

        // Nothing to stop for a service never created; starting one is Compose Up of that service.
        Assert.True((await CallAsync(f, "service_stop", """{"name":"db"}""")).IsError);
        var started = await CallAsync(f, "service_start", """{"name":"db"}""");
        Assert.False(started.IsError, started.Text);
        Assert.StartsWith("Done (start). db: running", started.Text, StringComparison.Ordinal);
        Assert.Matches(@"compose -f .*compose\.yaml -p app up -d db", File.ReadAllText(Path.Combine(engine, "calls.txt")));

        // Its logs, as the Containers window reads them.
        var logs = await CallAsync(f, "service_logs", """{"name":"web","lines":50}""");
        Assert.False(logs.IsError, logs.Text);
        Assert.Contains("web says hello\nweb listens on 8080", logs.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("logs --tail 50 app-web-1", File.ReadAllText(Path.Combine(engine, "calls.txt")), StringComparison.Ordinal);

        // A compose service has no traces; an unknown name says how to find the services.
        Assert.Contains("no traces of compose services", (await CallAsync(f, "service_traces", """{"name":"web"}""")).Text, StringComparison.Ordinal);
        var unknown = await CallAsync(f, "service_restart", """{"name":"nothing"}""");
        Assert.Equal((true, "No service is named 'nothing'. list_services lists them."), (unknown.IsError, unknown.Text));
    }

    [AvaloniaFact]
    public async Task Agents_list_stop_and_read_the_resources_of_a_running_aspire_apphost()
    {
        await using var f = ShellFixture.Create();
        var service = new StandInResourceService();
        using var client = new AspireResourceClient(new Uri("http://localhost:1"), "key", service);
        var detach = f.Shell.AttachAspireResourceService(Path.Combine(f.UserData, "AppHost", "AppHost.csproj"), client);
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.AspireWindow.Resources.Count == 2, describe: () => $"{f.Shell.AspireWindow.Resources.Count} resources");

            var listed = await CallAsync(f, "list_services");
            Assert.False(listed.IsError, listed.Text);
            Assert.Contains("Aspire AppHost AppHost (running without the debugger):", listed.Text, StringComparison.Ordinal);
            Assert.Contains("- frontend (Project): Running — http://localhost:5000 [can stop, restart]", listed.Text, StringComparison.Ordinal);
            Assert.Contains("- cache (Container): NotStarted [can start]", listed.Text, StringComparison.Ordinal);

            // Stop: the resource's own command, then its new state from the resource service.
            var stopped = await CallAsync(f, "service_stop", """{"name":"frontend"}""");
            Assert.False(stopped.IsError, stopped.Text);
            Assert.StartsWith("Done (stop). frontend (Project): Exited", stopped.Text, StringComparison.Ordinal);
            Assert.Equal(["resource-stop frontend-abc"], service.Commands);
            Assert.Contains("frontend: Stop (an AI agent)", f.Shell.AspireLog.GetText(), StringComparison.Ordinal);

            // A command the resource has disabled is refused without reaching the service.
            var refused = await CallAsync(f, "service_stop", """{"name":"cache"}""");
            Assert.True(refused.IsError, refused.Text);
            Assert.Single(service.Commands);

            // The console, without its colors.
            var logs = await CallAsync(f, "service_logs", """{"name":"frontend"}""");
            Assert.False(logs.IsError, logs.Text);
            Assert.EndsWith("Now listening on: http://localhost:5000\ninfo: started", logs.Text, StringComparison.Ordinal);

            // Traces and metrics are the dashboard's; a container resource isn't debugged here.
            var traces = await CallAsync(f, "service_metrics", """{"name":"frontend"}""");
            Assert.True(traces.IsError);
            Assert.Contains("Aspire dashboard", traces.Text, StringComparison.Ordinal);
            Assert.Contains("only the AppHost's project resources", (await CallAsync(f, "service_debug", """{"name":"cache"}""")).Text, StringComparison.Ordinal);
        }
        finally
        {
            await detach();
        }

        var after = await CallAsync(f, "service_start", """{"name":"frontend"}""");
        Assert.True(after.IsError);
        Assert.Contains("has stopped", after.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The AppHost's resource service: frontend (a running project, with stop and restart) and cache (a container not
    /// started, with start); WatchResources sends the list and ends (the shell watches again), a stop command makes
    /// frontend Exited, and the console has two lines.
    /// </summary>
    private sealed class StandInResourceService : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _commands = new();
        private volatile string _frontend = "Running";

        public IReadOnlyList<string> Commands => [.. _commands];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.AbsolutePath["/aspire.v1.DashboardService/".Length..];
            byte[]? message = null;
            switch (method)
            {
                case "WatchResources":
                    var running = _frontend == "Running";
                    var initial = new ProtoWriter()
                        .Bytes(1, Resource("frontend-abc", "Project", "frontend", _frontend, "http://localhost:5000", ("resource-start", !running), ("resource-stop", running), ("resource-restart", running)))
                        .Bytes(1, Resource("cache", "Container", "cache", "NotStarted", null, ("resource-start", true), ("resource-stop", false)))
                        .ToArray();
                    message = new ProtoWriter().Bytes(1, initial).ToArray();
                    break;
                case "WatchResourceConsoleLogs":
                    // ConsoleLogLine { text = 1; line_number = 3; }
                    message = new ProtoWriter()
                        .Bytes(1, [.. new ProtoWriter().String(1, "Now listening on: http://localhost:5000").ToArray(), 3 << 3, 1])
                        .Bytes(1, [.. new ProtoWriter().String(1, "\u001b[32minfo\u001b[0m: started").ToArray(), 3 << 3, 2])
                        .ToArray();
                    break;
                case "ExecuteResourceCommand":
                    var body = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                    var (command, name) = ReadCommand(body.AsSpan(5));
                    _commands.Enqueue(command + " " + name);
                    if (command == "resource-stop" && name == "frontend-abc")
                    {
                        _frontend = "Exited";
                    }

                    message = new ProtoWriter().Bool(1, true).ToArray(); // kind 1: succeeded
                    break;
            }

            var stream = new MemoryStream();
            if (message is not null)
            {
                var header = new byte[5];
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);
                stream.Write(header);
                stream.Write(message);
                stream.Position = 0;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new StreamContent(stream) };
            response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }

        /// <summary>Resource { name = 1; type = 2; display_name = 3; state = 5; commands = 11; urls = 13; }.</summary>
        private static byte[] Resource(string name, string type, string display, string state, string? url, params (string Name, bool Enabled)[] commands)
        {
            var resource = new ProtoWriter().String(1, name).String(2, type).String(3, display).String(5, state);
            foreach (var (command, enabled) in commands)
            {
                // ResourceCommand { name = 1; display_name = 2; state = 9 (0 enabled, 1 disabled); }
                byte[] entry = [.. new ProtoWriter().String(1, command).String(2, char.ToUpperInvariant(command["resource-".Length]) + command["resource-".Length..][1..]).ToArray(), .. enabled ? Array.Empty<byte>() : [9 << 3, 1]];
                resource.Bytes(11, entry);
            }

            if (url is not null)
            {
                resource.Bytes(13, new ProtoWriter().String(1, "http").String(2, url).ToArray());
            }

            return resource.ToArray();
        }

        private static (string Command, string Name) ReadCommand(ReadOnlySpan<byte> data)
        {
            var reader = new ProtoReader(data);
            string command = "", name = "";
            while (reader.Next(out var field, out var wire))
            {
                switch (field)
                {
                    case 1 when wire == 2:
                        command = reader.ReadString();
                        break;
                    case 2 when wire == 2:
                        name = reader.ReadString();
                        break;
                    default:
                        reader.Skip(wire);
                        break;
                }
            }

            return (command, name);
        }
    }
}
