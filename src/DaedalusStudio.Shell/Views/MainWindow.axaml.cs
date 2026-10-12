using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DaedalusStudio.Diagnostics.BenchEvents;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Input;
using DaedalusStudio.Workbench.Keybindings;
using Dock.Model.Core;

namespace DaedalusStudio.Shell.Views;

public sealed partial class MainWindow : Window, IShellWindow
{
    private readonly ShellHost _shell = null!;
    private IInputElement? _focusBeforePalette;
    private bool _chordPending;
    private bool _closeConfirmed;
    private PixelPoint _normalPosition;
    private Size _normalSize;

    /// <summary>For the XAML runtime loader and designer; the shell attaches through the other constructor.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(ShellHost shell)
        : this()
    {
        _shell = shell;
        shell.Window = this;
        DataContext = shell;
        ApplyPlacement(shell.InitialPlacement);
        MenuBuilder.Attach(this, MenuBar, shell);
        SetUpTitleBar();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // handledEventsToo: the code editor's text area marks GotFocus handled, which left editorTextFocus unset, so the
        // editor's key bindings (Ctrl+Space, F12…) never matched a focused editor (M34a).
        AddHandler(GotFocusEvent, (_, e) => OnFocusChanged(e.Source as Visual), RoutingStrategies.Bubble, handledEventsToo: true);
        PaletteLayer.PointerPressed += (_, e) =>
        {
            if (e.Source == PaletteLayer)
            {
                shell.Palette.Cancel();
            }
        };
        shell.Palette.Opened += (_, _) =>
        {
            _focusBeforePalette = FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(Palette.FocusInput, DispatcherPriority.Loaded);
        };
        shell.Palette.Closed += (_, _) =>
        {
            if (_focusBeforePalette is { } previous && previous is Visual visual && TopLevel.GetTopLevel(visual) == this)
            {
                previous.Focus();
            }

            _focusBeforePalette = null;
        };
        shell.Layout.FocusRequested += (_, dockable) => FocusDockableSoon(dockable, attempts: 10);
        PositionChanged += (_, _) => RememberNormalBounds();
        Resized += (_, _) => RememberNormalBounds();
        Opened += OnOpened;
        Closing += OnClosing;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) =>
        {
            if (!e.Handled && e.DataTransfer.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
            }
        });
        DragDrop.AddDropHandler(this, OnFileDrop);
    }

    // ------------------------------------------------------------------ title bar

    /// <summary>Width kept free for the system's minimize, maximize and close buttons in the extended title bar.</summary>
    private const double CaptionButtonsWidth = 138;

    /// <summary>
    /// On Windows the title row is the window's title bar (M29): the client area extends into it, the empty part drags
    /// the window and a double click maximizes it. Elsewhere the system keeps its title bar and the row sits below it.
    /// </summary>
    private void SetUpTitleBar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 32;
        TitleIcon.IsVisible = true;
        CaptionButtonsSpace.Width = CaptionButtonsWidth;
        TitleDragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    BeginMoveDrag(e);
                }
            }
        };
    }

    // ------------------------------------------------------------------ startup and shutdown

    private void OnOpened(object? sender, EventArgs e)
    {
        RememberNormalBounds();

        // A requestAnimationFrame callback runs at the start of a frame; the second one proves the first frame
        // was produced. "interactive" additionally waits for the UI thread to go idle.
        RequestAnimationFrame(_ => RequestAnimationFrame(_ =>
        {
            BenchEvents.Emit(BenchEventNames.FirstFrame);
            Dispatcher.UIThread.Post(() =>
            {
                BenchEvents.Emit(BenchEventNames.Interactive);
                _shell.OnInteractive();
                StartBenchDriver();
                if (_shell.Options.Screenshot is { } screenshot)
                {
                    // Late enough for startup plugins (the Start page) to have rendered.
                    DispatcherTimer.RunOnce(() => SaveScreenshot(screenshot), TimeSpan.FromSeconds(4));
                }

                if (_shell.Options.ExitOn == "interactive" && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(), DispatcherPriority.Background);
                }
            }, DispatcherPriority.Background);
        }));
    }

    /// <summary>Benchmarks (<c>--open</c>, <c>--bench</c>, <c>--exit-on</c>): see <see cref="Infrastructure.BenchDriver"/>.</summary>
    private void StartBenchDriver()
    {
        if (Infrastructure.BenchDriver.IsRequested(_shell.Options))
        {
            _ = new Infrastructure.BenchDriver(this, _shell).RunAsync();
        }

        if (_shell.Options.SmokeTest is { } samples)
        {
            _ = new Infrastructure.SmokeTest(this, _shell, samples).RunAsync();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && _shell.HasUnsavedDocuments)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }

        _shell.SaveState(Placement);
    }

    private async Task ConfirmCloseAsync()
    {
        if (await _shell.ConfirmQuitAsync().ConfigureAwait(true))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    public void RequestClose() => Close();

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public Task<string?> PickSolutionAsync() => PickAsync("Open Solution or Project", "Solutions, filters and projects", ["*.slnx", "*.sln", "*.slnf", "*.csproj", "*.fsproj", "*.vbproj"]);

    public Task<string?> PickProjectAsync() => PickAsync("Add Existing Project", "Projects", ["*.csproj", "*.fsproj", "*.vbproj"]);

    public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) => PickAsync(title, filterName, patterns);

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = patterns }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = title, AllowMultiple = true }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickAsync(string title, string filterName, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = patterns }],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Dropping a solution, project or folder from the file manager opens it.</summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || e.DataTransfer.TryGetFiles() is not [{ } item, ..] || item.TryGetLocalPath() is not { } path)
        {
            return;
        }

        e.Handled = true;
        _ = Directory.Exists(path) ? _shell.OpenFolderAsync(path) : _shell.OpenSolutionAsync(path);
    }

    private void SaveScreenshot(string path)
    {
        // The visual root includes the window decorations Avalonia draws (the caption buttons on Windows).
        Visual root = this;
        while (root.GetVisualParent() is { } parent)
        {
            root = parent;
        }

        var scaling = RenderScaling;
        var size = new PixelSize((int)(root.Bounds.Width * scaling), (int)(root.Bounds.Height * scaling));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(root);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    // ------------------------------------------------------------------ placement

    public Docking.WindowPlacement Placement =>
        new(_normalSize.Width, _normalSize.Height, _normalPosition.X, _normalPosition.Y, WindowState == WindowState.Maximized);

    private void RememberNormalBounds()
    {
        if (WindowState == WindowState.Normal && Bounds.Width > 0)
        {
            _normalPosition = Position;
            _normalSize = Bounds.Size;
        }
    }

    private void ApplyPlacement(Docking.WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        Width = placement.Width;
        Height = placement.Height;
        if (placement is { X: { } x, Y: { } y } && Screens.ScreenFromPoint(new PixelPoint(x + 40, y + 20)) is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>
    /// Takes every key press before key bindings when set (the key binding recorder). Returns true when the
    /// key was consumed.
    /// </summary>
    public Func<KeyEventArgs, bool>? KeyInterceptor { get; set; }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (KeyInterceptor is { } intercept && intercept(e))
        {
            e.Handled = true;
            return;
        }

        // A modal dialog owns the keyboard.
        if (e.Handled || _shell.Dialogs.IsOpen || KeyMapper.ToChord(e.Key, e.KeyModifiers) is not { } chord)
        {
            return;
        }

        var platform = _shell.Workbench.Platform;

        // A focused terminal gets every key except those bound to the commands that stay with the IDE
        // (terminal.commandsToSkipShell); chords (Ctrl+K…, Ctrl+R…) go to the shell too.
        if (e.Source is Terminal.TerminalControl && !_chordPending)
        {
            var peek = _shell.Workbench.Keybindings.Peek(chord);
            if (peek is not { Kind: KeyDispatchKind.Command, Rule: { } rule } || !_shell.KeepsCommandFromTerminal(rule.Command))
            {
                return;
            }
        }

        var result = _shell.Workbench.Keybindings.Dispatch(chord);
        switch (result.Kind)
        {
            case KeyDispatchKind.Pending:
                _chordPending = true;
                e.Handled = true;
                _shell.StatusBar.ShowMessage($"({result.Pending!.ToLabel(platform)}) was pressed. Waiting for the next key…");
                break;

            case KeyDispatchKind.Command:
                _chordPending = false;
                e.Handled = true;
                _shell.StatusBar.ClearMessage();
                _shell.Run(result.Rule!.Command, Arguments(result.Rule));
                break;

            default:
                if (_chordPending)
                {
                    _chordPending = false;
                    e.Handled = true;
                    _shell.StatusBar.ShowMessage($"The key combination ({chord.ToLabel(platform)}) is not a command.", TimeSpan.FromSeconds(4));
                }

                break;
        }
    }

    private static IReadOnlyList<System.Text.Json.JsonElement> Arguments(KeybindingRule rule) => rule.Args switch
    {
        { ValueKind: System.Text.Json.JsonValueKind.Array } array => [.. array.EnumerateArray()],
        { } single => [single],
        null => [],
    };

    // ------------------------------------------------------------------ focus and panes

    private void OnFocusChanged(Visual? source)
    {
        var pane = PaneHost.Of(source);
        var view = pane?.Dockable switch
        {
            ToolWindowDockable tool => tool.Id,
            ShellDocument document => document.Kind,
            _ => null,
        };
        _shell.Workbench.Context.Set("focusedView", view);
        _shell.Workbench.Context.Set("editorTextFocus", view == "textFile");
        _shell.Notifications.SuppressToasts = view == BuiltIns.NotificationsWindow;
    }

    /// <summary>F6 / Shift+F6: moves focus to the next visible pane in reading order.</summary>
    public void FocusPane(bool backwards)
    {
        var panes = PaneHost.VisiblePanes(this);
        if (panes.Count == 0)
        {
            return;
        }

        var current = PaneHost.Of(FocusManager?.GetFocusedElement() as Visual);
        var index = current is null ? -1 : IndexOf(panes, current);
        var next = backwards
            ? (index <= 0 ? panes.Count - 1 : index - 1)
            : (index + 1) % panes.Count;
        Activate(panes[next]);
    }

    private static int IndexOf(IReadOnlyList<PaneHost> panes, PaneHost pane)
    {
        for (var i = 0; i < panes.Count; i++)
        {
            if (panes[i] == pane)
            {
                return i;
            }
        }

        return -1;
    }

    private void Activate(PaneHost pane)
    {
        if (pane.Dockable.Owner is IDock owner)
        {
            _shell.Layout.Factory.SetFocusedDockable(owner, pane.Dockable);
        }

        pane.FocusContent();

        // Selecting the pane's tab can rebuild its content (or Dock can move focus while it activates the dockable):
        // once layout has settled, make sure the focus is still in that pane.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (pane.IsEffectivelyVisible && !pane.IsKeyboardFocusWithin && PaneHost.Of(FocusManager?.GetFocusedElement() as Visual) is null)
                {
                    pane.FocusContent();
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>Focuses a dockable's view once it has been created (Dock creates views lazily).</summary>
    private void FocusDockableSoon(IDockable dockable, int attempts)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (PaneHost.VisiblePanes(this).FirstOrDefault(p => p.Dockable == dockable) is { } pane)
            {
                pane.FocusContent();
            }
            else if (attempts > 0)
            {
                FocusDockableSoon(dockable, attempts - 1);
            }
        }, DispatcherPriority.Background);
    }
}
