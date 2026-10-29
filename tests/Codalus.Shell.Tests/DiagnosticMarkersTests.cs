using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// How a diagnostic is marked: errors and warnings by a squiggle along their span, information (Roslyn's suggestions,
/// such as an <c>.editorconfig</c> rule at <c>suggestion</c>) and hints by three dots under their start, as the classic
/// IDE draws suggestions (the maintainer's report: IDE0290 on a constructor was a full blue squiggle in Codalus IDE).
/// </summary>
public sealed class DiagnosticMarkersTests
{
    private const string Text = "public whDemoLoginService(int x) { }\npublic whDemoLoginService(int x) { }\npublic whDemoLoginService(int x) { }\n";

    [AvaloniaTheory]
    [InlineData(DiagnosticSeverity.Error, false)]
    [InlineData(DiagnosticSeverity.Warning, false)]
    [InlineData(DiagnosticSeverity.Information, true)]
    [InlineData(DiagnosticSeverity.Hint, true)]
    public void Information_and_hints_are_dots_under_the_start_and_errors_and_warnings_squiggles(int severity, bool dotted)
    {
        Assert.Equal(dotted, DiagnosticMarkers.IsDotted(severity));

        var editor = new TextEditor { Document = new TextDocument(Text), FontFamily = MarkdownRenderer.Monospace, Background = Brushes.Black, Foreground = Brushes.Black };
        var window = new Window { Width = 900, Height = 300, Content = editor, Background = Brushes.Black };
        window.Show();
        try
        {
            var markers = new DiagnosticMarkers(editor.Document) { Theme = EditorTheme.Dark };
            var view = editor.TextArea.TextView;
            view.BackgroundRenderers.Add(markers);
            var name = new Range(new Position(1, 7), new Position(1, 25)); // whDemoLoginService
            markers.Set([new Diagnostic(name, severity, "IDE0000", "analyzer", "A diagnostic")], p => editor.Document.GetOffset(p.Line + 1, p.Character + 1));
            view.InvalidateLayer(markers.Layer);
            ShellFixture.Pump();

            using var frame = window.CaptureRenderedFrame()!;
            var start = view.GetVisualPosition(new TextViewPosition(2, 8), AvaloniaEdit.Rendering.VisualYPosition.LineBottom).X;
            var end = view.GetVisualPosition(new TextViewPosition(2, 26), AvaloniaEdit.Rendering.VisualYPosition.LineBottom).X;
            var marked = MarkedColumns(frame, view, line: 2);
            Assert.NotEmpty(marked);
            Assert.All(marked, x => Assert.InRange(x, start - 1, end + 1));
            var extent = marked.Max() - marked.Min();
            if (dotted)
            {
                Assert.True(extent < 12, $"dots span {extent:F0} px of the {end - start:F0} px name");
            }
            else
            {
                Assert.True(extent > (end - start) * 0.8, $"the squiggle spans {extent:F0} px of the {end - start:F0} px name");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The x of every column (in the view) with a lit pixel in the lowest rows of a line; the text is black on black.</summary>
    private static List<double> MarkedColumns(Avalonia.Media.Imaging.WriteableBitmap frame, AvaloniaEdit.Rendering.TextView view, int line)
    {
        var columns = new List<double>();
        using var buffer = frame.Lock();
        var bottom = view.GetVisualTopByDocumentLine(line) - view.VerticalOffset + view.DefaultLineHeight;
        for (var x = 0.0; x < view.Bounds.Width; x++)
        {
            for (var dy = 1.0; dy <= 5; dy++)
            {
                var point = view.TranslatePoint(new Point(x, bottom - dy), TopLevel.GetTopLevel(view)!)!.Value;
                var offset = ((int)point.Y * buffer.RowBytes) + ((int)point.X * 4);
                if (Marshal.ReadByte(buffer.Address, offset) + Marshal.ReadByte(buffer.Address, offset + 1) + Marshal.ReadByte(buffer.Address, offset + 2) > 90)
                {
                    columns.Add(x);
                    break;
                }
            }
        }

        return columns;
    }
}
