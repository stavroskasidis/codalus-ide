using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Previewer;

/// <summary>Which previewer a XAML file gets: Avalonia's designer host (ADR 0048) or the WPF previewer (ADR 0072).</summary>
public enum PreviewDialect
{
    Avalonia,
    Wpf,
}

/// <summary>
/// What previewing a XAML file needs (ADR 0048): the project the file belongs to (its output is the assembly the XAML
/// is compiled into), the app that hosts the preview (that project when it is an executable, else an app that
/// references it, the startup project first), and the designer host: Avalonia's from the app's packages, or for WPF
/// (<see cref="Dialect"/>) the WPF previewer shipped with the IDE (ADR 0072).
/// </summary>
public sealed record PreviewTarget(
    string XamlPath,
    string Project,
    string HostProject,
    string Assembly,
    string HostAssembly,
    string DesignerHost,
    string ProjectRelativePath)
{
    public PreviewDialect Dialect { get; init; } = PreviewDialect.Avalonia;

    /// <summary>The projects whose sources go into the preview: the file's project and the host.</summary>
    public IEnumerable<string> Projects => Project == HostProject ? [Project] : [Project, HostProject];
}

/// <summary>Where the previewer is: what the tool window shows while it gets there.</summary>
public enum PreviewStage
{
    Idle,
    Building,
    Starting,
    Rendering,
    Ready,
    Failed,
}

/// <summary>
/// The XAML previewer (M19a, ADR 0048): resolves a file's <see cref="PreviewTarget"/>, builds what the preview needs
/// (incrementally, and only when the sources are newer than the output), runs one <see cref="PreviewerSession"/> per
/// app and sends it the editor's text. The designer host runs the user's code, so it starts only for trusted folders.
/// A build first stops the session that holds the app's assemblies (Windows locks them). A WPF project's XAML goes to
/// the WPF previewer (M40b, ADR 0072), on Windows only (D31): the same session, protocol and selection.
/// </summary>
public sealed class PreviewService : IAsyncDisposable
{
    public const string AgentFileName = "Codalus.Previewer.Agent.dll";
    public const string AgentVariable = "CODALUS_PREVIEWER_AGENT";

    /// <summary>The WPF previewer's designer host and live agent (ADR 0072), in <c>wpfpreviewer/</c> next to the shell on Windows.</summary>
    public const string WpfPreviewerFileName = "codalus-wpfpreviewer.dll";
    public const string WpfPreviewerVariable = "CODALUS_WPF_PREVIEWER";

    /// <summary>The oldest .NET whose WPF the WPF previewer and live agent load into (they're built for it).</summary>
    public const int WpfMinimumDotNet = 8;

    /// <summary>Off Windows, why WPF XAML doesn't preview (D31).</summary>
    public const string WpfWindowsOnlyMessage =
        "The WPF previewer runs on Windows only: WPF draws only there. On this OS, WPF XAML edits with completion, diagnostics and navigation.";

    private static readonly string[] s_properties = ["TargetPath", "AvaloniaPreviewerNetCoreToolPath", "OutputType"];
    private static readonly string[] s_wpfProperties = ["TargetPath", "OutputType"];

    private readonly SolutionService _solutions;
    private readonly Func<string, bool> _isTrusted;
    private readonly Func<string, CancellationToken, Task<bool>> _build;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> _properties;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PreviewerSession? _session;
    private string? _sessionHost;
    private PreviewDialect _sessionDialect;
    private double _scale = 1;
    private string _theme = "Default";

    public PreviewService(
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

    /// <summary>The startup project, preferred as the host of a library's XAML.</summary>
    public Func<string?> StartupProject { get; init; } = () => null;

    /// <summary>The agent to load, or null (tests and settings may turn it off).</summary>
    public Func<string?> Agent { get; init; } = ResolveAgent;

    /// <summary>The WPF previewer, or null when this install has none (off Windows).</summary>
    public Func<string?> WpfPreviewer { get; init; } = ResolveWpfPreviewer;

    /// <summary>Whether this is Windows, where WPF renders (D31); tests may say otherwise.</summary>
    public Func<bool> IsWindows { get; init; } = OperatingSystem.IsWindows;

    public PreviewStage Stage { get; private set; }

    /// <summary>The running session, if any.</summary>
    public PreviewerSession? Session => _session;

    public event EventHandler<PreviewStage>? StageChanged;

    /// <summary>A frame of the running session (any thread).</summary>
    public event EventHandler<PreviewFrame>? FrameReceived;

    /// <summary>Lines for the previewer's output channel (any thread).</summary>
    public event EventHandler<string>? Log;

    /// <summary>The session ended unexpectedly (any thread).</summary>
    public event EventHandler? SessionEnded;

    /// <summary>The agent: <c>CODALUS_PREVIEWER_AGENT</c>, then <c>debughost/</c> next to the shell, then next to the running assembly (tests).</summary>
    public static string? ResolveAgent()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(AgentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "debughost", AgentFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, AgentFileName));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The WPF previewer: <c>CODALUS_WPF_PREVIEWER</c>, then <c>wpfpreviewer/</c> next to the shell (the Windows package has it).</summary>
    public static string? ResolveWpfPreviewer()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(WpfPreviewerVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "wpfpreviewer", WpfPreviewerFileName));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The previewer a XAML file gets: an <c>.axaml</c> file, or a file of a project that references Avalonia, is
    /// Avalonia's; another <c>.xaml</c> file of a WPF project (<c>UseWPF</c>) is WPF's, as for the C# host's dialect
    /// (<c>XamlContext.IsWpf</c>, ADR 0071).
    /// </summary>
    public static PreviewDialect DialectOf(string xamlPath, ProjectInfo project) =>
        !xamlPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) && project.UsesWpf
        && !project.References.Any(r => r.Kind == ReferenceKinds.Package && r.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            ? PreviewDialect.Wpf
            : PreviewDialect.Avalonia;

    /// <summary>
    /// Why <paramref name="host"/> can't host a WPF preview or the live agent (its .NET is older than the oldest the
    /// WPF previewer is built for, or it's .NET Framework), or null when it can.
    /// </summary>
    public static string? WpfFrameworkProblem(ProjectInfo host)
    {
        if (host.TargetFrameworks.Length == 0)
        {
            return null;
        }

        var versions = host.TargetFrameworks.Select(DotNetMajor).ToList();
        if (versions.Any(v => v >= WpfMinimumDotNet))
        {
            return null;
        }

        return versions.All(v => v is null)
            ? $"{host.Name} targets .NET Framework ({string.Join(", ", host.TargetFrameworks)}): the WPF previewer and live tools need .NET {WpfMinimumDotNet} or later."
            : $"{host.Name} targets {string.Join(", ", host.TargetFrameworks)}: the WPF previewer and live tools need .NET {WpfMinimumDotNet} or later.";
    }

    /// <summary>A target framework's .NET (Core) major version: 8 for <c>net8.0-windows</c>, 3 for <c>netcoreapp3.1</c>; null for .NET Framework (<c>net48</c>) or others.</summary>
    public static int? DotNetMajor(string targetFramework)
    {
        var tfm = targetFramework.Split('-')[0];
        string digits;
        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            digits = tfm["netcoreapp".Length..];
        }
        else if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) && tfm.Contains('.', StringComparison.Ordinal))
        {
            digits = tfm[3..];
        }
        else
        {
            return null;
        }

        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        return int.TryParse(dot < 0 ? digits : digits[..dot], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    /// <summary>How the target's designer host starts: Avalonia's with the agent as a startup hook, the WPF previewer serving the agent itself.</summary>
    public static PreviewerLaunch LaunchFor(PreviewTarget target, string? agent) =>
        target.Dialect == PreviewDialect.Wpf
            ? new PreviewerLaunch(target.DesignerHost, target.HostAssembly, null) { AgentInHost = true }
            : new PreviewerLaunch(target.DesignerHost, target.HostAssembly, agent);

    private void SetStage(PreviewStage stage)
    {
        Stage = stage;
        StageChanged?.Invoke(this, stage);
    }

    // ------------------------------------------------------------------ targets

    /// <summary>The file's project and its host app, or a <see cref="PreviewerException"/> that says why it can't be previewed.</summary>
    public async Task<PreviewTarget> ResolveAsync(string xamlPath, CancellationToken cancellationToken)
    {
        if (!_solutions.IsOpen)
        {
            throw new PreviewerException("Open the solution or folder of this file to preview it.");
        }

        var projects = _solutions.Projects.Values.ToList();
        var owner = projects.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, xamlPath)))
            ?? projects.Where(p => xamlPath.StartsWith(Path.GetDirectoryName(p.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Path.Length).FirstOrDefault()
            ?? throw new PreviewerException($"{Path.GetFileName(xamlPath)} isn't in a project of the solution.");
        if (DialectOf(xamlPath, owner) == PreviewDialect.Wpf)
        {
            return await ResolveWpfAsync(xamlPath, owner, projects, cancellationToken).ConfigureAwait(false);
        }

        if (owner.UsesMaui && !xamlPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewerException("The XAML Previewer shows Avalonia and WPF XAML: .NET MAUI's XAML has no previewer.");
        }

        if (!_isTrusted(owner.Path))
        {
            throw new PreviewerException("The previewer runs the project's code: trust this folder to preview its XAML.");
        }

        var host = IsExecutable(owner) ? owner : FindHost(owner, projects, wpf: false)
            ?? throw new PreviewerException($"{owner.Name} is a library: the previewer needs an app project that references it.");
        var ownerProperties = await _properties(owner.Path, s_properties, cancellationToken).ConfigureAwait(false);
        var hostProperties = host == owner ? ownerProperties : await _properties(host.Path, s_properties, cancellationToken).ConfigureAwait(false);
        if (hostProperties.GetValueOrDefault("AvaloniaPreviewerNetCoreToolPath") is not { Length: > 0 } designer)
        {
            throw new PreviewerException($"{host.Name} doesn't reference Avalonia (no AvaloniaPreviewerNetCoreToolPath): restore it, or preview through an Avalonia app.");
        }

        var assembly = ownerProperties.GetValueOrDefault("TargetPath") is { Length: > 0 } target ? target : throw new PreviewerException($"{owner.Name} has no TargetPath.");
        var hostAssembly = hostProperties.GetValueOrDefault("TargetPath") is { Length: > 0 } hostTarget ? hostTarget : throw new PreviewerException($"{host.Name} has no TargetPath.");
        var relative = "/" + Path.GetRelativePath(Path.GetDirectoryName(owner.Path)!, xamlPath).Replace('\\', '/');
        return new PreviewTarget(xamlPath, owner.Path, host.Path, Path.GetFullPath(assembly), Path.GetFullPath(hostAssembly), Path.GetFullPath(designer), relative);
    }

    /// <summary>
    /// A WPF file's target (ADR 0072): on Windows only (D31), in a trusted folder, hosted by a WPF app on .NET 8 or
    /// later, and drawn by the WPF previewer shipped with the IDE.
    /// </summary>
    private async Task<PreviewTarget> ResolveWpfAsync(string xamlPath, ProjectInfo owner, IReadOnlyList<ProjectInfo> projects, CancellationToken cancellationToken)
    {
        if (!IsWindows())
        {
            throw new PreviewerException(WpfWindowsOnlyMessage);
        }

        if (!_isTrusted(owner.Path))
        {
            throw new PreviewerException("The previewer runs the project's code: trust this folder to preview its XAML.");
        }

        var host = IsExecutable(owner) ? owner : FindHost(owner, projects, wpf: true)
            ?? throw new PreviewerException($"{owner.Name} is a library: the WPF previewer needs a WPF app project that references it.");
        if (WpfFrameworkProblem(host) is { } problem)
        {
            throw new PreviewerException(problem);
        }

        var designer = WpfPreviewer()
            ?? throw new PreviewerException($"The WPF previewer (wpfpreviewer/{WpfPreviewerFileName}) isn't installed with this build of Codalus IDE.");
        var ownerProperties = await _properties(owner.Path, s_wpfProperties, cancellationToken).ConfigureAwait(false);
        var hostProperties = host == owner ? ownerProperties : await _properties(host.Path, s_wpfProperties, cancellationToken).ConfigureAwait(false);
        var assembly = ownerProperties.GetValueOrDefault("TargetPath") is { Length: > 0 } target ? target : throw new PreviewerException($"{owner.Name} has no TargetPath.");
        var hostAssembly = hostProperties.GetValueOrDefault("TargetPath") is { Length: > 0 } hostTarget ? hostTarget : throw new PreviewerException($"{host.Name} has no TargetPath.");
        var relative = "/" + Path.GetRelativePath(Path.GetDirectoryName(owner.Path)!, xamlPath).Replace('\\', '/');
        return new PreviewTarget(xamlPath, owner.Path, host.Path, Path.GetFullPath(assembly), Path.GetFullPath(hostAssembly), Path.GetFullPath(designer), relative)
        {
            Dialect = PreviewDialect.Wpf,
        };
    }

    private static bool IsExecutable(ProjectInfo project) =>
        project.OutputType is { } type && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An app that references <paramref name="library"/> (directly or through other projects), a WPF one for a WPF
    /// library: the startup project if it does, else the first by name.
    /// </summary>
    private ProjectInfo? FindHost(ProjectInfo library, IReadOnlyList<ProjectInfo> projects, bool wpf)
    {
        bool References(ProjectInfo project, HashSet<string> seen)
        {
            foreach (var reference in project.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null))
            {
                if (SolutionService.PathComparer.Equals(reference.Path, library.Path))
                {
                    return true;
                }

                if (seen.Add(reference.Path!) && projects.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, reference.Path)) is { } next && References(next, seen))
                {
                    return true;
                }
            }

            return false;
        }

        var hosts = projects.Where(p => IsExecutable(p) && (!wpf || p.UsesWpf) && References(p, new HashSet<string>(SolutionService.PathComparer))).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var startup = StartupProject();
        return hosts.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, startup)) ?? hosts.FirstOrDefault();
    }

    /// <summary>
    /// Whether the outputs are older than the sources (the file being previewed aside: its text is sent as it is in the
    /// editor), or missing.
    /// </summary>
    public bool NeedsBuild(PreviewTarget target)
    {
        if (!File.Exists(target.Assembly) || !File.Exists(target.HostAssembly) || !File.Exists(target.DesignerHost))
        {
            return true;
        }

        var built = Min(File.GetLastWriteTimeUtc(target.Assembly), File.GetLastWriteTimeUtc(target.HostAssembly));
        foreach (var project in target.Projects)
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
                if (item.Kind is "Folder" || SolutionService.PathComparer.Equals(item.Path, target.XamlPath))
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

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    // ------------------------------------------------------------------ previewing

    /// <summary>
    /// Shows <paramref name="xaml"/> (the editor's text of <paramref name="target"/>'s file): builds first when the
    /// output is stale (stopping the session that holds it), starts the app's designer host when none runs, and sends
    /// the text. Returns the loader's answer and the frame that shows it.
    /// </summary>
    public async Task<PreviewUpdateResult> ShowAsync(PreviewTarget target, string xaml, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (NeedsBuild(target))
            {
                await StopSessionAsync().ConfigureAwait(false);
                SetStage(PreviewStage.Building);
                Log?.Invoke(this, $"Building {Path.GetFileNameWithoutExtension(target.HostProject)} for the preview…");
                if (!await _build(target.HostProject, cancellationToken).ConfigureAwait(false))
                {
                    SetStage(PreviewStage.Failed);
                    throw new PreviewerException("The build failed: the previewer needs the project built. See the Error List.");
                }
            }

            if (_session is null || _session.HasExited || !SolutionService.PathComparer.Equals(_sessionHost, target.HostAssembly) || _sessionDialect != target.Dialect)
            {
                await StopSessionAsync().ConfigureAwait(false);
                SetStage(PreviewStage.Starting);
                var launch = LaunchFor(target, target.Dialect == PreviewDialect.Wpf ? null : Agent());
                Log?.Invoke(this, $"Starting the previewer for {Path.GetFileName(target.HostAssembly)}…");
                var session = await PreviewerSession.StartAsync(launch, _scale, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                session.Log += (_, line) => Log?.Invoke(this, line);
                session.FrameReceived += (_, frame) => FrameReceived?.Invoke(this, frame);
                session.Exited += (_, _) =>
                {
                    if (ReferenceEquals(_session, session))
                    {
                        Log?.Invoke(this, "The previewer exited.");
                        SessionEnded?.Invoke(this, EventArgs.Empty);
                    }
                };
                _session = session;
                _sessionHost = target.HostAssembly;
                _sessionDialect = target.Dialect;
                if (_theme != "Default" && target.Dialect == PreviewDialect.Avalonia)
                {
                    await TrySetThemeAsync(session, _theme, cancellationToken).ConfigureAwait(false);
                }
            }

            SetStage(PreviewStage.Rendering);
            var result = await _session.UpdateXamlAsync(xaml, target.Assembly, target.ProjectRelativePath, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            SetStage(result.Succeeded ? PreviewStage.Ready : PreviewStage.Failed);
            return result;
        }
        catch (PreviewerException)
        {
            SetStage(PreviewStage.Failed);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Renders at <paramref name="scale"/> from now on (the next update shows it).</summary>
    public async Task SetScaleAsync(double scale, CancellationToken cancellationToken)
    {
        _scale = scale;
        if (_session is { HasExited: false } session)
        {
            await session.SetScaleAsync(scale, cancellationToken).ConfigureAwait(false);
        }
    }

    public double RenderScale => _scale;

    public string Theme => _theme;

    /// <summary>The application's theme variant in the preview (<c>Default</c>, <c>Light</c>, <c>Dark</c>); WPF has none.</summary>
    public async Task SetThemeAsync(string variant, CancellationToken cancellationToken)
    {
        _theme = variant;
        if (_session is { HasExited: false } session && _sessionDialect == PreviewDialect.Avalonia)
        {
            await TrySetThemeAsync(session, variant, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySetThemeAsync(PreviewerSession session, string variant, CancellationToken cancellationToken)
    {
        try
        {
            if (!await session.SetThemeAsync(variant, cancellationToken).ConfigureAwait(false))
            {
                Log?.Invoke(this, "The previewer agent isn't running: the theme variant can't change.");
            }
        }
        catch (PreviewerException ex)
        {
            Log?.Invoke(this, ex.Message);
        }
    }

    /// <summary>The code changed (a save in one of the preview's projects): stop the session so the next show rebuilds and restarts.</summary>
    public async Task InvalidateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopSessionAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            _sessionHost = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
