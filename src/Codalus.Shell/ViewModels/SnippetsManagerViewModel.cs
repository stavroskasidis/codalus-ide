using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Snippets;

namespace Codalus.Shell.ViewModels;

/// <summary>A snippet in the Code Snippets Manager.</summary>
public sealed record SnippetRowViewModel(Snippet Snippet)
{
    public string Name => Snippet.Name;

    public string Prefixes => string.Join(", ", Snippet.Prefixes);

    /// <summary>Where it comes from: built in, yours, or a plugin's id.</summary>
    public string Source => Snippet.Source switch
    {
        "builtin" => "Built in",
        "user" => "Yours",
        var plugin => plugin,
    };

    public string Description => Snippet.Description ?? string.Empty;

    public bool IsUser => Snippet.Source == "user";

    public string AccessibleName => $"{Name} ({Prefixes}), {Source}";
}

/// <summary>
/// Tools → Code Snippets Manager (deferred from M12b): every language's snippets, built in, from plugins and yours (yours
/// replace others of the same name), with their prefixes and bodies; open your snippet file for the language, import
/// Visual Studio <c>.snippet</c> files, copy a snippet to yours to change it, and remove one of yours.
/// </summary>
public sealed class SnippetsManagerViewModel : ObservableObject, ILoadable
{
    private readonly ShellHost _shell;
    private string _language = string.Empty;
    private string _search = string.Empty;
    private SnippetRowViewModel? _selected;
    private string? _status;

    public SnippetsManagerViewModel(ShellHost shell, string? language)
    {
        _shell = shell;
        _language = language ?? string.Empty;
        OpenUserSnippetsCommand = new RelayCommand(() => _ = _shell.OpenUserSnippetsAsync(Language.Length > 0 && Language != "*" ? Language : null), () => Language.Length > 0);
        ImportCommand = new RelayCommand(() => _ = ImportAsync());
        CopyToUserCommand = new RelayCommand(() => CopyToUser(), () => _selected is { IsUser: false });
        RemoveCommand = new RelayCommand(() => _ = RemoveAsync(), () => _selected is { IsUser: true });
        shell.Workbench.Snippets.Changed += (_, _) => Ui.Post(Refresh);
    }

    public ObservableCollection<string> Languages { get; } = [];

    public ObservableCollection<SnippetRowViewModel> Snippets { get; } = [];

    public ICommand OpenUserSnippetsCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand CopyToUserCommand { get; }

    public ICommand RemoveCommand { get; }

    public string Language
    {
        get => _language;
        set
        {
            if (SetField(ref _language, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    public SnippetRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(Body));
                OnPropertyChanged(nameof(HasSelection));
                ((RelayCommand)CopyToUserCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RemoveCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The selected snippet's body, as written.</summary>
    public string Body => _selected?.Snippet.Body ?? string.Empty;

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public Task LoadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        var languages = _shell.Workbench.Snippets.Languages();
        if (!languages.SequenceEqual(Languages))
        {
            Languages.Clear();
            foreach (var language in languages)
            {
                Languages.Add(language);
            }
        }

        if (_language.Length == 0 || !Languages.Contains(_language))
        {
            _language = Languages.Contains("csharp") ? "csharp" : Languages.FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(Language));
        }

        var selected = _selected?.Name;
        Snippets.Clear();
        foreach (var snippet in _shell.Workbench.Snippets.For(_language).Where(Matches))
        {
            Snippets.Add(new SnippetRowViewModel(snippet));
        }

        Selected = Snippets.FirstOrDefault(s => s.Name == selected) ?? Snippets.FirstOrDefault();
        ((RelayCommand)OpenUserSnippetsCommand).RaiseCanExecuteChanged();
        Status = Snippets.Count == 1 ? "1 snippet" : $"{Snippets.Count} snippets";
    }

    private bool Matches(Snippet snippet) =>
        _search.Trim() is not { Length: > 0 } query
        || snippet.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || snippet.Prefixes.Any(p => p.Contains(query, StringComparison.OrdinalIgnoreCase))
        || snippet.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private async Task ImportAsync()
    {
        await _shell.ImportVisualStudioSnippetsAsync().ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Copies the selected snippet into your snippet file for its language, where you can change it.</summary>
    public void CopyToUser()
    {
        if (_selected?.Snippet is not { } snippet)
        {
            return;
        }

        _shell.ImportSnippets([snippet with { Source = "user", Language = snippet.Language == "*" ? _language : snippet.Language }]);
        Status = $"Copied '{snippet.Name}' to your snippets; yours replaces it.";
    }

    /// <summary>Removes the selected snippet of yours (after asking).</summary>
    public async Task RemoveAsync()
    {
        if (_selected is not { IsUser: true, Snippet: var snippet })
        {
            return;
        }

        if (!await _shell.Dialogs.ConfirmAsync("Remove Snippet", $"Remove your snippet '{snippet.Name}'?", "Remove", danger: true).ConfigureAwait(true))
        {
            return;
        }

        _shell.RemoveUserSnippet(snippet);
        Status = $"Removed '{snippet.Name}'.";
    }
}
