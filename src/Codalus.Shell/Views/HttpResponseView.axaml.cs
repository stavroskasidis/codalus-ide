using Avalonia;
using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The HTTP Response window: the selected exchange's status, request line and body in a colored read-only viewer,
/// its headers and request, and the history (M34h: the control set's design).
/// </summary>
public sealed partial class HttpResponseView : UserControl, IPaneView
{
    /// <summary>The history's width; it narrows with a narrow window, so the response keeps most of it.</summary>
    private const double HistoryWidth = 360;

    /// <summary>
    /// Narrower than this, the history goes under the response (the HTTP Response beside its file, half the documents'
    /// width): beside it, a history of 40 % left the response a column too narrow for its status and body.
    /// </summary>
    private const double StackBelow = 720;

    /// <summary>The history's height under the response: at most this, or two fifths of the window.</summary>
    private const double StackedHistoryHeight = 220;

    /// <summary>A response narrower than this shows its version, time and size on a line under the status chip.</summary>
    private const double DetailsInlineFrom = 520;

    private HttpResponseViewModel? _vm;
    private CodeViewer? _body;

    /// <summary>The user dragged the splitter: the history keeps that width (narrowed only to leave the response room).</summary>
    private bool _historySized;

    /// <summary>The history's width beside the response, while it is under it.</summary>
    private double _sideWidth = HistoryWidth;

    public HttpResponseView()
    {
        InitializeComponent();
        CopyBody.Click += async (_, _) =>
        {
            if (_vm is { HasBody: true } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, vm.Body.Text).ConfigureAwait(true);
            }
        };
        WrapToggle.IsCheckedChanged += (_, _) =>
        {
            if (_body is not null)
            {
                _body.Editor.WordWrap = WrapToggle.IsChecked == true;
            }
        };
        Root.SizeChanged += (_, e) => FitHistory(e.NewSize);
        ResponsePane.SizeChanged += (_, e) => FitHeader(e.NewSize.Width);
        HistorySplitter.DragCompleted += (_, _) => _historySized = true;
    }

    /// <summary>The body as shown (tests).</summary>
    public string BodyText => _body?.Editor.Document.Text ?? string.Empty;

    /// <summary>
    /// Beside the response, the history starts at 360 px, or two fifths of a narrower window; once the user drags the
    /// splitter it keeps their width, narrowed only so the response keeps a fifth of the window. Narrower than
    /// <see cref="StackBelow"/>, the history goes under the response, at most <see cref="StackedHistoryHeight"/> high.
    /// </summary>
    private void FitHistory(Size size)
    {
        var stacked = size.Width < StackBelow;
        if (stacked != IsHistoryBelow)
        {
            // The width beside the response, kept while the history is under it.
            if (stacked)
            {
                _sideWidth = Root.ColumnDefinitions[2].Width.Value;
            }

            Root.ColumnDefinitions = stacked ? new ColumnDefinitions("*") : new ColumnDefinitions($"*,5,{_sideWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            Root.RowDefinitions = stacked ? new RowDefinitions($"*,5,{StackedHistoryHeight}") : new RowDefinitions("*");
            foreach (var (control, index) in new (Control, int)[] { (HistorySplitter, 1), (HistoryPane, 2) })
            {
                Grid.SetColumn(control, stacked ? 0 : index);
                Grid.SetRow(control, stacked ? index : 0);
            }

            HistorySplitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
            HistorySplitter.Classes.Set("rows", stacked); // its rule across, not down
            IsHistoryBelow = stacked;
        }

        if (stacked)
        {
            Root.RowDefinitions[2].Height = new GridLength(Math.Max(0, Math.Min(StackedHistoryHeight, Math.Floor(size.Height * 0.4))));
            return;
        }

        var column = Root.ColumnDefinitions[2];
        var width = size.Width;
        var fitted = _historySized ? Math.Min(column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value, Math.Floor(width * 0.8)) : Math.Min(HistoryWidth, Math.Floor(width * 0.4));
        column.Width = new GridLength(Math.Max(0, fitted));
    }

    /// <summary>A narrow response puts the status's version, time and size on a line under the chip.</summary>
    private void FitHeader(double width)
    {
        var below = width < DetailsInlineFrom;
        Grid.SetRow(StatusDetails, below ? 1 : 0);
        Grid.SetColumn(StatusDetails, below ? 0 : 1);
        Grid.SetColumnSpan(StatusDetails, below ? 3 : 1);
        StatusDetails.Margin = below ? new Thickness(0, 6, 0, 0) : default;
    }

    /// <summary>The history is under the response, not beside it (a narrow window).</summary>
    internal bool IsHistoryBelow { get; private set; }

    /// <summary>The status's version, time and size are on a line of their own (tests).</summary>
    internal bool AreStatusDetailsBelow => Grid.GetRow(StatusDetails) == 1;

    /// <summary>The history's width beside the response (tests).</summary>
    internal double HistoryColumnWidth => IsHistoryBelow ? double.NaN : Root.ColumnDefinitions[2].Width.Value;

    /// <summary>The history's height under the response (tests).</summary>
    internal double HistoryRowHeight => IsHistoryBelow ? Root.RowDefinitions[2].Height.Value : double.NaN;

    /// <summary>As if the user dragged the splitter to <paramref name="width"/> (tests).</summary>
    internal void DragHistoryTo(double width)
    {
        Root.ColumnDefinitions[2].Width = new GridLength(width);
        HistorySplitter.RaiseEvent(new Avalonia.Input.VectorEventArgs { RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, Source = HistorySplitter });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.BodyChanged -= OnBodyChanged;
        }

        _vm = DataContext as HttpResponseViewModel;
        if (_vm is null)
        {
            return;
        }

        _body ??= new CodeViewer(_vm.Services, _vm.Font);
        _body.Editor.WordWrap = WrapToggle.IsChecked == true;
        BodyHost.Child = _body;
        _vm.BodyChanged += OnBodyChanged;
        ShowBody();
    }

    private void OnBodyChanged(object? sender, EventArgs e) => ShowBody();

    private void ShowBody()
    {
        if (_vm is not null && _body is not null)
        {
            var (text, language) = _vm.Body;
            _body.Show(text, language, null);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null)
        {
            _vm.BodyChanged -= OnBodyChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null)
        {
            _vm.BodyChanged -= OnBodyChanged;
            _vm.BodyChanged += OnBodyChanged;
            ShowBody();
        }
    }

    public void FocusPrimary()
    {
        if (_vm?.HasExchange == true)
        {
            Tabs.Focus();
        }
        else
        {
            HistoryList.Focus();
        }
    }
}
