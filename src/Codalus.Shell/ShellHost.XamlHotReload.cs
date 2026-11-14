using System.Globalization;
using System.Text.RegularExpressions;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;

namespace Codalus.Shell;

/// <summary>
/// XAML Hot Reload and failed bindings (M64, ADR 0103, D56), through the live agent an Avalonia app (or a WPF app on
/// Windows) gets when it is run or debugged from here (<see cref="AddLiveAgent"/>):
/// <list type="bullet">
/// <item>A saved <c>.axaml</c> or <c>.xaml</c> file is loaded again into each live instance of its <c>x:Class</c> in
/// the running app (<c>debug.xamlHotReload</c>, on by default). What can't be applied (a changed <c>x:Class</c>, a
/// handler or type the running app doesn't have, a parse error) is an Error List row at its line, with an offer to
/// restart, as a rude edit is.</item>
/// <item>A binding that fails in the app is an Error List warning at the XAML attribute that declares it.</item>
/// </list>
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Whether a saved XAML file reaches the running app (M64; on by default).</summary>
    public const string XamlHotReloadSetting = "debug.xamlHotReload";

    /// <summary>The failed-binding rows' code.</summary>
    public const string BindingFailureCode = "XAML0100";

    /// <summary>The code of an edit XAML Hot Reload couldn't apply.</summary>
    public const string XamlHotReloadCode = "XAML0101";

    private readonly Dictionary<string, string?> _xamlClassesAtStart = new(SolutionService.PathComparer);
    private readonly List<ErrorListRow> _bindingRows = [];
    private readonly Dictionary<string, ErrorListRow> _xamlReloadRows = new(SolutionService.PathComparer);
    private readonly HashSet<string> _bindingKeys = new(StringComparer.Ordinal);
    private bool _bindingsQueued;

    /// <summary>The last XAML Hot Reload's outcome, for the status bar and tests: the file and what happened.</summary>
    public (string Path, LiveReloadResult Result)? LastXamlReload { get; private set; }

    /// <summary>A new run's session: its own rows and the XAML classes the app was built with (UI thread).</summary>
    private void HookLiveXaml(LiveSession session)
    {
        session.Connected += (_, _) => Ui.Post(() =>
        {
            if (!ReferenceEquals(Live, session))
            {
                return;
            }

            _bindingRows.Clear();
            _bindingKeys.Clear();
            _xamlReloadRows.Clear();
            ShowLiveRows();
            _ = SnapshotXamlClassesAsync();
        });
        session.BindingsFailed += (_, _) => Ui.Post(() => _ = TakeBindingFailuresAsync(session));
    }

    /// <summary>The solution's XAML files (their paths on disk).</summary>
    private List<string> XamlFiles() =>
    [
        .. Solutions.Projects.Values.SelectMany(p => p.Items).Select(i => i.Path)
            .Where(p => p.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Distinct(SolutionService.PathComparer),
    ];

    private async Task SnapshotXamlClassesAsync()
    {
        var files = XamlFiles();
        var classes = await Task.Run(() => files.Select(f => (Path: f, Class: TryRead(f) is { } text ? XClassOf(text) : null)).ToList()).ConfigureAwait(true);
        _xamlClassesAtStart.Clear();
        foreach (var (path, name) in classes)
        {
            _xamlClassesAtStart[path] = name;
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? XClassOf(string xaml) => XClassRegex().Match(xaml) is { Success: true } match ? match.Groups["name"].Value.Trim() : null;

    [GeneratedRegex(@"\bx:Class\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex XClassRegex();

    private void ShowLiveRows()
    {
        ErrorList.SetLive(ErrorListViewModel.BindingSource, [.. _bindingRows]);
        ErrorList.SetLive(ErrorListViewModel.XamlHotReloadSource, [.. _xamlReloadRows.Values]);
    }

    // ------------------------------------------------------------------ failed bindings

    /// <summary>The app's new binding failures, at their XAML lines (a quarter of a second after the first: they come in bursts).</summary>
    private async Task TakeBindingFailuresAsync(LiveSession session)
    {
        if (_bindingsQueued)
        {
            return;
        }

        _bindingsQueued = true;
        try
        {
            await Task.Delay(250).ConfigureAwait(true);
            if (!ReferenceEquals(Live, session) || !session.IsConnected)
            {
                return;
            }

            var failures = await session.BindingFailuresAsync(CancellationToken.None).ConfigureAwait(true);
            var files = XamlFiles();
            var located = await Task.Run(() =>
            {
                var texts = files.Select(f => (Path: f, Text: TryRead(f))).Where(f => f.Text is not null).Select(f => (f.Path, f.Text!)).ToList();
                return failures.Select(f => (Failure: f, Location: XamlBindingLocator.Find(f, texts))).ToList();
            }).ConfigureAwait(true);
            foreach (var (failure, location) in located)
            {
                var row = BindingRow(failure, location);
                if (_bindingKeys.Add($"{row.Path}\u001f{row.Line}\u001f{row.Column}\u001f{failure.Property}\u001f{failure.Path}"))
                {
                    _bindingRows.Add(row);
                }
            }

            ShowLiveRows();
        }
        catch (PreviewerException ex)
        {
            PreviewerLog.AppendLine("Failed bindings: " + ex.Message);
        }
        finally
        {
            _bindingsQueued = false;
        }
    }

    private static ErrorListRow BindingRow(LiveBindingFailure failure, XamlBindingLocation? location)
    {
        var description = Localizer.Format("msg.xamlHotReload.bindingFailed", "The binding of {0} to '{1}' failed in the running app: {2}", failure.Property, failure.Path, failure.Message);
        var path = location?.Path ?? string.Empty;
        var position = location is null ? new Position(0, 0) : new Position(location.Line, location.Column);
        return new ErrorListRow("warning", BindingFailureCode, description, string.Empty, Path.GetFileName(path), position.Line + 1, position.Character + 1, path, position);
    }

    // ------------------------------------------------------------------ XAML Hot Reload

    /// <summary>A saved XAML file reaches the running app (with <c>debug.xamlHotReload</c> on).</summary>
    private void OnSavedForXamlHotReload(string path)
    {
        if (!(path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            || Live is not { IsConnected: true } session || !Workbench.Settings.GetBoolean(XamlHotReloadSetting, true))
        {
            return;
        }

        _ = ApplyXamlAsync(session, path);
    }

    /// <summary>Whether a save of <paramref name="path"/> goes to the running app through XAML Hot Reload.</summary>
    private bool XamlHotReloadTakes(string path) =>
        (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        && Live is { IsConnected: true } && Workbench.Settings.GetBoolean(XamlHotReloadSetting, true);

    /// <summary>Loads the saved file into the app, and says how it went: the status bar, the Error List, a restart offer.</summary>
    public async Task<LiveReloadResult?> ApplyXamlAsync(LiveSession session, string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var xaml = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            if (_xamlClassesAtStart.TryGetValue(path, out var started) && started != XClassOf(xaml))
            {
                return Report(path, new LiveReloadResult(LiveReloadOutcome.Failed, 0, LineOf(xaml, "x:Class"), 0,
                    Localizer.Format("msg.xamlHotReload.classChanged", "Its x:Class changed ({0} → {1}).", started ?? "none", XClassOf(xaml) ?? "none")));
            }

            var owner = Solutions.Projects.Values.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)));
            string loader = "-", assembly = "-", uri = "-";
            var compiled = false;
            if (owner is null || PreviewService.DialectOf(path, owner) == PreviewDialect.Avalonia)
            {
                // Avalonia: its runtime XAML compiler is in the app's own designer host, which the previewer resolves.
                var target = await Previewer.ResolveAsync(path, CancellationToken.None).ConfigureAwait(true);
                loader = target.DesignerHost;
                assembly = Path.GetFileNameWithoutExtension(target.Assembly);
                uri = "avares://" + assembly + target.ProjectRelativePath;
                var properties = await PreviewPropertiesAsync(target.Project, ["AvaloniaUseCompiledBindingsByDefault"], CancellationToken.None).ConfigureAwait(true);
                compiled = properties.GetValueOrDefault("AvaloniaUseCompiledBindingsByDefault")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            }

            var result = await session.ReloadAsync(loader, assembly, uri, compiled, xaml, CancellationToken.None, path).ConfigureAwait(true);
            return Report(path, result);
        }
        catch (Exception ex) when (ex is PreviewerException or IOException or UnauthorizedAccessException)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.xamlHotReload.failed", "XAML Hot Reload: {0} wasn't applied: {1}", name, ex.Message), TimeSpan.FromSeconds(8));
            return null;
        }
    }

    private static int LineOf(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? 0 : text.AsSpan(0, at).Count('\n') + 1;
    }

    private LiveReloadResult Report(string path, LiveReloadResult result)
    {
        var name = Path.GetFileName(path);
        LastXamlReload = (path, result);
        _xamlReloadRows.Remove(path);
        switch (result.Outcome)
        {
            case LiveReloadOutcome.Applied:
                StatusBar.ShowMessage(result.Count > 0
                    ? Localizer.Format("msg.xamlHotReload.applied", "XAML Hot Reload: {0} applied to the running app ({1} on screen).", name, result.Count.ToString(CultureInfo.CurrentCulture))
                    : Localizer.Format("msg.xamlHotReload.noInstance", "XAML Hot Reload: nothing of {0} is on screen; what opens later uses the XAML the app was built with.", name), TimeSpan.FromSeconds(6));
                break;
            case LiveReloadOutcome.NotApplicable:
                StatusBar.ShowMessage(Localizer.Format("msg.xamlHotReload.noClass", "XAML Hot Reload applies files with an x:Class: restart the app to apply {0}.", name), TimeSpan.FromSeconds(8));
                break;
            case LiveReloadOutcome.Failed:
                var line = Math.Max(1, result.Line);
                var column = Math.Max(1, result.Column);
                var position = new Position(line - 1, column - 1);
                var message = Localizer.Format("msg.xamlHotReload.rude", "This XAML edit can't be applied to the running app; restart it to apply it. {0}", result.Message);
                _xamlReloadRows[path] = new ErrorListRow("error", XamlHotReloadCode, message, string.Empty, name, line, column, path, position);
                var restart = Localizer.Get("action.debugHotReload.restart", "Restart");
                _ = Workbench.Notifications.Warning(message, restart).Result.ContinueWith(t =>
                {
                    if (t.Result == restart)
                    {
                        Ui.Post(() => _ = RestartLiveAppAsync());
                    }
                }, TaskScheduler.Default);
                break;
        }

        ShowLiveRows();
        return result;
    }

    /// <summary>Restarts the app XAML Hot Reload couldn't update: the debug session, or the program run without debugging.</summary>
    private async Task RestartLiveAppAsync()
    {
        if (Debugger.IsActive)
        {
            await RestartDebuggingAsync().ConfigureAwait(true);
            return;
        }

        await Runs.StopAllAsync().ConfigureAwait(true);
        await StartWithoutDebuggingAsync().ConfigureAwait(true);
    }
}
