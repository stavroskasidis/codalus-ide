using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;

namespace Daedalus.Spike.Avalonia;

/// <summary>Code editor tab content: an AvaloniaEdit editor bound to the document's text model.</summary>
public sealed class EditorView : UserControl
{
    internal EditorView(EditorDocument document)
    {
        Document = document;
        Editor = new TextEditor
        {
            Document = document.Text,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.Caret.CaretBrush = Brushes.White;
        Content = Editor;
        document.Controller.Attach(Editor.TextArea.TextView);
        // Opening a document puts the caret in it, as in every IDE (Dock may otherwise keep focus on the tab strip).
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Background);
    }

    internal EditorDocument Document { get; }

    public TextEditor Editor { get; }
}
