using System.Text.Json.Nodes;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.FSharp;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// F# (M36, ADR 0067): FsAutoComplete through the LSP bridge (D10), installed with Fantomas the first time an F# file
/// opens and the user agrees (or always, with <c>fsharp.languageService</c>); it loads F# projects with MSBuild itself,
/// so it runs only for trusted folders. Compile order in Solution Explorer: Move Up, Move Down, Add Above, Add Below.
/// </summary>
public sealed partial class ShellHost
{
    public const string FSharpLanguageId = "fsharp";

    public const string FSharpLanguageServiceSetting = "fsharp.languageService";

    /// <summary>The settings section FsAutoComplete reads.</summary>
    private const string FSharpSettingsSection = "FSharp";

    private bool _fsharpOffered;
    private Task? _fsharpInstall;

    public FSharpTools FSharp { get; private set; } = null!;

    private void InitializeFSharp()
    {
        var cache = Environment.GetEnvironmentVariable(NodeCacheVariable) is { Length: > 0 } shared ? shared : Paths.Cache;
        FSharp = new FSharpTools(cache, EnvironmentCheck.FindDotnet() ?? "dotnet");
        Workbench.LanguageServers.Register(new LanguageServiceDescriptor(FSharpLanguageId, "F#", [".fs", ".fsi", ".fsx", ".fsscript"], null, FSharpLaunch)
        {
            InitializationOptions = Json(new JsonObject { ["AutomaticWorkspaceInit"] = true }),
            Settings = () => Json(new JsonObject { [FSharpSettingsSection] = FSharpSettings() }),
        });
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Any(k => k.StartsWith("fsharp.", StringComparison.Ordinal)))
            {
                _ = ConfigureLanguagesAsync();
            }
        };
    }

    /// <summary>FsAutoComplete, or null: not installed yet, or the solution isn't trusted (it evaluates projects with MSBuild).</summary>
    private ProcessLaunchInfo? FSharpLaunch() => Solutions.Solution is { Trusted: false } ? null : FSharp.Launch();

    private static System.Text.Json.JsonElement Json(JsonNode node)
    {
        using var document = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>FsAutoComplete's <c>FSharp</c> settings section, from ours.</summary>
    private JsonObject FSharpSettings()
    {
        var settings = Workbench.Settings;
        var hints = settings.GetBoolean("fsharp.inlayHints", true);
        return new JsonObject
        {
            ["keywordsAutocomplete"] = true,
            ["externalAutocomplete"] = settings.GetBoolean("fsharp.completeUnopenedNamespaces", true),
            ["linter"] = settings.GetBoolean("fsharp.linter", true),
            ["unusedOpensAnalyzer"] = true,
            ["unusedDeclarationsAnalyzer"] = true,
            ["simplifyNameAnalyzer"] = true,
            ["resolveNamespaces"] = true,
            ["unionCaseStubGeneration"] = true,
            ["recordStubGeneration"] = true,
            ["interfaceStubGeneration"] = true,
            ["inlayHints"] = new JsonObject { ["enabled"] = hints, ["typeAnnotations"] = hints, ["parameterNames"] = hints },
            ["codeLenses"] = new JsonObject
            {
                ["signature"] = new JsonObject { ["enabled"] = settings.GetBoolean("fsharp.signatureCodeLens", true) },
                ["references"] = new JsonObject { ["enabled"] = settings.GetBoolean("fsharp.referencesCodeLens", true) },
            },
            ["enableReferenceCodeLens"] = settings.GetBoolean("fsharp.referencesCodeLens", true),
        };
    }

    /// <summary>The solution changed: a trusted F# solution starts FsAutoComplete again (it may have been held back before trust).</summary>
    private void OnFSharpSolutionChanged(Codalus.Projects.Protocol.SolutionInfo? solution)
    {
        // FsAutoComplete loads the solution's folder when it starts: a solution opened (or trusted) since starts it again.
        var key = solution is { Trusted: true } ? solution.Path : null;
        if (key == _fsharpSolution)
        {
            return;
        }

        _fsharpSolution = key;
        if (key is not null && FSharp.IsInstalled && solution!.Projects.Any(p => p.Path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Languages.RestartAsync(FSharpLanguageId);
        }
    }

    private string? _fsharpSolution;

    /// <summary>An F# file opened without a running server: install FsAutoComplete and Fantomas (asking first, per the setting).</summary>
    private async Task OfferFSharpAsync(string languageId)
    {
        if (languageId != FSharpLanguageId || FSharp.IsInstalled || _fsharpInstall is not null)
        {
            return;
        }

        if (Solutions.Solution is { Trusted: false })
        {
            LanguageLog.AppendLine("[fsharp] The F# language service runs only in trusted folders: it loads the projects with MSBuild.");
            return;
        }

        var mode = Workbench.Settings.GetString(FSharpLanguageServiceSetting, "ask");
        if (mode == "off")
        {
            return;
        }

        if (mode != "install")
        {
            if (_fsharpOffered)
            {
                return;
            }

            _fsharpOffered = true;
            var choice = await Workbench.Notifications.Show(NotificationSeverity.Info,
                $"Completion, errors, navigation and formatting in F# need FsAutoComplete {FSharpTools.FsAutoCompleteVersion} and Fantomas {FSharpTools.FantomasVersion}, which Codalus IDE installs once with dotnet tool install (about 100 MB, into its cache folder).",
                ["Install", "Not Now", "Never"]).Result.ConfigureAwait(true);
            if (choice == "Never")
            {
                await Workbench.Settings.SetAsync(FSharpLanguageServiceSetting, "off", Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                return;
            }

            if (choice != "Install")
            {
                return;
            }
        }

        await InstallFSharpAsync().ConfigureAwait(true);
    }

    /// <summary>Installs FsAutoComplete and Fantomas, then starts the language service for the open F# files.</summary>
    public Task InstallFSharpAsync() => _fsharpInstall ??= InstallFSharpCoreAsync();

    private async Task InstallFSharpCoreAsync()
    {
        var progress = new Progress<string>(message =>
        {
            StatusBar.ShowMessage(message);
            LanguageLog.AppendLine("[fsharp] " + message);
        });
        try
        {
            await Task.Run(() => FSharp.EnsureAsync(progress, CancellationToken.None)).ConfigureAwait(true);
            StatusBar.ShowMessage(Localizer.Get("msg.fsharp.ready", "The F# language service is ready."), TimeSpan.FromSeconds(5));
            await Languages.RetryAsync(FSharpLanguageId).ConfigureAwait(true);
            RefreshEditors();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            StatusBar.ClearMessage();
            LanguageLog.AppendLine("[fsharp] The install failed: " + ex);
            Workbench.Notifications.Error("The F# language service could not be installed: " + ex.Message);
        }
        finally
        {
            _fsharpInstall = null;
        }
    }

    // ------------------------------------------------------------------ compile order

    /// <summary>The selected file of an F# project, when it is in the compile order: its project, path and the order.</summary>
    private (string Project, string File, IReadOnlyList<string> Order)? OrderedSelection()
    {
        if (Explorer.Selected?.Node is not { Kind: ExplorerNodeKind.File, Path: { } file, ProjectPath: { } project } || !SolutionTree.IsOrdered(project)
            || !Solutions.Projects.TryGetValue(project, out var info))
        {
            return null;
        }

        var order = CompileOrder.Of(info);
        return order.Contains(file, SolutionService.PathComparer) ? (project, file, order) : null;
    }

    /// <summary>Move Up or Move Down: the selected F# file (with its signature file) one place in the compile order.</summary>
    public async Task MoveInCompileOrderAsync(bool down)
    {
        if (OrderedSelection() is not { } selection)
        {
            return;
        }

        var moves = CompileOrder.Step(selection.Order, selection.File, down);
        if (moves.Count == 0)
        {
            StatusBar.ShowMessage(down ? "It's the last file of its folder." : "It's the first file of its folder.", TimeSpan.FromSeconds(3));
            return;
        }

        await RunItemEditAsync(async () =>
        {
            foreach (var (path, before) in moves)
            {
                await ProjectItems.MoveAsync(selection.Project, path, before, after: false, CancellationToken.None).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
        Explorer.RevealWhenAvailable("f:" + selection.File);
    }

    /// <summary>Add Above or Add Below: Add New Item, with the new file placed before or after the selected one.</summary>
    public void AddInCompileOrder(bool below)
    {
        if (OrderedSelection() is not { } selection)
        {
            return;
        }

        if (OpenAddNewItem(selection.Project, Path.GetDirectoryName(selection.File)) is { } dialog)
        {
            dialog.Placement = (selection.File, below);
        }
    }
}
