using System.Globalization;
using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.CustomTools;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Build depth (M26b, ADR 0034 and 0035): build settings, Batch Build, the fast up-to-date check before running, Build
/// Timings, binary logs and their viewer, and custom tools (ResX, T4 and plugins' generators).
/// </summary>
public sealed partial class ShellHost
{
    public const string BuildVerbositySetting = "build.verbosity";
    public const string BuildLogFileVerbositySetting = "build.logFileVerbosity";
    public const string MaxParallelProjectsSetting = "build.maxParallelProjects";
    public const string BinaryLogSetting = "build.binaryLog";
    public const string FastUpToDateCheckSetting = "build.fastUpToDateCheck";
    public const string RunAnalyzersDuringBuildSetting = "build.runAnalyzersDuringBuild";
    public const string BuildBeforeRunSetting = "build.beforeRun";
    public const string RunCustomToolsOnSaveSetting = "build.runCustomToolsOnSave";

    private const int BinaryLogsKept = 20;
    private string? _nextBinaryLog;

    public BuildDepthService BuildDepth { get; private set; } = null!;

    public TextTemplatingService TextTemplates { get; private set; } = null!;

    public CustomToolService CustomTools { get; private set; } = null!;

    /// <summary>Where builds write binary logs (setting <c>build.binaryLog</c>, Build with Binary Log).</summary>
    public string BinaryLogsFolder => Path.Combine(Paths.Logs, "binlogs");

    /// <summary>The build log file (setting <c>build.logFileVerbosity</c>).</summary>
    public string BuildLogFile => Path.Combine(Paths.Logs, "build.log");

    private void InitializeBuildDepth()
    {
        BuildDepth = new BuildDepthService(Solutions);
        TextTemplates = new TextTemplatingService(TextTemplatingHostLaunch.Resolve);
        TextTemplates.Log += (_, line) => Workbench.Log.AppendLine("[t4host] " + line);
        CustomTools = new CustomToolService(ProjectItems, TextTemplates)
        {
            PluginTools = () => [.. Plugins.CustomTools.Select(t => new PluginCustomTool(t.Tool.Name, t.Tool.Command, t.Tool.Extension))],
            RunPluginTool = RunPluginCustomToolAsync,
        };
        Builds.Options = CurrentBuildOptions;

        // Binary logs live in the build host, which closes with the solution.
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            if (!Solutions.IsOpen)
            {
                foreach (var document in Layout.Documents.OfType<PageDocument>().Where(d => d.ViewModel is BinaryLogViewModel).ToList())
                {
                    Layout.CloseDocument(document);
                }
            }
        });
    }

    /// <summary>The options of the next build, from the build settings (ADR 0034).</summary>
    private BuildOptions CurrentBuildOptions()
    {
        var settings = Workbench.Settings;
        var logVerbosity = settings.GetString(BuildLogFileVerbositySetting, "off");
        string? binaryLog = Interlocked.Exchange(ref _nextBinaryLog, null);
        if (binaryLog is null && settings.GetBoolean(BinaryLogSetting, false))
        {
            binaryLog = NewBinaryLogPath();
        }

        return new BuildOptions
        {
            Verbosity = settings.GetString(BuildVerbositySetting, BuildVerbosities.Minimal),
            LogFile = logVerbosity == "off" ? null : BuildLogFile,
            LogFileVerbosity = logVerbosity == "off" ? null : logVerbosity,
            MaxParallelProjects = Math.Max(0, settings.GetInt32(MaxParallelProjectsSetting, 0)),
            BinaryLog = binaryLog,
            // A requested binary log needs MSBuild to run.
            UpToDateCheck = settings.GetBoolean(FastUpToDateCheckSetting, true) && binaryLog is null,
            Properties = settings.GetBoolean(RunAnalyzersDuringBuildSetting, true) ? null : new Dictionary<string, string> { ["RunAnalyzersDuringBuild"] = "false" },
        };
    }

    /// <summary>A new binary log's path (the solution's name and the time), keeping the newest <see cref="BinaryLogsKept"/>.</summary>
    private string NewBinaryLogPath()
    {
        Directory.CreateDirectory(BinaryLogsFolder);
        try
        {
            foreach (var old in new DirectoryInfo(BinaryLogsFolder).GetFiles("*.binlog").OrderByDescending(f => f.LastWriteTimeUtc).Skip(BinaryLogsKept - 1))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var name = Solutions.Solution?.Name is { Length: > 0 } solution ? string.Concat(solution.Split(Path.GetInvalidFileNameChars())) : "build";
        return Path.Combine(BinaryLogsFolder, $"{name}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.binlog");
    }

    // ------------------------------------------------------------------ builds before running

    /// <summary>
    /// What F5 and Ctrl+F5 do before starting a project (setting <c>build.beforeRun</c>): build it (the fast up-to-date
    /// check makes that quick), ask when it is out of date, or never build. Returns whether to start it.
    /// </summary>
    internal async Task<bool> BuildBeforeRunAsync(string project)
    {
        switch (Workbench.Settings.GetString(BuildBeforeRunSetting, "build"))
        {
            case "never":
                return true;
            case "prompt":
                try
                {
                    var verdict = await BuildDepth.CheckUpToDateAsync(project, CancellationToken.None).ConfigureAwait(true);
                    if (verdict.UpToDate)
                    {
                        return true;
                    }

                    var reason = verdict.Projects.FirstOrDefault(p => !p.UpToDate)?.Reason;
                    var answer = await Workbench.Notifications.Warning(
                        Localizer.Format("msg.buildDepth.isOutOfDateBuildIt", "{0} is out of date ({1}). Build it first?", Path.GetFileNameWithoutExtension(project), reason), "Build", "Run Anyway").Result.ConfigureAwait(true);
                    return answer switch
                    {
                        "Build" => await BuildAsync(BuildTargets.Build, project).ConfigureAwait(true),
                        "Run Anyway" => true,
                        _ => false,
                    };
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
                {
                    return await BuildAsync(BuildTargets.Build, project).ConfigureAwait(true);
                }

            default:
                return await BuildAsync(BuildTargets.Build, project).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ batch build, up-to-date check

    public BatchBuildViewModel OpenBatchBuild()
    {
        if (Layout.FindDocument<PageDocument>("batchBuild") is { ViewModel: BatchBuildViewModel existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            _ = existing.LoadAsync();
            return existing;
        }

        var viewModel = new BatchBuildViewModel(ProjectManagement, BatchBuildAsync);
        Layout.OpenDocument(new PageDocument("batchBuild", "Batch Build", "batchBuild", viewModel), focus: true);
        return viewModel;
    }

    /// <summary>Runs a batch build (trust, Save All and the Build output as for a build); null when it didn't run.</summary>
    public async Task<BatchBuildResult?> BatchBuildAsync(string target, IReadOnlyList<BatchBuildEntry> entries)
    {
        if (Solutions.Solution is not { Trusted: true })
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.buildDepth.buildsRunOnlyInTrustedFolders", "Builds run only in trusted folders."));
            return null;
        }

        if (Builds.IsBuilding)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.buildDepth.aBuildIsAlreadyRunning", "A build is already running."), TimeSpan.FromSeconds(4));
            return null;
        }

        if (Workbench.Settings.GetBoolean(SaveAllBeforeBuildSetting, true))
        {
            await SaveAllAsync().ConfigureAwait(true);
        }

        Workbench.Output.Show(Builds.Channel);
        return await Builds.BatchBuildAsync(target, entries, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Reports in the Build output whether the solution's (or a project's) projects are up to date.</summary>
    public async Task<UpToDateResult?> CheckUpToDateAsync(string? project = null)
    {
        if (Solutions.Solution is not { Trusted: true })
        {
            StatusBar.ShowMessage(Localizer.Get("msg.buildDepth.openATrustedSolutionToCheck", "Open a trusted solution to check it."), TimeSpan.FromSeconds(4));
            return null;
        }

        try
        {
            var result = await BuildDepth.CheckUpToDateAsync(project, CancellationToken.None).ConfigureAwait(true);
            var channel = Builds.Channel;
            channel.AppendLine("Fast up-to-date check:");
            foreach (var verdict in result.Projects)
            {
                channel.AppendLine($"  {Path.GetFileNameWithoutExtension(verdict.Project)}: {(verdict.UpToDate ? "up to date" : "not up to date, " + verdict.Reason)}");
            }

            Workbench.Output.Show(channel);
            StatusBar.ShowMessage(result.UpToDate ? "Everything is up to date." : "Some projects are out of date (see Output → Build).", TimeSpan.FromSeconds(6));
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error("The up-to-date check failed: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ timings and binary logs

    public BuildTimingsViewModel OpenBuildTimings()
    {
        if (Layout.FindDocument<PageDocument>("buildTimings") is { ViewModel: BuildTimingsViewModel existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            _ = existing.LoadAsync();
            return existing;
        }

        var viewModel = new BuildTimingsViewModel(BuildDepth, path =>
        {
            OpenBinaryLog(path);
            return Task.CompletedTask;
        });
        Layout.OpenDocument(new PageDocument("buildTimings", "Build Timings", "buildTimings", viewModel), focus: true);
        return viewModel;
    }

    /// <summary>Opens a binary log in the viewer (one document per file).</summary>
    public BinaryLogViewModel? OpenBinaryLog(string path)
    {
        if (!Solutions.IsOpen)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.buildDepth.binaryLogsAreReadByThe", "Binary logs are read by the build host: open a solution first."), TimeSpan.FromSeconds(6));
            return null;
        }

        var full = Path.GetFullPath(path);
        var id = "binlog:" + full;
        if (Layout.FindDocument<PageDocument>(id) is { ViewModel: BinaryLogViewModel existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            return existing;
        }

        var viewModel = new BinaryLogViewModel(BuildDepth, full, async (file, line) => await OpenFileAsync(file, at: new Codalus.LanguageServices.Protocol.Position(line, 0)).ConfigureAwait(true));
        var document = new PageDocument(id, Path.GetFileName(full), "binaryLog", viewModel);
        void OnClosed(object? sender, Docking.ShellDocument closed)
        {
            if (ReferenceEquals(closed, document))
            {
                Layout.DocumentClosed -= OnClosed;
                viewModel.Dispose();
            }
        }

        Layout.DocumentClosed += OnClosed;
        Layout.OpenDocument(document, focus: true);
        return viewModel;
    }

    public async Task OpenBinaryLogAsync(string? argument)
    {
        var path = argument;
        if (path is null && Window is not null)
        {
            path = await Window.PickFilesAsync("Open Binary Log").ConfigureAwait(true) is [var picked, ..] ? picked : null;
        }

        if (path is not null)
        {
            OpenBinaryLog(path);
        }
    }

    /// <summary>Builds the solution with a binary log, then opens it in the viewer.</summary>
    public async Task BuildWithBinaryLogAsync()
    {
        var path = NewBinaryLogPath();
        _nextBinaryLog = path;
        await BuildAsync(BuildTargets.Build, null).ConfigureAwait(true);
        _nextBinaryLog = null;
        if (Builds.LastBuild?.Result.BinaryLog is { } written && File.Exists(written))
        {
            OpenBinaryLog(written);
        }
        else if (Builds.LastBuild is not null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.buildDepth.theBuildDidntWriteABinary", "The build didn't write a binary log."), TimeSpan.FromSeconds(8));
        }
    }

    // ------------------------------------------------------------------ custom tools

    /// <summary>
    /// Runs the custom tool (the item's <c>Generator</c>) of <paramref name="argument"/>, or of the selected file or the
    /// active document. Its output is written next to it and nested under it; errors go to Output → Custom Tool.
    /// </summary>
    public async Task<CustomToolResult?> RunCustomToolAsync(string? argument, bool quiet = false)
    {
        var selected = Explorer.Selected?.Node is { Kind: ExplorerNodeKind.File, Path: { } file } ? file : null;
        var explorerFocused = Equals(Workbench.Context.GetValue("focusedView"), SolutionExplorerViewModel.ToolWindowId);
        var path = argument ?? (explorerFocused ? selected : null) ?? ActiveEditor?.Model.Path ?? selected;
        if (path is null)
        {
            return null;
        }

        path = Path.GetFullPath(path);
        if (Solutions.Projects.Values.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path))) is not { } owner)
        {
            if (!quiet)
            {
                StatusBar.ShowMessage(Localizer.Format("msg.buildDepth.isntInALoadedProject", "{0} isn't in a loaded project.", Path.GetFileName(path)), TimeSpan.FromSeconds(5));
            }

            return null;
        }

        try
        {
            var item = await ProjectItems.GetAsync(owner.Path, path, CancellationToken.None).ConfigureAwait(true);
            string? Metadata(string name) => item.Metadata.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value ? value : null;
            if (Metadata(ItemMetadataNames.Generator) is not { } generator)
            {
                if (!quiet)
                {
                    StatusBar.ShowMessage(Localizer.Format("msg.buildDepth.hasNoCustomToolSetCustom", "{0} has no custom tool (set Custom Tool in the Properties window).", Path.GetFileName(path)), TimeSpan.FromSeconds(6));
                }

                return null;
            }

            if (!CustomTools.IsKnown(generator))
            {
                if (!quiet)
                {
                    Workbench.Notifications.Warning(Localizer.Format("msg.buildDepth.codalusStudioHasNoCustomTool", "Codalus IDE has no custom tool named '{0}'. Plugins can add custom tools.", generator));
                }

                return null;
            }

            var properties = await ProjectManagement.GetPropertiesAsync(owner.Path, ["RootNamespace"], null, CancellationToken.None).ConfigureAwait(true);
            var rootNamespace = properties.Properties.FirstOrDefault(p => p.Name == "RootNamespace")?.Value ?? Path.GetFileNameWithoutExtension(owner.Path);
            var @namespace = Metadata(ItemMetadataNames.CustomToolNamespace)
                             ?? ItemTemplates.NamespaceFor(rootNamespace, Path.GetDirectoryName(owner.Path)!, Path.GetDirectoryName(path)!);
            var result = await CustomTools.RunAsync(owner.Path, path, generator, @namespace, rootNamespace, Solutions.Solution?.Trusted == true, CancellationToken.None).ConfigureAwait(true);
            ReportCustomTool(result, quiet);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.buildDepth.theCustomToolOfFailed", "The custom tool of {0} failed: {1}", Path.GetFileName(path), ex.Message));
            return null;
        }
    }

    private void ReportCustomTool(CustomToolResult result, bool quiet)
    {
        var channel = Workbench.Output.GetOrCreate("codalus.customTool", "Custom Tool");
        channel.AppendLine($"{result.Generator}: {result.Input}" + (result.Output is { } output ? " → " + Path.GetFileName(output) : " failed"));
        foreach (var diagnostic in result.Diagnostics)
        {
            var location = diagnostic.Line > 0 ? $"{diagnostic.File}({diagnostic.Line.ToString(CultureInfo.InvariantCulture)},{Math.Max(1, diagnostic.Column).ToString(CultureInfo.InvariantCulture)})" : diagnostic.File;
            channel.AppendLine($"{location}: {(diagnostic.IsWarning ? "warning" : "error")}: {diagnostic.Message}");
        }

        if (!result.Succeeded)
        {
            Workbench.Output.Show(channel);
            var first = result.Diagnostics.FirstOrDefault(d => !d.IsWarning)?.Message ?? "it produced no output";
            Workbench.Notifications.Warning(Localizer.Format("msg.buildDepth.theCustomToolOfFailed2", "The custom tool of {0} failed: {1}", Path.GetFileName(result.Input), first));
        }
        else if (!quiet)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.buildDepth.generatedFrom", "{0} generated from {1}.", Path.GetFileName(result.Output!), Path.GetFileName(result.Input)), TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>On save: the custom tool of a saved file that has one (setting <c>build.runCustomToolsOnSave</c>).</summary>
    private void RunCustomToolOnSave(string path)
    {
        if (!Workbench.Settings.GetBoolean(RunCustomToolsOnSaveSetting, true) || Solutions.Solution is not { Trusted: true })
        {
            return;
        }

        var extension = Path.GetExtension(path);
        var candidate = extension.Equals(".resx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tt", StringComparison.OrdinalIgnoreCase) || Plugins.CustomTools.Count > 0;
        if (candidate && Solutions.Projects.Values.Any(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path))))
        {
            _ = RunCustomToolAsync(path, quiet: true);
        }
    }

    /// <summary>A plugin's custom tool: its command gets <c>{ path, content, namespace, project }</c>.</summary>
    private async Task<(string? Content, string? Extension)> RunPluginCustomToolAsync(PluginCustomTool tool, string path, string content, string @namespace, string project, CancellationToken cancellationToken)
    {
        var argument = JsonArgument(writer =>
        {
            writer.WriteString("path", path);
            writer.WriteString("content", content);
            writer.WriteString("namespace", @namespace);
            writer.WriteString("project", project);
        });
        var result = await Workbench.Commands.ExecuteAsync(tool.Command, [argument], cancellationToken).ConfigureAwait(true);
        return result switch
        {
            { ValueKind: JsonValueKind.String } text => (text.GetString(), null),
            { ValueKind: JsonValueKind.Object } value => (
                value.TryGetProperty("content", out var generated) && generated.ValueKind == JsonValueKind.String ? generated.GetString() : null,
                value.TryGetProperty("extension", out var extension) && extension.ValueKind == JsonValueKind.String ? extension.GetString() : null),
            _ => (null, null),
        };
    }
}
