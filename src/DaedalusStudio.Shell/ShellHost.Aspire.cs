using System.Diagnostics;
using DaedalusStudio.Debugging;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Aspire;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// .NET Aspire (M17b, ADR 0043): an AppHost project runs (Ctrl+F5) or debugs (F5) with Daedalus Studio as its IDE. The
/// AppHost's orchestrator asks our IDE execution server to start each project resource, which runs like any project
/// (its Run output, or a program of the AppHost's compound debug session); the Aspire window shows the resources from
/// the AppHost's resource service, and the dashboard opens from it. The orchestrator is the user's (D12): never ours.
/// </summary>
public sealed partial class ShellHost
{
    private const string DashboardLoginMarker = "Login to the dashboard at ";

    private AspireRun? _aspire;

    public AspireViewModel AspireWindow { get; private set; } = null!;

    public OutputChannel AspireLog => Workbench.Output.GetOrCreate("daedalus.aspire", "Aspire");

    /// <summary>The running AppHost: its IDE server, its resource service client and what started it.</summary>
    private sealed class AspireRun(string project, bool debug, IdeExecutionServer server, AspireResourceClient client) : IAsyncDisposable
    {
        public string Project { get; } = project;

        public bool Debug { get; } = debug;

        public IdeExecutionServer Server { get; } = server;

        public AspireResourceClient Client { get; } = client;

        public CancellationTokenSource Lifetime { get; } = new();

        public bool OpenDashboard { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Lifetime.CancelAsync().ConfigureAwait(false);
            await Server.DisposeAsync().ConfigureAwait(false);
            Client.Dispose();
            Lifetime.Dispose();
        }
    }

    private void InitializeAspire()
    {
        AspireWindow = new AspireViewModel(ShellIntegration.OpenUrl);
        Debugger.Output += (_, output) => OnAspireLine(output.Text, fromDebugger: true);
        Debugger.Ended += (_, _) =>
        {
            if (_aspire is { Debug: true } run)
            {
                Ui.Post(() => _ = StopAspireAsync(run));
            }
        };
    }

    /// <summary>Whether a project is an Aspire AppHost (<c>IsAspireHost</c>).</summary>
    public bool IsAspireHost(string project) => Solutions.Projects.TryGetValue(project, out var info) && info.IsAspireHost;

    public void ShowAspire() => Layout.ShowToolWindow(AspireViewModel.ToolWindowId, focus: true);

    public void OpenAspireDashboard()
    {
        if (AspireWindow.HasDashboard)
        {
            AspireWindow.OpenDashboard();
        }
        else
        {
            StatusBar.ShowMessage(Localizer.Get("msg.aspire.noAspireDashboardIsRunningStart", "No Aspire dashboard is running: start an AppHost first."), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Runs or debugs a built AppHost with this IDE as its IDE: the IDE execution server, the resource service on a port
    /// of ours with an API key of ours, then the AppHost itself (in a compound debug session when debugging).
    /// </summary>
    public async Task<bool> StartAspireAsync(string project, bool debug)
    {
        if (_aspire is { } previous)
        {
            await StopAspireAsync(previous).ConfigureAwait(true);
        }

        var name = Path.GetFileNameWithoutExtension(project);
        var (info, profiles, error) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
        var profile = Runs.ResolveProfile(project, profiles);
        var plan = await Runs.PlanAsync(info, profile, CancellationToken.None).ConfigureAwait(true);
        var server = new IdeExecutionServer(StartRunSessionAsync);
        server.Log += line => AspireLog.AppendLine(line);
        try
        {
            server.Start();
        }
        catch (Exception ex) when (ex is System.Net.HttpListenerException or InvalidOperationException)
        {
            await server.DisposeAsync().ConfigureAwait(true);
            Workbench.Notifications.Error(Localizer.Format("msg.aspire.theAspireIdeEndpointCouldntStart", "The Aspire IDE endpoint couldn't start: {0}", ex.Message));
            return false;
        }

        var apiKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var resourcePort = global::DaedalusStudio.Workbench.Containers.ContainerLaunch.FreePort();
        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        foreach (var (variable, value) in server.AppHostEnvironment(debug))
        {
            environment[variable] = value;
        }

        // The resource service on a port of ours (plain HTTP/2 on the loopback interface), with our API key.
        environment["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://localhost:{resourcePort}";
        environment.Remove("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL");
        environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        environment["ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY"] = apiKey;
        plan = plan with { Environment = environment, BrowserUrl = null };

        var run = _aspire = new AspireRun(project, debug, server, new AspireResourceClient(new Uri($"http://localhost:{resourcePort}"), apiKey))
        {
            OpenDashboard = profile?.LaunchBrowser == true,
        };
        AspireLog.Clear();
        AspireLog.AppendLine($"> {plan.FileName} {plan.Arguments} (the AppHost, {(debug ? "debugging" : "running")} its projects through this IDE)".TrimEnd());
        if (error is not null)
        {
            AspireLog.AppendLine("Ignoring the launch settings: " + error);
        }

        AspireWindow.Attach(name, run.Client);
        Layout.ShowToolWindow(AspireViewModel.ToolWindowId, focus: false);
        _ = WatchAspireResourcesAsync(run);
        try
        {
            if (debug)
            {
                DebugLog.Clear();
                Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
                Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
                Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
                await Debugger.LaunchAsync([new DebugLaunch(name, plan)], CancellationToken.None, compound: true).ConfigureAwait(true);
            }
            else
            {
                var program = Runs.StartPlan(project, name, plan);
                program.LineReceived += line => OnAspireLine(line, fromDebugger: false);
                _ = program.Exited.ContinueWith(_ => Ui.Post(() => _ = StopAspireAsync(run)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.aspire.couldNotStart", "Could not start {0}: {1}", name, ex.Message));
            await StopAspireAsync(run).ConfigureAwait(true);
            return false;
        }
    }

    private void OnAspireLine(string text, bool fromDebugger)
    {
        if (_aspire is not { } run || run.Debug != fromDebugger)
        {
            return;
        }

        var at = text.IndexOf(DashboardLoginMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return;
        }

        var url = text[(at + DashboardLoginMarker.Length)..].Trim();
        Ui.Post(() =>
        {
            if (_aspire == run && AspireWindow.DashboardUrl is null)
            {
                AspireWindow.DashboardUrl = url;
                AspireLog.AppendLine("The dashboard: " + url);
                if (run.OpenDashboard && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    ShellIntegration.OpenUrl(uri);
                }
            }
        });
    }

    /// <summary>Follows the resource tree while the AppHost runs (its resource service starts with it, so it is retried).</summary>
    private async Task WatchAspireResourcesAsync(AspireRun run)
    {
        var token = run.Lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in run.Client.WatchResourcesAsync(token).ConfigureAwait(false))
                {
                    Ui.Post(() =>
                    {
                        if (_aspire == run)
                        {
                            AspireWindow.Apply(change);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // Not listening yet, or the AppHost restarted it.
            }

            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task StopAspireAsync(AspireRun run)
    {
        if (_aspire != run)
        {
            return;
        }

        _aspire = null;
        AspireWindow.Detach();
        AspireLog.AppendLine("The AppHost has stopped.");
        await run.DisposeAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ run sessions

    /// <summary>
    /// A project the AppHost asks for (<c>PUT /run_session</c>): its launch profile (the one named, or none), the
    /// AppHost's environment over it, and the AppHost's arguments; debugged in the AppHost's session when both ask
    /// for it, else run with its output in its Run channel and the dashboard's console logs.
    /// </summary>
    private async Task<IIdeRunSession> StartRunSessionAsync(IdeRunRequest request, CancellationToken cancellationToken)
    {
        var run = _aspire ?? throw new InvalidOperationException("No AppHost is running.");
        var project = Path.GetFullPath(request.Configuration.ProjectPath!);
        var name = Path.GetFileNameWithoutExtension(project);
        var (info, profiles, _) = await Runs.GetProfilesAsync(project, cancellationToken).ConfigureAwait(false);
        var profile = request.Configuration.DisableLaunchProfile ? null
            : request.Configuration.LaunchProfile is { Length: > 0 } named ? profiles.FirstOrDefault(p => p.Name == named) ?? Runs.ResolveProfile(project, profiles)
            : Runs.ResolveProfile(project, profiles);
        var plan = Runs.Adjust(LaunchPlanner.Plan(info, profile), info);
        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        foreach (var (variable, value) in request.Environment)
        {
            environment[variable] = value;
        }

        plan = plan with { Environment = environment, BrowserUrl = null };
        if (request.Arguments.Count > 0)
        {
            List<string> arguments = [.. global::DaedalusStudio.Workbench.Debugging.CommandLine.Split(info.RunArguments ?? string.Empty), .. request.Arguments];
            plan = plan with { ArgumentList = arguments, Arguments = string.Join(' ', arguments) };
        }

        if (run.Debug && request.Configuration.Debug)
        {
            var index = await Debugger.AddLaunchAsync(new DebugLaunch(name, plan), cancellationToken).ConfigureAwait(false);
            return new DebuggedRunSession(Debugger, index);
        }

        var program = Runs.StartPlan(project, name, plan);
        program.LineReceived += line => _ = run.Server.LogAsync(request.SessionId, line, isError: false);
        return new RunningRunSession(program);
    }

    /// <summary>A run session's program run without the debugger.</summary>
    private sealed class RunningRunSession(RunningProgram program) : IIdeRunSession
    {
        public Task<int> Started { get; } = Task.FromResult(program.ProcessId);

        public Task<int?> Exited { get; } = program.Exited.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (int?)null, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        public void Terminate() => program.Stop();
    }

    /// <summary>A run session's program in the AppHost's compound debug session: its process id and exit come from the Processes list.</summary>
    private sealed class DebuggedRunSession : IIdeRunSession
    {
        private readonly DebugService _debugger;
        private readonly int _index;
        private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DebuggedRunSession(DebugService debugger, int index)
        {
            _debugger = debugger;
            _index = index;
            debugger.ProcessesChanged += OnChanged;
            debugger.Ended += OnEnded;
            OnChanged(null, EventArgs.Empty);
        }

        public Task<int> Started => _started.Task;

        public Task<int?> Exited => _exited.Task;

        private void OnChanged(object? sender, EventArgs e)
        {
            var processes = _debugger.Processes;
            if (_index >= processes.Count)
            {
                return;
            }

            var process = processes[_index];
            if (process.Id is { } id)
            {
                _started.TrySetResult(id);
            }

            if (process.Ended)
            {
                Finish(process.ExitCode);
            }
        }

        private void OnEnded(object? sender, int? code) => Finish(null);

        private void Finish(int? code)
        {
            _started.TrySetCanceled();
            if (_exited.TrySetResult(code))
            {
                _debugger.ProcessesChanged -= OnChanged;
                _debugger.Ended -= OnEnded;
            }
        }

        public void Terminate()
        {
            if (_started.Task.IsCompletedSuccessfully)
            {
                try
                {
                    using var process = Process.GetProcessById(_started.Task.Result);
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }
        }
    }
}
