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

    private HttpResponseViewModel? _vm;
    private CodeViewer? _body;

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
        Root.SizeChanged += (_, e) => FitHistory(e.NewSize.Width);
    }

    /// <summary>The body as shown (tests).</summary>
    public string BodyText => _body?.Editor.Document.Text ?? string.Empty;

    /// <summary>The history keeps 360 px, or two fifths of a narrower window.</summary>
    private void FitHistory(double width)
    {
        if (Root.ColumnDefinitions.Count == 3)
        {
            Root.ColumnDefinitions[2].Width = new GridLength(Math.Min(HistoryWidth, Math.Max(0, Math.Floor(width * 0.4))));
        }

        // The body's view switch shows where there is room for it beside the tabs.
        BodyToolsHost.IsVisible = width - Root.ColumnDefinitions[2].Width.Value >= 440;
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
