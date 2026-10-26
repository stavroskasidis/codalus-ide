using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Terminal;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell.ViewModels;

/// <summary>A terminal tab: one session, or two side by side (split).</summary>
public sealed class TerminalTabViewModel : ObservableObject
{
    private TerminalSession? _secondary;
    private string _title;
    private bool _exited;

    public TerminalTabViewModel(TerminalSession primary)
    {
        Primary = primary;
        _title = primary.Title;
        primary.TitleChanged += (_, _) => Ui.Post(UpdateTitle);
        primary.Exited += (_, code) => Ui.Post(() => Exited = true);
    }

    public TerminalSession Primary { get; }

    public TerminalSession? Secondary
    {
        get => _secondary;
        set
        {
            if (SetField(ref _secondary, value))
            {
                OnPropertyChanged(nameof(IsSplit));
                if (value is not null)
                {
                    value.TitleChanged += (_, _) => Ui.Post(UpdateTitle);
                }

                UpdateTitle();
            }
        }
    }

    public bool IsSplit => _secondary is not null;

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public bool Exited
    {
        get => _exited;
        private set
        {
            if (SetField(ref _exited, value))
            {
                UpdateTitle();
            }
        }
    }

    private void UpdateTitle() =>
        Title = (_secondary is null ? Primary.Title : $"{Primary.Title} | {_secondary.Title}") + (_exited ? $" (exited: {Primary.ExitCode})" : string.Empty);

    public IEnumerable<TerminalSession> Sessions => _secondary is null ? [Primary] : [Primary, _secondary];
}

public sealed record TerminalProfileChoice(string Id, string Name);

public static class TerminalViewConverters
{
    public static readonly IValueConverter MoreThanOne = new FuncValueConverter<int, bool>(count => count > 1);
}

/// <summary>How terminals look (settings and theme), shared by every terminal control.</summary>
public sealed class TerminalAppearance : ObservableObject
{
    private FontFamily _fontFamily = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Noto Sans Mono, Ubuntu Mono, Liberation Mono, Noto Mono, monospace");
    private double _fontSize = 13;
    private TerminalTheme _palette = TerminalTheme.For("dark");
    private bool _copyOnSelect;

    public FontFamily FontFamily
    {
        get => _fontFamily;
        set => SetField(ref _fontFamily, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetField(ref _fontSize, value);
    }

    public TerminalTheme Palette
    {
        get => _palette;
        set => SetField(ref _palette, value);
    }

    public bool CopyOnSelect
    {
        get => _copyOnSelect;
        set => SetField(ref _copyOnSelect, value);
    }
}

/// <summary>
/// The Terminal tool window (ADR 0014): terminal tabs (each one session, or a split pair), new terminals by profile,
/// split, kill, clear and find.
/// </summary>
public sealed class TerminalViewModel : ObservableObject
{
    public const string ToolWindowId = "terminal";

    private readonly TerminalService _service;
    private readonly Func<string> _workingDirectory;
    private readonly Func<string?> _defaultProfile;
    private TerminalTabViewModel? _selected;
    private string _findText = string.Empty;
    private bool _findVisible;
    private string? _error;

    public TerminalViewModel(TerminalService service, Func<string> workingDirectory, Func<string?> defaultProfile, TerminalAppearance appearance, Action<TerminalLinkActivation> openLink)
    {
        _service = service;
        Appearance = appearance;
        OpenLink = openLink;
        _workingDirectory = workingDirectory;
        _defaultProfile = defaultProfile;
        NewCommand = new RelayCommand(p => NewTerminal(p as string));
        SplitCommand = new RelayCommand(() => Split(), () => _selected is { IsSplit: false });
        KillCommand = new RelayCommand(() => Kill(_selected), () => _selected is not null);
        ClearCommand = new RelayCommand(() => ClearRequested?.Invoke(this, EventArgs.Empty), () => _selected is not null);
        FindCommand = new RelayCommand(() => FindVisible = !FindVisible);
        foreach (var profile in service.Profiles)
        {
            Profiles.Add(new TerminalProfileChoice(profile.Id, profile.Name));
        }

        service.Removed += (_, session) => Ui.Post(() => OnRemoved(session));
    }

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    public TerminalAppearance Appearance { get; }

    /// <summary>Opens a Ctrl+clicked link (a URL in the browser, a file in the editor).</summary>
    public Action<TerminalLinkActivation> OpenLink { get; }

    public ObservableCollection<TerminalProfileChoice> Profiles { get; } = [];

    public ICommand NewCommand { get; }

    public ICommand SplitCommand { get; }

    public ICommand KillCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand FindCommand { get; }

    /// <summary>The view clears the focused terminal's screen and scrollback.</summary>
    public event EventHandler? ClearRequested;

    /// <summary>The view finds <see cref="FindText"/> (forward or back) in the focused terminal.</summary>
    public event EventHandler<bool>? FindRequested;

    public TerminalTabViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasTerminal));
                ((RelayCommand)SplitCommand).RaiseCanExecuteChanged();
                ((RelayCommand)KillCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasTerminal => _selected is not null;

    public string? Error
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

    public bool HasError => _error is not null;

    public bool FindVisible
    {
        get => _findVisible;
        set => SetField(ref _findVisible, value);
    }

    public string FindText
    {
        get => _findText;
        set
        {
            if (SetField(ref _findText, value ?? string.Empty) && _findText.Length > 0)
            {
                FindRequested?.Invoke(this, false);
            }
        }
    }

    public void FindNext(bool backwards) => FindRequested?.Invoke(this, backwards);

    /// <summary>Opens a terminal with a profile (null: the default profile).</summary>
    public TerminalSession? NewTerminal(string? profileId = null, string? workingDirectory = null)
    {
        var profile = _service.FindProfile(profileId ?? _defaultProfile());
        if (profile is null)
        {
            Error = "No shell was found for the terminal.";
            return null;
        }

        try
        {
            var session = _service.Start(profile, workingDirectory ?? _workingDirectory());
            var tab = new TerminalTabViewModel(session);
            Tabs.Add(tab);
            Selected = tab;
            Error = null;
            return session;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = $"Could not start {profile.Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Adds a tab for a session started elsewhere (run in terminal).</summary>
    public void Show(TerminalSession session)
    {
        var tab = Tabs.FirstOrDefault(t => t.Sessions.Contains(session)) ?? new TerminalTabViewModel(session);
        if (!Tabs.Contains(tab))
        {
            Tabs.Add(tab);
        }

        Selected = tab;
    }

    /// <summary>Splits the selected tab with a second terminal of the default profile in the same directory.</summary>
    public TerminalSession? Split()
    {
        if (_selected is not { IsSplit: false } tab || _service.FindProfile(_defaultProfile()) is not { } profile)
        {
            return null;
        }

        try
        {
            var session = _service.Start(profile, tab.Primary.CurrentDirectory);
            tab.Secondary = session;
            ((RelayCommand)SplitCommand).RaiseCanExecuteChanged();
            return session;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return null;
        }
    }

    public void Kill(TerminalTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        foreach (var session in tab.Sessions.ToList())
        {
            _service.Close(session);
        }
    }

    private void OnRemoved(TerminalSession session)
    {
        var tab = Tabs.FirstOrDefault(t => t.Sessions.Contains(session));
        if (tab is null)
        {
            return;
        }

        if (tab.Secondary == session)
        {
            tab.Secondary = null;
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (tab.Secondary is { } other)
        {
            _service.Close(other);
        }

        Tabs.Remove(tab);
        if (_selected == tab)
        {
            Selected = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }
}
