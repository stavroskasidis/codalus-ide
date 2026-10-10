using Avalonia.Controls;
using Avalonia.Threading;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.Views;

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

/// <summary>Renders the source document's Markdown, again 300 ms after each edit.</summary>
public sealed class MarkdownPreviewView : UserControl, IPaneView
{
    private readonly MarkdownPreviewDocument _document;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public MarkdownPreviewView(MarkdownPreviewDocument document)
    {
        _document = document;
        Content = _scroll;
        Avalonia.Automation.AutomationProperties.SetName(this, document.Title);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Render();
        };
        document.Source.ContentChanged += OnSourceChanged;
        Render();
    }

    /// <summary>The Markdown last rendered (tests).</summary>
    public string Rendered { get; private set; } = string.Empty;

    public void FocusPrimary() => _scroll.Focus();

    private void OnSourceChanged(object? sender, CodeDocumentChangedEventArgs e) => Ui.Post(() =>
    {
        _timer.Stop();
        _timer.Start();
    });

    private void Render()
    {
        var offset = _scroll.Offset;
        Rendered = _document.Source.Text.Text;
        _scroll.Content = MarkdownRenderer.Render(Rendered, _document.OpenLink);
        _scroll.Offset = offset;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_document.IsOpen())
        {
            _document.Source.ContentChanged -= OnSourceChanged;
            _timer.Stop();
        }
    }
}

internal static class MarkdownPreviewExtensions
{
    /// <summary>Whether the document is still in a dock (a detached view may be re-attached on layout changes).</summary>
    public static bool IsOpen(this MarkdownPreviewDocument document) => document.Owner is not null;
}
