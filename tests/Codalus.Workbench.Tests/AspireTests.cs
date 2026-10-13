using System.Collections.Concurrent;
using System.Diagnostics;
using Codalus.Workbench.Aspire;

namespace Codalus.Workbench.Tests;

/// <summary>The Aspire test runs alone, after the parallel ones: its orchestrator has 20 s to start on a busy runner.</summary>
[CollectionDefinition(nameof(AspireTests), DisableParallelization = true)]
public sealed class AspireTestsAlone;

/// <summary>
/// Aspire (M17b, ADR 0043) on samples/Containers/Aspire, with the user's Aspire packages (13.1, restored from nuget.org)
/// and their orchestrator: the AppHost asks our IDE execution server to start its two projects, and the resource service
/// describes them, a Redis container that waits for an explicit start, and their logs. No container is started.
/// </summary>
[Collection(nameof(AspireTests))]
public sealed class AspireTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-aspire-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentBag<Process> _processes = [];
    private readonly DateTime _started = DateTime.Now;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
            }
        }

        await AspireRuntime.StopOrchestratorsAsync(_started);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A program the test server starts for a run session: the built project's dll on dotnet.</summary>
    private sealed class TestSession(Process process) : IIdeRunSession
    {
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Started { get; } = Task.FromResult(process.Id);

        public Task<int?> Exited => _exited.Task;

        public void OnExit() => _exited.TrySetResult(process.ExitCode);

        public void Terminate() => process.Kill(entireProcessTree: true);
    }

    private static Process StartProcess(string fileName, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, Action<string> onLine)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { onLine(line); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { onLine(line); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    [Fact]
    public async Task The_apphost_starts_its_projects_through_the_ide_and_describes_its_resources()
    {
        // A step that never ends fails the test with what happened, instead of holding the test run (seen on Windows CI).
        var log = new ConcurrentQueue<string>();
        var body = RunAsync(log);
        if (await Task.WhenAny(body, Task.Delay(TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken)) != body)
        {
            Assert.Fail("The test didn't finish within 10 minutes:\n" + string.Join('\n', log));
        }

        await body;
    }

    private async Task RunAsync(ConcurrentQueue<string> log)
    {
        await AspireRuntime.RequireContainerEngineAsync();
        var ct = TestContext.Current.CancellationToken;
        var fixture = ContainerRuntime.CopyFixture(_root);
        var appHost = Path.Combine(fixture, "Aspire", "AppHost", "AppHost.csproj");
        ContainerRuntime.Build(appHost);

        var requests = new ConcurrentQueue<IdeRunRequest>();
        IdeExecutionServer? self = null;
        await using var server = new IdeExecutionServer((request, token) =>
        {
            requests.Enqueue(request);
            var project = request.Configuration.ProjectPath!;
            var name = Path.GetFileNameWithoutExtension(project);
            var dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", name + ".dll");
            TestSession? session = null;
            var process = StartProcess("dotnet", [dll, .. request.Arguments], Path.GetDirectoryName(project)!, request.Environment, line =>
            {
                log.Enqueue($"[{name}] {line}");
                _ = self?.LogAsync(request.SessionId, line, isError: false); // the dashboard's console logs
            });
            _processes.Add(process);
            session = new TestSession(process);
            process.Exited += (_, _) => session.OnExit();
            return Task.FromResult<IIdeRunSession>(session);
        });
        self = server;
        server.Log += line => log.Enqueue("[server] " + line);
        server.Start();

        var apiKey = Guid.NewGuid().ToString("N");
        var resourcePort = Containers.ContainerLaunch.FreePort();
        var environment = new Dictionary<string, string>(server.AppHostEnvironment(debug: false))
        {
            ["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://localhost:{resourcePort}",
            ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
            ["ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY"] = apiKey,
            ["ASPNETCORE_URLS"] = $"http://localhost:{Containers.ContainerLaunch.FreePort()}",
            ["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = $"http://localhost:{Containers.ContainerLaunch.FreePort()}",
        };
        Process StartAppHost()
        {
            var process = StartProcess("dotnet", ["run", "--project", appHost, "--no-build", "--no-launch-profile"], Path.GetDirectoryName(appHost)!, environment, line => log.Enqueue("[apphost] " + line));
            _processes.Add(process);
            return process;
        }

        var host = StartAppHost();
        string Log() => string.Join('\n', log);

        var deadline = Stopwatch.StartNew();
        var restarted = false;
        while (requests.Count < 2)
        {
            if (host.HasExited && !restarted && requests.IsEmpty && Log().Contains("Hosting failed to start", StringComparison.Ordinal))
            {
                // Aspire gives its orchestrator 20 s to start, which a loaded CI runner can miss: once more.
                restarted = true;
                log.Enqueue("[test] The orchestrator didn't start in time: starting the AppHost again.");
                host = StartAppHost();
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromMinutes(4) && !host.HasExited, "The AppHost didn't ask for its two projects:\n" + Log());
            await Task.Delay(250, ct);
        }

        Assert.Equal(["Api.csproj", "Frontend.csproj"], requests.Select(r => Path.GetFileName(r.Configuration.ProjectPath!)).Order());
        Assert.All(requests, r => Assert.False(r.Configuration.Debug));
        Assert.All(requests, r => Assert.True(r.Environment.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT") || r.Environment.Count > 0, Log()));

        // The resource tree: the two projects running, Redis waiting for its explicit start.
        using var client = new AspireResourceClient(new Uri($"http://localhost:{resourcePort}"), apiKey);
        var resources = new Dictionary<string, AspireResource>(StringComparer.Ordinal);
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watch.CancelAfter(TimeSpan.FromMinutes(2));
        var done = false;
        string? error = null;
        while (!done)
        {
            try
            {
                await foreach (var change in client.WatchResourcesAsync(watch.Token))
                {
                    foreach (var resource in change.Initial ?? (change.Upsert is { } upsert ? [upsert] : []))
                    {
                        resources[resource.DisplayName] = resource;
                    }

                    if (resources.Values.Count(r => r.Type == "Project" && r.State == "Running") == 2 && resources.ContainsKey("cache"))
                    {
                        done = true;
                        break;
                    }
                }
            }
            catch (HttpRequestException ex) when (!watch.IsCancellationRequested)
            {
                error = ex.Message; // the resource service starts with the dashboard
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"The resources never ran ({error}): " + string.Join(", ", resources.Values.Select(r => $"{r.DisplayName}={r.State}")) + "\n" + Log());
            }
        }

        Assert.Equal("Container", resources["cache"].Type);
        Assert.NotEqual("Running", resources["cache"].State);
        var frontend = resources.Values.Single(r => r.DisplayName == "frontend");
        var url = frontend.Urls.FirstOrDefault(u => !u.IsInternal)?.Url;
        Assert.NotNull(url);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Assert.StartsWith("Frontend: Sunny, from the api", await http.GetStringAsync(url, ct), StringComparison.Ordinal);

        // The api's console output, as the dashboard shows it.
        var api = resources.Values.Single(r => r.DisplayName == "api");
        using var logs = CancellationTokenSource.CreateLinkedTokenSource(ct);
        logs.CancelAfter(TimeSpan.FromSeconds(30));
        var lines = new List<string>();
        try
        {
            await foreach (var line in client.WatchLogsAsync(api.Name, logs.Token))
            {
                lines.Add(line.Text);
                if (line.Text.Contains("Now listening on", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Contains(lines, l => l.Contains("Now listening on", StringComparison.Ordinal));

        // Stop and start a project through its commands; the IDE gets a DELETE, then a new run session.
        Assert.Contains(api.Commands, c => c.Name == "resource-stop");
        using var command = CancellationTokenSource.CreateLinkedTokenSource(ct);
        command.CancelAfter(TimeSpan.FromSeconds(90));
        string? stopped;
        try
        {
            stopped = await client.ExecuteAsync("resource-stop", api, command.Token);
        }
        catch (OperationCanceledException)
        {
            stopped = "the stop command didn't finish";
        }

        Assert.True(stopped is null, stopped + "\n" + Log());
        deadline.Restart();
        while (requests.Count < 3 && deadline.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(100, ct);
        }

        Assert.Contains("Stopping run session", Log(), StringComparison.Ordinal);
    }
}

/// <summary>
/// Aspire 13.1's orchestrator wants a container runtime: without one (GitHub's macOS runner, a Mac without Docker or
/// Podman) the Aspire tests skip with the reason, and only CI's Linux runner, which has Docker, fails instead.
/// </summary>
internal static class AspireRuntime
{
    public static async Task RequireContainerEngineAsync()
    {
        var engine = Codalus.Workbench.Containers.ContainerEngine.Resolve(Environment.GetEnvironmentVariable("CODALUS_TEST_CONTAINER_TOOL"));
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None);
        if (os is null)
        {
            var reason = $"Aspire's orchestrator needs a container runtime, and none answers ({engine.Name}: {error})";
            if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }
    }

    /// <summary>
    /// Ends the orchestrator processes started since <paramref name="since"/> that outlived their AppHost (Aspire's
    /// DCP runs detached; a killed AppHost can leave it holding the app's ports).
    /// </summary>
    public static async Task StopOrchestratorsAsync(DateTime since)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var left = System.Diagnostics.Process.GetProcesses().Where(p => p.ProcessName is "dcp" or "dcpctrl" or "dcpproc" && Started(p) >= since).ToList();
            if (left.Count == 0)
            {
                return;
            }

            if (attempt == 19)
            {
                foreach (var process in left)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            await Task.Delay(500);
        }

        static DateTime Started(System.Diagnostics.Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return DateTime.MinValue;
            }
        }
    }
}