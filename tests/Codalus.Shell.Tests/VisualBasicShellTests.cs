using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// M35 (ADR 0066) in the headless shell on samples/VisualBasic: the VB projects go to the C# language host with the
/// C# library, a <c>.vb</c> file is Visual Basic served by that host (errors live as you type), a <c>.vbproj</c> gets
/// VB's property pages, and Add New Item offers VB's item templates.
/// </summary>
public sealed class VisualBasicShellTests
{
    [AvaloniaFact]
    public async Task Vb_files_are_served_by_the_csharp_host_with_vb_property_pages_and_templates()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("VisualBasic");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "VisualBasic.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 4 }, seconds: 240);

        // Program.vb is Visual Basic, and an error typed into it comes back from the C# host.
        var path = Path.Combine(folder, "VbConsole", "Program.vb");
        var document = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var model = document!.Model;
        Assert.Equal(ShellHost.VisualBasicLanguageId, model.LanguageId);
        Assert.Contains(f.Shell.Workbench.Snippets.For(ShellHost.VisualBasicLanguageId), s => s.Prefixes.Contains("ForEach") && s.Body.Contains("Next", StringComparison.Ordinal));
        var call = model.Text.Text.IndexOf("inventory.Add(", StringComparison.Ordinal);
        model.Text.Replace(call, "inventory.Add(".Length, "inventory.Ad(");
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.Get(path, ShellHost.CSharpLanguageId) is { } d && d.Items.Any(x => x.Code == "BC30456"), seconds: 120);
        model.Text.Replace(call, "inventory.Ad(".Length, "inventory.Add(");

        // The VB project's property pages: VB's options instead of C#'s language settings.
        var library = Path.Combine(folder, "VbLibrary", "VbLibrary.vbproj");
        var properties = f.Shell.OpenProjectProperties(library);
        Assert.NotNull(properties);
        var names = properties!.Pages.SelectMany(p => p.Properties).Select(p => p.Name).ToList();
        Assert.Contains("OptionStrict", names);
        Assert.Contains("OptionInfer", names);
        Assert.DoesNotContain("Nullable", names);
        Assert.DoesNotContain("AllowUnsafeBlocks", names);

        // Add New Item: VB's code templates for the VB project, C#'s for the C# library.
        var vb = new ItemTemplateProject(ItemTemplateProject.LanguageOf(library));
        var cs = new ItemTemplateProject(ItemTemplateProject.LanguageOf(Path.Combine(folder, "CsLibrary", "CsLibrary.csproj")));
        Assert.Contains(ItemTemplates.BuiltIn, t => t.ShortName == "vbmodule" && ItemTemplates.AppliesTo(t, vb));
        Assert.DoesNotContain(ItemTemplates.BuiltIn, t => t.ShortName == "class" && ItemTemplates.AppliesTo(t, vb));
        Assert.DoesNotContain(ItemTemplates.BuiltIn, t => t.ShortName == "vbclass" && ItemTemplates.AppliesTo(t, cs));
    }
}
