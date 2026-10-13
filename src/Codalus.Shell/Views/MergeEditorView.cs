using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>Highlights the conflict regions of the merge result.</summary>
internal sealed class ConflictRenderer(Func<IReadOnlyList<MergeConflict>> conflicts) : IBackgroundRenderer
{
    private static readonly IBrush s_conflict = new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0xE5, 0x8E, 0x26));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.Document is not { } document)
        {
            return;
        }

        foreach (var conflict in conflicts())
        {
            if (conflict.Start + conflict.Length > document.TextLength)
            {
                continue;
            }

            var segment = new AvaloniaEdit.Document.TextSegment { StartOffset = conflict.Start, Length = Math.Max(0, conflict.Length - 1) };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, extendToFullWidthAtLineEnd: true))
            {
                drawingContext.DrawRectangle(s_conflict, null, rect);
            }
        }
    }
}

/// <summary>
/// The merge editor (M13): the current and incoming versions above, the result below. The toolbar resolves the conflict
/// at the caret (or all of them) by taking a side or both, moves between conflicts, and accepts the merge when none are
/// left. The result stays editable throughout, with undo.
/// </summary>
public sealed class MergeEditorView : UserControl, IPaneView
{
    private readonly MergeEditorViewModel _viewModel;
    private readonly TextEditor _result;
    private readonly TextBlock _summary;
    private IReadOnlyList<MergeConflict> _conflicts = [];

    public MergeEditorView(MergeEditorViewModel viewModel, EditorFont font)
    {
        _viewModel = viewModel;
        var ours = Editor(font, readOnly: true, viewModel.OursTitle);
        ours.Text = viewModel.Ours;
        var theirs = Editor(font, readOnly: true, viewModel.TheirsTitle);
        theirs.Text = viewModel.Theirs;
        _result = Editor(font, readOnly: false, "Result");
        _result.Text = viewModel.InitialResult;
        _result.TextArea.TextView.BackgroundRenderers.Add(new ConflictRenderer(() => _conflicts));
        _result.TextChanged += (_, _) => Recount();

        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        _summary.Classes.Add("muted");
        var toolbar = new WrapPanel { Margin = new Thickness(6, 4) };
        toolbar.Children.Add(Button("Previous Conflict", () => GoTo(-1)));
        toolbar.Children.Add(Button("Next Conflict", () => GoTo(1)));
        toolbar.Children.Add(Button("Accept Current", () => Accept("current")));
        toolbar.Children.Add(Button("Accept Incoming", () => Accept("incoming")));
        toolbar.Children.Add(Button("Accept Both", () => Accept("both")));
        toolbar.Children.Add(Button("Accept All Current", () => AcceptAll("current")));
        toolbar.Children.Add(Button("Accept All Incoming", () => AcceptAll("incoming")));
        var accept = Button("Accept Merge", () => _ = AcceptMergeAsync());
        accept.Classes.Add("accent");
        accept.IsEnabled = viewModel.CanAccept;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MergeEditorViewModel.CanAccept))
            {
                accept.IsEnabled = viewModel.CanAccept;
            }
        };
        toolbar.Children.Add(accept);
        toolbar.Children.Add(_summary);

        var sides = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        sides.Children.Add(Title(viewModel.OursTitle, 0));
        sides.Children.Add(Title(viewModel.TheirsTitle, 2));
        Grid.SetRow(ours, 1);
        sides.Children.Add(ours);
        Grid.SetRow(theirs, 1);
        Grid.SetColumn(theirs, 2);
        sides.Children.Add(theirs);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetRowSpan(splitter, 2);
        sides.Children.Add(splitter);

        var body = new Grid { RowDefinitions = new RowDefinitions("*,4,Auto,*") };
        body.Children.Add(sides);
        var rows = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(rows, 1);
        body.Children.Add(rows);
        var resultTitle = Title("Result", 0);
        Grid.SetRow(resultTitle, 2);
        body.Children.Add(resultTitle);
        Grid.SetRow(_result, 3);
        body.Children.Add(_result);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(body);
        Content = root;
        Recount();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => GoTo(1), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Reports a failed accept (set by the shell).</summary>
    public static Action<Exception>? ReportError { get; set; }

    /// <summary>The result text (tests).</summary>
    public string ResultText => _result.Text;

    public TextEditor Result => _result;

    public void FocusPrimary() => _result.TextArea.Focus();

    private void Recount()
    {
        _conflicts = MergeEditorViewModel.Parse(_result.Text);
        _viewModel.Remaining = _conflicts.Count;
        _summary.Text = _viewModel.Summary;
        _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>The conflict at the caret, or the next one after it.</summary>
    private MergeConflict? Current()
    {
        var caret = _result.CaretOffset;
        return _conflicts.FirstOrDefault(c => caret >= c.Start && caret <= c.Start + c.Length)
               ?? _conflicts.FirstOrDefault(c => c.Start >= caret)
               ?? (_conflicts.Count > 0 ? _conflicts[^1] : null);
    }

    public void Accept(string choice)
    {
        if (Current() is { } conflict)
        {
            _result.Document.Replace(conflict.Start, conflict.Length, MergeEditorViewModel.ReplacementFor(conflict, choice));
            _result.CaretOffset = Math.Min(conflict.Start, _result.Document.TextLength);
            GoTo(1);
        }
    }

    public void AcceptAll(string choice)
    {
        _result.Document.BeginUpdate();
        try
        {
            // From the end, so earlier offsets stay valid.
            foreach (var conflict in MergeEditorViewModel.Parse(_result.Text).Reverse())
            {
                _result.Document.Replace(conflict.Start, conflict.Length, MergeEditorViewModel.ReplacementFor(conflict, choice));
            }
        }
        finally
        {
            _result.Document.EndUpdate();
        }
    }

    public void GoTo(int direction)
    {
        if (_conflicts.Count == 0)
        {
            return;
        }

        var caret = _result.CaretOffset;
        var target = direction > 0
            ? _conflicts.FirstOrDefault(c => c.Start > caret) ?? _conflicts[0]
            : _conflicts.LastOrDefault(c => c.Start < caret && !(caret <= c.Start + c.Length)) ?? _conflicts[^1];
        _result.CaretOffset = target.Start;
        var location = _result.Document.GetLocation(target.Start);
        _result.ScrollTo(location.Line, location.Column);
    }

    public async Task AcceptMergeAsync()
    {
        try
        {
            await _viewModel.AcceptAsync(_result.Text).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Codalus.Workbench.Git.GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    private static TextBlock Title(string text, int column)
    {
        var title = new TextBlock { Text = text, Margin = new Thickness(8, 2), FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(title, column);
        return title;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0) };
        button.Classes.Add("toolbar");
        button.Click += (_, _) => action();
        return button;
    }

    private static TextEditor Editor(EditorFont font, bool readOnly, string name)
    {
        var editor = new TextEditor
        {
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            FontFamily = font.Family,
            FontSize = font.Size,
            Padding = new Thickness(4, 2),
        };
        editor.Bind(BackgroundProperty, editor.GetResourceObservable("Shell.Editor.Background"));
        editor.Bind(ForegroundProperty, editor.GetResourceObservable("Shell.Editor.Foreground"));
        Avalonia.Automation.AutomationProperties.SetName(editor, name);
        return editor;
    }
}
