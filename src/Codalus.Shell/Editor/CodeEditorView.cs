using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using Codalus.LanguageServices.Protocol;
using Location = Codalus.LanguageServices.Protocol.Location;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

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
    private readonly Border _largeFileBar;
    private readonly Popup _hover = new() { IsLightDismissEnabled = true };
    private readonly Popup _exception = new() { IsLightDismissEnabled = true };
    private PeekPresenter? _peek;
    private EditorParts? _primary;
    private EditorParts? _split;
    private readonly NavigationBar _navigationBar = new();

    /// <summary>One editor of the view (the primary, or the split above it) and its chrome.</summary>
    private sealed record EditorParts(TextEditor Editor, CodeEditorController Controller, Minimap Minimap, StickyScroll Sticky, OverviewRuler Ruler);
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

        // Large-file mode (M56): what the editor leaves out of a file this large, as VS Code's notice says.
        _largeFileBar = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 3),
            Child = new TextBlock
            {
                Text = Localizer.Get("msg.editor.largeFile", "This file is large: colors, folding, CodeLens and the language service are off for it, so the editor stays responsive."),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            },
            IsVisible = document.Model.IsLarge,
        };
        _largeFileBar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Shell.Border"));
        _lightbulb.Click += (_, _) => _ = _services.ShowCodeActionsAsync();
        AutomationProperties.SetName(_lightbulb, "Show quick actions (Ctrl+.)");
        ToolTip.SetTip(_lightbulb, "Quick actions (Ctrl+.)");
        _context.SelectionChanged += OnContextSelected;
        AutomationProperties.SetName(_context, "Project context");

        // The editors fill the dock panel; popups sit beside it in a plain panel. (They used to be the dock panel's
        // last children: the last one took the fill, and the editors were docked left at their content's width, so the
        // minimap followed the longest line instead of the editor's right edge; M34a.)
        var layout = new DockPanel();
        DockPanel.SetDock(_contextBar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_navigationBar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_largeFileBar, Avalonia.Controls.Dock.Top);
        layout.Children.Add(_largeFileBar);
        layout.Children.Add(_contextBar);
        layout.Children.Add(_navigationBar);
        layout.Children.Add(_editors);
        var root = new Panel();
        root.Children.Add(layout);
        root.Children.Add(_hover);
        root.Children.Add(_exception);
        Content = root;
        Build();
    }

    public TextEditor Editor => _primary?.Editor ?? throw new InvalidOperationException("The view is not attached.");

    public CodeEditorController Controller => _primary?.Controller ?? throw new InvalidOperationException("The view is not attached.");

    public Minimap Minimap => _primary?.Minimap ?? throw new InvalidOperationException("The view is not attached.");

    public bool IsSplit => _split is not null;

    /// <summary>The type and member drop-downs (tests).</summary>
    public NavigationBar NavigationBar => _navigationBar;

    /// <summary>Whether the bar saying the file opened in large-file mode shows (tests).</summary>
    internal bool ShowsLargeFileBar => _largeFileBar.IsVisible;

    /// <summary>The primary editor's sticky scroll and overview ruler (tests).</summary>
    public StickyScroll StickyScroll => _primary?.Sticky ?? throw new InvalidOperationException("The view is not attached.");

    public OverviewRuler OverviewRuler => _primary?.Ruler ?? throw new InvalidOperationException("The view is not attached.");

    /// <summary>The Peek Definition popup, once shown (tests).</summary>
    public PeekPresenter? Peek => _peek;

    /// <summary>Shows a definition in a popup under the caret's line (Peek Definition, Alt+F12).</summary>
    public void ShowPeek(Location location, string title, string text, string? languageId)
    {
        if (_primary is not { } primary)
        {
            return;
        }

        if (_peek is null)
        {
            _peek = new PeekPresenter(primary.Editor, _services, _font, l =>
            {
                _services.OpenLocations([l], title);
                return Task.CompletedTask;
            });
            ((Panel)Content!).Children.Add(_peek.Popup);
        }

        var view = primary.Editor.TextArea.TextView;
        var caret = primary.Editor.TextArea.Caret;
        var bottom = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(caret.Line, 1), AvaloniaEdit.Rendering.VisualYPosition.LineBottom) - view.ScrollOffset;
        var origin = view.TranslatePoint(new Point(0, bottom.Y), primary.Editor) ?? new Point(0, 0);
        _peek.Show(new Rect(origin.X, origin.Y, 1, 1), primary.Editor.Bounds.Width - origin.X - 24, location, title, text, languageId);
    }

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
        _editors.Children.Add(Row(_primary, 2, _lightbulb));
        _primary.Controller.LightbulbChanged += OnLightbulbChanged;
        _navigationBar.Enabled = Options.NavigationBar;
        _navigationBar.Attach(_primary.Controller);
        _services.Diagnostics.Changed += OnDiagnosticsChanged;
        _services.Options.PropertyChanged += OnOptionsChanged;
        _services.ProjectContextsChanged += OnProjectContextsChanged;
        _font.PropertyChanged += OnFontChanged;
        UpdateProjectContext();
    }

    private void OnFontChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var parts in new[] { _primary, _split }.OfType<EditorParts>())
        {
            parts.Editor.FontFamily = _font.Family;
            parts.Editor.FontSize = _font.Size * parts.Controller.Zoom;
        }
    }

    private void TearDown()
    {
        if (_primary is not { } primary)
        {
            return;
        }

        AvaloniaEditFocus.Release(this);
        _services.Options.PropertyChanged -= OnOptionsChanged;
        _services.ProjectContextsChanged -= OnProjectContextsChanged;
        _services.Diagnostics.Changed -= OnDiagnosticsChanged;
        _font.PropertyChanged -= OnFontChanged;
        _navigationBar.Detach();
        primary.Controller.LightbulbChanged -= OnLightbulbChanged;
        primary.Controller.Dispose();
        _split?.Controller.Dispose();
        _peek?.Release();
        _peek = null;
        _primary = null;
        _split = null;
        _editors.Children.Clear();
    }

    private void OnDiagnosticsChanged(object? sender, string path)
    {
        // The ruler marks this file's problems: another file's (a background pass publishes every file's) leave it as it is.
        if (!SolutionPaths.Comparer.Equals(path, _document.Model.Path))
        {
            return;
        }

        Ui.Post(() =>
        {
            _primary?.Ruler.InvalidateProblems();
            _split?.Ruler.InvalidateProblems();
        });
    }

    private EditorParts CreateEditor(bool primary)
    {
        var editor = new CodeTextEditor
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

        // Outlining markers (M34a): the theme's muted glyph on the editor's background, brighter on hover (they were
        // white boxes in the dark themes).
        editor.Bind(AvaloniaEdit.Folding.FoldingMargin.FoldingMarkerBrushProperty, this.GetResourceObservable("Shell.Editor.Fold"));
        editor.Bind(AvaloniaEdit.Folding.FoldingMargin.FoldingMarkerBackgroundBrushProperty, this.GetResourceObservable("Shell.Editor.Background"));
        editor.Bind(AvaloniaEdit.Folding.FoldingMargin.SelectedFoldingMarkerBrushProperty, this.GetResourceObservable("Shell.Editor.Foreground"));
        editor.Bind(AvaloniaEdit.Folding.FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, this.GetResourceObservable("Shell.Editor.Fold.Hover"));
        AutomationProperties.SetName(editor, _document.Model.Path);
        var controller = new CodeEditorController(editor, _document, _services, _hover, attach: primary, exceptionPopup: primary ? _exception : null);
        controller.ZoomChanged += (_, _) =>
        {
            editor.FontSize = _font.Size * controller.Zoom;
            _services.ShowMessage($"Zoom {controller.Zoom:P0}");
        };
        var minimap = new Minimap(editor, controller.Colorizer) { IsVisible = Options.Minimap };
        minimap.Bind(BackgroundProperty, this.GetResourceObservable("Shell.Editor.Background"));
        minimap.Bind(Minimap.ForegroundProperty, this.GetResourceObservable("Shell.Editor.Foreground"));
        var sticky = new StickyScroll(editor, () => controller.Insights.Symbols, () => controller.Folding) { Enabled = Options.StickyScroll };
        controller.Insights.SymbolsChanged += (_, _) => sticky.Update();
        sticky.LinesPinned += (_, lines) => controller.KeepTokens(lines);
        var ruler = new OverviewRuler(editor, _services, () => _document.Model.Path, () => controller.GitChanges, () => controller.Search) { IsVisible = Options.OverviewRuler };
        controller.Search.ResultsChanged += (_, _) => ruler.Invalidate();
        return new EditorParts(editor, controller, minimap, sticky, ruler);
    }

    private static Grid Row(EditorParts parts, int row, Control? overlay = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetRow(grid, row);
        grid.Children.Add(parts.Editor);
        grid.Children.Add(parts.Controller.PinLayer);
        if (overlay is not null)
        {
            grid.Children.Add(new Canvas { Children = { overlay }, IsHitTestVisible = true, ClipToBounds = true, Background = null });
        }

        // Quick Find in the editor's top right corner, over the text and its sticky lines.
        grid.Children.Add(parts.Controller.Search);

        Grid.SetColumn(parts.Ruler, 1);
        grid.Children.Add(parts.Ruler);
        Grid.SetColumn(parts.Minimap, 2);
        grid.Children.Add(parts.Minimap);
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

        var parts = CreateEditor(primary: false);
        var editor = parts.Editor;
        _split = parts;
        _editors.RowDefinitions = new RowDefinitions("*,4,*");
        _editors.Children.Add(Row(parts, 0));
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        _editors.Children.Add(splitter);
        editor.CaretOffset = primary.Editor.CaretOffset;
    }

    /// <summary>The editor options of the document's language.</summary>
    private EditorOptions Options => _services.Options.For(_document.Model.LanguageId);

    private void OnOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var parts in new[] { _primary, _split }.OfType<EditorParts>())
        {
            parts.Minimap.IsVisible = Options.Minimap;
            parts.Ruler.IsVisible = Options.OverviewRuler;
            parts.Sticky.Enabled = Options.StickyScroll;
            parts.Sticky.Update();
        }

        if (_primary is { } primary && _navigationBar.Enabled != Options.NavigationBar)
        {
            _navigationBar.Enabled = Options.NavigationBar;
            _navigationBar.Attach(primary.Controller);
        }
    }

    // ------------------------------------------------------------------ project context

    private void OnProjectContextsChanged(object? sender, EventArgs e) => Ui.Post(UpdateProjectContext);

    private void UpdateProjectContext()
    {
        var context = _services.ProjectContextOf(_document.Model);
        _contextBar.IsVisible = context is not null;
        if (context is null || (_context.Tag is ProjectContext shown && shown.ProjectPath == context.ProjectPath && shown.Active == context.Active
                                && shown.Frameworks.SequenceEqual(context.Frameworks)))
        {
            return; // none, or the same: a burst of project changes refilled every editor's list
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
