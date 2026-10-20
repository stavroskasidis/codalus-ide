using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
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
/// it (a project's publish folder). Give the box an <c>AutomationProperties.Name</c>: its text box takes it. Browsing
/// raises <see cref="BrowsedEvent"/>, for views that write a value when it's entered.
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

    /// <summary>Raised (bubbling) after Browse put a choice in the box.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> BrowsedEvent = RoutedEvent.Register<PathBox, RoutedEventArgs>(nameof(Browsed), RoutingStrategies.Bubble);

    private readonly TextBox _box = new();
    private readonly Button _browse = new() { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    public PathBox()
    {
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
        DockPanel.SetDock(_browse, Avalonia.Controls.Dock.Right);
        Child = new DockPanel { Children = { _browse, _box } };
    }

    /// <summary>The Browse button's tooltip and name.</summary>
    internal static string BrowseLabel => Localizer.Get("msg.pathBox.browse", "Browse…");

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

    public event EventHandler<RoutedEventArgs>? Browsed
    {
        add => AddHandler(BrowsedEvent, value);
        remove => RemoveHandler(BrowsedEvent, value);
    }

    /// <summary>The text box.</summary>
    internal TextBox TextBox => _box;

    /// <summary>The Browse button.</summary>
    internal Button BrowseButton => _browse;

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
                var path = Environment.ExpandEnvironmentVariables(typed);
                if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
                }

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
