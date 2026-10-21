using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Controls;

/// <summary>What a path field's Browse button asks for.</summary>
public enum PathKind
{
    /// <summary>An existing folder.</summary>
    Folder,

    /// <summary>An existing file.</summary>
    File,
}

/// <summary>
/// A path field (the maintainer's 0.5 review): a text box and, beside it, the Browse button (the folder icon, as on the
/// WCF Test Client's address) that opens the OS's folder or file picker where the typed path is, and puts the choice in
/// the box. Every field where a path on this computer is typed is one, so typing and browsing work the same everywhere;
/// the input dialog has the same button (<see cref="ViewModels.DialogRequest.Browse"/>).
/// <para>
/// With <see cref="BaseFolder"/>, a relative path is read from that folder, and a choice inside it is written relative to
/// it (a project's publish folder). Give the box an <c>AutomationProperties.Name</c> (or <c>LabeledBy</c>): its text box
/// takes it. Browsing
/// raises <see cref="BrowsedEvent"/>, for views that write a value when it's entered.
/// </para>
/// <para>
/// With <see cref="Recent"/> (New Project's location, ADR 0082), the field has a chevron of its own, inside it so it
/// stays one field (as an editable combo box): it lists the recent paths, newest first; Alt+Down or F4 opens it, Up and
/// Down move, Enter chooses (the text and <see cref="RecentChosenEvent"/>), Escape closes it and keeps the text. A
/// <c>Label</c> whose <c>Target</c> is the box focuses its text box (the box forwards the focus; Tab goes straight to the
/// text box).
/// </para>
/// </summary>
public sealed class PathBox : Decorator
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PathBox, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<PathKind> KindProperty = AvaloniaProperty.Register<PathBox, PathKind>(nameof(Kind));

    public static readonly StyledProperty<string?> BaseFolderProperty = AvaloniaProperty.Register<PathBox, string?>(nameof(BaseFolder));

    public static readonly StyledProperty<string?> PatternsProperty = AvaloniaProperty.Register<PathBox, string?>(nameof(Patterns));

    public static readonly StyledProperty<string?> PlaceholderTextProperty = AvaloniaProperty.Register<PathBox, string?>(nameof(PlaceholderText));

    /// <summary>Recent paths for the field's drop-down, newest first; null or empty: no drop-down.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> RecentProperty = AvaloniaProperty.Register<PathBox, IReadOnlyList<string>?>(nameof(Recent));

    /// <summary>Raised (bubbling) after Browse put a choice in the box.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> BrowsedEvent = RoutedEvent.Register<PathBox, RoutedEventArgs>(nameof(Browsed), RoutingStrategies.Bubble);

    /// <summary>Raised (bubbling) after a recent path was chosen from the drop-down.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> RecentChosenEvent = RoutedEvent.Register<PathBox, RoutedEventArgs>(nameof(RecentChosen), RoutingStrategies.Bubble);

    private readonly TextBox _box = new();
    private readonly Button _browse = new() { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Button _recent = new() { IsTabStop = false, Padding = new Thickness(4, 0), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
    private readonly MenuFlyout _recentMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };

    public PathBox()
    {
        // The box itself takes no Tab stop; a Label's access key that targets it lands in the text box.
        Focusable = true;
        IsTabStop = false;
        _recent.Classes.Add("subtle");
        _recent.Content = new Icon { Kind = "chevron-down", Width = 12, Height = 12 };
        var recent = RecentLabel;
        ToolTip.SetTip(_recent, Localizer.Get("msg.pathBox.recentTip", "Recent locations (Alt+Down)"));
        AutomationProperties.SetName(_recent, recent);
        _recent.Click += (_, _) => OpenRecent();
        _recentMenu.Opening += (_, _) => FillRecent();
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        _browse.Classes.Add("icon");
        ButtonIcon.SetKind(_browse, "folder-open");
        var browse = BrowseLabel;
        ToolTip.SetTip(_browse, browse);
        AutomationProperties.SetName(_browse, browse);
        _browse.Click += (_, _) => _ = BrowseAsync();
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                SetCurrentValue(TextProperty, _box.Text);
            }
        };
        // The text box first, so Tab reaches it before Browse (the reading order).
        Grid.SetColumn(_browse, 1);
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _box, _browse } };
    }

    /// <summary>The Browse button's tooltip and name.</summary>
    internal static string BrowseLabel => Localizer.Get("msg.pathBox.browse", "Browse…");

    /// <summary>The recent paths' chevron's name.</summary>
    internal static string RecentLabel => Localizer.Get("msg.pathBox.recent", "Recent locations");

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>A folder (the default) or a file.</summary>
    public PathKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The folder relative paths are read from and written relative to, or null.</summary>
    public string? BaseFolder
    {
        get => GetValue(BaseFolderProperty);
        set => SetValue(BaseFolderProperty, value);
    }

    /// <summary>A file's patterns, separated by semicolons (<c>*.db;*.sqlite</c>), or null for any file.</summary>
    public string? Patterns
    {
        get => GetValue(PatternsProperty);
        set => SetValue(PatternsProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Recent paths for the drop-down, newest first (null or empty: none).</summary>
    public IReadOnlyList<string>? Recent
    {
        get => GetValue(RecentProperty);
        set => SetValue(RecentProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? Browsed
    {
        add => AddHandler(BrowsedEvent, value);
        remove => RemoveHandler(BrowsedEvent, value);
    }

    public event EventHandler<RoutedEventArgs>? RecentChosen
    {
        add => AddHandler(RecentChosenEvent, value);
        remove => RemoveHandler(RecentChosenEvent, value);
    }

    /// <summary>The text box.</summary>
    internal TextBox TextBox => _box;

    /// <summary>The Browse button.</summary>
    internal Button BrowseButton => _browse;

    /// <summary>The recent paths' chevron (shown with <see cref="Recent"/>).</summary>
    internal Button RecentButton => _recent;

    /// <summary>The recent paths' drop-down.</summary>
    internal MenuFlyout RecentMenu => _recentMenu;

    /// <summary>Asks for the path in place of the OS's picker (tests): the kind and the folder to start in.</summary>
    internal Func<PathKind, string?, Task<string?>>? Picker { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && _box.Text != Text)
        {
            _box.Text = Text;
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            _box.PlaceholderText = PlaceholderText;
        }
        else if (change.Property == AutomationProperties.NameProperty)
        {
            AutomationProperties.SetName(_box, AutomationProperties.GetName(this));
        }
        else if (change.Property == AutomationProperties.LabeledByProperty)
        {
            AutomationProperties.SetLabeledBy(_box, AutomationProperties.GetLabeledBy(this));
        }
        else if (change.Property == RecentProperty)
        {
            _box.InnerRightContent = Recent is { Count: > 0 } ? _recent : null;
        }
    }

    /// <summary>The focus that lands on the box (a Label's access key) goes on to its text box.</summary>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            _box.Focus(e.NavigationMethod);
        }
    }

    /// <summary>Alt+Down or F4 in the text box opens the recent paths.</summary>
    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (Recent is { Count: > 0 } && (e is { Key: Key.Down, KeyModifiers: KeyModifiers.Alt } or { Key: Key.F4, KeyModifiers: KeyModifiers.None }))
        {
            OpenRecent();
            e.Handled = true;
        }
    }

    /// <summary>Opens the recent paths under the field.</summary>
    internal void OpenRecent()
    {
        if (Recent is not { Count: > 0 })
        {
            return;
        }

        _recentMenu.ShowAt(_box);
        if (_recentMenu.Items.Count > 0 && _recentMenu.Items[0] is MenuItem first)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => first.Focus(NavigationMethod.Directional), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The menu's rows: the folder icon and the path (trimmed, the whole of it in the tooltip; no access keys).</summary>
    private void FillRecent()
    {
        _recentMenu.Items.Clear();
        foreach (var path in Recent ?? [])
        {
            var item = MenuEntry.Create(path, "folder", click: () => ChooseRecent(path));
            item.Header = new TextBlock { Text = path, MaxWidth = Math.Max(240, _box.Bounds.Width - 48), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(item, path);
            _recentMenu.Items.Add(item);
        }
    }

    /// <summary>A recent path chosen: it's the text, and the text box has the focus again.</summary>
    internal void ChooseRecent(string path)
    {
        SetCurrentValue(TextProperty, path);
        _recentMenu.Hide();
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
        RaiseEvent(new RoutedEventArgs(RecentChosenEvent));
    }

    private async Task BrowseAsync()
    {
        var start = StartFolder(Text, BaseFolder);
        var picked = Picker is { } picker
            ? await picker(Kind, start).ConfigureAwait(true)
            : await PickAsync(this, Kind, start, Patterns).ConfigureAwait(true);
        if (picked is null)
        {
            return;
        }

        SetCurrentValue(TextProperty, Relative(picked, BaseFolder));
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
        RaiseEvent(new RoutedEventArgs(BrowsedEvent));
    }

    /// <summary>
    /// The folder a picker opens in: the typed path's (its nearest existing folder; <c>~</c> and environment variables
    /// expanded, a relative path read from <paramref name="baseFolder"/>), else the base folder, else null (the OS's).
    /// </summary>
    internal static string? StartFolder(string? text, string? baseFolder)
    {
        if (text?.Trim() is { Length: > 0 } typed)
        {
            try
            {
                var path = Codalus.Platform.UserPaths.Expand(typed);
                path = baseFolder is not null ? Path.GetFullPath(path, baseFolder) : Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
                for (var folder = path; folder is not null; folder = Path.GetDirectoryName(folder))
                {
                    if (Directory.Exists(folder))
                    {
                        return folder;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path yet: start where the base folder is.
            }
        }

        return baseFolder is not null && Directory.Exists(baseFolder) ? baseFolder : null;
    }

    /// <summary><paramref name="path"/> relative to <paramref name="baseFolder"/> when it's inside it; otherwise as it is.</summary>
    internal static string Relative(string path, string? baseFolder)
    {
        if (baseFolder is null)
        {
            return path;
        }

        var relative = Path.GetRelativePath(baseFolder, path);
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : relative;
    }

    /// <summary>The OS's folder or file picker, opened in <paramref name="start"/>, for <paramref name="owner"/>'s window; null when cancelled.</summary>
    internal static async Task<string?> PickAsync(Visual owner, PathKind kind, string? start, string? patterns = null)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage)
        {
            return null;
        }

        using var location = start is null ? null : await storage.TryGetFolderFromPathAsync(start).ConfigureAwait(true);
        if (kind == PathKind.Folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localizer.Get("msg.pathBox.chooseFolder", "Choose a Folder"),
                SuggestedStartLocation = location,
            }).ConfigureAwait(true);
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("msg.pathBox.chooseFile", "Choose a File"),
            SuggestedStartLocation = location,
            FileTypeFilter = patterns is { Length: > 0 }
                ? [new FilePickerFileType(patterns) { Patterns = patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }, FilePickerFileTypes.All]
                : null,
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
