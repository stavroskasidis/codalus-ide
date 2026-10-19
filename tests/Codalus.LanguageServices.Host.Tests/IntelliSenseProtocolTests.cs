using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The <c>codalus/</c> extensions IntelliSense draws in the editor's colors: signature help's classified parts with
/// a generic base class's type arguments as the call site has them, Quick Info's and the completion tooltip's classified
/// descriptions with the overload count and each section once, and the completion items' glyphs.
/// </summary>
public sealed partial class LanguageServiceTests
{
    private const string Builders = """


        public class PageModel { }

        public class LayoutBuilderBase<TModel, TBuilder>
        {
            /// <summary>Method adds a tab.</summary>
            /// <param name="id">Id - incremental</param>
            /// <param name="tabPageBuilder">Builds the tab's page.</param>
            /// <returns>The builder, for <see cref="AddTab(string)"/> next.</returns>
            public TBuilder AddTab(int id, System.Action<TBuilder> tabPageBuilder) => default!;

            /// <summary>Method adds a tab with a title.</summary>
            public TBuilder AddTab(string title) => default!;
        }

        public class PageBuilder<TModel> : LayoutBuilderBase<TModel, PageBuilder<TModel>> { }

        public static class Usage
        {
            public static void Build(PageBuilder<PageModel> builder)
            {
                builder.AddTab(1, page => { });
                builder.
            }
        }
        """;

    private async Task<string> WithBuildersAsync()
    {
        var path = fixture.File("src", "Lib", "Square.Part.cs");
        await fixture.OpenAsync(path);
        await fixture.EditAsync(path, "public double Side { get; }\n}", "public double Side { get; }\n}" + Builders);
        return path;
    }

    private async Task WithoutBuildersAsync(string path)
    {
        await fixture.EditAsync(path, Builders, string.Empty);
        await fixture.Languages.CloseAsync(path);
    }

    [Fact]
    public async Task Signature_help_parts_are_classified_with_the_call_sites_type_arguments()
    {
        var path = await WithBuildersAsync();
        try
        {
            var help = await fixture.Client.SignatureHelpAsync(Uri(path), fixture.At(path, "builder.AddTab(1", "builder.AddTab(".Length), new SignatureHelpContext(2, "(", false), Ct);

            Assert.NotNull(help);
            Assert.Equal(2, help.Signatures.Length);
            var signature = help.Signatures[help.ActiveSignature];

            // The base class's type parameters are the call site's: TModel = PageModel, TBuilder = PageBuilder<PageModel>.
            Assert.Equal("PageBuilder<PageModel> LayoutBuilderBase<PageModel, PageBuilder<PageModel>>.AddTab(int id, System.Action<PageBuilder<PageModel>> tabPageBuilder)", signature.Label);
            Assert.NotNull(signature.Parts);
            Assert.Equal(signature.Label, string.Concat(signature.Parts.Select(p => p.Text)));
            Assert.Contains(signature.Parts, p => p.Text == "AddTab" && p.Kind == "method");
            Assert.Contains(signature.Parts, p => p.Text == "LayoutBuilderBase" && p.Kind == "class");
            Assert.Contains(signature.Parts, p => p.Text == "int" && p.Kind == "keyword" && p.Parameter == 0);
            Assert.Contains(signature.Parts, p => p.Text == "id" && p.Kind == "parameter" && p.Parameter == 0);
            Assert.Contains(signature.Parts, p => p.Text == "tabPageBuilder" && p.Kind == "parameter" && p.Parameter == 1);

            // Each parameter's runs are its label, a substring of the signature's (plain LSP clients still work).
            Assert.Equal("int id", string.Concat(signature.Parts.Where(p => p.Parameter == 0).Select(p => p.Text)));
            Assert.Equal("int id", signature.Parameters[0].Label);
            Assert.Equal(0, signature.ActiveParameter);
            Assert.Equal("Method adds a tab.", signature.Documentation?.Value);
            Assert.Equal("Id - incremental", signature.Parameters[0].Documentation?.Value);
        }
        finally
        {
            await WithoutBuildersAsync(path);
        }
    }

    [Fact]
    public async Task Hover_describes_the_symbol_as_classified_text_with_each_section_once()
    {
        var path = await WithBuildersAsync();
        try
        {
            var hover = await fixture.Client.HoverAsync(Uri(path), fixture.At(path, "builder.AddTab(1", "builder.Add".Length), Ct);

            Assert.NotNull(hover?.Description);
            var description = hover.Description;
            var signature = string.Concat(description.Signature.Select(r => r.Text));
            Assert.Contains("LayoutBuilderBase<PageModel, PageBuilder<PageModel>>.AddTab(int id, System.Action<PageBuilder<PageModel>> tabPageBuilder)", signature, StringComparison.Ordinal);
            Assert.DoesNotContain("overload", signature, StringComparison.Ordinal); // the count is apart
            Assert.Equal(1, description.Overloads);
            Assert.Equal(SymbolGlyphs.Method, description.Glyph);
            Assert.Contains(description.Signature, r => r.Text == "AddTab" && r.Kind == "method");

            var summary = Assert.Single(description.Sections, s => s.Kind == DescriptionSectionKinds.Summary);
            Assert.Equal("Method adds a tab.", string.Concat(summary.Text.Select(r => r.Text)));

            // Returns once, without Roslyn's own "Returns:" heading; its cref is classified code.
            var returns = Assert.Single(description.Sections, s => s.Kind == DescriptionSectionKinds.Returns);
            var returnsText = string.Concat(returns.Text.Select(r => r.Text));
            Assert.StartsWith("The builder, for ", returnsText, StringComparison.Ordinal);
            Assert.DoesNotContain("Returns", returnsText, StringComparison.Ordinal);
            Assert.Contains(returns.Text, r => r.Kind == "method" && r.Text == "AddTab");

            // The Markdown for other clients says it once too.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(hover.Contents.Value, "Returns:"));
            Assert.Contains("(+ 1 overload)", hover.Contents.Value, StringComparison.Ordinal);
        }
        finally
        {
            await WithoutBuildersAsync(path);
        }
    }

    [Fact]
    public async Task Completion_items_name_their_glyph_and_resolve_a_classified_description()
    {
        var path = await WithBuildersAsync();
        try
        {
            var list = await fixture.Client.CompletionAsync(Uri(path), fixture.At(path, "builder.\n", "builder.".Length), new CompletionContext(CompletionTriggerKind.TriggerCharacter, "."), Ct);
            var addTab = list.Items.Single(i => i.Label == "AddTab");
            Assert.Equal(SymbolGlyphs.Method, addTab.Glyph);
            Assert.Contains(list.Items, i => i.Label == "GetHashCode" && i.Glyph == SymbolGlyphs.Method);

            var resolved = await fixture.Client.ResolveCompletionAsync(addTab, Ct);
            Assert.NotNull(resolved.Description);
            Assert.Equal(1, resolved.Description.Overloads);
            Assert.Contains("AddTab(", string.Concat(resolved.Description.Signature.Select(r => r.Text)), StringComparison.Ordinal);
            Assert.Contains(resolved.Description.Signature, r => r.Kind == "keyword");
            Assert.Contains(resolved.Description.Sections, s => s.Kind == DescriptionSectionKinds.Summary && string.Concat(s.Text.Select(r => r.Text)).Contains("adds a tab", StringComparison.Ordinal));

            // A type: "class Lib.PageModel".
            var types = await fixture.Client.CompletionAsync(Uri(path), fixture.At(path, "builder.\n", "builder.".Length - 8), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
            var pageModel = types.Items.Single(i => i.Label == "PageModel");
            Assert.Equal(SymbolGlyphs.Class, pageModel.Glyph);
            var described = (await fixture.Client.ResolveCompletionAsync(pageModel, Ct)).Description;
            Assert.NotNull(described);
            Assert.Equal("class Lib.PageModel", string.Concat(described.Signature.Select(r => r.Text)));
        }
        finally
        {
            await WithoutBuildersAsync(path);
        }
    }
}
