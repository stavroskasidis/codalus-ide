using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Search;
using SettingScope = Codalus.Workbench.Settings.SettingScope;

namespace Codalus.Shell;

/// <summary>
/// Code Search (M47, ADR 0078): the classic IDE's Code Search window in place of Go To All's list. Results come from the
/// solution's files, the persistent index of the language host's declarations (read from the solution's state before
/// the host loads, refreshed when it is ready and after edits), the workspace symbols of every running language
/// service, the referenced assemblies' types ("include external items") and Find in Files' engine. The preview is a
/// real editor on the result's document: the model is the one an editor tab would share, held while it shows, and an
/// edit made there opens the document when the preview moves on, so nothing is lost. Feature Search is the title row's
/// search (commands, settings and options pages) in the same window.
/// </summary>
public sealed partial class ShellHost : ICodeSearchHost
{
    public const string CodeSearchToolWindowId = "codalus.codeSearch";
    public const string CodeSearchScopeSetting = "codeSearch.scope";
    public const string CodeSearchExternalSetting = "codeSearch.includeExternalItems";
    public const string CodeSearchGroupingSetting = "codeSearch.grouping";
    public const string CodeSearchTextResultsSetting = "codeSearch.textResults";

    private const int SymbolResults = 200;
    private const int FileResults = 100;

    private readonly HashSet<string> _indexPending = new(SolutionService.PathComparer);
    private DispatcherTimer? _indexTimer;
    private DispatcherTimer? _codeSearchRefresh;
    private CancellationTokenSource? _indexRefresh;
    private SolutionStateKey? _indexKey;
    private bool _indexUnsaved;
    private Dictionary<string, string>? _projectOfFile;

    private CodeDocument? _previewModel;
    private CodeEditorDocument? _previewDocument;
    private CodeEditorView? _previewView;
    private int _previewVersion;
    private string? _previewKey;

    public CodeSearchViewModel CodeSearch { get; private set; } = null!;

    /// <summary>The solution's persistent index (tests, the smoke test).</summary>
    public CodeSearchIndex CodeSearchIndex { get; } = new();

    /// <summary>The preview's editor, while one shows (tests, the smoke test).</summary>
    public CodeEditorView? CodeSearchPreview => _previewView;

    private void InitializeCodeSearch()
    {
        CodeSearch = new CodeSearchViewModel(this);
        ApplyCodeSearchSettings();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Any(k => k.StartsWith("codeSearch.", StringComparison.Ordinal)))
            {
                Ui.Post(ApplyCodeSearchSettings);
            }
        };
        CodeSearchIndex.Changed += (_, _) => Ui.Post(RefreshCodeSearchSoon);
        Languages.StatusChanged += (_, e) => Ui.Post(() => OnCodeSearchLanguageStatus(e.LanguageId, e.Status));
        Languages.DocumentChanged += (_, path) => Ui.Post(() => QueueIndexUpdate(path));
        Solutions.ProjectChanged += (_, _) =>
        {
            _projectOfFile = null;
            Interlocked.Increment(ref _codeSearchFilesVersion);
            Ui.Post(RefreshCodeSearchSoon);
        };
        Solutions.SolutionChanged += (_, _) => Interlocked.Increment(ref _codeSearchFilesVersion);
    }

    private void ApplyCodeSearchSettings()
    {
        var settings = Workbench.Settings;
        _applyingCodeSearchSettings = true;
        CodeSearch.Scope = settings.GetString(CodeSearchScopeSetting, "entireSolution") switch
        {
            "currentProject" => CodeSearchScope.CurrentProject,
            "currentDocument" => CodeSearchScope.CurrentDocument,
            _ => CodeSearchScope.EntireSolution,
        };
        CodeSearch.IncludeExternal = settings.GetBoolean(CodeSearchExternalSetting);
        CodeSearch.Grouped = settings.GetString(CodeSearchGroupingSetting, "interleaved") == "grouped";
        _applyingCodeSearchSettings = false;
    }

    private bool _applyingCodeSearchSettings;

    // ------------------------------------------------------------------ opening

    /// <summary>Ctrl+T and Ctrl+, (Go To All): Code Search, with the editor's selection to find.</summary>
    public Task GoToAllAsync()
    {
        ShowCodeSearch(CodeSearchFilter.All);
        return Task.CompletedTask;
    }

    /// <summary>Ctrl+Q and the title row's search box: Feature Search in the same window.</summary>
    public Task FeatureSearchAsync()
    {
        OpenCodeSearchWindow(CodeSearchViewModel.FeatureTab, query: null, filter: null);
        return Task.CompletedTask;
    }

    /// <summary>Code Search with a chip chosen (Go To File, Type, Member, Text).</summary>
    public void ShowCodeSearch(CodeSearchFilter filter, string? query = null)
    {
        OpenCodeSearchWindow(CodeSearchViewModel.CodeTab, query ?? SelectedTextToFind(), filter);
    }

    private void OpenCodeSearchWindow(int tab, string? query, CodeSearchFilter? filter)
    {
        if (CodeSearch.IsDocked)
        {
            Layout.ShowToolWindow(CodeSearchToolWindowId, focus: true);
        }

        CodeSearch.Open(tab, query, filter);
    }

    // ------------------------------------------------------------------ ICodeSearchHost: searching

    /// <summary>
    /// Whether results may be missing: a solution opening (its saved index answers meanwhile), or a trusted one with C#
    /// or Visual Basic projects that are still loading, or whose index the language host hasn't built in this session.
    /// </summary>
    public bool CodeSearchIncomplete
    {
        get
        {
            if (_indexKey is null)
            {
                return false;
            }

            if (Solutions.Solution is not { } solution)
            {
                return true;
            }

            return solution.Trusted
                && solution.Projects.Any(p => p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || p.Path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                && (Languages.IsLoading || !CodeSearchIndex.IsComplete);
        }
    }

    async IAsyncEnumerable<CodeSearchBatch> ICodeSearchHost.SearchCodeAsync(CodeSearchQuery query, CodeSearchScope scope, bool includeExternal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.IsLine)
        {
            yield return new CodeSearchBatch(LineResult(query) is { } line ? [line] : [], Incomplete: false);
            yield break;
        }

        var context = RankContext();
        var include = ScopeFilter(scope);
        var files = await CodeSearchFilesAsync().ConfigureAwait(true);
        var root = Solutions.Solution?.Directory;
        var projects = ProjectOfFile();
        var all = new List<CodeSearchResult>();

        // At once: the index and the files.
        all.AddRange(await Task.Run(() =>
        {
            var found = new List<CodeSearchResult>(CodeSearchIndex.Search(query, context, include, SymbolResults, cancellationToken));
            found.AddRange(CodeSearchIndex.SearchFiles(include is null ? files : files.Where(include), query, context, root, p => projects.GetValueOrDefault(p), FileResults, cancellationToken));
            return found;
        }, cancellationToken).ConfigureAwait(true));
        yield return Batch(all);

        // The running language services' workspace symbols: generated sources, F#, the LSP bridge's languages.
        if ((query.Wants(CodeSearchKind.Type) || query.Wants(CodeSearchKind.Member)) && query.Text.Length >= 2)
        {
            var live = await LiveSymbolsAsync(query, context, include, projects, cancellationToken).ConfigureAwait(true);
            if (live.Count > 0)
            {
                all.AddRange(live);
                yield return Batch(all);
            }
        }

        // Types of the referenced assemblies.
        if (includeExternal && query.Wants(CodeSearchKind.Type) && query.Text.Length >= 2 && scope == CodeSearchScope.EntireSolution
            && Languages.RunningClient(CSharpLanguageId) is { } client)
        {
            var external = await ExternalSymbolsAsync(client, query, context, cancellationToken).ConfigureAwait(true);
            if (external.Count > 0)
            {
                all.AddRange(external);
                yield return Batch(all);
            }
        }

        // Text, with Find in Files' engine (plain text, any case), up to the setting's limit.
        var limit = Workbench.Settings.GetInt32(CodeSearchTextResultsSetting, 100);
        if (query.Wants(CodeSearchKind.Text) && limit > 0 && (query.Text.Length >= 3 || query.Filter == CodeSearchFilter.Text && query.Text.Length > 0))
        {
            var text = await TextResultsAsync(query, include, files, projects, context, limit, cancellationToken).ConfigureAwait(true);
            if (text.Count > 0)
            {
                all.AddRange(text);
                yield return Batch(all);
            }
        }

        CodeSearchBatch Batch(List<CodeSearchResult> results) =>
            new(CodeSearchRanker.Order(results, SymbolResults + FileResults + Math.Max(0, Workbench.Settings.GetInt32(CodeSearchTextResultsSetting, 100))), CodeSearchIncomplete);
    }

    /// <summary><c>:42</c> or <c>:42:7</c>: that line of the active document.</summary>
    private CodeSearchResult? LineResult(CodeSearchQuery query)
    {
        if (ActiveEditor is not { } editor || query.Line is not ({ } line and > 0))
        {
            return null;
        }

        var lines = editor.Model.Text.LineCount;
        var target = Math.Min(line, lines);
        var name = query.Column is { } column
            ? Localizer.Format("msg.codeSearch.goToLineColumn", "Go to line {0}, column {1}", target, column)
            : Localizer.Format("msg.codeSearch.goToLine", "Go to line {0}", target);
        return new CodeSearchResult(CodeSearchKind.Line, name)
        {
            Path = editor.Model.IsReadOnly ? null : editor.Model.Path,
            Uri = editor.Model.IsReadOnly ? editor.Model.Uri : null,
            Line = target - 1,
            Column = Math.Max(0, (query.Column ?? 1) - 1),
            Container = Localizer.Format("msg.codeSearch.lineOf", "{0} ({1} lines)", editor.Model.Name, lines),
            Score = CodeSearchRanker.KindWeight(CodeSearchKind.Line, false),
        };
    }

    /// <summary>
    /// The files Code Search lists: the projects' items that exist (or the open files without a solution), and the
    /// saved index's while the projects evaluate. Checked on disk off the UI thread, once per change of the projects.
    /// </summary>
    private async Task<IReadOnlyList<string>> CodeSearchFilesAsync()
    {
        var version = _codeSearchFilesVersion;
        if (_codeSearchFiles is { } cached && cached.Version == version)
        {
            return cached.Files;
        }

        IReadOnlyList<string> candidates;
        if (Solutions.Solution is not { } solution)
        {
            candidates = [.. _models.Values.Where(m => !m.IsReadOnly).Select(m => m.Path)];
        }
        else
        {
            var set = new HashSet<string>(SolutionService.PathComparer);
            foreach (var project in Solutions.Projects.Values)
            {
                set.Add(project.Path);
                set.UnionWith(project.Items.Where(i => i.Kind != "Folder").Select(i => i.Path));
            }

            foreach (var folder in solution.Folders)
            {
                set.UnionWith(folder.Files);
            }

            if (Solutions.Progress is not { Completed: true })
            {
                set.UnionWith(CodeSearchIndex.Files);
            }

            candidates = [.. set];
        }

        var files = await Task.Run(() => (IReadOnlyList<string>)[.. candidates.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase)]).ConfigureAwait(true);
        if (Solutions.Solution is not null && Solutions.Progress is { Completed: true })
        {
            _codeSearchFiles = (version, files); // while projects evaluate, every query looks again
        }

        return files;
    }

    private (int Version, IReadOnlyList<string> Files)? _codeSearchFiles;
    private int _codeSearchFilesVersion;

    /// <summary>The scope as a filter of files: null for the whole solution.</summary>
    private Func<string, bool>? ScopeFilter(CodeSearchScope scope)
    {
        switch (scope)
        {
            case CodeSearchScope.CurrentDocument:
                var document = ActiveEditor?.Model.Path;
                return path => document is not null && SolutionService.PathComparer.Equals(path, document);
            case CodeSearchScope.CurrentProject:
                // A filter, so no file is checked on disk (M56: each of the project's, on the UI thread, at every query),
                // and only the projects of the active document's name are looked through.
                var active = ActiveEditor?.Model.Path;
                var named = active is not null && ProjectOfFile().TryGetValue(active, out var name) ? Solutions.Projects.Values.Where(p => p.Name == name) : [];
                var files = new HashSet<string>(ProjectListing(named, active) is { } project ? project.Items.Select(i => i.Path).Prepend(project.Path) : [], SolutionService.PathComparer);
                return files.Contains;
            default:
                return null;
        }
    }

    /// <summary>Recently opened files, the active document and its project, for the ranking.</summary>
    private CodeSearchRankContext RankContext()
    {
        var recent = RecentDocuments.Select(d => d.FilePath).OfType<string>().Take(10).ToList();
        var active = ActiveEditor?.Model.Path;
        return new CodeSearchRankContext(recent, active, active is null ? null : ProjectOfFile().GetValueOrDefault(active));
    }

    /// <summary>Each project item's project name (and each project file's), rebuilt when a project changes.</summary>
    private Dictionary<string, string> ProjectOfFile()
    {
        if (_projectOfFile is { } map)
        {
            return map;
        }

        map = new Dictionary<string, string>(SolutionService.PathComparer);
        foreach (var project in Solutions.Projects.Values)
        {
            map.TryAdd(project.Path, project.Name);
            foreach (var item in project.Items)
            {
                map.TryAdd(item.Path, project.Name);
            }
        }

        return _projectOfFile = map;
    }

    private async Task<List<CodeSearchResult>> LiveSymbolsAsync(CodeSearchQuery query, CodeSearchRankContext context, Func<string, bool>? include, Dictionary<string, string> projects, CancellationToken cancellationToken)
    {
        var clients = Languages.RunningClients().Where(c => c.Client.Capabilities?.WorkspaceSymbolProvider == true).ToList();
        var answers = await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                return await c.Client.WorkspaceSymbolsAsync(query.Text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException or ObjectDisposedException)
            {
                return []; // symbols are a bonus: the index and files still show
            }
        })).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<CodeSearchResult>();
        foreach (var symbol in answers.SelectMany(a => a))
        {
            var type = CodeSearchIndex.IsTypeKind(symbol.Kind);
            if (!query.Wants(type ? CodeSearchKind.Type : CodeSearchKind.Member))
            {
                continue;
            }

            var path = DocumentUris.ToPath(symbol.Location.Uri);
            if (include is not null && (path is null || !include(path)))
            {
                continue;
            }

            var match = FuzzyMatcher.Match(query.Text, symbol.Name);
            var result = new CodeSearchResult(type ? CodeSearchKind.Type : CodeSearchKind.Member, symbol.Name)
            {
                SymbolKind = symbol.Kind,
                Container = symbol.ContainerName,
                Project = path is null ? null : projects.GetValueOrDefault(path),
                Path = path,
                Uri = path is null ? symbol.Location.Uri : null,
                Line = symbol.Location.Range.Start.Line,
                Column = symbol.Location.Range.Start.Character,
                Length = symbol.Name.Length,
                Highlights = match?.Positions ?? [],
            };
            results.Add(result with { Score = CodeSearchRanker.Score(result, match?.Score ?? 0, context) });
        }

        return results;
    }

    private static async Task<List<CodeSearchResult>> ExternalSymbolsAsync(LanguageServices.Client.LanguageHostClient client, CodeSearchQuery query, CodeSearchRankContext context, CancellationToken cancellationToken)
    {
        SymbolInformation[] symbols;
        try
        {
            symbols = await client.ExternalSymbolsAsync(query.Text, 50, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException or ObjectDisposedException)
        {
            return [];
        }

        return [.. symbols.Select(s =>
        {
            var match = FuzzyMatcher.Match(query.Text, s.Name);
            var result = new CodeSearchResult(CodeSearchKind.Type, s.Name)
            {
                SymbolKind = s.Kind,
                Container = s.ContainerName,
                Uri = s.Location.Uri,
                IsExternal = true,
                Highlights = match?.Positions ?? [],
            };
            return result with { Score = CodeSearchRanker.Score(result, match?.Score ?? 0, context) };
        })];
    }

    /// <summary>Lines with the text, through Find in Files' matching (open documents as edited, bin, obj and dot folders left out).</summary>
    private async Task<List<CodeSearchResult>> TextResultsAsync(CodeSearchQuery query, Func<string, bool>? include, IReadOnlyList<string> files, Dictionary<string, string> projects, CodeSearchRankContext context, int limit, CancellationToken cancellationToken)
    {
        var options = new FindInFilesOptions(query.Text);
        var filter = new FileTypeFilter(options.FileTypes);
        var root = Solutions.Solution?.Directory;
        var candidates = files.Where(f => (include is null || include(f)) && filter.Matches(f, root)).ToList();
        Dictionary<string, string> open;
        lock (_snapshots)
        {
            // TODO(issue): every open document's whole text is made a string here, on the UI thread; search the
            // snapshots (immutable) inside the Task.Run below instead.
            open = _snapshots.Where(s => !s.Key.IsReadOnly).ToDictionary(s => s.Key.Path, s => s.Value.Source.Text, SolutionService.PathComparer);
        }

        var regex = options.CreateRegex();
        return await Task.Run(async () =>
        {
            var results = new List<CodeSearchResult>();
            foreach (var file in candidates.OrderBy(f => context.RecentFiles.Contains(f, SolutionService.PathComparer) ? 0 : 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = open.TryGetValue(file, out var edited) ? edited : await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    continue; // binary
                }

                var number = 0;
                foreach (var line in text.Split('\n'))
                {
                    number++;
                    if (FindResultsPage.MatchLine(file, number, line.TrimEnd('\r'), regex) is not { } row)
                    {
                        continue;
                    }

                    var result = new CodeSearchResult(CodeSearchKind.Text, query.Text)
                    {
                        Path = file,
                        Project = projects.GetValueOrDefault(file),
                        Line = number - 1,
                        Column = row.Column,
                        Length = row.Length,
                        LineText = row.Text,
                        Highlights = row.Highlights ?? [],
                    };
                    results.Add(result with { Score = CodeSearchRanker.Score(result, 0, context) });
                    if (results.Count >= limit)
                    {
                        return results;
                    }
                }
            }

            return results;
        }, cancellationToken).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ ICodeSearchHost: Feature Search

    IReadOnlyList<FeatureSearchItem> ICodeSearchHost.SearchFeatures(string query)
    {
        if (query.Length == 0)
        {
            return [];
        }

        var context = Workbench.Context;
        var found = new List<(FeatureSearchItem Item, int Score)>();
        foreach (var command in Workbench.Commands.Descriptors.Where(d => d.ShowInPalette && d.Enablement.Evaluate(context) && Workbench.Menus.IsVisibleInPalette(d.Id, context)))
        {
            if (FuzzyMatcher.Match(query, command.DisplayTitle) is { } match)
            {
                found.Add((new FeatureSearchItem(FeatureSearchKind.Command, command.DisplayTitle, command.Id)
                {
                    Detail = command.Category is { Length: > 0 } category
                        ? Localizer.Format("msg.codeSearch.featureCommandIn", "Command · {0}", category)
                        : Localizer.Get("msg.codeSearch.featureCommand", "Command"),
                    KeyLabel = Workbench.Keybindings.GetBinding(command.Id)?.ToLabel(Workbench.Platform),
                    Icon = Controls.CommandIcons.For(command.Id, id => Workbench.ToolWindows.Find(id)?.Icon)?.Kind ?? "play-outline",
                }, match.Score + 6));
            }
        }

        foreach (var setting in Workbench.Settings.Descriptors.Where(d => d.Title is not null))
        {
            if ((FuzzyMatcher.Match(query, setting.Title!) ?? FuzzyMatcher.Match(query, setting.Key)) is { } match)
            {
                found.Add((new FeatureSearchItem(FeatureSearchKind.Setting, setting.Title!, setting.Key)
                {
                    Detail = Localizer.Format("msg.codeSearch.featureSetting", "Setting · {0}", (setting.Category is { } category ? category + " · " : string.Empty) + setting.Key),
                    Icon = "gear",
                }, match.Score));
            }
        }

        foreach (var page in Workbench.Settings.Descriptors.Select(d => d.Category).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FuzzyMatcher.Match(query, page) is { } match)
            {
                found.Add((new FeatureSearchItem(FeatureSearchKind.OptionsPage, page, page)
                {
                    Detail = Localizer.Get("msg.codeSearch.featureOptionsPage", "Options page"),
                    Icon = "properties",
                }, match.Score + 3));
            }
        }

        return [.. found.OrderByDescending(f => f.Score).ThenBy(f => f.Item.Title.Length).Take(40).Select(f => f.Item)];
    }

    void ICodeSearchHost.RunFeature(FeatureSearchItem item)
    {
        switch (item.Kind)
        {
            case FeatureSearchKind.Command:
                Workbench.Commands.MarkRecent(item.Id);
                Run(item.Id);
                break;
            case FeatureSearchKind.Setting:
                OpenSettings();
                if (Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { } settings)
                {
                    settings.ViewModel.Search = item.Id;
                }

                break;
            case FeatureSearchKind.OptionsPage:
                OpenSettings();
                if (Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { } options)
                {
                    options.ViewModel.Search = string.Empty;
                    options.ViewModel.SelectedCategory = options.ViewModel.Categories.SelectMany(c => c.Children.Prepend(c))
                        .FirstOrDefault(c => string.Equals(c.Name, item.Id, StringComparison.OrdinalIgnoreCase)) ?? options.ViewModel.SelectedCategory;
                }

                break;
        }
    }

    // ------------------------------------------------------------------ ICodeSearchHost: opening, the window

    async Task ICodeSearchHost.OpenAsync(CodeSearchResult result)
    {
        RememberLocation();
        var position = new Position(result.Line, result.Column);
        if (result.Uri is { } uri && result.Path is null)
        {
            await OpenVirtualAsync(uri, result.Kind == CodeSearchKind.Type && result.IsExternal ? null : position).ConfigureAwait(true);
            return;
        }

        if (result.Path is not { } path)
        {
            return;
        }

        if (result.Kind == CodeSearchKind.File)
        {
            await OpenFileAsync(path).ConfigureAwait(true);
            return;
        }

        if (await OpenFileAsync(path, preview: false, position).ConfigureAwait(true) is { } document && result.Kind == CodeSearchKind.Text)
        {
            document.Navigate(position, result.Length); // the match selected, as Find Results does
        }
    }

    async Task ICodeSearchHost.SavePreviewAsync()
    {
        if (_previewModel is { } model)
        {
            await SaveAsync(model).ConfigureAwait(true);
            UpdatePreviewTitle();
        }
    }

    void ICodeSearchHost.OnClosed()
    {
        _ = ReleasePreviewAsync();
    }

    void ICodeSearchHost.OnOptionsChanged()
    {
        if (_applyingCodeSearchSettings || CodeSearch is null)
        {
            return;
        }

        var settings = Workbench.Settings;
        var scope = CodeSearch.Scope switch
        {
            CodeSearchScope.CurrentProject => "currentProject",
            CodeSearchScope.CurrentDocument => "currentDocument",
            _ => "entireSolution",
        };
        if (settings.GetString(CodeSearchScopeSetting, "entireSolution") != scope)
        {
            _ = settings.SetAsync(CodeSearchScopeSetting, scope, SettingScope.User, CancellationToken.None);
        }

        if (settings.GetBoolean(CodeSearchExternalSetting) != CodeSearch.IncludeExternal)
        {
            _ = settings.SetAsync(CodeSearchExternalSetting, CodeSearch.IncludeExternal, SettingScope.User, CancellationToken.None);
        }
    }

    void ICodeSearchHost.SetDocked(bool docked)
    {
        if (docked == CodeSearch.IsDocked)
        {
            return;
        }

        CodeSearch.IsDocked = docked;
        if (docked)
        {
            Layout.ShowToolWindow(CodeSearchToolWindowId, focus: true);
        }
        else
        {
            Layout.CloseToolWindow(CodeSearchToolWindowId);
        }

        CodeSearch.Open(CodeSearch.TabIndex);
    }

    void ICodeSearchHost.ReturnFocus() => ActiveEditor?.View?.FocusPrimary();

    // ------------------------------------------------------------------ the preview

    void ICodeSearchHost.ShowPreview(CodeSearchResult? result) => _ = ShowPreviewAsync(result);

    private async Task ShowPreviewAsync(CodeSearchResult? result)
    {
        var version = ++_previewVersion;
        if (result?.Path is not { } path || !CodeSearch.IsOpen || !File.Exists(path) || Views.ImageDocument.IsImage(path) || Views.HexDocument.LooksBinary(path))
        {
            await ReleasePreviewAsync().ConfigureAwait(true);
            return;
        }

        if (_previewModel is { } same && SolutionService.PathComparer.Equals(same.Path, path) && _previewKey == result.Key)
        {
            return; // the same result again (a refresh, a later batch): the caret stays where the user put it
        }

        if (_previewModel is not { } current || !SolutionService.PathComparer.Equals(current.Path, path))
        {
            CodeDocument model;
            try
            {
                model = await AcquireModelAsync(path).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ReleasePreviewAsync().ConfigureAwait(true);
                return;
            }

            if (version != _previewVersion || !CodeSearch.IsOpen)
            {
                ReleaseModel(model);
                return;
            }

            await ReleasePreviewAsync().ConfigureAwait(true);
            _previewModel = model;
            _previewDocument = CreateView(model, primary: false);
            _previewView = new CodeEditorView(_previewDocument, this, EditorFont);
            var area = _previewView.Editor.TextArea;
            area.Caret.PositionChanged += OnCodeSearchPreviewCaretChanged;
            area.SelectionChanged += OnCodeSearchPreviewCaretChanged;
            model.PropertyChanged += OnCodeSearchPreviewModelChanged;
            CodeSearch.PreviewContent = _previewView;
            UpdatePreviewTitle();
        }

        _previewKey = result.Key;
        RevealInPreview(result);
    }

    /// <summary>Scrolls the preview to the result and selects the match, without taking the focus from the box.</summary>
    private void RevealInPreview(CodeSearchResult result)
    {
        if (_previewView is not { } view || _previewModel is not { } model)
        {
            return;
        }

        var editor = view.Editor;
        var line = Math.Clamp(result.Line, 0, Math.Max(0, model.Text.LineCount - 1));
        var offset = model.OffsetAt(new Position(line, result.Column));
        var length = result.Kind is CodeSearchKind.File or CodeSearchKind.Line ? 0 : Math.Min(result.Length, model.Text.TextLength - offset);
        editor.TextArea.ClearSelection();
        editor.CaretOffset = offset;
        if (length > 0)
        {
            editor.Select(offset, length);
        }

        editor.ScrollTo(line + 1, result.Column + 1);
        Dispatcher.UIThread.Post(() =>
        {
            if (_previewView == view)
            {
                editor.ScrollTo(line + 1, result.Column + 1);
                editor.TextArea.TextView.MakeVisible(editor.TextArea.Caret.CalculateCaretRectangle());
            }
        }, DispatcherPriority.Background);
        UpdatePreviewStatus();
    }

    private void OnCodeSearchPreviewCaretChanged(object? sender, EventArgs e) => UpdatePreviewStatus();

    private void OnCodeSearchPreviewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.IsModified))
        {
            UpdatePreviewTitle();
        }
    }

    private void UpdatePreviewStatus()
    {
        if (_previewView is not { } view)
        {
            CodeSearch.PreviewStatus = null;
            return;
        }

        var caret = view.Editor.TextArea.Caret;
        var selected = view.Editor.SelectionLength;
        CodeSearch.PreviewStatus = selected > 0
            ? Localizer.Format("msg.codeSearch.previewStatusSelection", "Ln {0}, Col {1}, Sel {2}", caret.Line, caret.Column, selected)
            : Localizer.Format("msg.codeSearch.previewStatus", "Ln {0}, Col {1}", caret.Line, caret.Column);
    }

    private void UpdatePreviewTitle() => CodeSearch.PreviewTitle = _previewModel is { } model
        ? model.IsModified ? Localizer.Format("msg.codeSearch.previewModified", "{0} (modified, Ctrl+S saves)", model.Path) : model.Path
        : null;

    /// <summary>
    /// Lets go of the preview's document. An edit made in the preview that isn't saved opens the document (as editing a
    /// preview tab keeps it), so it can be saved or undone there.
    /// </summary>
    private async Task ReleasePreviewAsync()
    {
        if (_previewModel is not { } model)
        {
            CodeSearch.PreviewContent = null;
            CodeSearch.PreviewStatus = null;
            CodeSearch.PreviewTitle = null;
            return;
        }

        var view = _previewView;
        _previewKey = null;
        _previewModel = null;
        _previewDocument = null;
        _previewView = null;
        CodeSearch.PreviewContent = null;
        CodeSearch.PreviewStatus = null;
        CodeSearch.PreviewTitle = null;
        model.PropertyChanged -= OnCodeSearchPreviewModelChanged;
        if (view is not null)
        {
            view.Editor.TextArea.Caret.PositionChanged -= OnCodeSearchPreviewCaretChanged;
            view.Editor.TextArea.SelectionChanged -= OnCodeSearchPreviewCaretChanged;
            view.Close();
        }

        if (model.IsModified && !HasEditorFor(model.Path))
        {
            await OpenFileAsync(model.Path, preview: false, at: null, initialContent: null, textEditor: true).ConfigureAwait(true);
        }

        ReleaseModel(model);
    }

    // ------------------------------------------------------------------ the index

    /// <summary>A solution opens: its saved index is read in the background (before the language host has loaded anything).</summary>
    private void OpenCodeSearchIndex(SolutionStateKey key)
    {
        _ = CloseCodeSearchIndex(); // the previous solution's write is queued in the solution state
        _indexKey = key;
        _ = LoadCodeSearchIndexAsync(key);
    }

    private async Task LoadCodeSearchIndexAsync(SolutionStateKey key)
    {
        try
        {
            if (await CodeSearchIndex.LoadAsync(SolutionState, key, CancellationToken.None).ConfigureAwait(false))
            {
                Workbench.Log.AppendLine($"[code search] Read the index of {Path.GetFileName(key.Path)}: {CodeSearchIndex.SymbolCount} symbols in {CodeSearchIndex.DocumentCount} documents, {CodeSearchIndex.Files.Count} files.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
        {
            Workbench.Log.AppendLine($"[code search] Ignoring the saved index: {ex.Message}");
        }
    }

    /// <summary>
    /// The solution closes: the index is saved (when it changed) and forgotten. The returned task is the write, which
    /// is also queued in the solution's state (its flush waits for it).
    /// </summary>
    private Task CloseCodeSearchIndex()
    {
        _indexRefresh?.Cancel();
        _indexTimer?.Stop();
        _indexPending.Clear();
        var saving = SaveCodeSearchIndex(onlyIfChanged: true);
        _indexKey = null;
        _projectOfFile = null;
        CodeSearchIndex.Clear();
        _ = ReleasePreviewAsync();
        return saving;
    }

    /// <summary>
    /// Queues the index's write in the solution's state now (serialized on this thread, written in the background) and
    /// returns it; a failure is logged, never thrown.
    /// </summary>
    private Task SaveCodeSearchIndex(bool onlyIfChanged)
    {
        if (_indexKey is not { } key || onlyIfChanged && !_indexUnsaved || !CodeSearchIndex.IsComplete)
        {
            return Task.CompletedTask;
        }

        _indexUnsaved = false;
        if (_codeSearchFiles is { } files)
        {
            CodeSearchIndex.SetFiles(files.Files);
        }

        Task write;
        try
        {
            write = CodeSearchIndex.SaveAsync(SolutionState, key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            write = Task.FromException(ex);
        }

        return LogFailureAsync(write, key);

        async Task LogFailureAsync(Task pending, SolutionStateKey solution)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Workbench.Log.AppendLine($"[code search] Could not save the index of {Path.GetFileName(solution.Path)}: {ex.Message}");
            }
        }
    }

    /// <summary>How long a whole-solution index is current while the C# service only reports ready after edits.</summary>
    internal static readonly TimeSpan FullIndexInterval = TimeSpan.FromMinutes(3);

    /// <summary>The C# service reported loading (projects changed, a restart) since the last whole-solution index.</summary>
    private bool _indexAfterLoading = true;

    private void OnCodeSearchLanguageStatus(string languageId, WorkspaceStatusParams status)
    {
        // The whole solution once its projects are in the C# host (the index is from syntax: no need to wait for the
        // background pass), and again when it reports ready after loading (a restart, a reload). It reports ready after
        // every edit's background pass too: the edited documents are indexed on their own (QueueIndexUpdate), and asking
        // for every document again then cost every document's declarations from the host and 0.2–0.4 s of the UI thread
        // (144,000 symbols) at each pause in typing; files changed outside the editor are caught by one every few minutes.
        if (languageId == CSharpLanguageId)
        {
            if (status.State != WorkspaceStates.Ready)
            {
                _indexAfterLoading = true;
            }

            if (_indexKey is not null && !Languages.IsLoading
                && (!CodeSearchIndex.IsComplete
                    || status.State == WorkspaceStates.Ready && (_indexAfterLoading || DateTime.UtcNow - CodeSearchIndex.CompletedAt > FullIndexInterval)))
            {
                _indexAfterLoading = false;
                _ = RefreshCodeSearchIndexAsync();
            }
        }

        RefreshCodeSearchSoon();
    }

    /// <summary>Asks the C# host for the whole solution's declarations, replaces the index and saves it.</summary>
    public async Task RefreshCodeSearchIndexAsync()
    {
        if (Languages.RunningClient(CSharpLanguageId) is not { } client || _indexKey is not { } key)
        {
            return;
        }

        _indexRefresh?.Cancel();
        _indexRefresh?.Dispose();
        var cts = _indexRefresh = new CancellationTokenSource();
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var index = await client.SymbolIndexAsync(null, cts.Token).ConfigureAwait(true);
            var files = await CodeSearchFilesAsync().ConfigureAwait(true);
            if (cts.IsCancellationRequested || _indexKey != key)
            {
                return;
            }

            // Built off the UI thread. Then it becomes complete, unsaved and queued for writing with no await between:
            // a solution (or the window) closing the moment it is complete must never find it complete but not yet
            // marked unsaved; one that closed while it was built took its index with it (ReplaceAsync puts nothing back).
            if (!await CodeSearchIndex.ReplaceAsync(index.Documents).ConfigureAwait(true) || cts.IsCancellationRequested || _indexKey != key)
            {
                return;
            }

            CodeSearchIndex.SetFiles(files);
            _indexUnsaved = true;
            _ = SaveCodeSearchIndex(onlyIfChanged: false);
            Workbench.Log.AppendLine($"[code search] Indexed {CodeSearchIndex.SymbolCount} symbols in {CodeSearchIndex.DocumentCount} documents in {started.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException or ObjectDisposedException)
        {
            // A newer refresh, a restarting host: the next status brings another.
        }
    }

    /// <summary>An edit: the document's declarations are indexed again a moment after typing stops.</summary>
    private void QueueIndexUpdate(string path)
    {
        if (_indexKey is null || Languages.LanguageIdOf(path) is not (CSharpLanguageId or VisualBasicLanguageId))
        {
            return;
        }

        _indexPending.Add(path);
        if (_indexTimer is null)
        {
            _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _indexTimer.Tick += (_, _) =>
            {
                _indexTimer.Stop();
                _ = UpdateCodeSearchIndexAsync();
            };
        }

        StartTimer(_indexTimer);
    }

    private async Task UpdateCodeSearchIndexAsync()
    {
        if (_indexPending.Count == 0 || Languages.RunningClient(CSharpLanguageId) is not { } client)
        {
            return;
        }

        var paths = _indexPending.ToArray();
        _indexPending.Clear();
        try
        {
            var key = _indexKey;
            var index = await client.SymbolIndexAsync([.. paths.Select(DocumentUris.FromPath)], CancellationToken.None).ConfigureAwait(true);
            var gone = paths.Where(p => !File.Exists(p) && index.Documents.All(d => DocumentUris.ToPath(d.Uri) is not { } indexed || !SolutionService.PathComparer.Equals(indexed, p))).ToList();
            if (await CodeSearchIndex.UpdateAsync(index.Documents, gone).ConfigureAwait(true) && _indexKey == key)
            {
                _indexUnsaved = true;
            }
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException or ObjectDisposedException)
        {
            // The next refresh catches up.
        }
    }

    /// <summary>The index or the projects changed: an open window's results refresh (once for a burst of changes).</summary>
    private void RefreshCodeSearchSoon()
    {
        if (CodeSearch is not { IsOpen: true, IsCodeTab: true })
        {
            return;
        }

        if (_codeSearchRefresh is null)
        {
            _codeSearchRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _codeSearchRefresh.Tick += (_, _) =>
            {
                _codeSearchRefresh.Stop();
                CodeSearch.Refresh();
            };
        }

        if (!_codeSearchRefresh.IsEnabled)
        {
            StartTimer(_codeSearchRefresh);
        }
    }
}
