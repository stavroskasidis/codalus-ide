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
        var pageTitle = Assert.Single(tags.Items, i => i.Label == "PageTitle"); // the framework's components
        Assert.Contains(tags.Items, i => i.Label == "div");

        // Its summary, from the reference pack's documentation, when the item is resolved.
        Assert.Null(pageTitle.Documentation);
        Assert.Contains("Enables rendering an HTML", (await blazor.Client.ResolveCompletionAsync(pageTitle, Ct)).Documentation?.Value, StringComparison.Ordinal);

        await blazor.EditAsync(Home, "\n<Gree", "\n<Greeting Times=\"1\" ");
        var parameters = await blazor.Client.CompletionAsync(Uri(Home), blazor.At(Home, "<Greeting Times=\"1\" ", "<Greeting Times=\"1\" ".Length), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        var name = Assert.Single(parameters!.Items, i => i.Label == "Name");
        Assert.Equal("Name=\"$1\"", name.TextEdit?.NewText);
        Assert.Contains("Who to greet", (await blazor.Client.ResolveCompletionAsync(name, Ct)).Documentation?.Value, StringComparison.Ordinal);
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

    /// <summary>The maintainer's Routes.razor, reduced: components, HTML, directives, C# blocks and both kinds of comments.</summary>
    internal const string ColoredRoutes = """
        @using Microsoft.AspNetCore.Components.Authorization
        @inject ILogger<Routes> Logger
        @* A Razor comment *@
        <!-- An HTML comment -->
        <Router AppAssembly="@typeof(Program).Assembly">
            <Found Context="routeData">
                <RouteView RouteData="@routeData" DefaultLayout="@typeof(Layout.MainLayout)" />
            </Found>
        </Router>
        @if (Logger is not null)
        {
            <Greeting Name="Blazor" Times="2" />
        }
        else
        {
            <p class="note">Hello @Logger</p>
        }
        <button @onclick="Click" @onclick:preventDefault>Go</button>

        @code {
            private void Click() => Logger.LogInformation("Clicked");
        }

        """;

    [Fact]
    public async Task Razor_files_get_semantic_tokens_of_razor_markup_and_mapped_csharp()
    {
        // The maintainer's report: components and HTML colored alike by the grammar (components as invalid tags), no Razor colors.
        var routes = blazor.File("BlazorApp", "Components", "Routes.razor");
        var original = await blazor.OpenAsync(routes);
        await blazor.EditAsync(routes, original, ColoredRoutes);
        var text = blazor.Text(routes).Text;
        var lines = text.Split('\n');
        var legend = blazor.Client.Legend!.TokenTypes;
        List<(int Line, int Start, int Length, string Type)> tokens = [];
        await LanguageFixture.WaitUntilAsync(() =>
        {
            var data = blazor.Client.SemanticTokensAsync(Uri(routes), null, Ct).GetAwaiter().GetResult().Data;
            tokens = [.. Decode(data).Select(t => (t.Line, t.Start, t.Length, legend[t.Type]))];
            return tokens.Any(t => t.Type == "razorComponentElement") && tokens.Any(t => t.Type == "controlKeyword");
        }, TimeSpan.FromSeconds(90));
        var report = string.Join('\n', tokens.Select(t => $"{t.Line}:{t.Start} {t.Type} '{lines[t.Line].Substring(t.Start, Math.Min(t.Length, lines[t.Line].Length - t.Start))}'"));

        // The token (type and text) at the nth occurrence of a marker, plus a delta.
        void Expect(string? type, string tokenText, string marker, int delta = 0, int occurrence = 1)
        {
            var offset = -1;
            for (var i = 0; i < occurrence; i++)
            {
                offset = text.IndexOf(marker, offset + 1, StringComparison.Ordinal);
                Assert.True(offset >= 0, $"'{marker}' #{occurrence} not found");
            }

            var position = LanguageFixture.PositionOf(text, offset + delta);
            var token = tokens.FirstOrDefault(t => t.Line == position.Line && position.Character >= t.Start && position.Character < t.Start + t.Length);
            var actual = token.Type is null ? (null, string.Empty) : (token.Type, lines[position.Line].Substring(token.Start, token.Length));
            Assert.True((type, tokenText) == actual, $"At '{marker}'+{delta}: expected {type} '{tokenText}', got {actual.Item1} '{actual.Item2}'.\n{report}");
        }

        // Directives: the transition and the keyword in Razor's colors, their C# by Roslyn (the namespace, the type).
        Expect("razorTransition", "@", "@using");
        Expect("razorDirective", "using", "@using", 1);
        Expect("namespace", "Microsoft", "Microsoft.AspNetCore");
        Expect("razorDirective", "inject", "@inject", 1);
        Expect("interface", "ILogger", "ILogger<Routes>");

        // Comments: Razor's, with its transitions and stars; HTML's.
        Expect("razorCommentTransition", "@", "@*");
        Expect("razorCommentStar", "*", "@*", 1);
        Expect("razorComment", " A Razor comment ", "A Razor");
        Expect("razorCommentStar", "*", "*@");
        Expect("markupCommentPunctuation", "<!--", "<!--");
        Expect("markupComment", " An HTML comment ", "An HTML");

        // Components (the framework's, the project's, a child content tag), their parameters, delimiters and values.
        Expect("markupTagDelimiter", "<", "<Router");
        Expect("razorComponentElement", "Router", "<Router", 1);
        Expect("razorComponentAttribute", "AppAssembly", "AppAssembly");
        Expect("markupOperator", "=", "AppAssembly=", "AppAssembly".Length);
        Expect("markupAttributeQuote", "\"", "=\"@typeof", 1);
        Expect("razorTransition", "@", "@typeof");
        Expect("keyword", "typeof", "@typeof", 1);
        Expect("class", "Program", "Program)");
        Expect("razorComponentElement", "Found", "<Found", 1);
        Expect("razorComponentAttribute", "Context", "Context");
        Expect("markupAttributeValue", "routeData", "\"routeData\"", 1);
        Expect("razorComponentElement", "RouteView", "<RouteView", 1);
        Expect("markupTagDelimiter", "/>", "/>");
        Expect("razorComponentElement", "Found", "</Found", 2);
        Expect("razorComponentElement", "Router", "</Router", 2);
        Expect("razorComponentElement", "Greeting", "<Greeting", 1);
        Expect("razorComponentAttribute", "Name", "Name=");
        Expect("markupAttributeValue", "Blazor", "\"Blazor\"", 1);
        Expect("number", "2", "\"2\"", 1);

        // C# blocks: the transition, then Roslyn's control keywords and symbols.
        Expect("razorTransition", "@", "@if");
        Expect("controlKeyword", "if", "@if", 1);
        Expect("controlKeyword", "else", "else");
        Expect("property", "Logger", "Logger is");

        // HTML: elements, attributes and values; text has no token.
        Expect("markupElement", "p", "<p", 1);
        Expect("markupAttribute", "class", "class=");
        Expect("markupAttributeValue", "note", "\"note\"", 1);
        Expect("markupElement", "p", "</p>", 2);
        Expect("markupElement", "button", "<button", 1);
        Expect(null, string.Empty, "Go<");

        // Directive attributes, and the @code block's braces.
        Expect("razorTransition", "@", "@onclick=");
        Expect("razorDirectiveAttribute", "onclick", "@onclick=", 1);
        Expect("method", "Click", "\"Click\"", 1);
        Expect("razorDirectiveColon", ":", ":preventDefault");
        Expect("razorDirectiveAttribute", "preventDefault", "preventDefault");
        Expect("razorDirective", "code", "@code", 1);
        Expect("razorTransition", "{", "@code {", "@code ".Length);
        Expect("razorTransition", "}", "\n}", 1, occurrence: 3);
        Expect("extensionMethod", "LogInformation", "LogInformation");

        // A range request has the same tokens for its lines.
        var range = await blazor.Client.SemanticTokensAsync(Uri(routes), new Protocol.Range(new Position(4, 0), new Position(6, 0)), Ct);
        var ranged = Decode(range.Data).Select(t => (t.Line, t.Start, t.Length, legend[t.Type])).ToList();
        Assert.NotEmpty(ranged);
        Assert.Equal(tokens.Where(t => t.Line is >= 4 and <= 6), ranged);
    }

    private static List<(int Line, int Start, int Length, int Type)> Decode(int[] data)
    {
        var result = new List<(int, int, int, int)>();
        int line = 0, start = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            result.Add((line, start, data[i + 2], data[i + 3]));
        }

        return result;
    }
}
