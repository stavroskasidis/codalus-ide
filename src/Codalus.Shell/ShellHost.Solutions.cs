using System.Text.Json;
using Codalus.Diagnostics.BenchEvents;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>The open solution: opening and closing, trust, configurations, restore and their status (ADR 0004).</summary>
public sealed partial class ShellHost
{
    public const string RestoreOnOpenSetting = "projects.restoreOnOpen";

    private static readonly string[] s_openPatterns = ["*.slnx", "*.sln", "*.csproj", "*.fsproj", "*.vbproj", "*.slnf"];

    private bool _structureReported;
    private bool _evaluationReported;
    private bool _designTimeReported;

    public SolutionService Solutions { get; private set; } = null!;

    public TrustStore Trust { get; private set; } = null!;

    public RecentSolutions Recent { get; private set; } = null!;

    public SolutionExplorerViewModel Explorer { get; private set; } = null!;

    private OutputChannel BuildHostLog => Workbench.Output.GetOrCreate("codalus.buildhost", "Build Host");

    private OutputChannel RestoreLog => Workbench.Output.GetOrCreate("codalus.restore", "Restore");

    private void InitializeSolutions()
    {
        Solutions = new SolutionService(BuildHostLocator.Resolve);
        Trust = new TrustStore(Path.Combine(Paths.State, "trusted-folders.json"));
        Recent = new RecentSolutions(Path.Combine(Paths.State, "recent-solutions.json"));
        Explorer = new SolutionExplorerViewModel(this);

        Solutions.Log += (_, line) => BuildHostLog.AppendLine(line);
        Solutions.RestoreOutput += (_, line) => RestoreLog.AppendLine(line);
        Solutions.SolutionChanged += (_, _) => Ui.Post(OnSolutionChanged);
        Solutions.ProgressChanged += (_, progress) => Ui.Post(() => OnProgress(progress));
        Solutions.DesignTimeProgressChanged += (_, progress) =>
        {
            if (progress is { Completed: true, Total: > 0 } && !_designTimeReported)
            {
                _designTimeReported = true;
                var misses = progress.CacheMisses ?? [];
                BenchEvents.Emit(
                    BenchEventNames.DesignTimeDone,
                    [("cache_hits", progress.CacheHits ?? 0), ("cache_misses", misses.Length)],
                    misses.Length > 0 ? [("cache_miss_reasons", string.Join("; ", misses))] : []);
            }
        };
        Solutions.RestoreStateChanged += (_, e) => Ui.Post(() => OnRestoreState(e));
        Solutions.HostCrashed += (_, e) => CrashReporter.Write("host", $"The build host exited unexpectedly (exit code {e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}); restart: {e.WillRestart}.");
        Solutions.HostCrashed += (_, e) =>
        {
            if (e.ExitCode == SolutionService.NoSdkExitCode)
            {
                // Not a crash: the build host found no SDK it can use, and says why. Starting it again won't help.
                Ui.Post(async () =>
                {
                    var why = Solutions.LastHostMessage is { Length: > 0 } message ? message : "No .NET SDK was found.";
                    if (await Workbench.Notifications.Error("The solution can't be loaded: " + why, "Check Environment").Result.ConfigureAwait(true) == "Check Environment")
                    {
                        OpenCheckEnvironment();
                    }
                });
                return;
            }

            Workbench.Notifications.Show(
                e.WillRestart ? NotificationSeverity.Warning : NotificationSeverity.Error,
                e.WillRestart ? $"The build host stopped unexpectedly (exit code {e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}). Reopening the solution…"
                              : "The build host crashed repeatedly and was stopped. Reopen the solution to try again.");
        };
        Solutions.HostRestarted += (_, _) => Workbench.Notifications.Info(Localizer.Get("msg.solutions.theBuildHostRestartedAndThe", "The build host restarted and the solution was reopened."));
        UpdateSolutionStatus();
    }

    // ------------------------------------------------------------------ open and close

    /// <summary>
    /// Opens a solution or project: asks for one when <paramref name="path"/> is null, asks whether to trust its
    /// folder the first time, then loads it (structure first, projects as they are evaluated).
    /// </summary>
    public async Task OpenSolutionAsync(string? path)
    {
        path ??= Window is { } window ? await window.PickSolutionAsync().ConfigureAwait(true) : null;
        if (path is null)
        {
            return;
        }

        var remote = IsRemotePath(path);
        if (remote ? await Codalus.Workbench.FileSystem.FileSystems.DirectoryExistsAsync(path, CancellationToken.None).ConfigureAwait(true) : Directory.Exists(path))
        {
            await OpenFolderWorkspaceAsync(path).ConfigureAwait(true); // a recent folder (folder mode, ADR 0033)
            return;
        }

        path = remote ? path : Codalus.Platform.RealPath.Resolve(path);
        if (!(remote ? await Codalus.Workbench.FileSystem.FileSystems.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(true) : File.Exists(path)))
        {
            Recent.Remove(path);
            Workbench.Notifications.Error(Localizer.Format("msg.solutions.doesntExist", "'{0}' doesn't exist.", RemoteDisplay(path)));
            return;
        }

        var folder = Path.GetDirectoryName(path)!;
        var trusted = Trust.IsTrusted(folder);
        if (!trusted)
        {
            var choice = await Dialogs.ShowAsync(new DialogRequest(
                "Do you trust the authors of this folder?",
                $"Opening a solution runs code from it: MSBuild evaluates its project files and imports, and restore runs NuGet package targets.\n\n{RemoteDisplay(folder)}\n\n"
                + "Trust it to load projects and restore packages. Without trust, you see the solution's structure only.",
                ["Trust and Open", "Open Without Trusting", "Cancel"])).ConfigureAwait(true);
            if (choice.Button is null or "Cancel")
            {
                return;
            }

            trusted = choice.Button == "Trust and Open";
            if (trusted)
            {
                Trust.Trust(folder);
            }
        }

        _structureReported = false;
        _evaluationReported = false;
        _designTimeReported = false;
        if (Solutions.IsOpen)
        {
            await CloseSolutionAsync().ConfigureAwait(true);
        }

        try
        {
            StatusBar.ShowMessage(Localizer.Format("msg.solutions.opening", "Opening {0}…", Path.GetFileName(path)));
            await Solutions.OpenAsync(path, trusted, Workbench.Settings.GetBoolean(RestoreOnOpenSetting, true), CancellationToken.None).ConfigureAwait(true);
            StatusBar.ClearMessage();
            Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
            if (remote)
            {
                // A remote window (M21): recent lists, workspace settings and Git are this machine's (TODO(issue): M21b).
                OnRemoteWorkspaceOpened(path);
            }
            else
            {
                Recent.Add(path);
                _ = CheckSolutionSdkAsync(path);
                _ = OfferDevContainerAsync(Path.GetDirectoryName(path)!);
                Workbench.Settings.SetWorkspaceFile(Path.Combine(folder, ".codalus", "settings.json"));
                await RestoreEditorSessionAsync(path).ConfigureAwait(true);
                await OpenRepositoryAsync(folder).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusBar.ClearMessage();
            BuildHostLog.AppendLine($"[open] {path}: {ex}");
            if (ex is JsonRpcConnectionLostException)
            {
                // The lost connection may be reported before the host's exit: give it a moment to say why it stopped.
                for (var i = 0; i < 30 && !Solutions.StoppedForSdk; i++)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                }

                if (Solutions.StoppedForSdk)
                {
                    return; // the build host's own reason is shown instead
                }
            }

            ReportError($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens <paramref name="folder"/> from the command line or a drop: its solution (.slnx first, then .sln, then a single
    /// project), or the folder itself (folder mode, ADR 0033) when it has none.
    /// </summary>
    public Task OpenFolderAsync(string folder)
    {
        var candidates = s_openPatterns
            .Select(pattern => Directory.EnumerateFiles(folder, pattern).Order(StringComparer.OrdinalIgnoreCase).ToList())
            .FirstOrDefault(found => found.Count > 0);
        if (candidates is not [var single, ..] || candidates.Count > 1 && !single.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && !single.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return OpenFolderWorkspaceAsync(folder);
        }

        return OpenSolutionAsync(single);
    }

    public async Task CloseSolutionAsync()
    {
        SaveEditorSession();
        CloseSolutionDocuments();
        await Solutions.CloseAsync().ConfigureAwait(true);
        Git.Close();
        Workbench.Settings.SetWorkspaceFile(null);
    }

    public async Task TrustSolutionAsync()
    {
        if (Solutions.Solution is not { Trusted: false } solution)
        {
            return;
        }

        var folder = solution.Directory;
        if (await Dialogs.ConfirmAsync(Localizer.Get("msg.solutions.trustThisFolder", "Trust this folder?"), Localizer.Format("msg.solutions.projectsInWillBeEvaluatedAnd", "Projects in {0} will be evaluated and their packages restored.", folder), Localizer.Get("msg.solutions.trust", "Trust")).ConfigureAwait(true))
        {
            Trust.Trust(folder);
            await Solutions.TrustAsync(Workbench.Settings.GetBoolean(RestoreOnOpenSetting, true), CancellationToken.None).ConfigureAwait(true);
        }
    }

    public async Task OpenRecentAsync()
    {
        var items = Recent.Items.Select(p => new QuickPickItem(Path.GetFileName(p)) { Detail = Path.GetDirectoryName(p), Tag = p, Icon = Directory.Exists(p) ? Controls.FileIcons.ForFolder(Path.GetFileName(p), open: false) : Controls.FileIcons.ForFile(Path.GetFileName(p)) }).ToList();
        if (items.Count == 0)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.solutions.noRecentSolutionsYet", "No recent solutions yet."));
            return;
        }

        if (await Palette.ShowAsync(items, "Open a recent solution").ConfigureAwait(true) is { Tag: string path })
        {
            await OpenSolutionAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Picks the solution configuration. A multi-targeted project's active framework is picked in the editor's
    /// project-context bar.
    /// </summary>
    public async Task SelectConfigurationAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            return;
        }

        var items = solution.Configurations.Select(c => new QuickPickItem(c.Name) { Tag = c, Detail = c == solution.Active ? "active" : null }).ToList();
        var active = Math.Max(0, items.FindIndex(i => (SolutionConfigurationInfo)i.Tag! == solution.Active));
        if (await Palette.ShowAsync(items, "Select the solution configuration", active).ConfigureAwait(true) is { Tag: SolutionConfigurationInfo picked } && picked != solution.Active)
        {
            await Solutions.SetConfigurationAsync(picked, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public async Task RestoreSolutionAsync()
    {
        if (Solutions.Solution is { Trusted: false })
        {
            await TrustSolutionAsync().ConfigureAwait(true);
            return;
        }

        if (Solutions.IsOpen)
        {
            Workbench.Output.Show(RestoreLog);
            await Solutions.RestoreAsync(null, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public async Task AddExistingProjectAsync()
    {
        if (Solutions.Solution is not { } solution || Window is null)
        {
            return;
        }

        if (await Window.PickProjectAsync().ConfigureAwait(true) is { } project)
        {
            var folder = Explorer.Selected?.Node.SolutionFolderId;
            try
            {
                await Solutions.AddProjectAsync(project, folder, CancellationToken.None).ConfigureAwait(true);
            }
            catch (JsonRpcRemoteException ex)
            {
                Workbench.Notifications.Error(ex.Message);
            }
        }
    }

    /// <summary>Adds a project (created by a plugin) to the open solution, in the selected solution folder, and selects it.</summary>
    public async Task AddProjectFileAsync(string project)
    {
        var folder = Explorer.Selected?.Node.SolutionFolderId;
        try
        {
            Explorer.RevealWhenAvailable("p:" + Path.GetFullPath(project));
            await Solutions.AddProjectAsync(project, folder, CancellationToken.None).ConfigureAwait(true);
        }
        catch (JsonRpcRemoteException ex)
        {
            Workbench.Notifications.Error(ex.Message);
        }
    }

    /// <summary><c>solution.getInfo</c>: { path, format, trusted } of the open solution, or null.</summary>
    internal JsonElement? SolutionInfoJsonForPlugins()
    {
        if (Solutions.Solution is not { } solution)
        {
            return null;
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", solution.Path);
            writer.WriteString("format", solution.Format);
            writer.WriteBoolean("trusted", solution.Trusted);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary><c>workbench.getRecentSolutions</c>: [{ path, name }], most recent first (the Start page uses it).</summary>
    internal JsonElement? RecentSolutionsJsonForPlugins()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var path in Recent.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("name", Path.GetFileName(path));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ status

    private void OnSolutionChanged()
    {
        var solution = Solutions.Solution;
        Workbench.Context.Set("solutionOpen", solution is not null);
        StatusBar.HasSolution = solution is not null;
        Workbench.Context.Set("solutionTrusted", solution?.Trusted ?? false);
        if (solution is not null && !_structureReported)
        {
            _structureReported = true;
            BenchEvents.Emit(BenchEventNames.SolutionStructure);
        }

        if (solution is null)
        {
            Workbench.Settings.SetWorkspaceFile(null);
        }
        else if (solution.Trusted && solution.Projects.Any(p => p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            // The C# language host would start after the design-time builds anyway; starting it with the solution
            // overlaps its cold start with evaluation (test discovery from source needs it first).
            _ = Languages.ClientAsync(CSharpLanguageId);
        }

        UpdateSolutionStatus();
    }

    private void OnProgress(EvaluationProgressParams progress)
    {
        if (progress.Completed && !_evaluationReported)
        {
            _evaluationReported = true;
            BenchEvents.Emit(BenchEventNames.SolutionLoaded);
        }

        UpdateSolutionStatus();
    }

    private void OnRestoreState(RestoreStateChangedEventArgs e)
    {
        if (e.State == "failed")
        {
            var notification = Workbench.Notifications.Error($"Restore failed{(e.Message is null ? "." : ": " + e.Message)}", "Show Output");
            _ = notification.Result.ContinueWith(
                t =>
                {
                    if (t.Result == "Show Output")
                    {
                        Ui.Post(() => Workbench.Output.Show(RestoreLog));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        UpdateSolutionStatus();
    }

    private void UpdateSolutionStatus()
    {
        const string ConfigurationItem = "codalus.configuration";
        const string ProgressItem = "codalus.solutionProgress";
        var source = ContributionSource.Shell.Id;
        if (Solutions?.Solution is not { } solution)
        {
            Workbench.StatusBar.Remove(ConfigurationItem);
            Workbench.StatusBar.Remove(ProgressItem);
            UpdateToolbarSolution();
            return;
        }

        // The configuration and platform are in the toolbar (M29).
        Workbench.StatusBar.Remove(ConfigurationItem);
        UpdateToolbarSolution();

        string? progress = null;
        string? command = null;
        if (!solution.Trusted)
        {
            progress = "$(warning) Restricted mode";
            command = "solution.trust";
        }
        // Loading and restoring show as tasks with a progress bar (ShellHost.Progress.cs).
        else if (Solutions.RestoreState == "failed")
        {
            progress = "$(error) Restore failed";
            command = "solution.restore";
        }

        if (progress is null)
        {
            Workbench.StatusBar.Remove(ProgressItem);
        }
        else
        {
            Workbench.StatusBar.Set(new StatusBarItem(ProgressItem, progress, StatusBarAlignment.Left, 40, source)
            {
                Command = command,
                Tooltip = command == "solution.trust" ? "Projects aren't loaded until you trust this folder (click to trust)" : null,
            });
        }
    }
}
