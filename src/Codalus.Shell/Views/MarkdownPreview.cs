using Avalonia.Controls;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Views;

/// <summary>A live preview of a Markdown document (Open Preview to the Side, ADR 0016), beside its editor.</summary>
public sealed class MarkdownPreviewDocument : ShellDocument
{
    public MarkdownPreviewDocument(CodeDocument source, Action<string> openLink)
    {
        Source = source;
        OpenLink = openLink;
        Id = DocumentIdFor(source.Path);
        Title = "Preview " + Path.GetFileName(source.Path);
    }

    public override string Kind => "markdownPreview";

    public CodeDocument Source { get; }

    public Action<string> OpenLink { get; }

    public static string DocumentIdFor(string path) => "markdown-preview:" + path;
}

/// <summary>The preview tab of Open Preview to the Side: a <see cref="MarkdownLivePreview"/> as long as the tab is open.</summary>
public sealed class MarkdownPreviewView : UserControl, IPaneView
{
    private readonly MarkdownPreviewDocument _document;
    private readonly MarkdownLivePreview _preview;

    public MarkdownPreviewView(MarkdownPreviewDocument document)
    {
        _document = document;
        _preview = new MarkdownLivePreview(document.Source, document.OpenLink);
        Content = _preview;
        Avalonia.Automation.AutomationProperties.SetName(this, document.Title);
    }

    /// <summary>The Markdown last rendered (tests).</summary>
    public string Rendered => _preview.Rendered;

    public void FocusPrimary() => _preview.FocusPrimary();

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_document.IsOpen())
        {
            _preview.Release();
        }
    }
}

/// <summary>
/// Renders a document's Markdown, again 300 ms after each edit. While it isn't shown (a collapsed preview, an inactive
/// tab) edits only mark it stale: it renders when it shows again. Used by the preview tab and by the preview half of a
/// Markdown editor (<see cref="MarkdownSplit"/>).
/// </summary>
public sealed class MarkdownLivePreview : UserControl
{
    private readonly CodeDocument _source;
    private readonly Action<string> _openLink;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _stale = true;
    private bool _released;

    public MarkdownLivePreview(CodeDocument source, Action<string> openLink)
    {
        _source = source;
        _openLink = openLink;
        Scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = Scroll;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            RenderIfShown();
        };
        source.ContentChanged += OnSourceChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                RenderIfShown();
            }
        };
    }

    /// <summary>The scroll viewer around the rendered Markdown (the split's scroll sync).</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>The Markdown last rendered (tests).</summary>
    public string Rendered { get; private set; } = string.Empty;

    /// <summary>Raised after each render, once the new content is in place.</summary>
    public event EventHandler? RenderedChanged;

    public void FocusPrimary() => Scroll.Focus();

    /// <summary>Stops following the document (the tab closed).</summary>
    public void Release()
    {
        _released = true;
        _source.ContentChanged -= OnSourceChanged;
        _timer.Stop();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RenderIfShown();
    }

    private void OnSourceChanged(object? sender, CodeDocumentChangedEventArgs e) => Ui.Post(() =>
    {
        _stale = true;
        _timer.Stop();
        _timer.Start();
    });

    private void RenderIfShown()
    {
        if (!_stale || _released || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        _stale = false;
        var offset = Scroll.Offset;
        Rendered = _source.Text.Text;
        Scroll.Content = MarkdownRenderer.Render(Rendered, _openLink);
        Scroll.Offset = offset;
        RenderedChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class MarkdownPreviewExtensions
{
    /// <summary>Whether the document is still in a dock (a detached view may be re-attached on layout changes).</summary>
    public static bool IsOpen(this MarkdownPreviewDocument document) => document.Owner is not null;
}
