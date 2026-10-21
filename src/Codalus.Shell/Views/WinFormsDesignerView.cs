using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Views;

/// <summary>
/// The Windows Forms designer's view (M41a and M41b, ADR 0073): the designer host's frame of the form, and over it the
/// surface's own adorners and gestures: selection (a click; Ctrl or Shift to add; a rubber band), moving (a drag, into
/// another container when dropped in one) and resizing (the primary selection's eight grab handles) with Visual
/// Studio's snap lines (Alt turns them off), the arrow keys (Shift to resize), Delete, Ctrl+C, Ctrl+V and Ctrl+D,
/// Ctrl+Z and Ctrl+Y (the designer's undo), a double-click (the default event's handler), the Toolbox (a click or a
/// drag with an item armed, or an item dragged in), tab order mode, smart tags, menus and tool strips edited in place
/// (Type Here), the component tray, and the collection editors.
/// </summary>
public sealed class WinFormsDesignerView : UserControl, IPaneView
{
    /// <summary>A Toolbox item dragged onto the surface: its type's full name.</summary>
    public static readonly DataFormat<string> ToolboxFormat = DataFormat.CreateInProcessFormat<string>("codalus.toolbox.type");

    /// <summary>A tray component dragged within the tray: its name.</summary>
    public static readonly DataFormat<string> TrayFormat = DataFormat.CreateInProcessFormat<string>("codalus.winforms.tray");

    private readonly WinFormsDesignerDocument _document;
    private readonly DesignerSurface _surface;
    private readonly ScrollViewer _scroll;
    private readonly Canvas _overlay = new() { ClipToBounds = false };
    private readonly Border _collectionHost = new() { IsVisible = false, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
    private string? _renaming;

    public WinFormsDesignerView(WinFormsDesignerDocument document)
    {
        _document = document;
        DataContext = document;
        _surface = new DesignerSurface(document, this);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                Padding = new Thickness(16),
                Child = new Panel { Children = { _surface, _overlay } },
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            },
        };
        _scroll.Bind(BackgroundProperty, _scroll.GetResourceObservable("Shell.Editor.Background"));

        Button Tool(string icon, string name, System.Windows.Input.ICommand command)
        {
            var button = new Button { Content = new Icon { Kind = icon }, Classes = { "toolbar" }, Command = command };
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
            return button;
        }

        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "muted" }, Margin = new Thickness(8, 0) };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        var busy = new ProgressBar { IsIndeterminate = true, Width = 60, Height = 2, MinHeight = 2, VerticalAlignment = VerticalAlignment.Center };
        var language = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Classes = { "muted" } };
        var toolbar = new DockPanel
        {
            Margin = new Thickness(6, 4),
            Children =
            {
                Tool("code", Localizer.Get("msg.winFormsDesigner.viewCode", "View Code (F7)"), document.ViewCodeCommand),
                Tool("refresh", Localizer.Get("msg.winFormsDesigner.reload", "Load the form again (and restart the designer)"), document.RefreshCommand),
                busy,
                language,
                status,
            },
        };
        foreach (var child in toolbar.Children.Take(4))
        {
            DockPanel.SetDock((Control)child, Avalonia.Controls.Dock.Left);
        }

        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);

        // The statements the designer couldn't run, in the error color; a long list scrolls (M34g: it was cut at 120 px).
        var problems = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4), Classes = { "error" } };
        Avalonia.Automation.AutomationProperties.SetName(problems, Localizer.Get("msg.winFormsDesigner.problems", "What the designer couldn't run"));
        var problemBar = new Border { Child = new ScrollViewer { Content = problems, MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, BorderThickness = new Thickness(0, 1, 0, 0) };
        problemBar.Bind(Border.BorderBrushProperty, problemBar.GetResourceObservable("Shell.Border"));
        DockPanel.SetDock(problemBar, Avalonia.Controls.Dock.Bottom);

        var trayBar = new Border { Child = Tray(), BorderThickness = new Thickness(0, 1, 0, 0), MinHeight = 34 };
        trayBar.Bind(Border.BorderBrushProperty, trayBar.GetResourceObservable("Shell.Border"));
        DockPanel.SetDock(trayBar, Avalonia.Controls.Dock.Bottom);

        // Before the first frame (opening, reading the form, starting the designer) and when it fails: the status where
        // the form will be, wrapped (M34g: a failure's reason was one trimmed line in the toolbar).
        var message = new TextBlock { Classes = { "emptyState" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(message, Avalonia.Automation.AutomationLiveSetting.Polite);
        _collectionHost.Bind(Border.BackgroundProperty, _collectionHost.GetResourceObservable("Shell.Panel"));
        _collectionHost.Bind(Border.BorderBrushProperty, _collectionHost.GetResourceObservable("Shell.Border"));
        var surfaceArea = new Grid { Children = { _scroll, message, _collectionHost } };

        Content = new DockPanel { Children = { toolbar, problemBar, trayBar, surfaceArea } };

        // The document's state, without reflection bindings (the shell is Native AOT). With a Toolbox item armed, the
        // status line says what a click or a drag on the form does (#66: that hint sat above the Toolbox's list).
        void Update()
        {
            var line = document.Toolbox.ArmedText ?? document.Status;
            status.Text = line;
            ToolTip.SetTip(status, string.IsNullOrEmpty(line) ? null : line);
            message.Text = document.Status;
            message.IsVisible = document.Frame is null && !string.IsNullOrEmpty(document.Status);
            busy.IsVisible = document.IsBusy;
            problems.Text = document.Problems;
            problemBar.IsVisible = document.HasProblems;
            trayBar.IsVisible = document.HasTray;
            language.Text = document.IsLocalizable ? Localizer.Format("msg.winFormsDesigner.languageShown", "Language: {0}", document.Culture.Length == 0 ? Localizer.Get("msg.winFormsDesigner.defaultLanguage", "(Default)") : document.Culture) : null;
            language.IsVisible = document.IsLocalizable;
        }

        Update();
        document.PropertyChanged += (_, e) => Infrastructure.Ui.Post(() =>
        {
            Update();
            if (e.PropertyName == nameof(WinFormsDesignerDocument.ActionPanel))
            {
                ShowActions();
            }
            else if (e.PropertyName == nameof(WinFormsDesignerDocument.CollectionEditor))
            {
                ShowCollectionEditor();
            }
        });
        document.SurfaceChanged += (_, _) => Infrastructure.Ui.Post(Update);

        // The Toolbox outlives the designer: its armed item is followed only while the view is shown.
        void OnToolboxChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ToolboxViewModel.ArmedText))
            {
                Infrastructure.Ui.Post(Update);
            }
        }

        AttachedToVisualTree += (_, _) =>
        {
            document.Toolbox.PropertyChanged += OnToolboxChanged;
            Update();
        };
        DetachedFromVisualTree += (_, _) => document.Toolbox.PropertyChanged -= OnToolboxChanged;
        Avalonia.Automation.AutomationProperties.SetName(_surface, document.Title);
        _surface.ContextRequested += (_, e) =>
        {
            if (MenuBuilder.Context(document.Shell, BuiltIns.WinFormsSurfaceMenu) is { } menu)
            {
                menu.Open(_surface);
                e.Handled = true;
            }
        };
    }

    public DesignerSurface Surface => _surface;

    public void FocusPrimary() => _surface.Focus();

    // ------------------------------------------------------------------ the component tray

    private ItemsControl Tray()
    {
        var tray = new ItemsControl
        {
            ItemsSource = _document.Tray,
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DesignerTrayItem>((item, _) => item is null ? null : TrayItem(item)),
            Margin = new Thickness(6, 2),
        };
        Avalonia.Automation.AutomationProperties.SetName(tray, Localizer.Get("msg.winFormsDesigner.tray", "Component tray"));
        DragDrop.SetAllowDrop(tray, true);
        DragDrop.AddDragOverHandler(tray, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(TrayFormat) ? DragDropEffects.Move : TrayToolOf(e.DataTransfer) is not null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DragDrop.AddDropHandler(tray, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(TrayFormat) is { Length: > 0 } name)
            {
                var before = (e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true)?.Tag as string;
                e.Handled = true;
                _ = _document.MoveTrayItemAsync(name, before == name ? null : before);
            }
            else if (TrayToolOf(e.DataTransfer) is { } type)
            {
                // A component from the Toolbox (#66), as the classic component tray takes one: the host puts it in the tray.
                e.Handled = true;
                _ = _document.CreateAsync(type, 0, 0);
            }
        });
        tray.ContextMenu = new ContextMenu { Items = { MenuEntry.Create(Localizer.Get("msg.winFormsDesigner.lineUpIcons", "Line Up Icons"), "layout", click: () => _ = _document.LineUpTrayAsync()) } };
        return tray;
    }

    private Control TrayItem(DesignerTrayItem item)
    {
        if (_renaming == item.Name)
        {
            var box = new TextBox { Text = item.Name, MinWidth = 120, Margin = new Thickness(2) };
            Avalonia.Automation.AutomationProperties.SetName(box, Localizer.Format("msg.winFormsDesigner.renameTray", "Rename {0}", item.Name));
            void Commit(bool keep)
            {
                if (_renaming != item.Name)
                {
                    return;
                }

                _renaming = null;
                if (keep && box.Text is { Length: > 0 } name && name != item.Name)
                {
                    _ = _document.RenameAsync(item.Name, name.Trim());
                }

                _document.RefreshTray();
            }

            box.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Escape)
                {
                    Commit(e.Key == Key.Enter);
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => Commit(keep: true);
            box.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
            return box;
        }

        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new Icon { Kind = item.Icon, Width = 16, Height = 16 }, new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center } } },
            Command = item.Select,
            Classes = { "toolbar" },
            Margin = new Thickness(2),
            Tag = item.Name,
            BorderThickness = new Thickness(1),
        };
        if (item.IsSelected)
        {
            button.Bind(Button.BorderBrushProperty, button.GetResourceObservable("Shell.Accent"));
        }

        ToolTip.SetTip(button, $"{item.Name} ({item.ShortType})");
        Avalonia.Automation.AutomationProperties.SetName(button, item.Name);
        button.DoubleTapped += (_, _) => _ = _document.DefaultEventOfAsync(item.Name);
        button.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.F2:
                    StartRename(item.Name);
                    e.Handled = true;
                    break;
                case Key.Delete:
                    item.Select.Execute(null);
                    _ = _document.DeleteSelectionAsync();
                    e.Handled = true;
                    break;
            }
        };
        var rename = MenuEntry.Create(Localizer.Get("msg.winFormsDesigner.rename", "Rename"), "rename", click: () => StartRename(item.Name));
        var delete = MenuEntry.Create(Localizer.Get("msg.winFormsDesigner.delete", "Delete"), "trash", click: () =>
        {
            item.Select.Execute(null);
            _ = _document.DeleteSelectionAsync();
        });
        var lineUp = MenuEntry.Create(Localizer.Get("msg.winFormsDesigner.lineUpIcons", "Line Up Icons"), "layout", click: () => _ = _document.LineUpTrayAsync());
        button.ContextMenu = new ContextMenu { Items = { rename, delete, new Separator(), lineUp } };

        // Dragged before another tray component: the tray's order.
        PointerPressedEventArgs? pressed = null;
        button.AddHandler(PointerPressedEvent, (_, e) => pressed = e, RoutingStrategies.Tunnel);
        button.AddHandler(PointerReleasedEvent, (_, _) => pressed = null, RoutingStrategies.Tunnel);
        button.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (pressed is { } start && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed && Point.Distance(start.GetPosition(button), e.GetPosition(button)) > 6)
            {
                pressed = null;
                var data = new DataTransfer();
                data.Add(DataTransferItem.Create(TrayFormat, item.Name));
                await DragDrop.DoDragDropAsync(start, data, DragDropEffects.Move);
            }
        }, RoutingStrategies.Tunnel);
        return button;
    }

    /// <summary>
    /// A Toolbox item the tray takes (#66): a component that isn't a control (a timer, a tool tip, a context menu), as
    /// the classic component tray does; a control is refused. Not in tab order mode or while the host starts again.
    /// </summary>
    private string? TrayToolOf(IDataTransfer data) =>
        !_document.IsTabOrderMode && !_document.IsRestarting && data.TryGetValue(ToolboxFormat) is { Length: > 0 } type
        && _document.Toolbox.Items.FirstOrDefault(i => i.Type == type) is { IsControl: false } ? type : null;

    public void StartRename(string name)
    {
        _renaming = name;
        _document.RefreshTray();
    }

    // ------------------------------------------------------------------ the inline editor (Type Here, an item's text)

    /// <summary>A text box over the surface at <paramref name="bounds"/>: Enter commits, Esc cancels.</summary>
    public void Edit(DesignerRect bounds, string text, Func<string, Task> commit)
    {
        foreach (var old in _overlay.Children.OfType<TextBox>().ToList())
        {
            _overlay.Children.Remove(old);
        }

        var box = new TextBox { Text = text, Width = Math.Max(80, bounds.Width), MinHeight = Math.Max(20, bounds.Height), Padding = new Thickness(4, 1), FontSize = 12 };
        Avalonia.Automation.AutomationProperties.SetName(box, Localizer.Get("msg.winFormsDesigner.typeHere", "Type Here"));
        Canvas.SetLeft(box, bounds.X);
        Canvas.SetTop(box, bounds.Y);
        var done = false;
        void Finish(bool keep)
        {
            if (done)
            {
                return;
            }

            done = true;
            _overlay.Children.Remove(box);
            _surface.Focus();
            if (keep && box.Text is { Length: > 0 } value)
            {
                _ = commit(value);
            }
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Finish(e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Finish(keep: true);
        _overlay.Children.Add(box);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>A Type Here box of a tool strip or status strip: its item types, to pick one.</summary>
    public void PickItemType(DesignerTypeHere typeHere)
    {
        var menu = new ContextMenu();
        foreach (var type in typeHere.Types)
        {
            menu.Items.Add(MenuEntry.Create(type[(type.LastIndexOf('.') + 1)..], null, click: () => _ = _document.AddItemAsync(typeHere, text: null, type)));
        }

        menu.Open(_surface);
    }

    // ------------------------------------------------------------------ smart tags

    private void ShowActions()
    {
        foreach (var old in _overlay.Children.Where(c => c.Tag is "actions").ToList())
        {
            _overlay.Children.Remove(old);
        }

        if (_document.ActionPanel is not { } panel || _document.Find(panel.Component) is not { } view)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 4, Margin = new Thickness(10, 8), MinWidth = 200 };
        var header = new DockPanel { Children = { new TextBlock { Text = panel.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
        var close = new Button { Content = new Icon { Kind = "close" }, Classes = { "toolbar" }, Command = panel.CloseCommand };
        Avalonia.Automation.AutomationProperties.SetName(close, Localizer.Get("msg.winFormsDesigner.closeActions", "Close"));
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Right);
        header.Children.Insert(0, close);
        rows.Children.Add(header);
        if (panel.IsEmpty)
        {
            rows.Children.Add(new TextBlock { Text = Localizer.Get("msg.winFormsDesigner.noActions", "No tasks."), Classes = { "muted" } });
        }

        foreach (var row in panel.Rows)
        {
            rows.Children.Add(ActionRow(row));
        }

        var border = new Border { Child = rows, BorderThickness = new Thickness(1), Tag = "actions", BoxShadow = BoxShadows.Parse("0 2 8 0 #40000000") };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Shell.Panel"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("Shell.Border"));
        border.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _document.CloseActions();
                e.Handled = true;
            }
        };
        Canvas.SetLeft(border, view.X + view.Width + 14);
        Canvas.SetTop(border, view.Y);
        _overlay.Children.Add(border);
    }

    private static Control ActionRow(DesignerActionRowViewModel row)
    {
        Control control;
        if (row.IsHeader)
        {
            control = new TextBlock { Text = row.Title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        }
        else if (row.IsText)
        {
            control = new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        }
        else if (row.IsMethod)
        {
            control = new Button { Content = row.Title, Classes = { "link" }, Command = row.RunCommand, IsEnabled = row.IsAvailable, HorizontalAlignment = HorizontalAlignment.Left };
        }
        else if (row.IsBoolean)
        {
            var check = new CheckBox { Content = row.Title, IsChecked = row.IsChecked, IsEnabled = row.IsAvailable };
            check.IsCheckedChanged += (_, _) => row.IsChecked = check.IsChecked == true;
            control = check;
        }
        else
        {
            Control editor;
            if (row.IsChoice)
            {
                var combo = new ComboBox { ItemsSource = row.Options, SelectedItem = row.Selected, MinWidth = 120, IsEnabled = row.IsAvailable };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string value)
                    {
                        row.Selected = value;
                    }
                };
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = row.Text, MinWidth = 120, IsEnabled = row.IsAvailable };
                box.LostFocus += (_, _) => row.Text = box.Text ?? string.Empty;
                editor = box;
            }

            Avalonia.Automation.AutomationProperties.SetName(editor, row.Title);
            control = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = row.Title }, editor } };
        }

        ToolTip.SetTip(control, row.Description);
        Avalonia.Automation.AutomationProperties.SetName(control, row.Title);
        return control;
    }

    // ------------------------------------------------------------------ the collection editor

    private void ShowCollectionEditor()
    {
        if (_document.CollectionEditor is not { } editor)
        {
            _collectionHost.IsVisible = false;
            _collectionHost.Child = null;
            return;
        }

        _collectionHost.Child = CollectionEditor(editor);
        _collectionHost.IsVisible = true;
    }

    private static DockPanel CollectionEditor(DesignerCollectionEditorViewModel editor)
    {
        var title = new TextBlock { Text = editor.Title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        Button Command(string text, System.Windows.Input.ICommand command, bool accent = false)
        {
            var button = new Button { Content = text, Command = command, Margin = new Thickness(0, 0, 6, 0) };
            if (accent)
            {
                button.Classes.Add("accent");
            }

            Avalonia.Automation.AutomationProperties.SetName(button, text);
            return button;
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        if (!editor.IsLive)
        {
            footer.Children.Add(Command(Localizer.Get("msg.winFormsDesigner.ok", "OK"), editor.OkCommand, accent: true));
        }

        footer.Children.Add(Command(editor.IsLive ? Localizer.Get("msg.winFormsDesigner.close", "Close") : Localizer.Get("msg.winFormsDesigner.cancel", "Cancel"), editor.CloseCommand));
        Control body;
        if (editor.IsText)
        {
            var hint = new TextBlock { Text = Localizer.Get("msg.winFormsDesigner.oneLineEach", "One item a line:"), Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 4) };
            var box = new TextBox { Text = editor.Text, AcceptsReturn = true, Height = 220, Width = 380, TextWrapping = TextWrapping.NoWrap };
            box.TextChanged += (_, _) => editor.Text = box.Text ?? string.Empty;
            Avalonia.Automation.AutomationProperties.SetName(box, editor.Title);
            editor.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DesignerCollectionEditorViewModel.Text) && box.Text != editor.Text)
                {
                    box.Text = editor.Text;
                }
            };
            body = new StackPanel { Children = { hint, box } };
        }
        else
        {
            body = ItemsEditor(editor);
        }

        return new DockPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                Docked(title, Avalonia.Controls.Dock.Top),
                Docked(footer, Avalonia.Controls.Dock.Bottom),
                body,
            },
        };
    }

    private static Control Docked(Control control, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static StackPanel ItemsEditor(DesignerCollectionEditorViewModel editor)
    {
        var list = new ListBox { Width = 240, Height = 260, ItemsSource = editor.Items };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DesignerCollectionItemViewModel>((item, _) => new TextBlock { Text = item?.Display });
        list.SelectionChanged += (_, _) => editor.Selected = list.SelectedItem as DesignerCollectionItemViewModel;
        Avalonia.Automation.AutomationProperties.SetName(list, Localizer.Get("msg.winFormsDesigner.members", "Members"));
        Button Small(string icon, string name, System.Windows.Input.ICommand command)
        {
            var button = new Button { Content = new Icon { Kind = icon }, Classes = { "toolbar" }, Command = command };
            ToolTip.SetTip(button, name);
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            return button;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        if (editor.HasItemTypes)
        {
            var types = new ComboBox { ItemsSource = editor.ItemTypes, SelectedItem = editor.ItemType, MinWidth = 120 };
            types.SelectionChanged += (_, _) => editor.ItemType = types.SelectedItem as string ?? editor.ItemType;
            Avalonia.Automation.AutomationProperties.SetName(types, Localizer.Get("msg.winFormsDesigner.itemType", "Item type"));
            buttons.Children.Add(types);
        }

        buttons.Children.Add(Small("new", Localizer.Get("msg.winFormsDesigner.add", "Add"), editor.AddCommand));
        buttons.Children.Add(Small("trash", Localizer.Get("msg.winFormsDesigner.remove", "Remove"), editor.RemoveCommand));
        buttons.Children.Add(Small("arrow-up", Localizer.Get("msg.winFormsDesigner.moveUp", "Move Up"), editor.MoveUpCommand));
        buttons.Children.Add(Small("arrow-down", Localizer.Get("msg.winFormsDesigner.moveDown", "Move Down"), editor.MoveDownCommand));
        var left = new StackPanel { Children = { list, buttons } };

        var details = new StackPanel { Spacing = 4, Width = 300, Margin = new Thickness(12, 0, 0, 0) };
        void Details()
        {
            details.Children.Clear();
            if (editor.IsStyles && editor.Selected is { } style)
            {
                var sizeType = new ComboBox { ItemsSource = DesignerCollectionItemViewModel.SizeTypes, SelectedItem = style.SizeType, MinWidth = 120 };
                sizeType.SelectionChanged += (_, _) => style.SizeType = sizeType.SelectedItem as string ?? style.SizeType;
                var size = new TextBox { Text = style.Size, MinWidth = 80 };
                size.LostFocus += (_, _) => style.Size = size.Text ?? string.Empty;
                Avalonia.Automation.AutomationProperties.SetName(sizeType, Localizer.Get("msg.winFormsDesigner.sizeType", "Size Type"));
                Avalonia.Automation.AutomationProperties.SetName(size, Localizer.Get("msg.winFormsDesigner.size", "Size"));
                details.Children.Add(new TextBlock { Text = Localizer.Get("msg.winFormsDesigner.sizeType", "Size Type") });
                details.Children.Add(sizeType);
                details.Children.Add(new TextBlock { Text = Localizer.Get("msg.winFormsDesigner.size", "Size") });
                details.Children.Add(size);
                return;
            }

            foreach (var row in editor.Properties)
            {
                details.Children.Add(PropertyRow(row));
            }
        }

        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignerCollectionEditorViewModel.Selected))
            {
                Infrastructure.Ui.Post(() =>
                {
                    if (!ReferenceEquals(list.SelectedItem, editor.Selected))
                    {
                        list.SelectedItem = editor.Selected;
                    }

                    Details();
                });
            }
        };
        editor.Properties.CollectionChanged += (_, _) => Infrastructure.Ui.Post(Details);
        list.SelectedItem = editor.Selected;
        Details();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { left, new ScrollViewer { Content = details, MaxHeight = 300 } } };
    }

    /// <summary>A collection item's property in the editor's grid: its name and its editor (text, choice, flags), or its value.</summary>
    private static Grid PropertyRow(PropertyRowViewModel row)
    {
        Control editor;
        if (row.IsChoice || row.IsDock)
        {
            var combo = new ComboBox { ItemsSource = row.Options.Select(o => o.Label).ToList(), SelectedItem = row.SelectedOption?.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => row.SelectedOption = row.Options.FirstOrDefault(o => o.Label == combo.SelectedItem as string);
            editor = combo;
        }
        else if (row.IsTextEditor)
        {
            var box = new TextBox { Text = row.Text };
            box.LostFocus += (_, _) => row.Text = box.Text ?? string.Empty;
            editor = box;
        }
        else if (row.IsFlags || row.IsAnchor)
        {
            var flags = new WrapPanel();
            foreach (var flag in row.Flags)
            {
                var toggle = new ToggleButton { Content = flag.Name, IsChecked = flag.IsChecked, Classes = { "toolbar" }, Padding = new Thickness(4, 0) };
                toggle.IsCheckedChanged += (_, _) => flag.IsChecked = toggle.IsChecked == true;
                flags.Children.Add(toggle);
            }

            editor = flags;
        }
        else
        {
            editor = new SelectableTextBlock { Text = row.Value, Margin = new Thickness(4, 3) };
        }

        Avalonia.Automation.AutomationProperties.SetName(editor, row.Name);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var name = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = row.IsSet ? FontWeight.SemiBold : FontWeight.Normal };
        ToolTip.SetTip(name, row.Description);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(name);
        grid.Children.Add(editor);
        return grid;
    }
}

/// <summary>The frame and the adorners over it, and the pointer and keyboard gestures (frame coordinates: one unit a pixel of the form).</summary>
public sealed class DesignerSurface : Control
{
    /// <summary>A grab handle's side in device pixels at 100 % (odd, so it centers on the frame's line).</summary>
    private const int Handle = 7;
    private const double Glyph = 12;
    private readonly WinFormsDesignerDocument _document;
    private readonly WinFormsDesignerView _view;
    private Point? _pressed;
    private Mode _mode;
    private int _handle = -1;
    private Point _current;
    private string? _bandContainer;
    private bool _moved;
    private DesignerSnap? _snap;

    private enum Mode
    {
        None,
        Move,
        Resize,
        Band,
        Draw,
    }

    public DesignerSurface(WinFormsDesignerDocument document, WinFormsDesignerView view)
    {
        _document = document;
        _view = view;
        Focusable = true;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        document.SurfaceChanged += (_, _) => Infrastructure.Ui.Post(() =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        });
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WinFormsDesignerDocument.IsRestarting))
            {
                Infrastructure.Ui.Post(OnRestartingChanged);
            }
        };
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) =>
        {
            // TODO(issue): show the tool's outline and snap lines while a Toolbox item is dragged over the form, as the classic IDE does.
            e.DragEffects = AcceptsToolboxDrop(e.DataTransfer, e.GetPosition(this)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            var at = e.GetPosition(this);
            if (AcceptsToolboxDrop(e.DataTransfer, at) && e.DataTransfer.TryGetValue(WinFormsDesignerView.ToolboxFormat) is { Length: > 0 } type)
            {
                e.Handled = true;
                Focus();
                _ = DropToolboxItemAsync(type, at);
            }
        });
    }

    /// <summary>
    /// A Toolbox item dropped here creates it (#66): on the form only (outside it the cursor says no drop, as the classic
    /// IDE's), not in tab order mode (tools are ignored there), not while the host starts again.
    /// </summary>
    private bool AcceptsToolboxDrop(IDataTransfer data, Point at) =>
        data.Contains(WinFormsDesignerView.ToolboxFormat) && !_document.IsTabOrderMode && !_document.IsRestarting && _document.HitTest(at.X, at.Y) is not null;

    /// <summary>What a Toolbox item dropped at <paramref name="at"/> (frame coordinates) does: the drop handler's own work (tests and the smoke test).</summary>
    internal Task DropToolboxItemAsync(string type, Point at) => _document.CreateAsync(type, at.X, at.Y);

    /// <summary>The snap lines of the gesture in progress (tests).</summary>
    public IReadOnlyList<DesignerSnapLine> SnapLines => _snap?.Lines ?? [];

    /// <summary>
    /// The designer host starts again (#66): a gesture in progress ends (its frame is about to be replaced) and the
    /// surface takes none until the form is back, showing the wait cursor.
    /// </summary>
    private void OnRestartingChanged()
    {
        if (_document.IsRestarting)
        {
            _pressed = null;
            _mode = Mode.None;
            _snap = null;
            _moved = false;
        }

        Cursor = new Cursor(_document.IsRestarting ? StandardCursorType.Wait : StandardCursorType.Arrow);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(1, _document.FrameWidth);
        var height = Math.Max(1, _document.FrameHeight);
        foreach (var panel in _document.MenuPanels)
        {
            width = Math.Max(width, panel.Bounds.Right + 2);
            height = Math.Max(height, panel.Bounds.Bottom + 2);
        }

        return new Size(width, height);
    }

    private IBrush Resource(string key, IBrush fallback) => (this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null) ?? fallback;

    public override void Render(DrawingContext context)
    {
        if (_document.Frame is { } frame)
        {
            context.DrawImage(frame, new Rect(0, 0, _document.FrameWidth, _document.FrameHeight));
        }
        else
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        }

        // The adorners in the theme's tokens (M34g: the handles were white and the rubber band DodgerBlue in every theme).
        var accent = Token("Shell.Accent") ?? Token("Shell.Focus") ?? Brushes.Gray;
        var handleFill = Token("Shell.Panel") ?? Brushes.Transparent;
        var stroke = Pixel * Math.Max(1, Math.Round(Scaling));
        var thin = new Pen(accent, stroke, new DashStyle([2, 2], 0));
        var solid = new Pen(accent, stroke);
        if (_document.IsTabOrderMode)
        {
            RenderTabOrder(context, accent);
            return;
        }

        foreach (var name in _document.Selection)
        {
            if (_document.Find(name) is not { } view || !(view.IsControl || (view.IsItem && view.Visible)) || view.Width <= 0)
            {
                continue;
            }

            var rect = Rect(WinFormsDesignerDocument.Bounds(view));
            if (_mode == Mode.Move && _moved && view != _document.Root && _snap is not null && _document.Find(_document.PrimarySelection ?? string.Empty) is { } primary)
            {
                rect = rect.Translate(new Vector(_snap.Bounds.X - primary.X, _snap.Bounds.Y - primary.Y));
            }
            else if (_mode == Mode.Move && _moved && view != _document.Root)
            {
                rect = rect.Translate(_current - _pressed!.Value);
            }

            if (_mode == Mode.Resize && name == _document.PrimarySelection)
            {
                rect = Rect(_snap?.Bounds ?? Resized(WinFormsDesignerDocument.Bounds(view)));
            }

            // M34h: a designer's selection: a thin accent frame on the device pixels just outside the control's bounds and
            // grab handles centered on its corners and edges (the frame and the handles straddled pixels, drawn blurred
            // two pixels wide, and the handles centered on the control's edges, a pixel inside the frame).
            // Filled, not stroked: a filled rectangle on pixel edges covers its pixels fully, in the accent itself.
            var outline = Frame(rect);
            if (name == _document.PrimarySelection)
            {
                FillOutline(context, outline, stroke, accent);
            }
            else
            {
                context.DrawRectangle(null, thin, outline.Deflate(stroke / 2));
            }

            if (name == _document.PrimarySelection && !view.IsItem)
            {
                foreach (var handle in Handles(rect))
                {
                    context.FillRectangle(accent, handle);
                    context.FillRectangle(handleFill, handle.Deflate(stroke));
                }

                RenderLayoutAdorners(context, view, rect, thin, accent);
                if (view.HasActions && _mode == Mode.None)
                {
                    RenderGlyph(context, GlyphRect(rect), solid, accent, handleFill);
                }
            }
        }

        RenderMenus(context, thin, solid);
        if (_snap is { Lines: { Count: > 0 } lines } && _moved)
        {
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xFF)), 1);
            var baseline = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x00, 0xE0)), 1);
            var margin = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)), 1, new DashStyle([3, 2], 0));
            foreach (var line in lines)
            {
                context.DrawLine(line.Kind switch { DesignerSnapLine.Baseline => baseline, DesignerSnapLine.Margin => margin, _ => edge }, new Point(line.X1, line.Y1), new Point(line.X2, line.Y2));
            }
        }

        if (_mode is Mode.Band or Mode.Draw && _pressed is { } start)
        {
            var band = accent is ISolidColorBrush solidAccent ? new SolidColorBrush(solidAccent.Color, 0.12) : null;
            context.DrawRectangle(band, thin, new Rect(start, _current).Normalize());
        }
    }

    private IBrush? Token(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;

    /// <summary>A rectangle's outline <paramref name="stroke"/> wide inside it, as four filled bars.</summary>
    private static void FillOutline(DrawingContext context, Rect outline, double stroke, IBrush brush)
    {
        context.FillRectangle(brush, new Rect(outline.X, outline.Y, outline.Width, stroke));
        context.FillRectangle(brush, new Rect(outline.X, outline.Bottom - stroke, outline.Width, stroke));
        context.FillRectangle(brush, new Rect(outline.X, outline.Y + stroke, stroke, Math.Max(0, outline.Height - (2 * stroke))));
        context.FillRectangle(brush, new Rect(outline.Right - stroke, outline.Y + stroke, stroke, Math.Max(0, outline.Height - (2 * stroke))));
    }

    /// <summary>The window's scale (device pixels per unit).</summary>
    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling is { } scaling and > 0 ? scaling : 1;

    /// <summary>One device pixel, in units.</summary>
    private double Pixel => 1 / Scaling;

    /// <summary>Where the surface is in its window: device pixels count from the window's corner.</summary>
    private Point Origin => TopLevel.GetTopLevel(this) is { } top && this.TranslatePoint(default, top) is { } at ? at : default;

    /// <summary><paramref name="value"/> (along an axis on which the surface starts at <paramref name="origin"/>) on the nearest device pixel's edge.</summary>
    private double Snap(double value, double origin) => (Math.Round((value + origin) * Scaling) / Scaling) - origin;

    /// <summary>
    /// The selection frame around <paramref name="rect"/>: its outer edge, one line's width outside the control's bounds
    /// snapped to device pixels, so the line covers whole pixels and none of the control's.
    /// </summary>
    internal Rect Frame(Rect rect)
    {
        var line = Pixel * Math.Max(1, Math.Round(Scaling));
        var origin = Origin;
        var left = Snap(rect.Left, origin.X) - line;
        var top = Snap(rect.Top, origin.Y) - line;
        var right = Snap(rect.Right, origin.X) + line;
        var bottom = Snap(rect.Bottom, origin.Y) + line;
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Tab order mode: each control's tab index (after its containers', <c>2.1</c>) in a box at its top left, as Visual Studio shows them.</summary>
    private void RenderTabOrder(DrawingContext context, IBrush accent)
    {
        var typeface = new Typeface(FontFamily.Default);
        foreach (var view in _document.Components.Where(c => c.IsControl && !c.IsPart && c.Visible && c != _document.Root && c.Width > 0))
        {
            if (_document.TabOrderLabel(view) is not { } label)
            {
                continue;
            }

            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, Resource("Shell.AccentForeground", Brushes.White));
            var box = new Rect(view.X, view.Y, text.Width + 6, text.Height + 2);
            context.FillRectangle(accent, box);
            context.DrawText(text, new Point(box.X + 3, box.Y + 1));
        }
    }

    /// <summary>A docked control's dock and an anchored control's (other than top-left) anchors, as lines to its container's edges.</summary>
    private void RenderLayoutAdorners(DrawingContext context, DesignerComponentView view, Rect rect, Pen thin, IBrush accent)
    {
        if (view == _document.Root || view.Parent is not { } parentName || _document.Find(parentName) is not { } parent)
        {
            return;
        }

        if (view.Dock is { } dock && dock != "None")
        {
            var text = new FormattedText(Localizer.Format("msg.winFormsDesigner.docked", "Dock: {0}", dock), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 10, accent);
            context.DrawText(text, new Point(rect.X + 2, rect.Bottom - text.Height - 1));
            return;
        }

        if (view.Anchor is not { } anchor || anchor.Replace(" ", string.Empty, StringComparison.Ordinal) is "Top,Left")
        {
            return;
        }

        var client = DesignerSnapping.ClientOf(parent);
        foreach (var side in anchor.Split(',', StringSplitOptions.TrimEntries))
        {
            var (from, to) = side switch
            {
                "Top" => (new Point(rect.Center.X, rect.Top), new Point(rect.Center.X, client.Y)),
                "Bottom" => (new Point(rect.Center.X, rect.Bottom), new Point(rect.Center.X, client.Bottom)),
                "Left" => (new Point(rect.Left, rect.Center.Y), new Point(client.X, rect.Center.Y)),
                "Right" => (new Point(rect.Right, rect.Center.Y), new Point(client.Right, rect.Center.Y)),
                _ => (default(Point), default(Point)),
            };
            if (from != to)
            {
                context.DrawLine(thin, from, to);
            }
        }
    }

    private static Rect GlyphRect(Rect rect) => new(rect.Right + 4, rect.Top - 2, Glyph, Glyph);

    /// <summary>The smart tag's glyph: a small box with an arrow, at the selected component's top right.</summary>
    private static void RenderGlyph(DrawingContext context, Rect glyph, Pen pen, IBrush accent, IBrush fill)
    {
        context.DrawRectangle(fill, pen, glyph);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(glyph.X + 4, glyph.Y + 3), isFilled: true);
            g.LineTo(new Point(glyph.Right - 3, glyph.Center.Y));
            g.LineTo(new Point(glyph.X + 4, glyph.Bottom - 3));
            g.EndFigure(isClosed: true);
        }

        context.DrawGeometry(accent, null, geometry);
    }

    /// <summary>The menus edited in place: a strip's Type Here, and the open drop-downs with their rows and Type Here.</summary>
    private void RenderMenus(DrawingContext context, Pen thin, Pen solid)
    {
        var typeface = new Typeface(FontFamily.Default);
        var background = Resource("Shell.Panel", Brushes.White);
        var foreground = Resource("Shell.Foreground", Brushes.Black);
        var muted = Resource("Shell.MutedForeground", Brushes.Gray);
        var typeHereText = Localizer.Get("msg.winFormsDesigner.typeHere", "Type Here");
        void TypeHere(DesignerTypeHere typeHere)
        {
            var rect = Rect(typeHere.Bounds);
            context.DrawRectangle(background, thin, rect);
            var text = new FormattedText(typeHere.CanType ? typeHereText : "▾ " + typeHereText, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, muted);
            context.DrawText(text, new Point(rect.X + 4, rect.Y + ((rect.Height - text.Height) / 2)));
        }

        if (_document.StripTypeHere is { } strip)
        {
            TypeHere(strip);
        }

        foreach (var panel in _document.MenuPanels)
        {
            var bounds = Rect(panel.Bounds);
            context.DrawRectangle(background, new Pen(Resource("Shell.Border", Brushes.Gray), 1), bounds);
            if (panel.Header is { } header)
            {
                var text = new FormattedText(header, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, muted);
                context.DrawText(text, new Point(bounds.X + 6, bounds.Y + 4));
            }

            foreach (var row in panel.Rows)
            {
                var rect = Rect(row.Bounds);
                if (_document.Selection.Contains(row.Item))
                {
                    context.DrawRectangle(Resource("Shell.Accent", Brushes.DodgerBlue) is ISolidColorBrush accent ? new SolidColorBrush(accent.Color, 0.15) : null, solid, rect.Deflate(1));
                }

                if (row.IsSeparator)
                {
                    context.DrawLine(new Pen(muted, 1), new Point(rect.X + 24, rect.Center.Y), new Point(rect.Right - 4, rect.Center.Y));
                    continue;
                }

                var text = new FormattedText(row.Text.Replace("&", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, foreground);
                context.DrawText(text, new Point(rect.X + 24, rect.Y + ((rect.Height - text.Height) / 2)));
                if (row.HasDropDown)
                {
                    context.DrawText(new FormattedText("›", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, foreground), new Point(rect.Right - 12, rect.Y + 3));
                }
            }

            TypeHere(panel.TypeHere);
        }
    }

    private static Rect Rect(DesignerRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// The eight grab handles: corners and edge middles of the selection frame, clockwise from the top left, each a
    /// square of whole device pixels centered on the frame's line.
    /// </summary>
    internal Rect[] Handles(Rect rect)
    {
        var frame = Frame(rect);
        var line = Pixel * Math.Max(1, Math.Round(Scaling));
        var size = ((Math.Floor(Handle * Scaling / 2) * 2) + 1) * Pixel; // odd in device pixels
        var origin = Origin;
        double[] xs = [frame.Left + (line / 2), Snap(frame.Center.X - (line / 2), origin.X) + (line / 2), frame.Right - (line / 2)];
        double[] ys = [frame.Top + (line / 2), Snap(frame.Center.Y - (line / 2), origin.Y) + (line / 2), frame.Bottom - (line / 2)];
        (int X, int Y)[] at = [(0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (1, 2), (0, 2), (0, 1)];
        return [.. at.Select(p => new Rect(xs[p.X] - (size / 2), ys[p.Y] - (size / 2), size, size))];
    }

    private DesignerRect Resized(DesignerRect start)
    {
        if (_pressed is not { } pressed)
        {
            return start;
        }

        var dx = _current.X - pressed.X;
        var dy = _current.Y - pressed.Y;
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        switch (_handle)
        {
            case 0: left += dx; top += dy; break;
            case 1: top += dy; break;
            case 2: right += dx; top += dy; break;
            case 3: right += dx; break;
            case 4: right += dx; bottom += dy; break;
            case 5: bottom += dy; break;
            case 6: left += dx; bottom += dy; break;
            case 7: left += dx; break;
        }

        return DesignerRect.Between(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    private int HandleAt(Point point)
    {
        if (_document.PrimarySelection is not { } primary || _document.Find(primary) is not { IsControl: true, IsPart: false } view)
        {
            return -1;
        }

        var handles = Handles(Rect(WinFormsDesignerDocument.Bounds(view)));
        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i].Inflate(2).Contains(point))
            {
                // The root resizes from its right, bottom and bottom-right handles only (its location is the designer's).
                return view == _document.Root && i is not (3 or 4 or 5) ? -1 : i;
            }
        }

        return -1;
    }

    /// <summary>The snapped bounds of the moving primary selection (Alt turns snap lines off).</summary>
    private void Snap(KeyModifiers modifiers)
    {
        _snap = null;
        if (modifiers.HasFlag(KeyModifiers.Alt) || _pressed is not { } pressed || _document.PrimarySelection is not { } primary || _document.Find(primary) is not { } view)
        {
            return;
        }

        if (_mode == Mode.Move && view.IsControl && view != _document.Root && !view.IsPart)
        {
            var moving = new HashSet<string>(_document.Selection, StringComparer.Ordinal);
            var bounds = WinFormsDesignerDocument.Bounds(view);
            _snap = DesignerSnapping.Move(_document, view, bounds with { X = bounds.X + _current.X - pressed.X, Y = bounds.Y + _current.Y - pressed.Y }, moving);
        }
        else if (_mode == Mode.Resize)
        {
            _snap = DesignerSnapping.Resize(_document, view, Resized(WinFormsDesignerDocument.Bounds(view)), _handle);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_document.IsRestarting)
        {
            e.Handled = true;
            return;
        }

        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed && !_document.IsTabOrderMode && _document.HitTest(point.X, point.Y) is { } target && !_document.Selection.Contains(target.Name))
        {
            // A right-click selects what it's on first, for the context menu.
            _document.Click(point.X, point.Y, toggle: false);
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_document.IsTabOrderMode)
        {
            _ = _document.TabOrderClickAsync(point.X, point.Y);
            e.Handled = true;
            return;
        }

        // The smart tag's glyph.
        if (_document.PrimarySelection is { } selected && _document.Find(selected) is { HasActions: true, IsItem: false } tagged && tagged.Width > 0
            && GlyphRect(Rect(WinFormsDesignerDocument.Bounds(tagged))).Inflate(2).Contains(point))
        {
            if (_document.ActionPanel?.Component == selected)
            {
                _document.CloseActions();
            }
            else
            {
                _ = _document.OpenActionsAsync(selected);
            }

            e.Handled = true;
            return;
        }

        if (_document.ActionPanel is not null)
        {
            _document.CloseActions();
        }

        // The menus edited in place.
        var (item, typeHere) = _document.MenuHitTest(point.X, point.Y);
        if (typeHere is { } box)
        {
            if (box.CanType)
            {
                _view.Edit(box.Bounds, string.Empty, text => _document.AddItemAsync(box, text));
            }
            else
            {
                _view.PickItemType(box);
            }

            e.Handled = true;
            return;
        }

        if (item is not null)
        {
            if (e.ClickCount == 2 || _document.PrimarySelection == item)
            {
                EditItem(item);
            }
            else
            {
                _document.SetSelection([item], notify: true);
            }

            e.Handled = true;
            return;
        }

        // A tool strip's item on the form: selected (a second click edits its text).
        if (_document.Components.FirstOrDefault(c => c.IsItem && c.Visible && WinFormsDesignerDocument.Bounds(c).Contains(point.X, point.Y)) is { } stripItem)
        {
            if (e.ClickCount == 2 || _document.PrimarySelection == stripItem.Name)
            {
                EditItem(stripItem.Name);
            }
            else
            {
                _document.SetSelection([stripItem.Name], notify: true);
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            _mode = Mode.None;
            _pressed = null;
            _ = _document.DefaultEventAsync(point.X, point.Y);
            e.Handled = true;
            return;
        }

        _pressed = point;
        _current = point;
        _moved = false;
        _snap = null;
        if (Toolbox.Armed is not null)
        {
            _mode = Mode.Draw;
        }
        else if ((_handle = HandleAt(point)) >= 0)
        {
            _mode = Mode.Resize;
        }
        else
        {
            var toggle = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0;
            var hit = _document.HitTest(point.X, point.Y);
            if (hit is null || hit == _document.Root || (hit.IsContainer && !_document.Selection.Contains(hit.Name) && !toggle && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
            {
                _mode = Mode.Band;
                _bandContainer = hit?.Name ?? _document.Root?.Name;
                if (!toggle)
                {
                    _document.Click(point.X, point.Y, toggle: false);
                }
            }
            else
            {
                _document.Click(point.X, point.Y, toggle);
                _mode = Mode.Move;
            }
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>An item's text edited in place, over its row or its place on the strip.</summary>
    private void EditItem(string item)
    {
        var view = _document.Find(item);
        var row = _document.MenuPanels.SelectMany(p => p.Rows).FirstOrDefault(r => r.Item == item);
        if (view is null || row is { IsSeparator: true } || (row is null && !view.Visible))
        {
            return;
        }

        var bounds = row?.Bounds ?? WinFormsDesignerDocument.Bounds(view);
        _view.Edit(bounds, view.Text ?? string.Empty, text => _document.SetItemTextAsync(item, text));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_document.IsRestarting)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_pressed is { } pressed)
        {
            _current = point;
            _moved |= Math.Abs(point.X - pressed.X) >= 3 || Math.Abs(point.Y - pressed.Y) >= 3;
            if (_moved && _mode is Mode.Move or Mode.Resize)
            {
                Snap(e.KeyModifiers);
            }

            InvalidateVisual();
            return;
        }

        var handle = HandleAt(point);
        Cursor = new Cursor(handle switch
        {
            0 or 4 => StandardCursorType.TopLeftCorner,
            2 or 6 => StandardCursorType.TopRightCorner,
            1 or 5 => StandardCursorType.SizeNorthSouth,
            3 or 7 => StandardCursorType.SizeWestEast,
            _ => Toolbox.Armed is not null ? StandardCursorType.Cross : _document.IsTabOrderMode ? StandardCursorType.Hand : StandardCursorType.Arrow,
        });
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is not { } pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var mode = _mode;
        var snap = _snap;
        _pressed = null;
        _mode = Mode.None;
        _snap = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        switch (mode)
        {
            case Mode.Draw when Toolbox.Armed is { } item:
                if (_moved)
                {
                    var rect = DesignerRect.Between(pressed.X, pressed.Y, point.X, point.Y);
                    _ = _document.CreateAsync(item.Type, rect.X, rect.Y, rect.Width, rect.Height);
                }
                else
                {
                    _ = _document.CreateAsync(item.Type, point.X, point.Y);
                }

                break;
            case Mode.Move when _moved:
                if (snap is not null && _document.Find(_document.PrimarySelection ?? string.Empty) is { } primary)
                {
                    var dx = snap.Bounds.X - primary.X;
                    var dy = snap.Bounds.Y - primary.Y;
                    _ = _document.MoveSelectionAsync(dx, dy, point.X, point.Y);
                }
                else
                {
                    _ = _document.MoveSelectionAsync(point.X - pressed.X, point.Y - pressed.Y, point.X, point.Y);
                }

                break;
            case Mode.Resize when _moved && _document.PrimarySelection is { } resized && _document.Find(resized) is { } view:
                _current = point;
                _pressed = pressed;
                var bounds = snap?.Bounds ?? Resized(WinFormsDesignerDocument.Bounds(view));
                _pressed = null;
                _ = _document.ResizeAsync(resized, bounds);
                break;
            case Mode.Band when _moved && _bandContainer is { } container:
                _document.SelectInRectangle(DesignerRect.Between(pressed.X, pressed.Y, point.X, point.Y), container, add: (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0);
                break;
        }

        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_document.IsRestarting && e.Key != Key.Escape)
        {
            return;
        }

        var primary = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var item = _document.PrimarySelection is { } selected && _document.Find(selected) is { IsItem: true } itemView ? itemView : null;
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Left or Key.Right when item is not null && primary:
                // An item moved among its owner's items.
                _ = _document.MoveItemAsync(item.Name, e.Key is Key.Up or Key.Left ? -1 : 1);
                break;
            case Key.Enter when item is not null:
                EditItem(item.Name);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                var dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                _ = _document.NudgeAsync(dx, dy, resize: shift);
                break;
            case Key.Delete:
                _ = _document.DeleteSelectionAsync();
                break;
            case Key.Escape:
                if (_document.IsTabOrderMode)
                {
                    _document.IsTabOrderMode = false;
                }
                else if (_document.ActionPanel is not null)
                {
                    _document.CloseActions();
                }
                else if (Toolbox.Armed is not null)
                {
                    Toolbox.Disarm();
                }
                else if (_document.PrimarySelection is { } current && _document.Find(current) is { } view && (view.Owner ?? view.Parent) is { } parent)
                {
                    _document.SetSelection([parent], notify: true);
                }

                break;
            case Key.C when primary:
                _document.Copy();
                break;
            case Key.X when primary:
                _document.Copy();
                _ = _document.DeleteSelectionAsync();
                break;
            case Key.V when primary:
                _ = _document.PasteAsync();
                break;
            case Key.D when primary:
                _ = _document.DuplicateAsync();
                break;
            case Key.Z when primary && !shift:
                _document.Undo();
                break;
            case Key.Y when primary:
            case Key.Z when primary && shift:
                _document.Redo();
                break;
            case Key.A when primary:
                if (_document.Root is { } root)
                {
                    _document.SetSelection([.. _document.ChildrenOf(root.Name).Select(c => c.Name)], notify: true);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private ToolboxViewModel Toolbox => _document.Toolbox;
}
