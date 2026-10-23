using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Editor;

/// <summary>
/// Pinned data tips (M14b, D44): each one floats over the code to the right of its line as the data tip's header, shows
/// the expression's value at the last pause (in red when it changed, muted when the program isn't paused), expands into
/// the data tip's cascade while it's paused, and keeps a comment. Double-click its name for QuickWatch; the menu edits
/// the comment, copies, adds a watch or unpins. With <c>relocate</c>, tips follow edits.
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
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            CloseCascades();
            Arrange();
        };
        editor.TextArea.TextView.PointerPressed += (_, _) => CloseCascades();
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
        CloseCascades();
        _cascades.Clear();
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

    /// <summary>The open tips' cascades (tests).</summary>
    internal IReadOnlyList<DataTipCascade> Cascades => [.. _cascades];

    private readonly List<DataTipCascade> _cascades = [];

    /// <summary>Closes every pinned tip's child tips (a click in the text, a rebuild).</summary>
    public void CloseCascades()
    {
        foreach (var cascade in _cascades)
        {
            cascade.CloseFrom(0);
        }
    }

    /// <summary>
    /// A line's pinned tips, stacked (A16): each the data tip's header with a strip to unpin it (A15), its comment under
    /// it; while the program is stopped it expands into the same cascade as the hover's, and its value is red when it
    /// changed at the last pause; while it runs, the last value is muted and nothing expands.
    /// </summary>
    private StackPanel Build(IReadOnlyList<PinnedDataTip> tips)
    {
        var debugger = _services.Debugger;
        var stopped = debugger.State == DebugState.Stopped;
        var panel = new StackPanel { Spacing = 1 };
        foreach (var tip in tips)
        {
            var model = new ViewModels.DataTipViewModel(debugger, tip.Expression, new Codalus.Debugging.EvaluateResponse(tip.Value ?? "…", tip.IsError ? null : tip.Type, stopped && !tip.IsError ? tip.VariablesReference : 0), IconOf(tip.Expression))
            {
                PinExpression = (expression, value) =>
                {
                    Store.Pin(tip.Path, tip.Line, expression, value);
                    _ = Store.RefreshAsync((e, token) => debugger.EvaluateAsync(e, "watch", token));
                },
            };
            model.Root.IsChanged = tip.Changed && stopped;
            DataTipCascade? cascade = null;
            cascade = new DataTipCascade(_editor.TextArea.TextView, this, model.Root, new DataTipActions
            {
                Wheel = e =>
                {
                    cascade?.CloseFrom(0);
                    if (_editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                    {
                        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y - (e.Delta.Y * 3 * _editor.TextArea.TextView.DefaultLineHeight)));
                    }
                },
                NameGesture = (_, doubleClick) =>
                {
                    if (doubleClick)
                    {
                        _services.ShowQuickWatch(tip.Expression);
                    }
                },
                FadedMove = (_, _) => { },
                FadedPress = (_, _) => { },
                SetValue = async (node, value) =>
                {
                    var error = await model.SetValueAsync(node, value).ConfigureAwait(true);
                    if (error is null)
                    {
                        _ = Store.RefreshAsync((expression, token) => debugger.EvaluateAsync(expression, "watch", token));
                    }

                    return error;
                },
                ReadOnlyReason = node => stopped ? model.ReadOnlyReason(node) : string.Empty,
                Pin = node => ReferenceEquals(node, model.Root) ? null : model.PinAction(node),
                Close = () => cascade?.CloseFrom(0),
                Menu = (node, _) => ReferenceEquals(node, model.Root) ? MenuItems(tip, cascade!.Header) : null,
                Unpin = () => Store.Unpin(tip),
                Muted = !stopped,
            });
            _cascades.Add(cascade);
            var header = cascade.Header;
            header.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(header, $"{tip.Expression} = {tip.Value}{(tip.Comment is null ? string.Empty : ", " + tip.Comment)}");
            panel.Children.Add(header);
            if (tip.Comment is { } comment)
            {
                panel.Children.Add(new Border
                {
                    Classes = { "editorHover", "pinnedDataTipComment" },
                    Padding = new Thickness(6, 1),
                    Margin = new Thickness(20, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Icon { Kind = "comment", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock { Text = comment, FontStyle = FontStyle.Italic, MaxWidth = 480, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                });
            }
        }

        AutomationProperties.SetName(panel, "Pinned data tips");
        return panel;
    }

    /// <summary>A pinned expression's icon: a local's for a name, a member's kind guessed from the last name (<c>env.EnvironmentName</c>: a property).</summary>
    private static string IconOf(string expression) =>
        expression.LastIndexOf('.') is var dot and > 0 ? ViewModels.VariableIcons.Guess(expression[(dot + 1)..]) : "symbol-local";

    /// <summary>A pinned tip's menu (A16): its comment, copying, Add Watch, Unpin and Clear All DataTips.</summary>
    private IReadOnlyList<Control> MenuItems(PinnedDataTip tip, Control anchor)
    {
        static MenuItem Item(string header, string icon, Action action) => Controls.MenuEntry.Create(header, icon, click: action);

        return
        [
            Item(tip.Comment is null ? "Add Comment" : "Edit Comment", "comment", () => EditComment(tip, anchor)),
            Item("Copy Value", "copy", () => _ = VariableActions.CopyText?.Invoke(tip.Value ?? string.Empty)),
            Item("Copy Expression", "copy", () => _ = VariableActions.CopyText?.Invoke(tip.Expression)),
            Item("Add Watch", "watch", () => _ = VariableActions.AddWatch?.Invoke(tip.Expression)),
            new Separator(),
            Item("Unpin", "pin", () => Store.Unpin(tip)),
            Item("Clear All DataTips", "clear-all", () => _services.RunCommand("debug.clearDataTips")),
        ];
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
