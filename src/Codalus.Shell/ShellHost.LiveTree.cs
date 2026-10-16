using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// The Live Visual Tree and Live Property Explorer (M19b, ADR 0052; WPF in M40b, ADR 0072): an Avalonia or WPF app run
/// or debugged from here gets its live agent (a startup hook: Avalonia's, or the WPF previewer's on Windows) and a
/// <see cref="LiveSession"/> it connects to; the two tool windows show its elements and their properties, highlight the
/// selection in the app, pick an element by clicking it, set values and go to the XAML.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Whether running and debugging an Avalonia or WPF app adds the live agent (on by default).</summary>
    public const string LiveVisualTreeSetting = "debug.liveVisualTree";

    public LiveTreeViewModel LiveTree { get; private set; } = null!;

    public LivePropertiesViewModel LiveProperties { get; private set; } = null!;

    /// <summary>The live session of the last Avalonia app run (null before one, or with the setting off).</summary>
    public LiveSession? Live { get; private set; }

    private void InitializeLiveTree()
    {
        LiveTree = new LiveTreeViewModel(() => Live, GoToXamlAsync);
        LiveProperties = new LivePropertiesViewModel(() => Live);
        LiveTree.SelectionChanged += (_, node) => _ = LiveProperties.ShowAsync(node);
        Runs.ProgramPlan = AddLiveAgent;
    }

    /// <summary>Whether a project is an Avalonia app: it references an Avalonia package itself (an app references Avalonia.Desktop).</summary>
    private bool IsAvaloniaApp(string projectPath) =>
        Solutions.Projects.TryGetValue(projectPath, out var project)
        && project.References.Any(r => r.Kind == ReferenceKinds.Package && r.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The live agent for a project's app: Avalonia's for an Avalonia app; for a WPF app on Windows (D31) the WPF
    /// previewer's, when its .NET is one the agent loads into; else none.
    /// </summary>
    internal string? LiveAgentFor(string projectPath)
    {
        if (IsAvaloniaApp(projectPath))
        {
            return PreviewService.ResolveAgent();
        }

        return OperatingSystem.IsWindows() && Solutions.Projects.TryGetValue(projectPath, out var project) && project.UsesWpf
            && PreviewService.WpfFrameworkProblem(project) is null
            ? PreviewService.ResolveWpfPreviewer()
            : null;
    }

    /// <summary>An Avalonia or WPF app's plan gets its live agent and a new session for this run (any thread).</summary>
    private LaunchPlan AddLiveAgent(RunInfo info, LaunchPlan plan)
    {
        if (!Workbench.Settings.GetBoolean(LiveVisualTreeSetting, true) || LiveAgentFor(info.Path) is not { } agent)
        {
            return plan;
        }

        var previous = Live;
        if (LiveSession.Start(agent) is not { } session)
        {
            return plan;
        }

        Live = session;
        session.Connected += (_, _) => Ui.Post(() => _ = LiveTree.RefreshAsync());
        session.Disconnected += (_, _) => Ui.Post(() =>
        {
            if (ReferenceEquals(Live, session))
            {
                _ = LiveTree.RefreshAsync();
            }
        });
        session.Picked += (_, e) => Ui.Post(() => _ = LiveTree.SelectAsync(e.Element));
        if (previous is not null)
        {
            _ = previous.DisposeAsync().AsTask();
        }

        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        foreach (var (name, value) in session.Environment(plan.Environment))
        {
            environment[name] = value;
        }

        return plan with { Environment = environment };
    }

    /// <summary>
    /// Go to XAML: the element's logical path from its window, the window's XAML (the file whose <c>x:Class</c> is the
    /// window's class), and the element there through the C# host's XAML logical tree (as the previewer's selection).
    /// </summary>
    public async Task GoToXamlAsync(LiveNodeViewModel node)
    {
        if (Live is not { IsConnected: true } session)
        {
            return;
        }

        try
        {
            var path = await session.PathAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
            if (await Task.Run(() => XamlFileOf(path.WindowType)).ConfigureAwait(true) is not { } file)
            {
                StatusBar.ShowMessage(Localizer.Format("msg.liveTree.noXamlFileDeclaresXClass", "No XAML file declares {0} (x:Class).", path.WindowType), TimeSpan.FromSeconds(6));
                return;
            }

            if (await OpenFileAsync(file, preview: false).ConfigureAwait(true) is not { } editor)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = await Languages.ClientAsync(editor.Model.LanguageId ?? XamlLanguageId).ConfigureAwait(true);
            var tree = client is null ? null : await client.XamlLogicalTreeAsync(editor.Model.Uri, timeout.Token).ConfigureAwait(true);
            if (tree is null || PreviewSelection.NodeFor(tree.Nodes, path.Steps) is not { } xaml || editor.Controller is not { } controller)
            {
                StatusBar.ShowMessage(Localizer.Get("msg.liveTree.theElementIsntInTheWindows", "The element isn't in the window's XAML (a template or code made it)."), TimeSpan.FromSeconds(6));
                return;
            }

            var start = editor.Model.OffsetAt(xaml.Tag.Start);
            var end = editor.Model.OffsetAt(xaml.Tag.End);
            controller.Editor.Select(start, Math.Max(0, end - start));
            controller.Editor.ScrollTo(xaml.Tag.Start.Line + 1, xaml.Tag.Start.Character + 1);
        }
        catch (Exception ex) when (ex is PreviewerException or OperationCanceledException)
        {
            StatusBar.ShowMessage("Go to XAML: " + ex.Message, TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>The .axaml or .xaml file of the solution whose root's <c>x:Class</c> is <paramref name="type"/>.</summary>
    private string? XamlFileOf(string type)
    {
        var needle = "x:Class=\"" + type + "\"";
        foreach (var path in Solutions.Projects.Values.SelectMany(p => p.Items).Select(i => i.Path)
                     .Where(p => p.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                     .Distinct(SolutionService.PathComparer))
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Contains(needle, StringComparison.Ordinal))
                {
                    return path;
                }
            }
            catch (IOException)
            {
            }
        }

        return null;
    }
}
