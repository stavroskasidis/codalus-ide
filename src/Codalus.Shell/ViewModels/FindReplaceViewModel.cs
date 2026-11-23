using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.ViewModels;

/// <summary>Where Find in Files looks: one of these, or a folder's path.</summary>
public static class FindScopes
{
    public const string EntireSolution = "Entire solution";

    /// <summary>Folder mode's whole folder (the classic IDE's Open Folder names it so); the same search as <see cref="EntireSolution"/>.</summary>
    public const string EntireWorkspace = "Entire Workspace";
    public const string CurrentProject = "Current project";
    public const string CurrentDocument = "Current document";
    public const string OpenDocuments = "All open documents";

    private const string CurrentDirectoryPrefix = "Current directory (";

    /// <summary>A solution's scopes, and those without one.</summary>
    public static IReadOnlyList<string> All { get; } = [EntireSolution, CurrentProject, CurrentDocument, OpenDocuments];

    /// <summary>
    /// Folder mode's scopes, in the classic IDE's order: the folder, the current project, the active document's folder
    /// (<paramref name="currentDirectory"/>, when there is one), the open documents and the current document.
    /// </summary>
    public static IReadOnlyList<string> ForFolder(string? currentDirectory) =>
        currentDirectory is null
            ? [EntireWorkspace, CurrentProject, OpenDocuments, CurrentDocument]
            : [EntireWorkspace, CurrentProject, CurrentDirectory(currentDirectory), OpenDocuments, CurrentDocument];

    /// <summary>The scope of a folder and its subfolders, named after it: <c>Current directory (C:\src\App)</c>.</summary>
    public static string CurrentDirectory(string folder) => CurrentDirectoryPrefix + folder + ")";

    /// <summary>Whether <paramref name="lookIn"/> is a <see cref="CurrentDirectory"/> scope, and its folder.</summary>
    public static bool IsCurrentDirectory(string lookIn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? folder)
    {
        folder = lookIn.StartsWith(CurrentDirectoryPrefix, StringComparison.Ordinal) && lookIn.EndsWith(')') && lookIn.Length > CurrentDirectoryPrefix.Length + 1
            ? lookIn[CurrentDirectoryPrefix.Length..^1]
            : null;
        return folder is not null;
    }

    /// <summary>A scope (not a folder that was browsed to).</summary>
    public static bool IsScope(string lookIn) => lookIn == EntireWorkspace || All.Contains(lookIn, StringComparer.Ordinal) || IsCurrentDirectory(lookIn, out _);

    /// <summary>
    /// What <paramref name="lookIn"/> becomes among <paramref name="scopes"/>: itself when offered or a folder; the
    /// entire solution and the entire workspace each other; another folder's current directory this one's.
    /// </summary>
    public static string Equivalent(string lookIn, IReadOnlyList<string> scopes)
    {
        if (!IsScope(lookIn) || scopes.Contains(lookIn, StringComparer.Ordinal) || scopes.Count == 0)
        {
            return lookIn;
        }

        return IsCurrentDirectory(lookIn, out _) && scopes.FirstOrDefault(s => IsCurrentDirectory(s, out _)) is { } current ? current : scopes[0];
    }
}

/// <summary>One Find in Files or Replace in Files search: what to find, how to match it, and where.</summary>
public sealed record FindInFilesOptions(
    string Query,
    bool MatchCase = false,
    bool WholeWord = false,
    bool UseRegex = false,
    string LookIn = FindScopes.EntireSolution,
    string FileTypes = FindInFilesOptions.DefaultFileTypes)
{
    /// <summary>Every file but those under <c>bin</c>, <c>obj</c> and dot folders (<c>.git</c>, <c>.vs</c>…).</summary>
    public const string DefaultFileTypes = @"!*\bin\*;!*\obj\*;!*\.*\*";

    /// <summary>The pattern a line is matched with; throws <see cref="ArgumentException"/> for a bad regular expression.</summary>
    public Regex CreateRegex()
    {
        var pattern = UseRegex ? Query : Regex.Escape(Query);
        if (WholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        return new Regex(pattern, (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The text a match becomes: with a regular expression, <c>$1</c> and <c>${name}</c> are its groups.</summary>
    public string Replacement(Match match, string replaceWith) => UseRegex ? match.Result(replaceWith) : replaceWith;

    /// <summary>How the Find Results' title describes the search: <c>Find all "x", Match case, Entire solution, "*.cs"</c>.</summary>
    public string Describe(string verb)
    {
        var parts = new List<string> { $"{verb} \"{Query}\"" };
        if (MatchCase)
        {
            parts.Add("Match case");
        }

        if (WholeWord)
        {
            parts.Add("Whole word");
        }

        if (UseRegex)
        {
            parts.Add("Regular expressions");
        }

        parts.Add(LookIn);
        if (FileTypes.Length > 0)
        {
            parts.Add($"\"{FileTypes}\"");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// The File types box: patterns separated by <c>;</c> or <c>,</c>. A pattern with a path separator matches the path
/// below the search's root (<c>*\bin\*</c>), one without matches the file name (<c>*.cs</c>); <c>!</c> excludes. With
/// no including pattern every file is included.
/// </summary>
public sealed class FileTypeFilter
{
    private readonly List<(Regex Pattern, bool OnPath)> _include = [];
    private readonly List<(Regex Pattern, bool OnPath)> _exclude = [];
    private readonly List<Regex> _excludedFolders = [];

    public FileTypeFilter(string patterns)
    {
        foreach (var raw in patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exclude = raw.StartsWith('!');
            var body = (exclude ? raw[1..] : raw).Trim().Replace('\\', '/');
            if (body.Length == 0)
            {
                continue;
            }

            (exclude ? _exclude : _include).Add((Glob(body), body.Contains('/', StringComparison.Ordinal)));

            // "!<folder>/*" leaves out everything in the folders it matches: a search needn't go into them.
            if (exclude && body.Length > 2 && body.EndsWith("/*", StringComparison.Ordinal) && body[..^2].Contains('/', StringComparison.Ordinal))
            {
                _excludedFolders.Add(Glob(body[..^2]));
            }
        }
    }

    /// <param name="path">A file's full path.</param>
    /// <param name="root">The folder path patterns are relative to (the solution's), or null for the full path.</param>
    public bool Matches(string path, string? root)
    {
        var normalized = Relative(path, root);
        var name = Path.GetFileName(path);
        bool Hit((Regex Pattern, bool OnPath) p) => p.Pattern.IsMatch(p.OnPath ? normalized : name);
        return (_include.Count == 0 || _include.Any(Hit)) && !_exclude.Any(Hit);
    }

    /// <summary>
    /// Whether every file in <paramref name="folder"/> (at any depth) is left out by a <c>!…\*</c> pattern, so a walk of
    /// the disk needn't go into it (<c>bin</c>, <c>obj</c> and dot folders with the default patterns).
    /// </summary>
    public bool ExcludesFolder(string folder, string? root)
    {
        var normalized = Relative(Path.TrimEndingDirectorySeparator(folder), root);
        return _excludedFolders.Exists(p => p.IsMatch(normalized));
    }

    private static Regex Glob(string body) => new(
        "^" + Regex.Escape(body).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The path with <c>/</c> separators, below <paramref name="root"/> (as <c>/src/A.cs</c>) when it's in it.</summary>
    private static string Relative(string path, string? root)
    {
        var normalized = path.Replace('\\', '/');
        if (root is { Length: > 0 })
        {
            var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = "/" + normalized[prefix.Length..];
            }
        }

        return normalized;
    }
}

/// <summary>
/// Find and Replace (Ctrl+Shift+F, Ctrl+Shift+H): the floating window over the documents with the Find in Files and
/// Replace in Files tabs. Find All and Replace All list what they found in Find Results and close it; Find Next,
/// Find Previous, Skip File and Replace walk the results and leave it open, as the classic IDE does.
/// </summary>
public sealed class FindReplaceViewModel : ObservableObject
{
    private const int MaxHistory = 20;

    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<FindInFilesOptions, string, Task> _replaceAll;
    private readonly Func<FindInFilesOptions, FindResultRow, string, Task<FindResultRow?>> _replaceOne;
    private bool _isOpen;
    private bool _isReplace;
    private string _query = string.Empty;
    private string _replaceWith = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _useRegex;
    private string _lookIn = FindScopes.EntireSolution;
    private string _fileTypes = FindInFilesOptions.DefaultFileTypes;
    private bool _appendResults;
    private string _error = string.Empty;

    /// <param name="results">The Find Results window the searches fill.</param>
    /// <param name="pickFolder">Asks for a folder to look in.</param>
    /// <param name="replaceAll">Replaces every match of the options with the text (after the user confirms).</param>
    /// <param name="replaceOne">
    /// Replaces a result's match with the text: the row after it (the line's next match, or the line marked replaced),
    /// or null when the file no longer matches there.
    /// </param>
    public FindReplaceViewModel(
        FindResultsViewModel results,
        Func<Task<string?>> pickFolder,
        Func<FindInFilesOptions, string, Task> replaceAll,
        Func<FindInFilesOptions, FindResultRow, string, Task<FindResultRow?>> replaceOne)
    {
        Results = results;
        _pickFolder = pickFolder;
        _replaceAll = replaceAll;
        _replaceOne = replaceOne;
        FindAllCommand = new RelayCommand(() => _ = FindAllAsync());
        FindNextCommand = new RelayCommand(() => _ = FindNextAsync(backwards: false));
        FindPreviousCommand = new RelayCommand(() => _ = FindNextAsync(backwards: true));
        SkipFileCommand = new RelayCommand(() => _ = SkipFileAsync());
        ReplaceCommand = new RelayCommand(() => _ = ReplaceAsync());
        ReplaceAllCommand = new RelayCommand(() => _ = ReplaceAllAsync());
        BrowseCommand = new RelayCommand(() => _ = BrowseAsync());
        CloseCommand = new RelayCommand(Close);
        results.ModifyFindRequested += (_, page) => ModifyFind(page);
    }

    public FindResultsViewModel Results { get; }

    /// <summary>Searches made, newest first (the Find what box's drop-down).</summary>
    public ObservableCollection<string> QueryHistory { get; } = [];

    /// <summary>Replacements made, newest first.</summary>
    public ObservableCollection<string> ReplaceHistory { get; } = [];

    /// <summary>The Look in drop-down: the scopes, then folders that were browsed to.</summary>
    public ObservableCollection<string> LookInOptions { get; } = [.. FindScopes.All];

    /// <summary>
    /// The scopes the workspace offers, asked each time the window shows: a solution's (<see cref="FindScopes.All"/>), or
    /// folder mode's (<see cref="FindScopes.ForFolder"/>).
    /// </summary>
    public Func<IReadOnlyList<string>> Scopes { get; set; } = () => FindScopes.All;

    public ObservableCollection<string> FileTypeOptions { get; } =
        [FindInFilesOptions.DefaultFileTypes, "*.cs", "*.cs;*.razor;*.cshtml", "*.axaml;*.xaml", "*.csproj;*.props;*.targets", "*.json;*.xml;*.config", string.Empty];

    public RelayCommand FindAllCommand { get; }

    public RelayCommand FindNextCommand { get; }

    public RelayCommand FindPreviousCommand { get; }

    public RelayCommand SkipFileCommand { get; }

    public RelayCommand ReplaceCommand { get; }

    public RelayCommand ReplaceAllCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand CloseCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    /// <summary>True on the Replace in Files tab.</summary>
    public bool IsReplace
    {
        get => _isReplace;
        set
        {
            if (SetField(ref _isReplace, value))
            {
                OnPropertyChanged(nameof(TabIndex));
                OnPropertyChanged(nameof(IsFind));
            }
        }
    }

    public bool IsFind => !_isReplace;

    /// <summary>The selected tab: 0 Find in Files, 1 Replace in Files.</summary>
    public int TabIndex
    {
        get => _isReplace ? 1 : 0;
        set => IsReplace = value == 1;
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value ?? string.Empty))
            {
                Error = string.Empty;
            }
        }
    }

    public string ReplaceWith
    {
        get => _replaceWith;
        set => SetField(ref _replaceWith, value ?? string.Empty);
    }

    public bool MatchCase
    {
        get => _matchCase;
        set => SetField(ref _matchCase, value);
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set => SetField(ref _wholeWord, value);
    }

    public bool UseRegex
    {
        get => _useRegex;
        set
        {
            if (SetField(ref _useRegex, value))
            {
                Error = string.Empty;
            }
        }
    }

    /// <summary>One of <see cref="FindScopes"/>, or a folder's path.</summary>
    public string LookIn
    {
        get => _lookIn;
        set => SetField(ref _lookIn, string.IsNullOrWhiteSpace(value) ? FindScopes.EntireSolution : value);
    }

    /// <summary>The <see cref="FileTypeFilter"/> patterns; empty: every file.</summary>
    public string FileTypes
    {
        get => _fileTypes;
        set => SetField(ref _fileTypes, value ?? string.Empty);
    }

    /// <summary>Find All adds its results under the earlier ones instead of replacing them.</summary>
    public bool AppendResults
    {
        get => _appendResults;
        set => SetField(ref _appendResults, value);
    }

    /// <summary>Why the last action didn't run (an invalid regular expression), or empty.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>Raised when the Find what box should get focus.</summary>
    public event EventHandler? FocusRequested;

    public FindInFilesOptions Options => new(_query, _matchCase, _wholeWord, _useRegex, _lookIn, _fileTypes);

    /// <summary>Shows the window on a tab; <paramref name="query"/> (the editor's selection) fills Find what.</summary>
    public void Open(string? query, bool replace)
    {
        if (!string.IsNullOrEmpty(query))
        {
            Query = query;
        }

        RefreshScopes();
        Show(replace);
    }

    /// <summary>
    /// Look in's scopes as the workspace has them now (folder mode has no solution, and the current directory is the
    /// active document's), with the folders that were browsed to after them; Look in becomes its equivalent among them.
    /// </summary>
    public void RefreshScopes()
    {
        var scopes = Scopes();
        var lookIn = FindScopes.Equivalent(_lookIn, scopes);
        List<string> options = [.. scopes, .. LookInOptions.Where(o => !FindScopes.IsScope(o))];
        if (!options.SequenceEqual(LookInOptions, StringComparer.Ordinal))
        {
            LookInOptions.Clear();
            foreach (var option in options)
            {
                LookInOptions.Add(option);
            }
        }

        LookIn = lookIn;
    }

    private void Show(bool replace)
    {
        IsReplace = replace;
        Error = string.Empty;
        IsOpen = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Close() => IsOpen = false;

    /// <summary>Modify Find: shows the window with the search behind a Find Results page (on Replace for a Replace All's).</summary>
    public void ModifyFind(FindResultsPage page)
    {
        if (page.Options is not { } options)
        {
            Open(null, IsReplace);
            return;
        }

        RefreshScopes();
        Query = options.Query;
        MatchCase = options.MatchCase;
        WholeWord = options.WholeWord;
        UseRegex = options.UseRegex;
        // The entire solution and the entire workspace are one search; another scope (an earlier document's current
        // directory) stays the search's own.
        var lookIn = options.LookIn is FindScopes.EntireSolution or FindScopes.EntireWorkspace ? FindScopes.Equivalent(options.LookIn, LookInOptions) : options.LookIn;
        if (!LookInOptions.Contains(lookIn, StringComparer.Ordinal))
        {
            LookInOptions.Add(lookIn);
        }

        LookIn = lookIn;
        FileTypes = options.FileTypes;
        if (page.ReplaceWith is { } replaceWith)
        {
            ReplaceWith = replaceWith;
        }

        Show(page.ReplaceWith is not null);
    }

    /// <summary>Find All: lists every match in Find Results and closes the window.</summary>
    public async Task FindAllAsync()
    {
        if (!Validate())
        {
            return;
        }

        Remember(QueryHistory, _query);
        IsOpen = false;
        await Results.SearchAsync(Options, _appendResults).ConfigureAwait(true);
    }

    /// <summary>Find Next and Find Previous: the next match in the results, searching first when the options changed.</summary>
    public async Task FindNextAsync(bool backwards)
    {
        if (await EnsureResultsAsync().ConfigureAwait(true))
        {
            await Results.GoToAsync(backwards ? -1 : 1).ConfigureAwait(true);
        }
    }

    /// <summary>Skip File: the first match in the next file.</summary>
    public async Task SkipFileAsync()
    {
        if (await EnsureResultsAsync().ConfigureAwait(true))
        {
            await Results.GoToNextFileAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Replace: replaces the current match (going to the first one when none is current) and moves on.</summary>
    public async Task ReplaceAsync()
    {
        if (!await EnsureResultsAsync().ConfigureAwait(true))
        {
            return;
        }

        Remember(ReplaceHistory, _replaceWith);
        if (Results.SelectedRow is not { IsHeader: false } row)
        {
            await Results.GoToAsync(1).ConfigureAwait(true);
            return;
        }

        var rest = await _replaceOne(Options, row, _replaceWith).ConfigureAwait(true);
        if (rest is { IsReplaced: false })
        {
            await Results.ReplaceRowAsync(row, rest, open: true).ConfigureAwait(true); // the line's next match
            return;
        }

        if (rest is not null)
        {
            await Results.ReplaceRowAsync(row, rest, open: false).ConfigureAwait(true);
        }

        await Results.GoToAsync(1).ConfigureAwait(true);
    }

    /// <summary>Replace All: every match, in every file of the scope, after the user confirms.</summary>
    public async Task ReplaceAllAsync()
    {
        if (!Validate())
        {
            return;
        }

        Remember(QueryHistory, _query);
        Remember(ReplaceHistory, _replaceWith);
        IsOpen = false;
        await _replaceAll(Options, _replaceWith).ConfigureAwait(true);
    }

    public async Task BrowseAsync()
    {
        if (await _pickFolder().ConfigureAwait(true) is { } folder)
        {
            if (!LookInOptions.Contains(folder, StringComparer.Ordinal))
            {
                LookInOptions.Add(folder);
            }

            LookIn = folder;
        }
    }

    private async Task<bool> EnsureResultsAsync()
    {
        if (!Validate())
        {
            return false;
        }

        if (Results.LastSearch != Options)
        {
            Remember(QueryHistory, _query);
            await Results.SearchAsync(Options, append: false).ConfigureAwait(true);
        }

        return true;
    }

    private bool Validate()
    {
        if (_query.Length == 0)
        {
            Error = "Type the text to find.";
            return false;
        }

        try
        {
            _ = Options.CreateRegex();
        }
        catch (ArgumentException ex)
        {
            Error = "Invalid regular expression: " + ex.Message;
            return false;
        }

        Error = string.Empty;
        return true;
    }

    private static void Remember(ObservableCollection<string> history, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var index = history.IndexOf(value);
        if (index == 0)
        {
            return;
        }

        if (index > 0)
        {
            history.RemoveAt(index);
        }

        history.Insert(0, value);
        while (history.Count > MaxHistory)
        {
            history.RemoveAt(history.Count - 1);
        }
    }
}
