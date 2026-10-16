using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>Class View and the Object Browser (deferred from M12a), through the C# host's <c>codalus/browse</c>.</summary>
public sealed class SymbolBrowserTests
{
    [AvaloniaFact]
    public async Task Class_view_walks_the_solutions_code_and_goes_to_declarations()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        f.Shell.Layout.ShowToolWindow(SymbolBrowserViewModel.ClassViewId, focus: true);
        var view = f.Shell.ClassView;
        await ShellFixture.PumpUntil(() => view.Roots.Select(r => r.Name).Order().SequenceEqual(["App", "Lib"]), seconds: 60);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SymbolBrowserView>().Any(v => v.IsEffectivelyVisible));

        // Project → namespace → type → members.
        var lib = view.Roots.Single(r => r.Name == "Lib");
        await lib.LoadAsync();
        var ns = lib.Children.Single(c => c.Name == "Lib");
        await ns.LoadAsync();
        var circle = ns.Children.Single(c => c.Name == "Circle");
        await circle.LoadAsync();
        Assert.Contains(circle.Children, m => m is { Name: "Area", Detail: "double Area()" });

        // The selection's declaration and summary; Enter goes to it.
        view.Selected = circle;
        await ShellFixture.PumpUntil(() => view.Declaration == "public sealed class Circle", seconds: 30);
        await view.OpenSelectedAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(folder, "src", "Lib", "Shapes.cs"), seconds: 30);

        // Search lists matching types and members.
        view.Search = "Circ";
        await ShellFixture.PumpUntil(() => view.Roots.Any(r => r.Name == "Circle"), seconds: 30);
        view.Search = string.Empty;
        await ShellFixture.PumpUntil(() => view.Roots.Any(r => r.Name == "Lib"), seconds: 30);
    }

    [AvaloniaFact]
    public async Task The_object_browser_shows_a_projects_assemblies_down_to_library_members()
    {
        await using var f = ShellFixture.Create();
        await f.OpenLanguageFeaturesAsync();
        var browser = f.Shell.OpenObjectBrowser();
        await ShellFixture.PumpUntil(() => browser.Projects.Count == 2 && browser.Roots.Count > 1, seconds: 60);
        browser.Project = browser.Projects.Single(p => p.Name == "Lib");
        await ShellFixture.PumpUntil(() => browser.Roots.FirstOrDefault()?.Name == "Lib" && browser.Roots.Any(r => r.Name == "System.Runtime"), seconds: 60);

        browser.Search = "IsNullOrWhiteSpace";
        await ShellFixture.PumpUntil(() => browser.Roots.Any(r => r.Name == "IsNullOrWhiteSpace"), seconds: 60);
        var member = browser.Roots.First(r => r.Name == "IsNullOrWhiteSpace");
        browser.Selected = member;
        await ShellFixture.PumpUntil(() => browser.Declaration?.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal) == true, seconds: 30);
        Assert.Contains("Member of System.String", browser.Description, StringComparison.Ordinal);

        // Library code opens read-only (decompiled or from its PDB).
        await browser.OpenSelectedAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model is { IsReadOnly: true } model && model.Text.Text.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal), seconds: 60);
    }
}
