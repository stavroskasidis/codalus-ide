using System.Diagnostics;
using System.Globalization;
using System.Text;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Build;

/// <summary>A program started by Run Without Debugging. Its output goes to its own Output channel.</summary>
public sealed class RunningProgram : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RunningProgram(string project, string name, Process process, OutputChannel? channel)
    {
        Project = project;
        Name = name;
        _process = process;
        Channel = channel;
        ProcessId = process.Id;
    }

    public string Project { get; }

    public string Name { get; }

    public int ProcessId { get; }

    /// <summary>The channel with the program's output; null when it runs in a console window of its own.</summary>
    public OutputChannel? Channel { get; }

    /// <summary>The Hot Reload state when the program runs under <c>dotnet watch</c> (M15b); null otherwise.</summary>
    public HotReloadSession? HotReload { get; init; }

    /// <summary>Completes with the exit code.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>Completes with the first URL a server reports ("Now listening on: http://localhost:5045").</summary>
    public Task<string> Listening => _listening.Task;

    internal void OnLine(string line)
    {
        const string marker = "Now listening on:";
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            _listening.TrySetResult(line[(at + marker.Length)..].Trim());
        }
    }

    public bool HasExited => _exited.Task.IsCompleted;

    internal void OnExited(bool waitForOutput)
    {
        int code;
        try
        {
            if (waitForOutput)
            {
                // Lets the asynchronous readers deliver the last lines first.
                _process.WaitForExit();
            }

            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        _exited.TrySetResult(code);
        _listening.TrySetCanceled();
    }

    /// <summary>Ends the program and every process it started.</summary>
    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Run Without Debugging (ADR 0006): the startup project, its launch profiles from <c>launchSettings.json</c>, and the
/// programs started from it. The build before a run is the caller's (the shell builds, then calls <see cref="StartAsync"/>).
/// </summary>
public sealed class RunService : IDisposable
{
    private static readonly string[] s_listeningMarkers = ["Now listening on:", "Application started."];

    private readonly SolutionService _solutions;
    private readonly OutputService _output;
    private readonly Action<Uri> _openBrowser;
    private readonly Lock _gate = new();
    private readonly List<RunningProgram> _running = [];
    private readonly Dictionary<string, string> _profiles = new(SolutionService.PathComparer);
    private string? _startupProject;

    public RunService(SolutionService solutions, OutputService output, Action<Uri> openBrowser)
    {
        _solutions = solutions;
        _output = output;
        _openBrowser = openBrowser;
    }

    /// <summary>The project Run starts: the chosen one while it is in the solution, else the first executable project.</summary>
    public string? StartupProject
    {
        get
        {
            var projects = _solutions.Solution?.Projects ?? [];
            if (_startupProject is { } chosen && projects.Any(p => SolutionService.PathComparer.Equals(p.Path, chosen)))
            {
                return chosen;
            }

            return projects.Select(p => p.Path).FirstOrDefault(IsExecutable);
        }
    }

    /// <summary>The user's choice (null: the default), for saving with the solution's state.</summary>
    public string? ChosenStartupProject => _startupProject;

    /// <summary>The chosen launch profile names by project path, for saving with the solution's state.</summary>
    public IReadOnlyDictionary<string, string> ChosenProfiles
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_profiles, SolutionService.PathComparer);
            }
        }
    }

    public IReadOnlyList<RunningProgram> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _running];
            }
        }
    }

    public event EventHandler? StartupChanged;

    public event EventHandler? RunningChanged;

    /// <summary>Whether a project builds a program (OutputType Exe or WinExe, once evaluated).</summary>
    public bool IsExecutable(string projectPath) =>
        _solutions.Projects.TryGetValue(projectPath, out var project) && project.OutputType is { } type
        && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets the single startup project (Set as Startup Project): multiple startup projects are turned off.</summary>
    public void SetStartupProject(string? projectPath)
    {
        lock (_gate)
        {
            _startupProject = projectPath;
            _multiple = false;
        }

        StartupChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The launch profile chosen for a project (null: the first runnable one).</summary>
    public string? ChosenProfile(string projectPath)
    {
        lock (_gate)
        {
            return _profiles.GetValueOrDefault(projectPath);
        }
    }

    public void SetProfile(string projectPath, string? profile)
    {
        lock (_gate)
        {
            if (profile is null)
            {
                _profiles.Remove(projectPath);
            }
            else
            {
                _profiles[projectPath] = profile;
            }
        }

        StartupChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores the solution's saved choices (or clears them when a solution closes).</summary>
    public void Load(string? startupProject, IReadOnlyDictionary<string, string>? profiles, bool multiple = false, IReadOnlyList<StartupEntry>? entries = null)
    {
        lock (_gate)
        {
            _startupProject = startupProject;
            _profiles.Clear();
            foreach (var (project, profile) in profiles ?? new Dictionary<string, string>())
            {
                _profiles[project] = profile;
            }

            _multiple = multiple;
            _entries = [.. entries ?? []];
        }

        StartupChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ multiple startup projects (M14c)

    private bool _multiple;
    private List<StartupEntry> _entries = [];

    /// <summary>Whether Start and Run start several projects (the Startup Projects dialog's "Multiple startup projects").</summary>
    public bool MultipleStartup
    {
        get
        {
            lock (_gate)
            {
                return _multiple;
            }
        }
    }

    /// <summary>Every project's action in the multiple-startup setting, in start order (projects without one are None).</summary>
    public IReadOnlyList<StartupEntry> StartupEntries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// The projects a start starts, in order: with multiple startup projects, those whose action isn't None and that
    /// are still in the solution; otherwise the startup project, started.
    /// </summary>
    public IReadOnlyList<StartupEntry> StartupTargets
    {
        get
        {
            var projects = _solutions.Solution?.Projects ?? [];
            lock (_gate)
            {
                if (_multiple)
                {
                    return [.. _entries.Where(e => e.Action != StartupAction.None && projects.Any(p => SolutionService.PathComparer.Equals(p.Path, e.Project)))];
                }
            }

            return StartupProject is { } single ? [new StartupEntry(single, StartupAction.Start)] : [];
        }
    }

    /// <summary>Sets single or multiple startup projects (the Startup Projects dialog's Apply).</summary>
    public void SetStartupProjects(bool multiple, IReadOnlyList<StartupEntry> entries, string? single = null)
    {
        lock (_gate)
        {
            _multiple = multiple;
            _entries = [.. entries];
            if (!multiple && single is not null)
            {
                _startupProject = single;
            }
        }

        StartupChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A project's launch profiles (empty without a launch settings file).</summary>
    public async Task<(RunInfo Info, IReadOnlyList<LaunchProfile> Profiles, string? Error)> GetProfilesAsync(string projectPath, CancellationToken cancellationToken)
    {
        var info = await _solutions.GetRunInfoAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var profiles = LaunchSettings.Read(info.LaunchSettings, out var error);
        return (info, profiles, error);
    }

    /// <summary>
    /// The ASP.NET Core environment every run and debug session uses (M15a), over the launch profile's
    /// (<c>ASPNETCORE_ENVIRONMENT</c> and <c>DOTNET_ENVIRONMENT</c>); null keeps the profile's.
    /// </summary>
    public string? EnvironmentOverride { get; set; }

    /// <summary>Applies <see cref="EnvironmentOverride"/> to a plan.</summary>
    public LaunchPlan Adjust(LaunchPlan plan)
    {
        if (EnvironmentOverride is not { Length: > 0 } environment)
        {
            return plan;
        }

        var variables = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = environment,
            ["DOTNET_ENVIRONMENT"] = environment,
        };
        return WslLaunch.Apply(plan with { Environment = variables });
    }

    /// <summary>The profile a run of <paramref name="projectPath"/> uses: the chosen one, else the first runnable one.</summary>
    public LaunchProfile? ResolveProfile(string projectPath, IReadOnlyList<LaunchProfile> profiles) =>
        profiles.FirstOrDefault(p => p.Name == ChosenProfile(projectPath)) ?? profiles.FirstOrDefault(p => p.IsRunnable);

    /// <summary>
    /// What a run of <paramref name="projectPath"/> starts (program, arguments, directory, environment) with its launch
    /// profile, for running it somewhere else (the integrated terminal). <c>Error</c> explains ignored launch settings.
    /// </summary>
    public async Task<(LaunchPlan Plan, LaunchProfile? Profile, string? Error)> PlanAsync(string projectPath, CancellationToken cancellationToken)
    {
        var (info, profiles, error) = await GetProfilesAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var profile = ResolveProfile(projectPath, profiles);
        return (await PlanAsync(info, profile, cancellationToken).ConfigureAwait(false), profile, error);
    }

    /// <summary>
    /// The plan of a project's run with <paramref name="profile"/>, the environment override applied. A
    /// <c>DebugRoslynComponent</c> profile runs the compiler on its target project (M14c, <see cref="RoslynComponents"/>).
    /// </summary>
    public async Task<LaunchPlan> PlanAsync(RunInfo info, LaunchProfile? profile, CancellationToken cancellationToken)
    {
        var plan = profile is { CommandName: LaunchProfile.RoslynComponent }
            ? await RoslynComponents.PlanAsync(_solutions, info, profile, cancellationToken).ConfigureAwait(false)
            : LaunchPlanner.Plan(info, profile);
        return Adjust(plan);
    }

    /// <summary>The folder of the <c>dotnet</c> the solution builds with (null: the one on PATH), for Hot Reload.</summary>
    public string? DotnetRoot { get; set; }

    /// <summary>
    /// The plan of a Hot Reload run (ADR 0022): <c>dotnet watch run</c> on the project, with the launch profile already
    /// applied (so <c>--no-launch-profile</c>), restarting on rude edits instead of asking, and plain-text messages.
    /// </summary>
    public LaunchPlan HotReloadPlan(string projectPath, LaunchPlan plan, string? commandLineArgs)
    {
        var dotnet = DotnetRoot is { } root && File.Exists(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
            : "dotnet";
        var arguments = $"watch run --project \"{projectPath}\" --no-launch-profile --non-interactive";
        if (!string.IsNullOrWhiteSpace(commandLineArgs))
        {
            arguments += " -- " + commandLineArgs;
        }

        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal)
        {
            ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1",
            ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true",
            ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1",
        };
        return plan with { FileName = dotnet, Arguments = arguments.TrimEnd(), ArgumentList = null, Wsl = null, Environment = environment };
    }

    /// <summary>
    /// Starts a (built) project with its launch profile. Output goes to a "Run: name" channel, unless
    /// <paramref name="externalConsole"/> (Windows console apps) gives it a console window of its own. With
    /// <paramref name="hotReload"/>, it runs under <c>dotnet watch</c>: saved edits are applied to the running program.
    /// </summary>
    /// <param name="openBrowser">False keeps the launch profile's browser closed (browser debugging opens its own, ADR 0026).</param>
    public async Task<RunningProgram> StartAsync(string projectPath, bool externalConsole, CancellationToken cancellationToken, bool hotReload = false, bool openBrowser = true)
    {
        var (info, profiles, error) = await GetProfilesAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var channel = _output.GetOrCreate("daedalus.run." + name, "Run: " + name);
        if (error is not null)
        {
            channel.AppendLine($"Ignoring the launch settings: {error}");
        }

        var profile = ResolveProfile(projectPath, profiles);
        var plan = await PlanAsync(info, profile, cancellationToken).ConfigureAwait(false);
        if (!openBrowser)
        {
            plan = plan with { BrowserUrl = null };
        }

        if (hotReload)
        {
            plan = HotReloadPlan(projectPath, plan, profile?.CommandLineArgs);
            externalConsole = false;
        }

        var console = externalConsole && OperatingSystem.IsWindows() && string.Equals(info.OutputType, "Exe", StringComparison.OrdinalIgnoreCase);
        return Launch(projectPath, name, plan, channel, console, hotReload, profile is null ? string.Empty : $"   (profile '{profile.Name}')");
    }

    /// <summary>
    /// Starts a plan made elsewhere (folder mode's launch configurations, ADR 0033): its output goes to a "Run: name"
    /// channel. <paramref name="key"/> identifies what runs (a project or a program).
    /// </summary>
    public RunningProgram StartPlan(string key, string name, LaunchPlan plan) =>
        Launch(key, name, plan, _output.GetOrCreate("daedalus.run." + name, "Run: " + name), console: false, hotReload: false, string.Empty);

    private RunningProgram Launch(string projectPath, string name, LaunchPlan plan, OutputChannel channel, bool console, bool hotReload, string note)
    {
        var start = CreateStartInfo(plan, console);

        channel.Clear();
        channel.AppendLine($"> {plan.FileName} {plan.Arguments}".TrimEnd() + note);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<RunningProgram>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => _ = ready.Task.ContinueWith(t => OnExited(t.Result, plan, channel, console), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"{plan.FileName} did not start.");
        }

        var program = new RunningProgram(projectPath, name, process, console ? null : channel) { HotReload = hotReload ? new HotReloadSession() : null };
        var browserOpened = plan.BrowserUrl is null;
        void OpenBrowserOnce()
        {
            lock (program)
            {
                if (browserOpened)
                {
                    return;
                }

                browserOpened = true;
            }

            if (Uri.TryCreate(plan.BrowserUrl, UriKind.Absolute, out var url))
            {
                _openBrowser(url);
            }
        }

        if (!console)
        {
            void OnLine(string? line)
            {
                if (line is null)
                {
                    return;
                }

                channel.AppendLine(line);
                program.OnLine(line);
                program.HotReload?.OnLine(line);
                if (!browserOpened && s_listeningMarkers.Any(m => line.Contains(m, StringComparison.Ordinal)))
                {
                    OpenBrowserOnce();
                }
            }

            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close(); // no console to read from: input ends right away instead of hanging
        }

        if (!browserOpened)
        {
            // Servers that don't log "Now listening on" still get their browser.
            _ = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(_ =>
            {
                if (!program.HasExited)
                {
                    OpenBrowserOnce();
                }
            }, TaskScheduler.Default);
        }

        lock (_gate)
        {
            _running.Add(program);
        }

        ready.SetResult(program);
        RunningChanged?.Invoke(this, EventArgs.Empty);
        return program;
    }

    private void OnExited(RunningProgram program, LaunchPlan plan, OutputChannel channel, bool console)
    {
        program.OnExited(waitForOutput: !console);
        var code = program.Exited.Result;
        channel.AppendLine($"The program '[{program.ProcessId.ToString(CultureInfo.InvariantCulture)}] {Path.GetFileName(plan.FileName)}' has exited with code {code.ToString(CultureInfo.InvariantCulture)} (0x{code:x}).");
        lock (_gate)
        {
            _running.Remove(program);
        }

        RunningChanged?.Invoke(this, EventArgs.Empty);
        program.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo(LaunchPlan plan, bool console)
    {
        ProcessStartInfo start;
        if (console)
        {
            // cmd keeps the window open after the program ends, as an IDE's console runs do.
            start = new ProcessStartInfo("cmd.exe", $"/s /c \"\"{plan.FileName}\" {plan.Arguments} & echo. & pause\"")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
        }
        else
        {
            start = new ProcessStartInfo(plan.FileName, plan.ArgumentList ?? [])
            {
                Arguments = plan.ArgumentList is null ? plan.Arguments : string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }

        start.WorkingDirectory = plan.WorkingDirectory;
        foreach (var (name, value) in plan.Environment)
        {
            start.Environment[name] = value;
        }

        return start;
    }

    /// <summary>Stops every running program.</summary>
    public void StopAll()
    {
        foreach (var program in Running)
        {
            program.Stop();
        }
    }

    public void Dispose() => StopAll();
}

/// <summary>What a start does with a project of multiple startup projects (M14c).</summary>
public enum StartupAction
{
    None,

    /// <summary>Start Debugging debugs it; Start Without Debugging runs it.</summary>
    Start,

    /// <summary>It runs without the debugger either way.</summary>
    StartWithoutDebugging,
}

/// <summary>A project's action in the multiple-startup setting.</summary>
public sealed record StartupEntry(string Project, StartupAction Action);
