using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>The lightbulb's question, whether a diagnostic touches a line, answered from the markers' tree as by a walk.</summary>
public sealed class DiagnosticMarkersAnyOnTests
{
    private static Diagnostic At(int startLine, int startCharacter, int endLine, int endCharacter) =>
        new(new Range(new Position(startLine, startCharacter), new Position(endLine, endCharacter)), DiagnosticSeverity.Error, "CS1", "csharp", "x");

    [AvaloniaFact] // the markers are an Avalonia object: made on the UI thread
    public void A_diagnostic_lights_the_lines_it_touches_and_not_the_next_ones()
    {
        var document = new TextDocument("first line\r\nsecond\r\nthird\r\nfourth");
        var markers = new DiagnosticMarkers(document);
        int Offset(Position p) => document.GetOffset(p.Line + 1, p.Character + 1);
        bool Lit(int line) => document.GetLineByNumber(line) is var l && markers.AnyOn(l.Offset, l.Length);
        bool Walk(int line) => document.GetLineByNumber(line) is var l && markers.Segments.Any(s => s.StartOffset <= l.EndOffset && s.EndOffset >= l.Offset);

        Assert.False(Lit(1));
        markers.Set([At(0, 6, 0, 10)], Offset); // touching the end of line 1
        Assert.Equal([true, false, false, false], Enumerable.Range(1, 4).Select(Lit));
        markers.Set([At(0, 10, 0, 10)], Offset); // zero-width at the end of line 1
        Assert.Equal(Enumerable.Range(1, 4).Select(Walk), Enumerable.Range(1, 4).Select(Lit));
        markers.Set([At(0, 2, 2, 1)], Offset); // from line 1 into line 3
        Assert.Equal([true, true, true, false], Enumerable.Range(1, 4).Select(Lit));
        markers.Set([At(3, 0, 3, 2)], Offset); // the start of line 4 only
        Assert.Equal([false, false, false, true], Enumerable.Range(1, 4).Select(Lit));
        Assert.Equal(Enumerable.Range(1, 4).Select(Walk), Enumerable.Range(1, 4).Select(Lit));
    }
}
