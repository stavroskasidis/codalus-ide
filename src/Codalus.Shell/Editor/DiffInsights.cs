using Avalonia.Threading;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Editor;

/// <summary>
/// What the language service says about a diff's working-tree side (#86, the maintainer's report: no Quick Info and no
/// CodeLens in a diff): the open document's symbols (the navigation bar), its CodeLens rows (the service's counts, then
/// the Git history and the test outcomes, as the editor shows them) and Quick Info. The diff holds the document while it
/// shows it, so this works whether an editor shows the file or not. Lenses are asked for once per text and resolved
/// near what the diff shows. UI thread only.
/// </summary>
public sealed class DiffInsights : IDisposable
{
    private static readonly TimeSpan s_refreshDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan s_resolveDelay = TimeSpan.FromMilliseconds(150);

    private readonly IEditorServices _services;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = s_refreshDelay };
    private readonly DispatcherTimer _resolveTimer = new() { Interval = s_resolveDelay };
    private Dictionary<int, List<Lens>> _lenses = [];
    private Dictionary<int, string> _gitLenses = [];
    private Dictionary<int, string> _testLenses = [];
    private GitLensIndex? _blame;
    private int _version = -1;
    private (int First, int Last) _shown = (1, 80);
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _resolveCts;
    private int _failures;
    private bool _disposed;

    public DiffInsights(CodeDocument model, IEditorServices services)
    {
        Model = model;
        _services = services;
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshAsync();
        };
        _resolveTimer.Tick += (_, _) =>
        {
            _resolveTimer.Stop();
            _ = ResolveAsync();
        };
        model.ContentChanged += OnContentChanged;
        model.Reloaded += OnReloaded;
        services.Options.PropertyChanged += OnOptionsChanged;
        services.Tests.Changed += OnTestsChanged;
        UpdateTestLenses();
        _ = RefreshAsync();
    }

    /// <summary>The document of the working-tree side.</summary>
    public CodeDocument Model { get; }

    /// <summary>The document's symbols, nested (the navigation bar); empty until the service lists them.</summary>
    public IReadOnlyList<DocumentSymbol> Symbols { get; private set; } = [];

    /// <summary>Raised when <see cref="Symbols"/> changed.</summary>
    public event EventHandler? SymbolsChanged;

    /// <summary>Raised when the CodeLens rows changed: their lines, or their titles.</summary>
    public event EventHandler? LensesChanged;

    /// <summary>Whether CodeLens shows for the document's language (the user's setting).</summary>
    public bool ShowsLenses => Options.CodeLens;

    private EditorOptions Options => _services.Options.For(Model.LanguageId);

    private LanguageHostClient? Client => _services.Client(Model);

    /// <summary>
    /// The CodeLens row above a one-based line of the document: its titles joined as the editor joins them, empty while
    /// its lenses wait for their counts (the row keeps its room), or null for none.
    /// </summary>
    public string? LensRow(int line)
    {
        if (!Options.CodeLens)
        {
            return null;
        }

        var lenses = _lenses.GetValueOrDefault(line);
        var titles = new List<string>();
        if (lenses is not null)
        {
            titles.AddRange(lenses.Where(l => l.Title is { } title && title != "-").Select(l => l.Title!));
        }

        if (Options.TestCodeLens && _testLenses.TryGetValue(line, out var test))
        {
            titles.Add(test);
        }

        if (Options.GitCodeLens && _gitLenses.TryGetValue(line, out var git))
        {
            titles.Add(git);
        }

        return titles.Count > 0 ? string.Join(" | ", titles) : lenses is { Count: > 0 } ? string.Empty : null;
    }

    /// <summary>The lines with a CodeLens row (tests).</summary>
    internal IReadOnlyCollection<int> LensLines => [.. _lenses.Keys.Concat(_testLenses.Keys).Distinct().Order()];

    /// <summary>The innermost symbols containing a position, outermost first.</summary>
    public IReadOnlyList<DocumentSymbol> SymbolPath(Position position)
    {
        var path = new List<DocumentSymbol>();
        var level = Symbols;
        while (level.FirstOrDefault(s => Contains(s.Range, position)) is { } symbol)
        {
            path.Add(symbol);
            level = symbol.Children ?? [];
        }

        return path;
    }

    private static bool Contains(LanguageServices.Protocol.Range range, Position position) =>
        (position.Line > range.Start.Line || (position.Line == range.Start.Line && position.Character >= range.Start.Character))
        && (position.Line < range.End.Line || (position.Line == range.End.Line && position.Character <= range.End.Character));

    /// <summary>The lines the diff shows now (one-based): their lenses are resolved first.</summary>
    public void Show(int first, int last)
    {
        if ((first, last) == _shown)
        {
            return;
        }

        _shown = (first, last);
        _resolveTimer.Stop();
        _resolveTimer.Start();
    }

    /// <summary>Asks for everything again: the service (re)started or finished loading projects.</summary>
    public void Refresh()
    {
        _version = -1;
        _failures = 0;
        _refreshTimer.Stop();
        _ = RefreshAsync();
    }

    /// <summary>Quick Info at a position of the document, or null.</summary>
    public async Task<Hover?> HoverAsync(Position position, CancellationToken cancellationToken)
    {
        if (_disposed || Client is not { Capabilities.HoverProvider: true } client)
        {
            return null;
        }

        try
        {
            return await client.HoverAsync(Model.Uri, position, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the first lens of a one-based line, as a click on the editor's row does: resolved first if it isn't yet, then
    /// its command at its position (<paramref name="run"/>: the shell opens the file there and runs it).
    /// </summary>
    public async Task RunLensAsync(int line, Func<Position, string, Task> run)
    {
        if (_lenses.GetValueOrDefault(line) is not { Count: > 0 } lenses || Client is not { } client)
        {
            return;
        }

        var lens = lenses.FirstOrDefault(l => l.Title is { } title && title != "-") ?? lenses[0];
        if (lens.Command is null)
        {
            try
            {
                if ((await client.ResolveCodeLensAsync(lens.Value, CancellationToken.None).ConfigureAwait(true)).Command is { } resolved)
                {
                    lens.Resolve(resolved);
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                return;
            }
        }

        if (_disposed || lens.Command is not { Arguments: [_, var at, ..] } command || string.IsNullOrEmpty(command.CommandId)
            || System.Text.Json.JsonSerializer.Deserialize(at, ProtocolJsonContext.Default.Position) is not { } position)
        {
            return;
        }

        await run(position, command.CommandId).ConfigureAwait(true);
    }

    private void OnContentChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        if (!_disposed)
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }
    });

    private void OnReloaded(object? sender, EventArgs e) => Ui.Post(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    private void OnOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateTestLenses();
        Refresh();
    }

    private void OnTestsChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        if (!_disposed && UpdateTestLenses())
        {
            LensesChanged?.Invoke(this, EventArgs.Empty);
        }
    });

    /// <summary>The symbols and the lenses for the current text, then the Git history of the members with a lens.</summary>
    private async Task RefreshAsync()
    {
        if (_disposed || Client is not { } client)
        {
            return;
        }

        var token = Restart(ref _refreshCts).Token;
        var version = Model.Version;
        if (version == _version)
        {
            return;
        }

        try
        {
            if (client.Capabilities?.DocumentSymbolProvider == true)
            {
                var symbols = await client.DocumentSymbolsAsync(Model.Uri, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed || Model.Version != version)
                {
                    return;
                }

                Symbols = symbols;
                SymbolsChanged?.Invoke(this, EventArgs.Empty);
            }

            if (Options.CodeLens && client.Capabilities?.CodeLensProvider is not null)
            {
                var listed = await client.CodeLensesAsync(Model.Uri, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed || Model.Version != version)
                {
                    return;
                }

                // The titles of the last text's lenses stay until the new ones' counts arrive, so the rows don't flicker.
                var previous = _lenses.SelectMany(p => p.Value.Where(l => l.Title is not null).Select(l => (Key: (p.Key, l.Key), l.Title))).DistinctBy(p => p.Key).ToDictionary(p => p.Key, p => p.Title);
                var lenses = new Dictionary<int, List<Lens>>();
                foreach (var value in listed)
                {
                    var line = value.Range.Start.Line + 1;
                    var lens = new Lens(value);
                    if (value.Command is { } command)
                    {
                        lens.Resolve(command);
                    }
                    else if (previous.TryGetValue((line, lens.Key), out var title))
                    {
                        lens.Title = title;
                    }

                    (lenses.TryGetValue(line, out var list) ? list : lenses[line] = []).Add(lens);
                }

                _lenses = lenses;
            }
            else
            {
                _lenses = [];
            }

            _version = version;
            _failures = 0;
            UpdateGitLenses();
            LensesChanged?.Invoke(this, EventArgs.Empty);
            await ResolveAsync().ConfigureAwait(true);
            await RefreshBlameAsync(version, token).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            RetryAfterFailure(ex);
        }
    }

    /// <summary>Resolves the lenses near the lines the diff shows.</summary>
    private async Task ResolveAsync()
    {
        if (_disposed || Client is not { Capabilities.CodeLensProvider.ResolveProvider: true } client)
        {
            return;
        }

        var token = Restart(ref _resolveCts).Token;
        var version = Model.Version;
        var (first, last) = (_shown.First - 5, _shown.Last + 5);
        try
        {
            foreach (var lens in _lenses.Where(p => p.Key >= first && p.Key <= last).SelectMany(p => p.Value).Where(l => l.Command is null).ToList())
            {
                var resolved = await client.ResolveCodeLensAsync(lens.Value, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed || Model.Version != version)
                {
                    return;
                }

                if (resolved.Command is { } command)
                {
                    lens.Resolve(command);
                    LensesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // The next scroll or refresh asks again.
        }
    }

    /// <summary>The Git history of each member with a lens, from the document's blame (its unsaved text included).</summary>
    private async Task RefreshBlameAsync(int version, CancellationToken token)
    {
        if (!Options.CodeLens || !Options.GitCodeLens || Model.IsLarge || _services.Git.RepositoryFor(Model.Path) is null)
        {
            return;
        }

        try
        {
            var snapshot = Model.IsModified ? Model.Text.CreateSnapshot() : null;
            var text = snapshot is null ? null : await Task.Run(() => snapshot.Text, token).ConfigureAwait(true);
            var blame = await _services.Git.BlameInRepositoryAsync(Model.Path, text, token).ConfigureAwait(true);
            var index = await Task.Run(() => new GitLensIndex(blame), token).ConfigureAwait(true);
            if (token.IsCancellationRequested || _disposed || Model.Version != version)
            {
                return;
            }

            _blame = index;
            if (UpdateGitLenses())
            {
                LensesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is Codalus.Workbench.Git.GitException or OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _blame = null;
        }
    }

    /// <summary>The history of the member each lens names (its document symbol's lines); true when it changed.</summary>
    private bool UpdateGitLenses()
    {
        var lenses = new Dictionary<int, string>();
        if (Options.CodeLens && Options.GitCodeLens && _blame is { Blame.Count: > 0 } blame)
        {
            var named = new Dictionary<int, DocumentSymbol>();
            foreach (var symbol in Flatten(Symbols))
            {
                var at = symbol.SelectionRange.Start.Line;
                if (!named.TryGetValue(at, out var known) || Lines(symbol) < Lines(known))
                {
                    named[at] = symbol;
                }
            }

            var now = DateTimeOffset.Now;
            foreach (var line in _lenses.Keys)
            {
                if (named.TryGetValue(line - 1, out var symbol) && blame.Text(symbol.Range.Start.Line + 1, symbol.Range.End.Line + 1, now) is { } text)
                {
                    lenses[line] = text;
                }
            }
        }

        if (lenses.Count == _gitLenses.Count && lenses.All(p => _gitLenses.TryGetValue(p.Key, out var text) && text == p.Value))
        {
            return false;
        }

        _gitLenses = lenses;
        return true;

        static IEnumerable<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> level) =>
            level.SelectMany(s => Flatten(s.Children ?? []).Prepend(s));

        static int Lines(DocumentSymbol symbol) => symbol.Range.End.Line - symbol.Range.Start.Line;
    }

    /// <summary>The test outcomes by the line their test (or class) starts on, the innermost first; true when they changed.</summary>
    private bool UpdateTestLenses()
    {
        var lenses = !(Options.CodeLens && Options.TestCodeLens) ? [] : _services.Tests.InDocument(DocumentUris.FromPath(Model.Path), nodes =>
        {
            var texts = new Dictionary<int, string>();
            foreach (var node in nodes.OrderBy(n => n.Range!.EndLine - n.Range.StartLine))
            {
                if (LensDetails.Tests(node) is { } text)
                {
                    texts.TryAdd(node.Range!.StartLine + 1, text);
                }
            }

            return texts;
        });

        if (lenses.OrderBy(p => p.Key).SequenceEqual(_testLenses.OrderBy(p => p.Key)))
        {
            return false;
        }

        _testLenses = lenses;
        return true;
    }

    /// <summary>A request failed (the service restarting, or not ready yet): asked again later, a few times.</summary>
    private void RetryAfterFailure(Exception ex)
    {
        if (ex is OperationCanceledException || _disposed || _failures >= 5)
        {
            return;
        }

        _failures++;
        _services.Log($"{Model.Name} (diff): language service request failed ({ex.GetType().Name}: {ex.Message}); retrying");
        _version = -1;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private static bool IsTransient(Exception ex) =>
        ex is OperationCanceledException or IOException or System.Text.Json.JsonException or Codalus.Rpc.JsonRpcConnectionLostException
            or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException or InvalidOperationException;

    private static CancellationTokenSource Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _resolveTimer.Stop();
        _refreshCts?.Cancel();
        _resolveCts?.Cancel();
        Model.ContentChanged -= OnContentChanged;
        Model.Reloaded -= OnReloaded;
        _services.Options.PropertyChanged -= OnOptionsChanged;
        _services.Tests.Changed -= OnTestsChanged;
    }

    /// <summary>A lens of the service, and its title once resolved (or the last text's, kept until then).</summary>
    private sealed class Lens(CodeLens value)
    {
        public CodeLens Value { get; } = value;

        /// <summary>What tells it from the other lenses of its line across texts (the editor's key: its data's last part).</summary>
        public string? Key { get; } = value.Data?.GetRawText()?.Split(',')[^1];

        public Command? Command { get; private set; }

        public string? Title { get; set; }

        public void Resolve(Command command)
        {
            Command = command;
            Title = command.Title;
        }
    }
}
