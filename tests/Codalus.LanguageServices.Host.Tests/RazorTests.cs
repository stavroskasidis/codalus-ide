using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>samples/Blazor in the real build and language hosts.</summary>
public sealed class BlazorFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("Blazor", "Blazor.slnx", 1);
}

/// <summary>Razor through the Razor source generator (ADR 0023): C# features mapped into .razor files, and Razor's own completion.</summary>
public sealed class RazorTests(BlazorFixture blazor) : IClassFixture<BlazorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Counter => blazor.File("BlazorApp", "Components", "Pages", "Counter.razor");

    private string Home => blazor.File("BlazorApp", "Components", "Pages", "Home.razor");

    private string Greeting => blazor.File("BlazorApp", "Components", "Greeting.razor");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    [Fact]
    public async Task Csharp_in_razor_hovers_navigates_and_completes()
    {
        await blazor.OpenAsync(Counter);

        // Hover on the field in an implicit expression, and its range in the Razor file.
        var hover = await blazor.Client.HoverAsync(Uri(Counter), blazor.At(Counter, "@currentCount", 3), Ct);
        Assert.Contains("currentCount", hover?.Contents.Value, StringComparison.Ordinal);
        Assert.Equal(blazor.At(Counter, "@currentCount", 1).Line, hover?.Range?.Start.Line);

        // Go to Definition from the event handler attribute to the method in the @code block.
        var definition = await blazor.Client.DefinitionAsync(Uri(Counter), blazor.At(Counter, "\"IncrementCount\"", 3), Ct);
        var target = Assert.Single(definition);
        Assert.Equal(Uri(Counter), target.Uri);
        Assert.Equal(blazor.At(Counter, "void IncrementCount", 5).Line, target.Range.Start.Line);

        // Completion inside the @code block maps its edit range back to the Razor file.
        var list = await blazor.Client.CompletionAsync(Uri(Counter), blazor.At(Counter, "currentCount++", "current".Length), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        var item = Assert.Single(list!.Items, i => i.Label == "currentCount");
        var range = list.ItemDefaults?.EditRange ?? item.TextEdit?.Range;
        Assert.Equal(blazor.At(Counter, "currentCount++").Line, range?.Start.Line);
        var resolved = await blazor.Client.ResolveCompletionAsync(item, Ct);
        Assert.Null(resolved.AdditionalTextEdits);

        // References of the field include its uses in markup and in the @code block.
        var references = await blazor.Client.ReferencesAsync(Uri(Counter), blazor.At(Counter, "int currentCount", 5), includeDeclaration: true, Ct);
        Assert.True(references.Count(r => r.Uri == Uri(Counter)) >= 3, string.Join(", ", references.Select(r => $"{r.Uri} {r.Range.Start.Line}")));
    }

    [Fact]
    public async Task Razor_markup_completes_components_parameters_and_directives()
    {
        await blazor.OpenAsync(Home);
        await blazor.EditAsync(Home, "<Greeting Name=\"Blazor\" Times=\"2\" />", "<Greeting Name=\"Blazor\" Times=\"2\" />\n<Gree");
        var tags = await blazor.Client.CompletionAsync(Uri(Home), blazor.At(Home, "\n<Gree", "\n<Gree".Length), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        Assert.Contains(tags!.Items, i => i.Label == "Greeting");
        Assert.Contains(tags.Items, i => i.Label == "PageTitle"); // the framework's components
        Assert.Contains(tags.Items, i => i.Label == "div");

        await blazor.EditAsync(Home, "\n<Gree", "\n<Greeting Times=\"1\" ");
        var parameters = await blazor.Client.CompletionAsync(Uri(Home), blazor.At(Home, "<Greeting Times=\"1\" ", "<Greeting Times=\"1\" ".Length), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        var name = Assert.Single(parameters!.Items, i => i.Label == "Name");
        Assert.Equal("Name=\"$1\"", name.TextEdit?.NewText);
        Assert.Contains("Who to greet", name.Documentation?.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(parameters.Items, i => i.Label == "Times"); // already set
        Assert.Contains(parameters.Items, i => i.Label == "@onclick");

        await blazor.EditAsync(Home, "@page \"/\"", "@page \"/\"\n@in");
        var directives = await blazor.Client.CompletionAsync(Uri(Home), blazor.At(Home, "\n@in", "\n@in".Length), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        Assert.Contains(directives!.Items, i => i.Label == "inject");
        Assert.Contains(directives.Items, i => i.Label == "code");
    }

    [Fact]
    public async Task Components_hover_and_navigate_and_errors_map_into_razor()
    {
        var home = await blazor.OpenAsync(Home);
        await blazor.OpenAsync(Greeting);
        var hover = await blazor.Client.HoverAsync(Uri(Home), blazor.At(Home, "<Greeting", 3), Ct);
        Assert.Contains("BlazorApp.Components.Greeting", hover?.Contents.Value, StringComparison.Ordinal);
        var parameter = await blazor.Client.HoverAsync(Uri(Home), blazor.At(Home, "Times=", 1), Ct);
        Assert.Contains("int Greeting.Times", parameter?.Contents.Value, StringComparison.Ordinal);

        var definition = await blazor.Client.DefinitionAsync(Uri(Home), blazor.At(Home, "<Greeting", 3), Ct);
        Assert.Contains(definition, l => l.Uri == Uri(Greeting));
        var property = await blazor.Client.DefinitionAsync(Uri(Home), blazor.At(Home, "Name=", 1), Ct);
        Assert.Contains(property, l => l.Uri == Uri(Greeting) && l.Range.Start.Line == blazor.At(Greeting, "public string Name").Line);

        // An error in C# inside the Razor file is reported there, at the expression.
        await blazor.EditAsync(Home, "@DateTime.Now.Year", "@DateTime.Now.Yeer");
        await LanguageFixture.WaitUntilAsync(() => blazor.Languages.Diagnostics.For(Home).Any(d => d.Code == "CS1061"), TimeSpan.FromSeconds(90));
        var error = blazor.Languages.Diagnostics.For(Home).First(d => d.Code == "CS1061");
        Assert.Equal(blazor.At(Home, "@DateTime.Now.Yeer").Line, error.Range.Start.Line);
        await blazor.EditAsync(Home, "@DateTime.Now.Yeer", "@DateTime.Now.Year");
        await LanguageFixture.WaitUntilAsync(() => !blazor.Languages.Diagnostics.For(Home).Any(d => d.Code == "CS1061"), TimeSpan.FromSeconds(90));
        Assert.NotEmpty(home);
    }
}
