using Codalus.Workbench.Snippets;

namespace Codalus.Workbench.Tests;

/// <summary>M12b snippets: the TextMate syntax, VS Code's and the classic IDE's snippet files, and the registry's precedence.</summary>
public sealed class SnippetTests
{
    [Fact]
    public void The_parser_reads_tab_stops_defaults_choices_variables_and_escapes()
    {
        var parts = SnippetParser.Parse(@"for (var ${1:i} = 0; $1 < ${2|a,b|}; $1++) { $TM_SELECTED_TEXT${3:${TM_FILENAME_BASE:x}} \$0 }$0");

        Assert.Equal(new SnippetText("for (var "), parts[0]);
        Assert.Equal(new SnippetPlaceholder(1, "i", null), parts[1]);
        Assert.Equal(new SnippetPlaceholder(1, string.Empty, null), parts[3]);
        var choice = Assert.IsType<SnippetPlaceholder>(parts[5]);
        Assert.Equal((2, "a"), (choice.Index, choice.Default));
        Assert.Equal(["a", "b"], choice.Choices!);
        Assert.Contains(parts, p => p is SnippetVariable { Name: "TM_SELECTED_TEXT" });
        Assert.Contains(parts, p => p is SnippetPlaceholder { Index: 3, Default: "x" }); // a nested variable keeps its default
        Assert.Contains(parts, p => p is SnippetText t && t.Text.Contains(" $0 ", StringComparison.Ordinal)); // escaped
        Assert.Equal(new SnippetPlaceholder(0, string.Empty, null), parts[^1]);

        Assert.Equal("for (var i = 0; i < a; i++) { x $0 }", SnippetParser.ToPlainText(@"for (var ${1:i} = 0; ${1:i} < ${2|a,b|}; ${1:i}++) { ${3:x} \$0 }$0"));
    }

    [Fact]
    public void VS_Code_snippet_files_parse_with_scopes_and_array_bodies()
    {
        const string json = """
            {
              // A comment.
              "Print": { "prefix": ["pr", "print"], "body": ["Console.WriteLine($1);", "$0"], "description": "Print a line" },
              "Everywhere": { "prefix": "ev", "body": "x", "scope": "csharp,fsharp" },
            }
            """;
        var snippets = SnippetRegistry.ParseJson(json, "csharp", "user");

        var print = snippets.Single(s => s.Name == "Print");
        Assert.Equal(["pr", "print"], print.Prefixes);
        Assert.Equal("Console.WriteLine($1);\n$0", print.Body);
        Assert.Equal("Print a line", print.Description);
        Assert.Equal(["csharp", "fsharp"], snippets.Where(s => s.Name == "Everywhere").Select(s => s.Language));
    }

    [Fact]
    public void Visual_Studio_snippets_convert_literals_end_and_selected()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
              <CodeSnippet Format="1.0.0">
                <Header><Title>My if</Title><Shortcut>myif</Shortcut><Description>An if</Description></Header>
                <Snippet>
                  <Declarations>
                    <Literal><ID>expression</ID><Default>true</Default></Literal>
                  </Declarations>
                  <Code Language="csharp"><![CDATA[if ($expression$)
            {
                $selected$ $end$ // $expression$ costs $$5
            }]]></Code>
                </Snippet>
              </CodeSnippet>
            </CodeSnippets>
            """;
        var snippet = Assert.Single(SnippetRegistry.ParseVisualStudio(xml, "user"));

        Assert.Equal(("My if", "csharp", "An if"), (snippet.Name, snippet.Language, snippet.Description));
        Assert.Equal(["myif"], snippet.Prefixes);
        Assert.Equal("if (${1:true})\n{\n    $TM_SELECTED_TEXT $0 // $1 costs \\$5\n}", snippet.Body);
        Assert.True(snippet.IsSurround);
        Assert.Equal("if (true)\n{\n      // true costs $5\n}", SnippetParser.ToPlainText(snippet.Body));

        // Round trip through the user's JSON file.
        var json = SnippetRegistry.ToJson([snippet]);
        Assert.Equal(snippet.Body, Assert.Single(SnippetRegistry.ParseJson(json, "csharp", "user")).Body);
    }

    [Fact]
    public void The_users_snippets_replace_built_in_ones_with_the_same_name()
    {
        var registry = new SnippetRegistry();
        registry.Set("builtin", [new Snippet("prop", ["prop"], "builtin", null, "csharp", "builtin"), new Snippet("all", ["a"], "x", null, "*", "builtin")]);
        registry.Set("contoso.plugin", [new Snippet("prop", ["prop"], "plugin", null, "csharp", "contoso.plugin")]);
        registry.Set("user", [new Snippet("prop", ["prop"], "user", null, "csharp", "user")]);

        var csharp = registry.For("csharp");
        Assert.Equal("user", csharp.Single(s => s.Name == "prop").Body);
        Assert.Contains(csharp, s => s.Name == "all");
        Assert.DoesNotContain(registry.For("fsharp"), s => s.Name == "prop");
    }
}
