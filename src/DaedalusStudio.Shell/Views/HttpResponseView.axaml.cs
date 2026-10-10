using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The HTTP Response window: the selected exchange's body in a colored read-only viewer, headers, the request, and the history.</summary>
public sealed partial class HttpResponseView : UserControl, IPaneView
{
    private HttpResponseViewModel? _vm;
    private CodeViewer? _body;

    public HttpResponseView()
    {
        InitializeComponent();
        Clear.Click += (_, _) => _vm?.ClearHistory();
    }

    /// <summary>The body as shown (tests).</summary>
    public string BodyText => _body?.Editor.Document.Text ?? string.Empty;

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

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null)
        {
            _vm.BodyChanged -= OnBodyChanged;
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null)
        {
            _vm.BodyChanged -= OnBodyChanged;
            _vm.BodyChanged += OnBodyChanged;
            ShowBody();
        }
    }

    public void FocusPrimary() => Tabs.Focus();
}
