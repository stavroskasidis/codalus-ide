using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Editor;

/// <summary>
/// Pinned data tips (M14b): each one floats over the code to the right of its line, shows the expression's value at
/// the last pause (in red when it changed, muted when the program isn't paused), and keeps a comment. Double-click for
/// QuickWatch; the menu edits the comment, copies, adds a watch or unpins. With <c>relocate</c>, tips follow edits.
/// </summary>
public sealed class PinnedDataTipLayer : Canvas
{
    private readonly TextEditor _editor;
    private readonly IEditorServices _services;
    private readonly Func<string> _path;
    private readonly bool _relocate;
    private readonly Dictionary<int, Control> _byLine = [];
    private readonly Dictionary<int, TextAnchor> _anchors = [];
    private TextDocument? _document;
    private bool _attached;

    public PinnedDataTipLayer(TextEditor editor, IEditorServices services, Func<string> path, bool relocate)
    {
        _editor = editor;
        _services = services;
        _path = path;
        _relocate = relocate;
        ClipToBounds = true;
        Background = null;
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => Arrange();
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Arrange();
        editor.DocumentChanged += (_, _) => Rebuild();
    }

    private PinnedDataTipStore Store => _services.Debugger.PinnedTips;

    /// <summary>The tip controls by line (tests).</summary>
    public IReadOnlyDictionary<int, Control> Pins => _byLine;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_attached)
        {
            _attached = true;
            Store.Changed += OnStoreChanged;
            _services.Debugger.StateChanged += OnDebuggerChanged;
        }

        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    /// <summary>Stops following the store and the document (the editor closed).</summary>
    public void Detach()
    {
        if (_attached)
        {
            _attached = false;
            Store.Changed -= OnStoreChanged;
            _services.Debugger.StateChanged -= OnDebuggerChanged;
        }

        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            _document = null;
        }
    }

    private void OnStoreChanged(object? sender, string? path)
    {
        if (path is null || Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(path, _path()))
        {
            Ui.Post(Rebuild);
        }
    }

    private void OnDebuggerChanged(object? sender, EventArgs e)
    {
        if (_byLine.Count > 0)
        {
            Ui.Post(Rebuild);
        }
    }

    /// <summary>Builds a control per pinned line, then places them.</summary>
    public void Rebuild()
    {
        Children.Clear();
        _byLine.Clear();
        var tips = _attached ? Store.For(_path()) : [];
        foreach (var line in tips.GroupBy(t => t.Line))
        {
            var control = Build([.. line]);
            _byLine[line.Key] = control;
            Children.Add(control);
        }

        TrackDocument(tips);
        Arrange();
    }

    private void Arrange()
    {
        var view = _editor.TextArea.TextView;
        if (_byLine.Count == 0 || !view.VisualLinesValid)
        {
            return;
        }

        foreach (var (number, control) in _byLine)
        {
            var visual = view.VisualLines.FirstOrDefault(l => l.FirstDocumentLine.LineNumber <= number && l.LastDocumentLine.LineNumber >= number);
            var end = visual is null ? (Point?)null : visual.GetVisualPosition(visual.VisualLengthWithEndOfLineMarker, VisualYPosition.LineTop) - view.ScrollOffset;
            if (end is not { } at || view.TranslatePoint(new Point(at.X + 32, at.Y), this) is not { } origin)
            {
                control.IsVisible = false;
                continue;
            }

            // Right of the line, but kept in view: a long line puts the tip over its own end, as a hover would.
            control.Measure(Size.Infinity);
            var left = Math.Min(origin.X, Bounds.Width - control.DesiredSize.Width - 8);
            SetLeft(control, Math.Max(48, left));
            SetTop(control, origin.Y - 2);
            control.IsVisible = true;
        }
    }

    private Border Build(IReadOnlyList<PinnedDataTip> tips)
    {
        var stopped = _services.Debugger.State == DebugState.Stopped;
        var panel = new StackPanel { Spacing = 1 };
        foreach (var tip in tips)
        {
            var value = new TextBlock
            {
                Text = tip.Value ?? "…",
                FontFamily = _editor.FontFamily,
                MaxWidth = 480,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(value, tip.Value);
            if (tip.Changed && stopped)
            {
                value.Bind(TextBlock.ForegroundProperty, value.GetResourceObservable("Shell.Error"));
            }
            else if (tip.IsError || !stopped)
            {
                value.Classes.Add("muted");
            }

            var unpin = new Button { Classes = { "toolbar" }, Padding = new Thickness(1), Content = new Icon { Kind = "close", Width = 12, Height = 12 }, VerticalAlignment = VerticalAlignment.Center };
            unpin.Click += (_, _) => Store.Unpin(tip);
            AutomationProperties.SetName(unpin, "Unpin " + tip.Expression);
            ToolTip.SetTip(unpin, "Unpin");
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Background = Brushes.Transparent,
                Children =
                {
                    new Icon { Kind = "pin", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = tip.Expression, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    value,
                    unpin,
                },
            };
            row.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                _services.ShowQuickWatch(tip.Expression);
            };
            row.ContextMenu = Menu(tip, row);
            AutomationProperties.SetName(row, $"{tip.Expression} = {tip.Value}{(tip.Comment is null ? string.Empty : ", " + tip.Comment)}");
            panel.Children.Add(row);
            if (tip.Comment is { } comment)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = comment,
                    FontStyle = FontStyle.Italic,
                    Classes = { "muted" },
                    Margin = new Thickness(18, 0, 0, 0),
                    MaxWidth = 480,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        var border = new Border { Classes = { "editorHover", "pinnedDataTip" }, Padding = new Thickness(6, 2), Child = panel };
        AutomationProperties.SetName(border, "Pinned data tips");
        return border;
    }

    private ContextMenu Menu(PinnedDataTip tip, Control anchor)
    {
        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        return new ContextMenu
        {
            ItemsSource = new List<Control>
            {
                Item(tip.Comment is null ? "Add Comment" : "Edit Comment", () => EditComment(tip, anchor)),
                Item("Copy Value", () => _ = VariableActions.CopyText?.Invoke(tip.Value ?? string.Empty)),
                Item("Copy Expression", () => _ = VariableActions.CopyText?.Invoke(tip.Expression)),
                Item("Add Watch", () => _ = VariableActions.AddWatch?.Invoke(tip.Expression)),
                new Separator(),
                Item("Unpin", () => Store.Unpin(tip)),
                Item("Clear All DataTips", () => _services.RunCommand("debug.clearDataTips")),
            },
        };
    }

    /// <summary>The comment, typed in a flyout under the tip: Enter keeps it, Escape cancels.</summary>
    public void EditComment(PinnedDataTip tip, Control anchor)
    {
        var box = new TextBox { Text = tip.Comment ?? string.Empty, Width = 280, PlaceholderText = "A comment for this data tip" };
        AutomationProperties.SetName(box, "Comment on " + tip.Expression);
        var flyout = new Flyout { Content = box, Placement = PlacementMode.Bottom };
        var original = tip.Comment;
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                if (e.Key == Key.Escape)
                {
                    box.Text = original;
                }

                flyout.Hide();
            }
        };
        flyout.Opened += (_, _) => box.Focus();
        flyout.Closed += (_, _) =>
        {
            if (box.Text != original)
            {
                Store.SetComment(tip, box.Text);
            }
        };
        flyout.ShowAt(anchor);
    }

    // ------------------------------------------------------------------ following edits

    private void TrackDocument(IReadOnlyList<PinnedDataTip> tips)
    {
        if (!ReferenceEquals(_document, _editor.Document))
        {
            if (_document is not null)
            {
                _document.Changed -= OnDocumentChanged;
            }

            _document = _relocate && _attached ? _editor.Document : null;
            if (_document is not null)
            {
                _document.Changed += OnDocumentChanged;
            }
        }

        _anchors.Clear();
        if (_document is not { } document)
        {
            return;
        }

        foreach (var line in tips.Select(t => t.Line).Distinct().Where(l => l >= 1 && l <= document.LineCount))
        {
            var anchor = document.CreateAnchor(document.GetLineByNumber(line).Offset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            anchor.SurviveDeletion = true;
            _anchors[line] = anchor;
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_anchors.Count == 0 || _anchors.All(a => a.Value.Line == a.Key))
        {
            return;
        }

        var map = _anchors.ToDictionary(a => a.Key, a => a.Value.Line);
        Store.Relocate(_path(), line => map.TryGetValue(line, out var moved) ? moved : line);
    }
}
