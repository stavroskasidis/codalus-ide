using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Diagnostics.BenchEvents;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Input;
using Codalus.Workbench.Keybindings;
using Dock.Model.Core;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

public sealed partial class MainWindow : Window, IShellWindow
{
    private readonly ShellHost _shell = null!;
    private IInputElement? _focusBeforePalette;
    private IInputElement? _focusBeforeCodeSearch;
    private IInputElement? _focusBeforeFind;
    private bool _chordPending;
    private bool _closeConfirmed;
    private PixelPoint _normalPosition;
    private Size _normalSize;

    /// <summary>
    /// The mouse's back and forward buttons (XButton1 and XButton2) are Navigate Backward and Forward anywhere in the
    /// window (M50), as in the classic IDE: nothing handled them before.
    /// </summary>
    private void OnMouseNavigation(object? sender, PointerPressedEventArgs e)
    {
        var command = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.XButton1Pressed => "workbench.navigateBack",
            PointerUpdateKind.XButton2Pressed => "workbench.navigateForward",
            _ => null,
        };
        if (command is not null && _shell is { } shell)
        {
            shell.Run(command);
            e.Handled = true;
        }
    }

    /// <summary>For the XAML runtime loader and designer; the shell attaches through the other constructor.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(ShellHost shell)
        : this()
    {
        _shell = shell;
        shell.Window = this;
        if (Codalus.Workbench.Localization.Localizer.IsRightToLeft)
        {
            FlowDirection = Avalonia.Media.FlowDirection.RightToLeft; // M23b: right-to-left languages (and the qps-plocm pseudo-language)
        }

        DataContext = shell;
        ApplyPlacement(shell.InitialPlacement);
        MenuBuilder.Attach(this, MenuBar, shell);
        DockDropSizer.Attach(this, shell.Layout.Factory);

        // Connect to <agent>'s view is made the first time it opens (M50): hidden, it still cost every start its controls
        // (0.6.0 added it; macOS's idle memory and startup grew). IsOpen changes before the view model's Opened, which the
        // view handles.
        shell.ConnectAgent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.ConnectAgentViewModel.IsOpen) && shell.ConnectAgent.IsOpen && ConnectAgentLayer.Children.Count == 0)
            {
                ConnectAgentLayer.Children.Add(new ConnectAgentView
                {
                    DataContext = shell.ConnectAgent,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            }
        };
        shell.Feedback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.FeedbackViewModel.IsOpen) && shell.Feedback.IsOpen && FeedbackLayer.Children.Count == 0)
            {
                FeedbackLayer.Children.Add(new FeedbackView
                {
                    DataContext = shell.Feedback,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            }
        };
        SetUpTitleBar();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnMouseNavigation, RoutingStrategies.Tunnel, handledEventsToo: true);
        SwitcherLayer.PointerPressed += (_, e) =>
        {
            if (e.Source == SwitcherLayer)
            {
                shell.Switcher.Close();
            }
        };
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
        CodeSearchLayer.PointerPressed += (_, e) =>
        {
            if (e.Source == CodeSearchLayer && !shell.CodeSearch.IsPinned)
            {
                shell.CodeSearch.Close();
            }
        };
        CodeSearchLayer.Background = shell.CodeSearch.IsPinned ? null : Avalonia.Media.Brushes.Transparent;
        shell.CodeSearch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.CodeSearchViewModel.IsPinned))
            {
                CodeSearchLayer.Background = shell.CodeSearch.IsPinned ? null : Avalonia.Media.Brushes.Transparent;
            }
        };
        shell.CodeSearch.Opened += (_, _) =>
        {
            if (!shell.CodeSearch.IsDocked && FocusManager?.GetFocusedElement() is Visual focused && !CodeSearch.IsVisualAncestorOf(focused))
            {
                _focusBeforeCodeSearch = focused as IInputElement;
            }
        };
        shell.CodeSearch.Closed += (_, _) =>
        {
            // The focus goes back where it was, unless a result opened a document (which took it).
            var now = FocusManager?.GetFocusedElement() as Visual;
            if (_focusBeforeCodeSearch is Visual previous && TopLevel.GetTopLevel(previous) == this && previous.IsEffectivelyVisible
                && (now is null || !now.IsEffectivelyVisible || CodeSearch.IsVisualAncestorOf(now)))
            {
                _focusBeforeCodeSearch.Focus();
            }

            _focusBeforeCodeSearch = null;
        };
        shell.FindReplace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModels.FindReplaceViewModel.IsOpen))
            {
                return;
            }

            if (shell.FindReplace.IsOpen)
            {
                _focusBeforeFind ??= FocusManager?.GetFocusedElement() is Visual focused && !FindReplace.IsVisualAncestorOf(focused) ? focused as IInputElement : null;
            }
            else
            {
                // Closed: the focus goes back where it was, unless something else took it meanwhile (an editor that
                // Find Next opened).
                var now = FocusManager?.GetFocusedElement() as Visual;
                if (_focusBeforeFind is Visual previous && TopLevel.GetTopLevel(previous) == this
                    && (now is null || !now.IsEffectivelyVisible || FindReplace.IsVisualAncestorOf(now)))
                {
                    _focusBeforeFind.Focus();
                }

                _focusBeforeFind = null;
            }
        };
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
    /// <remarks>
    /// The extended title bar stays <see cref="TitleBarPanel.FirstRowHeight"/> (32 px) tall when the menu wraps onto a
    /// second row in a narrow window (0.6.0): the caption buttons keep the first row's height at its right end, as in the
    /// classic IDE, and the second row is ordinary client area. Its empty parts (around the search box, the drag area
    /// under the first row's) drag the window like the first row's, through <see cref="Window.BeginMoveDrag"/>.
    /// </remarks>
    private void SetUpTitleBar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleRow.FirstRowHeight;
        TitleIcon.IsVisible = true;
        CaptionButtonsSpace.Width = CaptionButtonsWidth;
        TitleBar.PointerPressed += (_, e) =>
        {
            // The drag area, or the row's background between the menu, the search box and the name.
            if (e.Handled || (e.Source != TitleBar && e.Source != TitleRow && e.Source != TitleDragArea))
            {
                return;
            }

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

    /// <summary>
    /// What is wrong with the title row and the standard toolbar at the window's current width (0.6.0; the smoke test
    /// and the tests check it at several widths): anything reaching into the caption buttons' room or past the window's
    /// edge, a drag area under its minimum, a menu not shown, a toolbar on more than one row, a "…" with nothing in it.
    /// </summary>
    internal IReadOnlyList<string> TitleAndToolbarProblems()
    {
        var problems = new List<string>();
        var width = Bounds.Width;
        Rect In(Visual visual) => visual.TranslatePoint(default, this) is { } origin ? new Rect(origin, visual.Bounds.Size) : default;

        var caption = In(CaptionButtonsSpace);
        if (Math.Abs(caption.Right - width) > 1 || caption.Top > 1)
        {
            problems.Add($"the caption buttons' room is at {caption}, not at the top right of a window {width} px wide");
        }

        var right = width - CaptionButtonsSpace.Bounds.Width + 0.5;
        var parts = new List<(string Name, Visual Visual)> { ("the search box", SearchBox) };
        if (MenuBar.IsVisible)
        {
            parts.AddRange(MenuBar.Items.OfType<MenuItem>().Where(m => m.IsVisible).Select(m => ($"the {Avalonia.Automation.AutomationProperties.GetName(m)} menu", (Visual)m)));
        }

        parts.AddRange(TitleRow.Children.Where(c => TitleBarPanel.GetRole(c) is TitleBarRole.Name or TitleBarRole.Icon && c.IsVisible).Select(c => (TitleBarPanel.GetRole(c).ToString(), (Visual)c)));
        foreach (var (name, visual) in parts)
        {
            var rect = In(visual);
            if (!visual.IsEffectivelyVisible || rect.Width <= 0 || rect.X < -0.5 || rect.Right > right || rect.Bottom > TitleBar.Bounds.Height + 0.5
                || (visual is MenuItem && !In(MenuBar).Inflate(0.5).Contains(rect)))
            {
                problems.Add($"{name} is at {rect}, outside the title row's {width - CaptionButtonsSpace.Bounds.Width} px before the caption buttons");
            }
        }

        if (In(TitleDragArea) is var drag && (drag.Width < TitleRow.MinDragWidth - 0.5 || drag.Right > right))
        {
            problems.Add($"the drag area is at {drag}, under its {TitleRow.MinDragWidth} px or in the caption buttons' room");
        }

        if (this.GetVisualDescendants().OfType<StandardToolbar>().FirstOrDefault(t => t.IsEffectivelyVisible) is { } toolbar)
        {
            var row = In(toolbar.Panel);
            if (row.Height > 40)
            {
                problems.Add($"the standard toolbar is {row.Height} px tall: on more than one row");
            }

            // The row clips: an item past its right end is cut off, even when it's still inside the window.
            foreach (var item in toolbar.Panel.Children.Where(c => c.IsVisible && !toolbar.Panel.Folded.Contains(c)).Concat(toolbar.Panel.Overflows))
            {
                if (In(item) is var rect && (rect.Right > Math.Min(row.Right, width) + 0.5 || rect.Y < row.Y - 0.5 || rect.Bottom > row.Bottom + 0.5))
                {
                    problems.Add($"the toolbar's {Avalonia.Automation.AutomationProperties.GetName(item) ?? item.GetType().Name} is at {rect}, off its row of {row}");
                }
            }

            if (toolbar.Panel.Folded.Count > 0 && !toolbar.Panel.Overflows.Any())
            {
                problems.Add("the toolbar folded items without a \"…\" drop-down");
            }
        }

        return problems;
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
            Dispatcher.UIThread.Post(async () =>
            {
                BenchEvents.Emit(BenchEventNames.Interactive);

                // Started from the Finder or a desktop launcher, the startup's work (which starts processes and goes online)
                // waits for the login shell's PATH and network settings; the window didn't (M56). At once otherwise.
                await _shell.EnvironmentReady.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                _shell.OnInteractive();
                StartBenchDriver();
                if (_shell.Options.Screenshot is { } screenshot && _shell.Options.Remote is not null)
                {
                    // A remote window (M21): once it's connected and its C# service is ready (three minutes at most).
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    DispatcherTimer? poll = null;
                    poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
                    {
                        if (started.Elapsed > TimeSpan.FromMinutes(3) || _shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready" })
                        {
                            poll!.Stop();
                            DispatcherTimer.RunOnce(() => SaveScreenshot(screenshot), TimeSpan.FromSeconds(2));
                        }
                    });
                    poll.Start();
                }
                else if (_shell.Options.Screenshot is { } shot)
                {
                    // Late enough for startup plugins (the Start page) to have rendered.
                    DispatcherTimer.RunOnce(() => SaveScreenshot(shot), TimeSpan.FromSeconds(4));
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
        if (_shuttingDown)
        {
            return; // the shutdown below, closing the window for good
        }

        if (!_closeConfirmed && _shell.HasUnsavedDocuments)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }

        _shell.SaveState(Placement);

        // The user closed the window (or quit): it goes at once, and the shell shuts down behind it with the UI thread free
        // (M56); the lifetime's Exit waited for that on the UI thread, up to 3 s, with the window gone and the app not
        // answering. A forced shutdown (the smoke test's, the benchmarks') can't be cancelled, and the OS's logout mustn't
        // be: the Exit still waits for those.
        if (e.CloseReason is WindowCloseReason.OSShutdown || (e.CloseReason is WindowCloseReason.ApplicationShutdown && e.IsProgrammatic))
        {
            return;
        }

        e.Cancel = true;
        _ = ShutDownAsync();
    }

    private bool _shuttingDown;

    private async Task ShutDownAsync()
    {
        _shuttingDown = true;
        _shell.StopListening(); // a codalus started now opens its own window, not one in this closing process
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        foreach (var window in desktop?.Windows.ToList() ?? [this])
        {
            window.Hide(); // the floating tool windows and the start window too
        }

        await _shell.ShutDownAsync(ShellHost.ShutdownLimit).ConfigureAwait(true);
        if (desktop is not null)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
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

    private WindowState _beforeFullScreen = WindowState.Normal;

    public bool IsFullScreen
    {
        get => WindowState == WindowState.FullScreen;
        set
        {
            if (value == IsFullScreen)
            {
                return;
            }

            if (value)
            {
                _beforeFullScreen = WindowState;
                WindowState = WindowState.FullScreen;
            }
            else
            {
                WindowState = _beforeFullScreen;
            }
        }
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
        {
            // Activate alone only flashes the taskbar button while another program has the focus.
            Codalus.Platform.WindowsForeground.Bring(handle.Handle);
        }
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

    /// <summary>
    /// Dropping a solution, project or folder from the file manager opens it; dropped files open in editors together, the
    /// last one active (M56).
    /// </summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || e.DataTransfer.TryGetFiles() is not [{ } item, ..] items || item.TryGetLocalPath() is not { } path)
        {
            return;
        }

        e.Handled = true;
        if (Directory.Exists(path) || Codalus.Workbench.Projects.SolutionTree.IsSolutionOrProjectFile(path))
        {
            _ = Directory.Exists(path) ? _shell.OpenFolderAsync(path) : _shell.OpenSolutionAsync(path);
            return;
        }

        _shell.OpenFromOs([.. items.Select(i => i.TryGetLocalPath()).OfType<string>()]);
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

        // Code Search takes Esc, Enter, the arrows, Alt and a chip's letter, and Ctrl+S in its preview, before key bindings.
        if (e.Source is Visual source && source.FindAncestorOfType<CodeSearchView>(includeSelf: true) is { } codeSearch && codeSearch.TakesKey(e))
        {
            return;
        }

        // The Ctrl+Tab switcher: the arrows move, Enter switches, Esc leaves (Tab comes through its key binding).
        if (_shell.Switcher.IsOpen && e.Key is Key.Up or Key.Down or Key.Enter or Key.Escape)
        {
            switch (e.Key)
            {
                case Key.Up:
                    _shell.Switcher.Move(-1);
                    break;
                case Key.Down:
                    _shell.Switcher.Move(+1);
                    break;
                case Key.Enter:
                    _shell.Switcher.Commit();
                    break;
                default:
                    _shell.Switcher.Close();
                    break;
            }

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
                _shell.StatusBar.ShowMessage(Localizer.Format("msg.mainWindowaxaml.wasPressedWaitingForTheNext", "({0}) was pressed. Waiting for the next key…", result.Pending!.ToLabel(platform)));
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
                    _shell.StatusBar.ShowMessage(Localizer.Format("msg.mainWindowaxaml.theKeyCombinationIsNotA", "The key combination ({0}) is not a command.", chord.ToLabel(platform)), TimeSpan.FromSeconds(4));
                }

                break;
        }
    }

    /// <summary>Releasing Ctrl switches to the document the Ctrl+Tab switcher has selected.</summary>
    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (_shell.Switcher.IsOpen && e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _shell.Switcher.Commit();
            e.Handled = true;
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
        if (source is not null and not MenuItem && source.FindAncestorOfType<MenuBase>() is null)
        {
            _shell.LastFocus = source; // Edit → Cut/Copy/Paste act on it after a menu took the focus (M34a)
        }

        var pane = PaneHost.Of(source);
        var view = pane?.Dockable switch
        {
            ToolWindowDockable tool => tool.Id,
            ViewModels.SolutionManagementDocument { Id: "wcfTestClient" } => "wcfTestClient", // F5 invokes there (M34h)
            ShellDocument document => document.Kind,
            _ => null,
        };
        _shell.Workbench.Context.Set("focusedView", view);
        _shell.Workbench.Context.Set("editorTextFocus", view == "textFile");

        // A property grid has the keyboard (the Properties window's, or the collection editor's in a designer): F4 opens its drop-down (#68).
        _shell.Workbench.Context.Set("propertyGridFocus", source?.FindAncestorOfType<PropertyGridView>(includeSelf: true) is not null);
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
