using System.Collections.ObjectModel;
using System.Windows.Input;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Menus;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>Tools → Customize (M23b) as a document.</summary>
public sealed class CustomizeDocument : ShellDocument
{
    public const string DocumentId = "customize";

    public CustomizeDocument(CustomizeViewModel viewModel)
    {
        Id = DocumentId;
        Title = "Customize";
        ViewModel = viewModel;
    }

    public override string Kind => "customize";

    public CustomizeViewModel ViewModel { get; }
}

/// <summary>A menu, context menu or toolbar to customize.</summary>
public sealed record CustomizeTarget(string Title, string Location)
{
    public bool IsUserToolbar => Location.StartsWith(MenuCustomizations.ToolbarPrefix, StringComparison.Ordinal);

    public override string ToString() => Title;
}

/// <summary>An item of the chosen target.</summary>
public sealed record CustomizeItem(string Key, string Title, string? Note)
{
    public bool HasNote => Note is not null;
}

/// <summary>
/// Tools → Customize: choose a menu, context menu or toolbar; add commands, remove or restore items, move them, reset
/// the target, and create or delete toolbars. Every change is saved at once (<see cref="ShellHost"/>'s Customize methods).
/// </summary>
public sealed class CustomizeViewModel : ObservableObject
{
    private readonly ShellHost _shell;
    private CustomizeTarget? _target;
    private CustomizeItem? _selected;

    public CustomizeViewModel(ShellHost shell)
    {
        _shell = shell;
        AddCommand = new RelayCommand(() => _ = AddAsync(), () => _target is not null);
        RemoveCommand = new RelayCommand(() => Run(location => _shell.CustomizeRemoveAsync(location, _selected!.Key)), () => _selected is not null);
        MoveUpCommand = new RelayCommand(() => Run(location => _shell.CustomizeMoveAsync(location, _selected!.Key, -1)), () => _selected is not null);
        MoveDownCommand = new RelayCommand(() => Run(location => _shell.CustomizeMoveAsync(location, _selected!.Key, 1)), () => _selected is not null);
        ResetCommand = new RelayCommand(() => Run(_shell.CustomizeResetAsync), () => _target is { IsUserToolbar: false });
        NewToolbarCommand = new RelayCommand(() => _ = NewToolbarAsync());
        DeleteToolbarCommand = new RelayCommand(() => _ = DeleteToolbarAsync(), () => _target is { IsUserToolbar: true });
        RefreshTargets();
        Target = Targets.FirstOrDefault();
    }

    public ObservableCollection<CustomizeTarget> Targets { get; } = [];

    public ObservableCollection<CustomizeItem> Items { get; } = [];

    public CustomizeTarget? Target
    {
        get => _target;
        set
        {
            if (SetField(ref _target, value))
            {
                RefreshItems();
            }
        }
    }

    public CustomizeItem? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(RemoveTitle));
            }
        }
    }

    /// <summary>"Restore" for a removed built-in toolbar button.</summary>
    public string RemoveTitle => _selected?.Note == "removed" ? "Restore" : "Remove";

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand NewToolbarCommand { get; }

    public ICommand DeleteToolbarCommand { get; }

    /// <summary>The toolbars, every menu bar menu and its submenus, and the context menus.</summary>
    public void RefreshTargets()
    {
        var keep = _target?.Location;
        Targets.Clear();
        Targets.Add(new("Toolbar: Standard", MenuCustomizations.StandardToolbar));
        foreach (var (name, _) in _shell.Customizations.Toolbars)
        {
            Targets.Add(new($"Toolbar: {name}", MenuCustomizations.ToolbarLocation(name)));
        }

        var menus = _shell.Workbench.Menus;
        void AddMenu(string location, string title, int depth)
        {
            Targets.Add(new($"Menu: {title}", location));
            if (depth < 3)
            {
                foreach (var submenu in menus.GetAllItems(location).Where(i => i.Submenu is not null))
                {
                    AddMenu(submenu.Submenu!, $"{title} › {submenu.Title}", depth + 1);
                }
            }
        }

        foreach (var menu in menus.MenuBar)
        {
            AddMenu(MenuLocations.MenuBar(menu.Id), menu.Title.Replace("_", string.Empty, StringComparison.Ordinal), 0);
        }

        Targets.Add(new("Context Menu: Editor", MenuLocations.EditorContext));
        Targets.Add(new("Context Menu: Solution Explorer", MenuLocations.SolutionExplorerContext));
        if (keep is not null && Targets.FirstOrDefault(t => t.Location == keep) is { } same)
        {
            _target = same;
            OnPropertyChanged(nameof(Target));
        }
    }

    public void RefreshItems()
    {
        var keep = _selected?.Key;
        Items.Clear();
        if (_target is null)
        {
            return;
        }

        var customization = _shell.Customizations.For(_target.Location);
        var titles = _shell.Workbench.Menus.GetAllItems(_target.Location).ToDictionary(i => i.Submenu ?? i.Command, i => i.Submenu is null ? i.Title : i.Title + " ›", StringComparer.Ordinal);
        foreach (var key in _shell.CustomizeKeys(_target.Location))
        {
            var title = titles.GetValueOrDefault(key) ?? _shell.Workbench.Commands.Find(key)?.Title ?? key;
            var note = customization.Remove.Contains(key) ? "removed" : customization.Add.Contains(key) ? "added" : null;
            Items.Add(new(key, title, note));
        }

        Selected = Items.FirstOrDefault(i => i.Key == keep);
    }

    private async void Run(Func<string, Task> change)
    {
        if (_target is not { } target)
        {
            return;
        }

        await change(target.Location).ConfigureAwait(true);
        RefreshItems();
    }

    private async Task AddAsync()
    {
        if (_target is not { } target)
        {
            return;
        }

        var items = _shell.Workbench.Commands.Descriptors
            .Where(c => !c.Id.StartsWith('_'))
            .OrderBy(c => c.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new QuickPickItem(c.DisplayTitle) { Detail = c.Id, Tag = c.Id })
            .ToList();
        if (await _shell.Palette.ShowAsync(items, $"Add a command to {target.Title}").ConfigureAwait(true) is { Tag: string command })
        {
            await _shell.CustomizeAddAsync(target.Location, command).ConfigureAwait(true);
            RefreshItems();
            Selected = Items.FirstOrDefault(i => i.Key == command);
        }
    }

    private async Task NewToolbarAsync()
    {
        var name = await _shell.Dialogs.InputAsync("New Toolbar", "A name for the toolbar", string.Empty,
            n => n.Trim().Length == 0 ? "A toolbar needs a name." : _shell.Customizations.Toolbars.Any(t => t.Name == n.Trim()) ? "There is already a toolbar with that name." : null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _shell.NewToolbarAsync(name.Trim()).ConfigureAwait(true);
        RefreshTargets();
        Target = Targets.FirstOrDefault(t => t.Location == MenuCustomizations.ToolbarLocation(name.Trim()));
    }

    private async Task DeleteToolbarAsync()
    {
        if (_target is not { IsUserToolbar: true } target)
        {
            return;
        }

        await _shell.DeleteToolbarAsync(target.Location[MenuCustomizations.ToolbarPrefix.Length..]).ConfigureAwait(true);
        RefreshTargets();
        Target = Targets.FirstOrDefault();
    }
}
