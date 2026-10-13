using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Docking;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>M12b analysis and file tools: the Syntax Visualizer, Code Metrics, the image viewer and closing tabs.</summary>
public class AnalysisTests
{
    /// <summary>A 3×2 red PNG.</summary>
    private const string RedPng = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAEUlEQVR4nGP4z8DwH4YZkDkAm34L9XKwuTwAAAAASUVORK5CYII=";

    [AvaloniaFact]
    public async Task The_syntax_visualizer_follows_the_caret_and_code_metrics_measure_the_solution()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        await ShellFixture.PumpUntil(() => document!.Controller is not null);
        var editor = document!.Controller!;
        editor.Editor.CaretOffset = editor.Model.Text.Text.IndexOf("round ? System", StringComparison.Ordinal) + 1;

        f.Shell.ShowSyntaxVisualizer();
        var visualizer = f.Shell.SyntaxVisualizer;
        await ShellFixture.PumpUntil(() => visualizer.Selected?.Item.Text == "round" && visualizer.Properties.Any(p => p.Name == "Type"), seconds: 60);
        Assert.Contains(visualizer.Properties, p => p is { Name: "Type", Value: "bool" });
        Assert.Equal("ParameterReference", Assert.Single(visualizer.Operations).Label.Split(' ')[0]);

        // Choosing a node selects its text in the editor.
        var conditional = visualizer.Roots[0];
        while (conditional.Item.Kind != "ConditionalExpression")
        {
            conditional = conditional.Children.First(c => c.Item.OnPath);
        }

        visualizer.Selected = conditional;
        Assert.StartsWith("round ? System.Math.Round", editor.Editor.SelectedText, StringComparison.Ordinal);

        // Code Metrics for the solution: both projects, with Geometry's complexity.
        await f.Shell.CalculateCodeMetricsAsync(projectOnly: false);
        var metrics = f.Shell.CodeMetrics;
        Assert.Equal(["App", "Lib"], metrics.Roots.Select(r => r.Name));
        var geometry = metrics.Roots[1].DescendantsAndSelf().Single(n => n.Name == "Geometry");
        Assert.Equal("3", geometry.Complexity);
        Assert.StartsWith("Scope\tName\tMaintainability Index", metrics.ToTable(), StringComparison.Ordinal);
        Assert.True(f.Shell.Layout.IsVisible(CodeMetricsViewModel.ToolWindowId));

        // For the active document's project only.
        await f.Shell.CalculateCodeMetricsAsync(projectOnly: true);
        Assert.Equal("Lib", Assert.Single(metrics.Roots).Name);
    }

    [AvaloniaFact]
    public async Task Images_open_in_the_image_viewer_with_zoom()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "red.png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(RedPng));

        Assert.Null(await f.Shell.OpenFileAsync(path));
        var document = f.Shell.Layout.FindDocument<ImageDocument>(ImageDocument.IdFor(path));
        Assert.NotNull(document);
        Assert.Same(document, f.Shell.Layout.ActiveDocument);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ImageViewerView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<ImageViewerView>().Single();
        await view.ImageLoaded;
        Assert.Equal(new Avalonia.PixelSize(3, 2), view.PixelSize);
        view.SetZoom(1);
        view.Step(1);
        Assert.Equal(1.5, view.Zoom);
        view.Step(-1);
        view.Step(-1);
        Assert.Equal(0.75, view.Zoom);
    }

    [AvaloniaFact]
    public async Task Close_all_to_the_right_keeps_the_tabs_before_the_active_one()
    {
        await using var f = ShellFixture.Create();
        var paths = Enumerable.Range(1, 4).Select(i => Path.Combine(f.UserData, $"file{i}.txt")).ToList();
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, path);
            await f.Shell.OpenFileAsync(path);
        }

        await f.Shell.OpenFileAsync(paths[1]);
        f.Shell.CloseDocumentsToTheRight();
        ShellFixture.Pump();
        Assert.Equal(["file1.txt", "file2.txt"], f.Shell.Layout.Documents.OfType<ShellDocument>().Select(d => d.Title).Where(t => t!.StartsWith("file", StringComparison.Ordinal)));
    }
}
