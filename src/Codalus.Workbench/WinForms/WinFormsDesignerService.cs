using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.WinForms;

/// <summary>
/// A Windows Forms designer host (M41a, ADR 0073): the process that draws a form and applies the designer's gestures,
/// behind an interface so the shell's tests use a fake one.
/// </summary>
public interface IWinFormsDesignerHost : IAsyncDisposable
{
    Task<DesignerRenderResult> LoadAsync(DesignerLoadParams parameters, CancellationToken cancellationToken);

    Task<DesignerRenderResult> RenderAsync(CancellationToken cancellationToken);

    Task<DesignerChangeResult> CreateAsync(DesignerCreateParams parameters, CancellationToken cancellationToken);

    Task<DesignerChangeResult> SetPropertiesAsync(DesignerSetPropertiesParams parameters, CancellationToken cancellationToken);

    Task<DesignerChangeResult> SetParentAsync(DesignerSetParentParams parameters, CancellationToken cancellationToken);

    Task<DesignerChangeResult> DeleteAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken);

    Task<DesignerPropertiesResult> PropertiesAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken);

    Task<DesignerEventsResult> EventsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken);

    Task SelectAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken);

    /// <summary>Moves a control in its parent's z-order (M41b).</summary>
    Task<DesignerChangeResult> SetChildIndexAsync(DesignerChildIndexParams parameters, CancellationToken cancellationToken);

    /// <summary>A collection's items, for its editor (M41b).</summary>
    Task<DesignerCollectionResult> CollectionAsync(DesignerCollectionParams parameters, CancellationToken cancellationToken);

    /// <summary>Replaces a collection's items (M41b: the collection editors, a menu's Type Here).</summary>
    Task<DesignerChangeResult> SetCollectionAsync(DesignerSetCollectionParams parameters, CancellationToken cancellationToken);

    /// <summary>A component's smart tag: its designer actions (M41b).</summary>
    Task<DesignerActionsResult> ActionsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken);

    /// <summary>Runs a designer action, or sets an action's property (M41b).</summary>
    Task<DesignerChangeResult> InvokeActionAsync(DesignerInvokeActionParams parameters, CancellationToken cancellationToken);

    /// <summary>Whether a build replaced what the host loaded since it started (#66): it must start again to show it.</summary>
    Task<bool> IsOutdatedAsync(CancellationToken cancellationToken);

    /// <summary>A line of the host's output (any thread).</summary>
    event EventHandler<string>? Log;

    /// <summary>The host restarted after a crash and lost the form (any thread): load it again.</summary>
    event EventHandler? Restarted;
}

/// <summary>
/// What designing a form needs (ADR 0073): its code file, its project (whose build output has the form's types and
/// resources), the app whose runtime the designer host runs on (the project when it's an app, else an app that
/// references it; null for a library on its own, which runs on the host's own runtimeconfig), and the designer host.
/// </summary>
public sealed record WinFormsDesignerTarget(string CodeFile, string Project, string ProjectAssembly, string? HostAssembly, string DesignerHost)
{
    /// <summary>The projects whose sources the designer's build output comes from.</summary>
    public string? HostProject { get; init; }
}

/// <summary>
/// The Windows Forms designer's service (M41a, ADR 0073): which files open in the designer, the target that designs a
/// form (Windows only, D31; trusted folders only, since the designer host runs the project's code), a build when the
/// output is older than the sources, and a designer host per designed form.
/// </summary>
public sealed class WinFormsDesignerService
{
    /// <summary>The designer host, in <c>winformsdesigner/</c> next to the shell on Windows.</summary>
    public const string DesignerFileName = "codalus-winformsdesigner.dll";

    public const string DesignerVariable = "CODALUS_WINFORMS_DESIGNER";

    /// <summary>Off Windows, why a form opens as code (D31).</summary>
    public const string WindowsOnlyMessage =
        "The Windows Forms designer runs on Windows only: Windows Forms draws only there. On this OS the form opens as code, which edits, completes and builds.";

    private static readonly string[] s_properties = ["TargetPath", "OutputType"];

    private readonly SolutionService _solutions;
    private readonly Func<string, bool> _isTrusted;
    private readonly Func<string, CancellationToken, Task<bool>> _build;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> _properties;

    public WinFormsDesignerService(
        SolutionService solutions,
        Func<string, bool> isTrusted,
        Func<string, CancellationToken, Task<bool>> build,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> properties)
    {
        _solutions = solutions;
        _isTrusted = isTrusted;
        _build = build;
        _properties = properties;
    }

    /// <summary>Whether this is Windows, where Windows Forms draws (D31); tests may say otherwise.</summary>
    public Func<bool> IsWindows { get; init; } = OperatingSystem.IsWindows;

    /// <summary>The designer host, or null when this install has none (off Windows).</summary>
    public Func<string?> DesignerHost { get; init; } = ResolveDesignerHost;

    /// <summary>The startup project, preferred as the app a library's forms run in.</summary>
    public Func<string?> StartupProject { get; init; } = () => null;

    /// <summary>Whether a build is running (#66): the designer waits for it before it checks the output or builds.</summary>
    public Func<bool> IsBuilding { get; init; } = () => false;

    /// <summary>Starts a designer host for a target (tests give a fake one).</summary>
    public Func<WinFormsDesignerTarget, CancellationToken, Task<IWinFormsDesignerHost>> HostFactory { get; init; } = WinFormsDesignerProcess.StartAsync;

    /// <summary>The designer host: <c>CODALUS_WINFORMS_DESIGNER</c>, then <c>winformsdesigner/</c> next to the shell (the Windows package has it).</summary>
    public static string? ResolveDesignerHost()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(DesignerVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "winformsdesigner", DesignerFileName));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>A form's code file (<c>MainForm.cs</c>) for its code or designer file, or null when it has no designer file.</summary>
    public static string? CodeFileOf(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var code = path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ? path[..^".Designer.cs".Length] + ".cs" : path;
        var designer = code[..^".cs".Length] + ".Designer.cs";
        return File.Exists(code) && File.Exists(designer) ? code : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> (a code file) opens in the designer: a <c>.cs</c> file of a Windows Forms project
    /// with a <c>.Designer.cs</c> next to it that has InitializeComponent (a form, a user control).
    /// </summary>
    public bool HasDesigner(string path)
    {
        if (path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) || CodeFileOf(path) is not { } code)
        {
            return false;
        }

        if (OwnerOf(code) is { } project && !project.UsesWindowsForms)
        {
            return false;
        }

        try
        {
            var designer = code[..^".cs".Length] + ".Designer.cs";
            return File.ReadAllText(designer).Contains("InitializeComponent()", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ProjectInfo? OwnerOf(string path)
    {
        var projects = _solutions.Projects.Values.ToList();
        return projects.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)))
            ?? projects.Where(p => path.StartsWith(Path.GetDirectoryName(p.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Path.Length).FirstOrDefault();
    }

    /// <summary>What designs <paramref name="codeFile"/>, or a <see cref="PreviewerException"/> that says why it can't be designed here.</summary>
    public async Task<WinFormsDesignerTarget> ResolveAsync(string codeFile, CancellationToken cancellationToken)
    {
        if (!IsWindows())
        {
            throw new PreviewerException(WindowsOnlyMessage);
        }

        if (!_solutions.IsOpen)
        {
            throw new PreviewerException("Open the solution or folder of this form to design it.");
        }

        var owner = OwnerOf(codeFile) ?? throw new PreviewerException($"{Path.GetFileName(codeFile)} isn't in a project of the solution.");
        if (!_isTrusted(owner.Path))
        {
            throw new PreviewerException("The designer runs the project's code: trust this folder to design its forms.");
        }

        var designer = DesignerHost()
            ?? throw new PreviewerException($"The Windows Forms designer (winformsdesigner/{DesignerFileName}) isn't installed with this build of Codalus IDE.");
        var projects = _solutions.Projects.Values.ToList();
        var host = IsExecutable(owner) ? owner : FindHost(owner, projects);
        var ownerProperties = await _properties(owner.Path, s_properties, cancellationToken).ConfigureAwait(false);
        var assembly = ownerProperties.GetValueOrDefault("TargetPath") is { Length: > 0 } target ? target : throw new PreviewerException($"{owner.Name} has no TargetPath.");
        string? hostAssembly = null;
        if (host is not null)
        {
            var hostProperties = host == owner ? ownerProperties : await _properties(host.Path, s_properties, cancellationToken).ConfigureAwait(false);
            hostAssembly = hostProperties.GetValueOrDefault("TargetPath") is { Length: > 0 } hostTarget ? Path.GetFullPath(hostTarget) : null;
        }

        return new WinFormsDesignerTarget(codeFile, owner.Path, Path.GetFullPath(assembly), hostAssembly, Path.GetFullPath(designer)) { HostProject = host?.Path };
    }

    private static bool IsExecutable(ProjectInfo project) =>
        project.OutputType is { } type && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    /// <summary>A Windows Forms app that references <paramref name="library"/>: the startup project if it does, else the first by name.</summary>
    private ProjectInfo? FindHost(ProjectInfo library, IReadOnlyList<ProjectInfo> projects)
    {
        bool References(ProjectInfo project, HashSet<string> seen) =>
            project.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null).Any(r =>
                SolutionService.PathComparer.Equals(r.Path, library.Path)
                || (seen.Add(r.Path!) && projects.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, r.Path)) is { } next && References(next, seen)));
        var hosts = projects.Where(p => IsExecutable(p) && p.UsesWindowsForms && References(p, new HashSet<string>(SolutionService.PathComparer)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var startup = StartupProject();
        return hosts.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, startup)) ?? hosts.FirstOrDefault();
    }

    /// <summary>Whether the build output is missing or older than the sources (the form's own code and designer files aside: the designer reads them as they are in the editor).</summary>
    public bool NeedsBuild(WinFormsDesignerTarget target)
    {
        if (!File.Exists(target.ProjectAssembly) || (target.HostAssembly is { } host && !File.Exists(host)))
        {
            return true;
        }

        var built = File.GetLastWriteTimeUtc(target.ProjectAssembly);
        var designer = target.CodeFile[..^".cs".Length] + ".Designer.cs";
        foreach (var project in new[] { target.Project, target.HostProject }.OfType<string>().Distinct(SolutionService.PathComparer))
        {
            if (!_solutions.Projects.TryGetValue(project, out var info))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(project) > built)
            {
                return true;
            }

            foreach (var item in info.Items)
            {
                if (item.Kind is "Folder" || SolutionService.PathComparer.Equals(item.Path, target.CodeFile) || SolutionService.PathComparer.Equals(item.Path, designer) || FormResources(target.CodeFile).Any(r => SolutionService.PathComparer.Equals(r.Path, item.Path)))
                {
                    continue;
                }

                if (File.Exists(item.Path) && File.GetLastWriteTimeUtc(item.Path) > built)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The form's resource files that exist (M41b): the default one first (an empty culture), then one per culture
    /// (<c>MainForm.fr.resx</c>). The designer reads them from the editor, so they don't need a build.
    /// </summary>
    public static IReadOnlyList<(string Culture, string Path)> FormResources(string codeFile)
    {
        var folder = Path.GetDirectoryName(codeFile)!;
        var stem = Path.GetFileNameWithoutExtension(codeFile);
        var files = new List<(string, string)>();
        var main = Path.Combine(folder, stem + ".resx");
        if (File.Exists(main))
        {
            files.Add((string.Empty, main));
        }

        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, stem + ".*.resx").Order(StringComparer.OrdinalIgnoreCase))
            {
                var culture = Path.GetFileName(file)[(stem.Length + 1)..^".resx".Length];
                if (culture.Length > 0 && !culture.Contains('.', StringComparison.Ordinal))
                {
                    files.Add((culture, file));
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Waits for a running build (#66: its output may be half written, and a second build can't start), builds when the
    /// output is stale, then starts a designer host for the target.
    /// </summary>
    public async Task<IWinFormsDesignerHost> StartAsync(WinFormsDesignerTarget target, Action<string> progress, CancellationToken cancellationToken)
    {
        await WaitForBuildAsync(progress, cancellationToken).ConfigureAwait(false);
        if (NeedsBuild(target))
        {
            bool built;
            try
            {
                built = await BuildAsync(target, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A build started between the wait and this one ("A build is already running."): wait for it, then check again.
                await WaitForBuildAsync(progress, cancellationToken).ConfigureAwait(false);
                built = !NeedsBuild(target) || await BuildAsync(target, progress, cancellationToken).ConfigureAwait(false);
            }

            if (!built)
            {
                throw new PreviewerException("The build failed: the designer needs the project built. See the Error List.");
            }
        }

        progress(Localizer.Get("msg.winFormsDesigner.starting", "Starting the designer…"));
        return await HostFactory(target, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> BuildAsync(WinFormsDesignerTarget target, Action<string> progress, CancellationToken cancellationToken)
    {
        progress(Localizer.Format("msg.winFormsDesigner.building", "Building {0} for the designer…", Path.GetFileNameWithoutExtension(target.HostProject ?? target.Project)));
        return _build(target.HostProject ?? target.Project, cancellationToken);
    }

    private async Task WaitForBuildAsync(Action<string> progress, CancellationToken cancellationToken)
    {
        var told = false;
        while (IsBuilding())
        {
            if (!told)
            {
                progress(Localizer.Get("msg.winFormsDesigner.waitingForBuild", "Waiting for the build to finish…"));
                told = true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The target's assemblies as they are on disk (#66): their lengths and times, or that they're missing. A designer
    /// whose host didn't start compares it when it becomes active, to start again once a build has replaced them (or
    /// written them for the first time).
    /// </summary>
    public static string OutputStamp(WinFormsDesignerTarget? target)
    {
        if (target is null)
        {
            return string.Empty;
        }

        var stamp = new System.Text.StringBuilder();
        foreach (var file in new[] { target.ProjectAssembly, target.HostAssembly }.OfType<string>().Distinct(SolutionService.PathComparer))
        {
            stamp.Append(file).Append('|');
            try
            {
                var info = new FileInfo(file);
                stamp.Append(info.Exists ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{info.Length}|{info.LastWriteTimeUtc.Ticks}") : "missing");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stamp.Append('?');
            }

            stamp.Append(';');
        }

        return stamp.ToString();
    }

    /// <summary>The load request for a model read by the C# host.</summary>
    public static DesignerLoadParams LoadParams(WinFormsDesignerTarget target, Codalus.LanguageServices.Protocol.DesignerModel model, string? classFullName) =>
        new(model, target.ProjectAssembly, classFullName, [.. new[] { Path.GetDirectoryName(target.ProjectAssembly), target.HostAssembly is null ? null : Path.GetDirectoryName(target.HostAssembly) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)]);
}

/// <summary>How long the shell waits for the designer host (<see cref="WinFormsDesignerProcess"/>; tests shorten them).</summary>
public sealed record WinFormsDesignerTimeouts
{
    public static WinFormsDesignerTimeouts Default { get; } = new();

    /// <summary>From the process's start to <c>designer/ready</c>: a cold start loads the runtime, Windows Forms and the app's assemblies for the first time.</summary>
    public TimeSpan Start { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary><c>designer/load</c>: the first one loads the designers' assemblies and the user's code, and draws with fonts and GDI+ not yet warm.</summary>
    public TimeSpan Load { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Every other request.</summary>
    public TimeSpan Request { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The <c>designer/status</c> asked when a request runs out of time.</summary>
    public TimeSpan Status { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The designer host process (ADR 0073): started on the app's runtime (<c>dotnet exec --runtimeconfig … --depsfile …
/// codalus-winformsdesigner.dll</c>; a library on its own runs on the host's own runtimeconfig), supervised (a crash
/// restarts it and the document loads the form again), speaking JSON-RPC on stdio. It runs on a copy of the build
/// output (#66, <see cref="BuildOutputShadowCopy"/>), so a build can replace the original while a form is open: the
/// launch names the copy's runtimeconfig, deps file and folder, <c>designer/load</c> the copy's assemblies, and the copy
/// goes when the host ends. A start waits until the host's window pumps messages (<c>designer/ready</c>), so a
/// request's limit counts only its own work; a request that runs out of time fails with what the host says it's doing
/// (<c>designer/status</c>), whether it's still running, and its last lines of standard error.
/// </summary>
public sealed class WinFormsDesignerProcess : IWinFormsDesignerHost
{
    private const int ErrorLines = 20;
    private const int EarlyLogLines = 50;
    private static readonly WinFormsDesignerJsonContext s_json = WinFormsDesignerJsonContext.Default;
    private static readonly System.Text.Json.JsonElement s_noParams = EmptyObject();
    private readonly SupervisedProcess _process;
    private readonly WinFormsDesignerTimeouts _timeouts;
    private readonly Queue<string> _errors = new();
    private readonly System.Diagnostics.Stopwatch _started = System.Diagnostics.Stopwatch.StartNew();
    private readonly BuildOutputShadowCopy? _copy;
    private readonly Lock _logGate = new();
    private readonly Queue<string> _earlyLog = new();
    private EventHandler<string>? _log;
    private bool _logSubscribed;
    private string? _exited;

    private WinFormsDesignerProcess(SupervisedProcess process, WinFormsDesignerTimeouts timeouts, ProcessLaunchInfo launch, BuildOutputShadowCopy? copy)
    {
        _process = process;
        _timeouts = timeouts;
        _copy = copy;
        Launch = launch;
        if (copy is not null)
        {
            var skipped = copy.Skipped.Count == 0 ? string.Empty : $", skipped: {string.Join(", ", copy.Skipped.Take(3).Select(Path.GetFileName))}{(copy.Skipped.Count > 3 ? $" and {copy.Skipped.Count - 3} more" : string.Empty)}";
            WriteLog($"Copied the build output ({copy.Files} files, {copy.Bytes / 1048576.0:0.0} MB{skipped}) to {copy.Folder} in {copy.Took.TotalSeconds:0.0} s: builds can replace the original while the designer runs.");
        }

        _process.Log += (_, line) =>
        {
            lock (_errors)
            {
                _errors.Enqueue(line);
                while (_errors.Count > ErrorLines)
                {
                    _errors.Dequeue();
                }
            }

            WriteLog(line);
        };
        _process.Restarted += (_, _) => Restarted?.Invoke(this, EventArgs.Empty);
        _process.Crashed += (_, e) =>
        {
            var exit = $"The designer host exited ({e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no exit code"}) after {_started.Elapsed.TotalSeconds:0} s";
            _exited = exit;
            WriteLog(exit + (e.WillRestart ? ": restarting it." : "."));
        };
    }

    /// <summary>
    /// A line of the host's output. The lines written before the first handler (the copy, the host's start and its
    /// ready line: the document subscribes once the start returns) reach that handler when it's added.
    /// </summary>
    public event EventHandler<string>? Log
    {
        add
        {
            lock (_logGate)
            {
                _log += value;
                if (!_logSubscribed && value is not null)
                {
                    _logSubscribed = true;
                    while (_earlyLog.TryDequeue(out var line))
                    {
                        value(this, line);
                    }
                }
            }
        }

        remove
        {
            lock (_logGate)
            {
                _log -= value;
            }
        }
    }

    public event EventHandler? Restarted;

    /// <summary>How the host was started (tests).</summary>
    internal ProcessLaunchInfo Launch { get; }

    /// <summary>The copy of the build output the host runs on (tests), or null.</summary>
    internal string? CopyFolder => _copy?.Folder;

    private void WriteLog(string line)
    {
        EventHandler<string>? log;
        lock (_logGate)
        {
            if (!_logSubscribed)
            {
                _earlyLog.Enqueue(line);
                while (_earlyLog.Count > EarlyLogLines)
                {
                    _earlyLog.Dequeue();
                }

                return;
            }

            log = _log;
        }

        log?.Invoke(this, line);
    }

    /// <summary>The command line for a target: the app's runtime and dependencies, or the host's own.</summary>
    public static ProcessLaunchInfo LaunchFor(WinFormsDesignerTarget target)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        if (target.HostAssembly is { } app && File.Exists(Path.ChangeExtension(app, ".runtimeconfig.json")))
        {
            return new ProcessLaunchInfo(dotnet, ["exec", "--runtimeconfig", Path.ChangeExtension(app, ".runtimeconfig.json"), "--depsfile", Path.ChangeExtension(app, ".deps.json"), target.DesignerHost])
            {
                WorkingDirectory = Path.GetDirectoryName(app),
                Environment = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
            };
        }

        return new ProcessLaunchInfo(dotnet, [target.DesignerHost])
        {
            WorkingDirectory = Path.GetDirectoryName(target.ProjectAssembly),
            Environment = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_ROLL_FORWARD"] = "Major" },
        };
    }

    /// <summary>
    /// Starts the host for a target on a copy of its build output (#66) and waits until it's ready
    /// (<c>designer/ready</c>).
    /// </summary>
    public static Task<IWinFormsDesignerHost> StartAsync(WinFormsDesignerTarget target, CancellationToken cancellationToken) =>
        StartAsync(target, WinFormsDesignerTimeouts.Default, cancellationToken);

    /// <summary>
    /// Copies the app's output (and a designed library's, when it's elsewhere) to <paramref name="copyRoot"/> (by
    /// default <see cref="BuildOutputShadowCopy.DefaultRoot"/>) and starts the host on the copy; a start that fails
    /// deletes the copy. <paramref name="launcher"/> starts the process (tests).
    /// </summary>
    internal static async Task<IWinFormsDesignerHost> StartAsync(WinFormsDesignerTarget target, WinFormsDesignerTimeouts timeouts, CancellationToken cancellationToken, IProcessLauncher? launcher = null, string? copyRoot = null)
    {
        string[] assemblies = [.. new[] { target.HostAssembly, target.ProjectAssembly }.OfType<string>().Distinct(SolutionService.PathComparer)];
        string[] folders = [.. assemblies.Select(Path.GetDirectoryName).OfType<string>().Distinct(SolutionService.PathComparer)];
        var copy = await Task.Run(() => BuildOutputShadowCopy.Create(folders, assemblies, copyRoot, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        try
        {
            var copied = target with { ProjectAssembly = copy.Map(target.ProjectAssembly), HostAssembly = target.HostAssembly is { } app ? copy.Map(app) : null };
            var launch = LaunchFor(copied);
            if (launcher is not null)
            {
                launch = launch with { Launcher = launcher };
            }

            return await StartAsync(launch, timeouts, cancellationToken, copy).ConfigureAwait(false);
        }
        catch
        {
            await copy.DeleteAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<IWinFormsDesignerHost> StartAsync(ProcessLaunchInfo launch, WinFormsDesignerTimeouts timeouts, CancellationToken cancellationToken, BuildOutputShadowCopy? copy = null)
    {
        var process = new SupervisedProcess("Windows Forms designer", launch, new SupervisionPolicy { MaxRestarts = 2 });
        var host = new WinFormsDesignerProcess(process, timeouts, launch, copy);
        process.Initialize = async (connection, isRestart, ct) =>
        {
            try
            {
                await host.WaitReadyAsync(connection, ct).ConfigureAwait(false);
            }
            catch (PreviewerException ex) when (isRestart)
            {
                // A restart's failure is the supervisor's to log (it catches this type); the next request reports the host's state.
                throw new InvalidOperationException(ex.Message, ex);
            }
        };
        try
        {
            await process.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PreviewerException)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw ex as PreviewerException ?? new PreviewerException($"The designer didn't start: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return host;
    }

    /// <summary>Waits for <c>designer/ready</c>: the host's UI thread runs its window's message loop.</summary>
    private async Task WaitReadyAsync(JsonRpcConnection connection, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeouts.Start);
        var asked = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var ready = await connection.InvokeAsync(WinFormsDesignerMethods.Ready, s_noParams, s_json.JsonElement, s_json.DesignerStatus, timeout.Token).ConfigureAwait(false);
            WriteLog($"The designer host is ready after {_started.Elapsed.TotalSeconds:0.0} s: {ready?.State}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var here = DescribeHere(connection, asked, _timeouts.Start);
            throw new PreviewerException($"The designer didn't start in {_timeouts.Start.TotalSeconds:0} seconds: {await DiagnoseAsync(connection, here).ConfigureAwait(false)}");
        }
        catch (JsonRpcConnectionLostException ex)
        {
            throw new PreviewerException($"The designer exited while starting: {await DiagnoseAsync(null, null).ConfigureAwait(false)}", ex);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new PreviewerException($"The designer didn't start: {ex.Message}", ex);
        }
    }

    private async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        var allowed = limit ?? _timeouts.Request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(allowed);
        JsonRpcConnection? connection = null;
        var asked = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            connection = _process.Connection;
            return await connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var here = DescribeHere(connection, asked, allowed);
            throw new PreviewerException($"The designer didn't answer {method} in {allowed.TotalSeconds:0} seconds: {await DiagnoseAsync(connection, here).ConfigureAwait(false)}");
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new PreviewerException(ex.Message, ex);
        }
    }

    /// <summary>This side of a request that ran out of time (<see cref="DescribeHere(TimeSpan, TimeSpan, int, long, string?)"/>), taken when its limit fired.</summary>
    private static string DescribeHere(JsonRpcConnection? connection, long asked, TimeSpan allowed) =>
        DescribeHere(System.Diagnostics.Stopwatch.GetElapsedTime(asked), allowed, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, connection?.Describe());

    /// <summary>
    /// This side of a request that ran out of time, in words: how long the wait really took, this process's thread pool
    /// (its threads and the work queued for them) and the connection's state (a request still being written: the host
    /// doesn't read its input). A limit that fires late means the pool was too busy to run its timer; on Windows every
    /// read and write of the host's pipes is a work item on that pool, so the host's answer may have waited unread too.
    /// </summary>
    internal static string DescribeHere(TimeSpan waited, TimeSpan allowed, int threads, long queued, string? connection)
    {
        var late = waited - allowed;
        var text = late > TimeSpan.FromSeconds(2)
            ? $"This process waited {waited.TotalSeconds:0} s, its limit's timer {late.TotalSeconds:0} s late: its thread pool was too busy, so the host's answer may have waited unread"
            : $"This process waited {waited.TotalSeconds:0} s";
        text += $" ({threads} pool threads, {queued} work items queued)";
        return (connection is { Length: > 0 } state ? $"{text}; its connection: {state}" : text) + ".";
    }

    /// <summary>
    /// Why the host doesn't answer, in words: whether it's running, what it says it's doing (<c>designer/status</c>,
    /// answered off its UI thread: the work there, its phase, what waits for it), this side's state
    /// (<paramref name="here"/>), and the host's last lines of standard error.
    /// </summary>
    private async Task<string> DiagnoseAsync(JsonRpcConnection? connection, string? here)
    {
        string state;
        if (_exited is { } exited && (connection is null || !_process.IsRunning))
        {
            state = exited + ".";
        }
        else if (connection is null)
        {
            state = "the designer host isn't running.";
        }
        else
        {
            using var timeout = new CancellationTokenSource(_timeouts.Status);
            try
            {
                var status = await connection.InvokeAsync(WinFormsDesignerMethods.Status, s_noParams, s_json.JsonElement, s_json.DesignerStatus, timeout.Token).ConfigureAwait(false);
                state = $"the host (process {_process.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}, started {_started.Elapsed.TotalSeconds:0} s ago) says: {status?.State}";
            }
            catch (OperationCanceledException)
            {
                state = $"the host (process {_process.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}, started {_started.Elapsed.TotalSeconds:0} s ago) didn't answer {WinFormsDesignerMethods.Status} in {_timeouts.Status.TotalSeconds:0} seconds either: it isn't reading its input, its output is blocked, or this process is too busy to read the answer.";
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException or IOException or ObjectDisposedException)
            {
                state = (_exited ?? "the designer host's connection failed") + $": {ex.Message}";
            }
        }

        if (here is not null)
        {
            state += " " + here;
        }

        string[] errors;
        lock (_errors)
        {
            errors = [.. _errors];
        }

        return errors.Length == 0
            ? state + " It wrote nothing to standard error."
            : state + " Its last standard error:" + Environment.NewLine + string.Join(Environment.NewLine, errors);
    }

    /// <summary>Loads a form; the project's assembly and the search folders are the copy's (the document knows only the real paths).</summary>
    public Task<DesignerRenderResult> LoadAsync(DesignerLoadParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Load, _copy is { } copy ? parameters with { ProjectAssembly = copy.Map(parameters.ProjectAssembly), SearchFolders = [.. parameters.SearchFolders.Select(copy.Map)] } : parameters,
            s_json.DesignerLoadParams, s_json.DesignerRenderResult, cancellationToken, _timeouts.Load);

    public Task<DesignerRenderResult> RenderAsync(CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Render, new DesignerComponentsParams([]), s_json.DesignerComponentsParams, s_json.DesignerRenderResult, cancellationToken);

    public Task<DesignerChangeResult> CreateAsync(DesignerCreateParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Create, parameters, s_json.DesignerCreateParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<DesignerChangeResult> SetPropertiesAsync(DesignerSetPropertiesParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.SetProperties, parameters, s_json.DesignerSetPropertiesParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<DesignerChangeResult> SetParentAsync(DesignerSetParentParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.SetParent, parameters, s_json.DesignerSetParentParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<DesignerChangeResult> DeleteAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Delete, parameters, s_json.DesignerComponentsParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<DesignerPropertiesResult> PropertiesAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Properties, parameters, s_json.DesignerComponentsParams, s_json.DesignerPropertiesResult, cancellationToken);

    public Task<DesignerEventsResult> EventsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Events, parameters, s_json.DesignerComponentsParams, s_json.DesignerEventsResult, cancellationToken);

    public async Task SelectAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken) =>
        await InvokeAsync(WinFormsDesignerMethods.Select, parameters, s_json.DesignerComponentsParams, s_json.JsonElement, cancellationToken).ConfigureAwait(false);

    public Task<DesignerChangeResult> SetChildIndexAsync(DesignerChildIndexParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.SetChildIndex, parameters, s_json.DesignerChildIndexParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<DesignerCollectionResult> CollectionAsync(DesignerCollectionParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Collection, parameters, s_json.DesignerCollectionParams, s_json.DesignerCollectionResult, cancellationToken);

    public Task<DesignerChangeResult> SetCollectionAsync(DesignerSetCollectionParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.SetCollection, parameters, s_json.DesignerSetCollectionParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<DesignerActionsResult> ActionsAsync(DesignerComponentsParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Actions, parameters, s_json.DesignerComponentsParams, s_json.DesignerActionsResult, cancellationToken);

    public Task<DesignerChangeResult> InvokeActionAsync(DesignerInvokeActionParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.InvokeAction, parameters, s_json.DesignerInvokeActionParams, s_json.DesignerChangeResult, cancellationToken);

    public Task<bool> IsOutdatedAsync(CancellationToken cancellationToken) =>
        _copy is { } copy ? Task.Run(copy.IsOutdated, cancellationToken) : Task.FromResult(false);

    /// <summary>Stops the host, then deletes its copy of the build output (never throws: what stays goes at the next sweep).</summary>
    public async ValueTask DisposeAsync()
    {
        await _process.DisposeAsync().ConfigureAwait(false);
        if (_copy is { } copy)
        {
            await copy.DeleteAsync().ConfigureAwait(false);
        }
    }

    private static System.Text.Json.JsonElement EmptyObject()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
