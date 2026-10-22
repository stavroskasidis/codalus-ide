using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.Views.DataTips;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// The hover's diagnostics while debugging (D44, decision 17, A3, A18): a strip right above the data tip's header, with
/// the code's help link and the message as Quick Info shows them, at most 470 px wide; under the header when the tips
/// open upwards near the editor's bottom edge. The diagnostics are the C# service's, for the file's unknown names.
/// </summary>
public sealed class DataTipDiagnosticsTests
{
    private static string Code()
    {
        var lines = new List<string> { "class Program", "{", "    static void Main()", "    {" };
        for (var i = 0; i < 150; i++)
        {
            lines.Add($"        app.Use(items); // {i}");
        }

        lines.AddRange(["    }", "}"]);
        return string.Join('\n', lines) + "\n";
    }

    private static object[] Stops() =>
    [
        new Dictionary<string, object>
        {
            ["evaluate"] = new Dictionary<string, object> { ["app"] = Result("{App.Builder}", "App.Builder", 10) },
            ["variables"] = new Dictionary<string, object>
            {
                ["10"] = Enumerable.Range(0, 30).Select(i => (object)Var($"Item{i:00}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "int", 0, $"app.Item{i:00}")).ToArray(),
            },
        },
    ];

    [AvaloniaFact]
    public async Task The_hovers_diagnostics_sit_in_a_strip_above_the_header_and_under_it_when_the_tips_flip()
    {
        await using var s = await StartAsync(Code(), 5, Stops());
        var word = s.OffsetOf(5, "app", 1);

        // The C# service reports app as unknown (CS0103): the hover shows it with the data tip.
        await ShellFixture.PumpUntil(() => s.Editor.Markers.At(word).Count > 0, seconds: 120, describe: () => $"{s.Editor.Markers.Segments.Count()} diagnostics");
        var tip = await s.HoverAsync(word);
        await ShellFixture.PumpUntil(() => tip.Strip is { IsOpen: true, Child.Bounds.Width: > 0 });
        ShellFixture.RenderFrame(s.F.Window);
        var strip = tip.Strip!.Child!;
        ShellFixture.RenderFrame(HostOf(strip));
        var header = s.InTextView(tip.Header);
        var bounds = s.InTextView(strip);
        Assert.True(Math.Abs(bounds.Bottom - (header.Top - 2)) <= 1.5, $"the strip at {bounds}, the header at {header}");
        Assert.Equal(header.Left, bounds.Left, 1);
        Assert.True(bounds.Width <= DataTipCascade.StripMaxWidth + 0.5, $"the strip is {bounds.Width} px wide");
        var texts = string.Join(" ", strip.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("CS0103", texts, StringComparison.Ordinal);
        Assert.Contains("Fix", texts, StringComparison.Ordinal); // Fix and Other Fixes, as in Quick Info
        Assert.Contains("Other Fixes", texts, StringComparison.Ordinal);
        Assert.Contains(tip.Bounds, b => Math.Abs(b.Top - bounds.Top) <= 1 && Math.Abs(b.Height - bounds.Height) <= 1); // the pointer on its way to it keeps the hover open
        s.F.Press(Avalonia.Input.Key.Escape);

        // Near the bottom edge the members open upwards: the strip goes under the header.
        s.View.EnsureVisualLines();
        var last = s.View.VisualLines.Last(l => l.VisualTop + l.Height - s.View.VerticalOffset <= s.View.Bounds.Height).FirstDocumentLine.LineNumber;
        var bottom = s.OffsetOf(last, "app", 1);
        await ShellFixture.PumpUntil(() => s.Editor.Markers.At(bottom).Count > 0, seconds: 60);
        tip = await s.HoverAsync(bottom);
        tip.SetOpen(null, tip.Header, tip.Root, true);
        await ShellFixture.PumpUntil(() => tip.Panels is [{ Rows.Count: 30 }] && tip.Strip is { IsOpen: true });
        ShellFixture.RenderFrame(s.F.Window);
        Assert.True(tip.IsFlipped);
        header = s.InTextView(tip.Header);
        bounds = s.InTextView(tip.Strip!.Child!);
        Assert.True(Math.Abs(bounds.Top - (header.Bottom + 2)) <= 1.5, $"the strip at {bounds}, the header at {header}");
        Assert.True(s.InTextView(tip.Panels[0]).Bottom <= header.Top + 1);
    }
}
