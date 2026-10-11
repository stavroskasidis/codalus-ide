using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Search;

namespace DaedalusStudio.Shell;

/// <summary>The standard toolbar and the search box (M29, ADR 0019).</summary>
public sealed partial class ShellHost
{
    /// <summary>The startup list's value for a folder's launch configuration.</summary>
    private const string LaunchChoicePrefix = "launch:";

    public ToolbarViewModel Toolbar { get; private set; } = null!;

    private void InitializeToolbar()
    {
        Toolbar = new ToolbarViewModel(
            id => CommandFor(id),
            (configuration, platform) =>
            {
                if (Solutions.Solution?.Configurations.FirstOrDefault(c => c.Configuration == configuration && c.Platform == platform) is { } picked
                    && picked != Solutions.Solution.Active)
                {
                    _ = Solutions.SetConfigurationAsync(picked, CancellationToken.None);
                }
            },
            path =>
            {
                // In folder mode the startup list holds the launch configurations (ADR 0033).
                if (path?.StartsWith(LaunchChoicePrefix, StringComparison.Ordinal) == true)
                {
                    SelectLaunchConfiguration(path[LaunchChoicePrefix.Length..]);
                }
                else
                {
                    SetStartupProject(path);
                }
            },
            () => ActiveEditor?.Controller?.Editor.Undo(),
            () => ActiveEditor?.Controller?.Editor.Redo());
    }

    private void UpdateToolbarSolution()
    {
        if (Toolbar is null)
        {
            return;
        }

        if (Solutions?.Solution is not { } solution)
        {
            Toolbar.SetSolution(null, null);
            Toolbar.SetStartup([], null, null);
            return;
        }

        Toolbar.SetSolution(
            [.. solution.Configurations.Select(c => (c.Configuration, c.Platform))],
            solution.Active is { } active ? (active.Configuration, active.Platform) : null,
            solution.Name);
        UpdateToolbarStartup();
    }

    private void UpdateToolbarStartup()
    {
        if (Toolbar is null)
        {
            return;
        }

        if (Solutions?.Solution is not { } solution)
        {
            Toolbar.SetStartup([], null, null);
            return;
        }

        if (solution.IsFolder && LaunchConfigurations() is { Count: > 0 } configurations)
        {
            Toolbar.SetStartup([.. configurations.Select(c => new StartupProjectChoice(c.Name, LaunchChoicePrefix + c.Name))], LaunchChoicePrefix + CurrentLaunchConfiguration!.Name, null);
            return;
        }

        var startup = Runs.StartupProject;
        Toolbar.SetStartup(
            [.. solution.Projects.Where(p => Runs.IsExecutable(p.Path) || DaedalusStudio.Workbench.Projects.SolutionService.PathComparer.Equals(p.Path, startup)).Select(p => new StartupProjectChoice(p.Name, p.Path))],
            startup,
            startup is null ? null : Runs.ChosenProfile(startup));
    }

    // ------------------------------------------------------------------ search (Ctrl+Q)

    /// <summary>
    /// The search box (Ctrl+Q): commands, settings, files, and types and members of the solution, in one list. A
    /// command runs, a setting opens in Settings, a file or symbol opens in the editor.
    /// </summary>
    public async Task FeatureSearchAsync()
    {
        var files = SearchableFiles();
        var context = Workbench.Context;
        var commands = Workbench.Commands.Descriptors
            .Where(d => d.ShowInPalette && d.Enablement.Evaluate(context) && Workbench.Menus.IsVisibleInPalette(d.Id, context))
            .ToList();
        var settings = Workbench.Settings.Descriptors.Where(d => d.Title is not null).ToList();

        async Task<IReadOnlyList<QuickPickItem>> Provider(string query, CancellationToken cancellationToken)
        {
            query = query.Trim();
            if (query.Length == 0)
            {
                return [];
            }

            var items = new List<QuickPickItem>();
            items.AddRange(commands
                .Select(d => (Descriptor: d, Match: FuzzyMatcher.Match(query, d.DisplayTitle)))
                .Where(m => m.Match is not null)
                .OrderByDescending(m => m.Match!.Score)
                .Take(8)
                .Select(m => new QuickPickItem(m.Descriptor.DisplayTitle)
                {
                    Detail = "Command",
                    KeyLabel = Workbench.Keybindings.GetBinding(m.Descriptor.Id)?.ToLabel(Workbench.Platform),
                    Tag = new FeatureSearchCommand(m.Descriptor.Id),
                }));
            items.AddRange(settings
                .Select(d => (Descriptor: d, Match: FuzzyMatcher.Match(query, d.Title!) ?? FuzzyMatcher.Match(query, d.Key)))
                .Where(m => m.Match is not null)
                .OrderByDescending(m => m.Match!.Score)
                .Take(5)
                .Select(m => new QuickPickItem(m.Descriptor.Title!)
                {
                    Detail = "Setting · " + (m.Descriptor.Category is { } category ? category + " · " : string.Empty) + m.Descriptor.Key,
                    Tag = new FeatureSearchSetting(m.Descriptor.Key),
                }));
            items.AddRange(files
                .Select(f => (Path: f, Match: FuzzyMatcher.Match(query, Path.GetFileName(f))))
                .Where(m => m.Match is not null)
                .OrderByDescending(m => m.Match!.Score)
                .Take(8)
                .Select(m => new QuickPickItem(Path.GetFileName(m.Path)) { Detail = "File · " + Path.GetDirectoryName(m.Path), Tag = m.Path }));
            if (query.Length >= 2 && Languages.RunningClient(CSharpLanguageId) is { } client)
            {
                try
                {
                    foreach (var symbol in (await client.WorkspaceSymbolsAsync(query, cancellationToken).ConfigureAwait(true)).Take(10))
                    {
                        items.Add(new QuickPickItem(symbol.Name)
                        {
                            Detail = $"{KindName(symbol.Kind)}{(symbol.ContainerName is { } container ? " in " + container : string.Empty)} · {Path.GetFileName(DocumentUris.ToPath(symbol.Location.Uri))}",
                            Tag = symbol.Location,
                        });
                    }
                }
                catch (Exception ex) when (ex is DaedalusStudio.Rpc.JsonRpcRemoteException or DaedalusStudio.Rpc.JsonRpcConnectionLostException or InvalidOperationException)
                {
                    // Symbols are a bonus: commands, settings and files still show.
                }
            }

            return items;
        }

        Workbench.Context.Set("inCommandPalette", true);
        switch (await Palette.ShowAsync(Provider, "Search commands, settings, files and code (Ctrl+Q)").ConfigureAwait(true))
        {
            case { Tag: FeatureSearchCommand command }:
                Workbench.Commands.MarkRecent(command.Id);
                Run(command.Id);
                break;
            case { Tag: FeatureSearchSetting setting }:
                OpenSettings();
                if (Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { } document)
                {
                    document.ViewModel.Search = setting.Key;
                }

                break;
            case { Tag: Location location }:
                RememberLocation();
                await OpenLocationAsync(location).ConfigureAwait(true);
                break;
            case { Tag: string path }:
                await OpenFileAsync(path).ConfigureAwait(true);
                break;
        }
    }

    private sealed record FeatureSearchCommand(string Id);

    private sealed record FeatureSearchSetting(string Key);
}
