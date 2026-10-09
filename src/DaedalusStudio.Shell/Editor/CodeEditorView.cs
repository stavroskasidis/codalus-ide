using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// The view of a <see cref="CodeEditorDocument"/>: a project-context bar (multi-targeted projects only), the editor
/// with its minimap, and optionally a second editor on the same text above it (Window → Split). Built in code so the
/// editor controls can be wired directly; colors come from theme resources. The view lives as long as its tab: Dock
/// detaches the views of inactive tabs and the document keeps them for reuse; closing the tab disposes it.
/// </summary>
public sealed class CodeEditorView : UserControl, IPaneView
{
    private readonly CodeEditorDocument _document;
    private readonly IEditorServices _services;
    private readonly EditorFont _font;
    private readonly Grid _editors = new();
    private readonly ComboBox _context = new() { MinWidth = 140, Margin = new Thickness(4, 2) };
    private readonly Border _contextBar;
    private readonly Popup _hover = new() { IsLightDismissEnabled = true };
    private (TextEditor Editor, CodeEditorController Controller, Minimap Minimap)? _primary;
    private (TextEditor Editor, CodeEditorController Controller, Minimap Minimap)? _split;
    private readonly Button _lightbulb = new()
    {
        Classes = { "toolbar" },
        Padding = new Thickness(1),
        IsVisible = false,
        Content = new Icon { Kind = "lightbulb", Width = 14, Height = 14 },
    };
    private bool _updatingContext;

    public CodeEditorView(CodeEditorDocument document, IEditorServices services, EditorFont font)
    {
        _document = document;
        _services = services;
        _font = font;

        _contextBar = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new TextBlock { Text = "Project context:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }, _context },
            },
            IsVisible = false,
        };
        _contextBar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Shell.Border"));
        _lightbulb.Click += (_, _) => _ = _services.ShowCodeActionsAsync();
        AutomationProperties.SetName(_lightbulb, "Show quick actions (Ctrl+.)");
        ToolTip.SetTip(_lightbulb, "Quick actions (Ctrl+.)");
        _context.SelectionChanged += OnContextSelected;
        AutomationProperties.SetName(_context, "Project context");

        var root = new DockPanel();
        DockPanel.SetDock(_contextBar, Avalonia.Controls.Dock.Top);
        root.Children.Add(_contextBar);
        root.Children.Add(_editors);
        root.Children.Add(_hover);
        Content = root;
        Build();
    }

    public TextEditor Editor => _primary?.Editor ?? throw new InvalidOperationException("The view is not attached.");

    public CodeEditorController Controller => _primary?.Controller ?? throw new InvalidOperationException("The view is not attached.");

    public Minimap Minimap => _primary?.Minimap ?? throw new InvalidOperationException("The view is not attached.");

    public bool IsSplit => _split is not null;

    /// <summary>The lightbulb next to the caret's line when it has diagnostics: click for code fixes (or Ctrl+.).</summary>
    private void OnLightbulbChanged(object? sender, EventArgs e)
    {
        if (_primary is not { } primary || primary.Controller.LightbulbLine is not { } line || _document.Model.IsReadOnly)
        {
            _lightbulb.IsVisible = false;
            return;
        }

        var view = primary.Editor.TextArea.TextView;
        var top = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(line, 1), AvaloniaEdit.Rendering.VisualYPosition.LineTop) - view.ScrollOffset;
        var origin = view.TranslatePoint(new Point(0, top.Y), primary.Editor);
        if (origin is not { } point || point.Y < 0 || point.Y > primary.Editor.Bounds.Height - 8)
        {
            _lightbulb.IsVisible = false;
            return;
        }

        Canvas.SetLeft(_lightbulb, Math.Max(0, point.X - 18));
        Canvas.SetTop(_lightbulb, point.Y);
        _lightbulb.IsVisible = true;
    }

    /// <summary>The tab closed: releases the editors and their language-service work.</summary>
    public void Close() => TearDown();

    private void Build()
    {
        if (_primary is not null)
        {
            return;
        }

        _primary = CreateEditor(primary: true);
        _editors.Children.Clear();
        _editors.RowDefinitions = new RowDefinitions("0,0,*");
        _editors.Children.Add(Row(_primary.Value.Editor, _primary.Value.Minimap, 2, _lightbulb));
        _primary.Value.Controller.LightbulbChanged += OnLightbulbChanged;
        _services.Options.PropertyChanged += OnOptionsChanged;
        _services.ProjectContextsChanged += OnProjectContextsChanged;
        _font.PropertyChanged += OnFontChanged;
        UpdateProjectContext();
    }

    private void OnFontChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var editor in new[] { _primary?.Editor, _split?.Editor }.OfType<TextEditor>())
        {
            editor.FontFamily = _font.Family;
            editor.FontSize = _font.Size;
        }
    }

    private void TearDown()
    {
        if (_primary is not { } primary)
        {
            return;
        }

        _services.Options.PropertyChanged -= OnOptionsChanged;
        _services.ProjectContextsChanged -= OnProjectContextsChanged;
        _font.PropertyChanged -= OnFontChanged;
        primary.Controller.LightbulbChanged -= OnLightbulbChanged;
        primary.Controller.Dispose();
        _split?.Controller.Dispose();
        _primary = null;
        _split = null;
        _editors.Children.Clear();
    }

    private (TextEditor, CodeEditorController, Minimap) CreateEditor(bool primary)
    {
        var editor = new TextEditor
        {
            Padding = new Thickness(4, 2, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        editor.FontFamily = _font.Family;
        editor.FontSize = _font.Size;
        editor.Bind(TextEditor.BackgroundProperty, this.GetResourceObservable("Shell.Editor.Background"));
        editor.Bind(TextEditor.ForegroundProperty, this.GetResourceObservable("Shell.Editor.Foreground"));
        editor.Bind(TextEditor.LineNumbersForegroundProperty, this.GetResourceObservable("Shell.Editor.LineNumbers"));
        AutomationProperties.SetName(editor, _document.Model.Path);
        var controller = new CodeEditorController(editor, _document, _services, _hover, attach: primary);
        var minimap = new Minimap(editor, controller.Colorizer) { IsVisible = _services.Options.Minimap };
        minimap.Bind(BackgroundProperty, this.GetResourceObservable("Shell.Editor.Background"));
        minimap.Bind(Minimap.ForegroundProperty, this.GetResourceObservable("Shell.Editor.Foreground"));
        return (editor, controller, minimap);
    }

    private static Grid Row(TextEditor editor, Minimap minimap, int row, Control? overlay = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetRow(grid, row);
        grid.Children.Add(editor);
        if (overlay is not null)
        {
            grid.Children.Add(new Canvas { Children = { overlay }, IsHitTestVisible = true, ClipToBounds = true, Background = null });
        }

        Grid.SetColumn(minimap, 1);
        grid.Children.Add(minimap);
        return grid;
    }

    /// <summary>Shows or hides a second editor on the same document above this one (Window → Split).</summary>
    public void ToggleSplit()
    {
        if (_primary is not { } primary)
        {
            return;
        }

        if (_split is { } split)
        {
            _editors.Children.RemoveRange(1, _editors.Children.Count - 1);
            _editors.RowDefinitions = new RowDefinitions("0,0,*");
            split.Controller.Dispose();
            _split = null;
            primary.Editor.TextArea.Focus();
            return;
        }

        var (editor, controller, minimap) = CreateEditor(primary: false);
        _split = (editor, controller, minimap);
        _editors.RowDefinitions = new RowDefinitions("*,4,*");
        _editors.Children.Add(Row(editor, minimap, 0));
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        _editors.Children.Add(splitter);
        editor.CaretOffset = primary.Editor.CaretOffset;
    }

    private void OnOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var minimap in new[] { _primary?.Minimap, _split?.Minimap }.OfType<Minimap>())
        {
            minimap.IsVisible = _services.Options.Minimap;
        }
    }

    // ------------------------------------------------------------------ project context

    private void OnProjectContextsChanged(object? sender, EventArgs e) => Ui.Post(UpdateProjectContext);

    private void UpdateProjectContext()
    {
        var context = _services.ProjectContextOf(_document.Model);
        _contextBar.IsVisible = context is not null;
        if (context is null)
        {
            return;
        }

        _updatingContext = true;
        _context.ItemsSource = context.Frameworks.Select(f => $"{context.ProjectName} ({f})").ToList();
        _context.SelectedIndex = Math.Max(0, context.Frameworks.ToList().IndexOf(context.Active));
        _context.Tag = context;
        _updatingContext = false;
    }

    private void OnContextSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingContext || _context.Tag is not ProjectContext context || _context.SelectedIndex < 0)
        {
            return;
        }

        var framework = context.Frameworks[_context.SelectedIndex];
        if (framework != context.Active)
        {
            _ = _services.SetActiveFrameworkAsync(context.ProjectPath, framework);
        }
    }

    public void FocusPrimary() => _primary?.Editor.TextArea.Focus();
}
