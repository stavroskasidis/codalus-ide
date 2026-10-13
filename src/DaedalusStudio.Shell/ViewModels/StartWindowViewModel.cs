using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Localization;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A row of the start window's list: a group's heading or a recent entry.</summary>
public abstract class StartRowViewModel : ObservableObject;

public sealed class StartGroupRowViewModel(string title) : StartRowViewModel
{
    public string Title { get; } = title;
}

public sealed class StartEntryRowViewModel(RecentEntry entry, StartWindowViewModel owner) : StartRowViewModel
{
    public RecentEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Folder => Path.GetDirectoryName(Entry.Path) ?? Entry.Path;

    public string Icon => Directory.Exists(Entry.Path) ? FileIcons.ForFolder(Entry.Name, open: false) : FileIcons.ForFile(Entry.Name);

    /// <summary>When it was last opened, in the user's format; blank for entries from before M38 (no time kept).</summary>
    public string Opened => Entry.LastOpened == DateTimeOffset.MinValue ? string.Empty : Entry.LastOpened.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public bool Exists => File.Exists(Entry.Path) || Directory.Exists(Entry.Path);

    public bool IsPinned => Entry.Pinned;

    public string PinTitle => Entry.Pinned ? Localizer.Get("msg.startWindow.unpin", "Unpin") : Localizer.Get("msg.startWindow.pin", "Pin");

    public string AccessibleName => Exists ? $"{Name}, {Folder}" : Localizer.Format("msg.startWindow.missingEntry", "{0}, not found", Name);

    public ICommand OpenCommand => new RelayCommand(() => owner.Open(this));

    public ICommand PinCommand => new RelayCommand(() => owner.SetPinned(this, !Entry.Pinned));

    public ICommand RemoveCommand => new RelayCommand(() => owner.Remove(this));

    public ICommand CopyPathCommand => new RelayCommand(() => owner.CopyPath(this));

    public ICommand OpenInNewWindowCommand => new RelayCommand(() => owner.OpenInNewWindow(this));
}

/// <summary>
/// The start window (M38): recent entries in groups (Pinned, Today, Yesterday, This week, This month, Older), filtered
/// by the search box, and the ways to start. Choosing anything closes the window, then does it.
/// </summary>
public sealed class StartWindowViewModel : ObservableObject
{
    private readonly ShellHost _shell;
    private readonly Func<DateTimeOffset> _clock;
    private string _search = string.Empty;
    private StartRowViewModel? _selected;

    public StartWindowViewModel(ShellHost shell, Func<DateTimeOffset>? clock = null)
    {
        _shell = shell;
        _clock = clock ?? (() => DateTimeOffset.Now);
        shell.Recent.Changed += (_, _) => Ui.Post(Refresh);
        CreateProjectCommand = Action("templates.newProject");
        OpenSolutionCommand = Action("workbench.openSolution");
        OpenFolderCommand = Action("workbench.openFolder");
        CloneCommand = Action("git.clone");
        ContinueCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        Refresh();
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<StartRowViewModel> Rows { get; } = [];

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value))
            {
                Refresh();
            }
        }
    }

    public StartRowViewModel? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public bool IsEmpty => !Rows.OfType<StartEntryRowViewModel>().Any();

    /// <summary>"No recent solutions yet", or nothing matching the search.</summary>
    public string EmptyText => _search.Length > 0
        ? Localizer.Get("msg.startWindow.noMatches", "Nothing recent matches the search.")
        : Localizer.Get("msg.startWindow.noRecents", "Solutions, projects and folders you open will be listed here.");

    public ICommand CreateProjectCommand { get; }

    public ICommand OpenSolutionCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand CloneCommand { get; }

    public ICommand ContinueCommand { get; }

    public void Refresh()
    {
        var keep = (_selected as StartEntryRowViewModel)?.Entry.Path;
        Rows.Clear();
        var entries = _shell.Recent.Entries.Where(e => _search.Length == 0
            || e.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
            || e.Path.Contains(_search, StringComparison.CurrentCultureIgnoreCase));
        foreach (var (group, items) in RecentSolutions.Group(entries, _clock()))
        {
            Rows.Add(new StartGroupRowViewModel(GroupTitle(group)));
            foreach (var entry in items)
            {
                Rows.Add(new StartEntryRowViewModel(entry, this));
            }
        }

        Selected = Rows.OfType<StartEntryRowViewModel>().FirstOrDefault(r => r.Entry.Path == keep);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>The first entry (Enter in the search box opens it).</summary>
    public StartEntryRowViewModel? FirstEntry => Rows.OfType<StartEntryRowViewModel>().FirstOrDefault();

    internal void Open(StartEntryRowViewModel row)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _ = _shell.OpenRecentEntryAsync(row.Entry.Path);
    }

    internal void SetPinned(StartEntryRowViewModel row, bool pinned) => _shell.Recent.SetPinned(row.Entry.Path, pinned);

    internal void Remove(StartEntryRowViewModel row) => _shell.Recent.Remove(row.Entry.Path);

    internal void CopyPath(StartEntryRowViewModel row)
    {
        if (_shell.Window?.Clipboard is { } clipboard)
        {
            _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, row.Entry.Path);
        }
    }

    internal void OpenInNewWindow(StartEntryRowViewModel row)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.OpenRecentInNewWindow(row.Entry.Path);
    }

    private RelayCommand Action(string command) => new(() =>
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.Run(command);
    });

    private static string GroupTitle(string group) => group switch
    {
        "Pinned" => Localizer.Get("msg.startWindow.pinned", "Pinned"),
        "Today" => Localizer.Get("msg.startWindow.today", "Today"),
        "Yesterday" => Localizer.Get("msg.startWindow.yesterday", "Yesterday"),
        "This week" => Localizer.Get("msg.startWindow.thisWeek", "This week"),
        "This month" => Localizer.Get("msg.startWindow.thisMonth", "This month"),
        _ => Localizer.Get("msg.startWindow.older", "Older"),
    };
}
