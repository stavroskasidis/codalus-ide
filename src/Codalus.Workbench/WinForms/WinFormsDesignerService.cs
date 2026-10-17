using Codalus.Projects.Protocol;
using Codalus.Rpc;
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
                if (item.Kind is "Folder" || SolutionService.PathComparer.Equals(item.Path, target.CodeFile) || SolutionService.PathComparer.Equals(item.Path, designer))
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

    /// <summary>Builds when the output is stale, then starts a designer host for the target.</summary>
    public async Task<IWinFormsDesignerHost> StartAsync(WinFormsDesignerTarget target, Action<string> progress, CancellationToken cancellationToken)
    {
        if (NeedsBuild(target))
        {
            progress($"Building {Path.GetFileNameWithoutExtension(target.HostProject ?? target.Project)} for the designer…");
            if (!await _build(target.HostProject ?? target.Project, cancellationToken).ConfigureAwait(false))
            {
                throw new PreviewerException("The build failed: the designer needs the project built. See the Error List.");
            }
        }

        progress("Starting the designer…");
        return await HostFactory(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The load request for a model read by the C# host.</summary>
    public static DesignerLoadParams LoadParams(WinFormsDesignerTarget target, Codalus.LanguageServices.Protocol.DesignerModel model, string? classFullName) =>
        new(model, target.ProjectAssembly, classFullName, [.. new[] { Path.GetDirectoryName(target.ProjectAssembly), target.HostAssembly is null ? null : Path.GetDirectoryName(target.HostAssembly) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)]);
}

/// <summary>
/// The designer host process (ADR 0073): started on the app's runtime (<c>dotnet exec --runtimeconfig … --depsfile …
/// codalus-winformsdesigner.dll</c>; a library on its own runs on the host's own runtimeconfig), supervised (a crash
/// restarts it and the document loads the form again), speaking JSON-RPC on stdio.
/// </summary>
public sealed class WinFormsDesignerProcess : IWinFormsDesignerHost
{
    private static readonly WinFormsDesignerJsonContext s_json = WinFormsDesignerJsonContext.Default;
    private readonly SupervisedProcess _process;

    private WinFormsDesignerProcess(SupervisedProcess process)
    {
        _process = process;
        _process.Log += (_, line) => Log?.Invoke(this, line);
        _process.Restarted += (_, _) => Restarted?.Invoke(this, EventArgs.Empty);
        _process.Crashed += (_, e) => Log?.Invoke(this, $"The designer host exited ({e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no exit code"}){(e.WillRestart ? ": restarting it." : ".")}");
    }

    public event EventHandler<string>? Log;

    public event EventHandler? Restarted;

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

    public static async Task<IWinFormsDesignerHost> StartAsync(WinFormsDesignerTarget target, CancellationToken cancellationToken)
    {
        var process = new SupervisedProcess("Windows Forms designer", LaunchFor(target), new SupervisionPolicy { MaxRestarts = 2 });
        var host = new WinFormsDesignerProcess(process);
        try
        {
            await process.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw new PreviewerException($"The designer didn't start: {ex.Message}", ex);
        }

        return host;
    }

    private static TimeSpan Timeout => TimeSpan.FromSeconds(60);

    private async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await _process.Connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreviewerException($"The designer didn't answer {method} in {Timeout.TotalSeconds:0} seconds.");
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new PreviewerException(ex.Message, ex);
        }
    }

    public Task<DesignerRenderResult> LoadAsync(DesignerLoadParams parameters, CancellationToken cancellationToken) =>
        InvokeAsync(WinFormsDesignerMethods.Load, parameters, s_json.DesignerLoadParams, s_json.DesignerRenderResult, cancellationToken);

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

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
