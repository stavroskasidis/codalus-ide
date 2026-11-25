using Codalus.LanguageServices.Host.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>A XAML file's parse is made once per text and shared by the requests on it; its elements in document order.</summary>
public sealed class XamlParseSharingTests
{
    private const string Text = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid x:Name="Root"><Grid.RowDefinitions><RowDefinition/></Grid.RowDefinitions><TextBlock Text="a"/></Grid>
          <StackPanel><Button/></StackPanel>
        </Window>
        """;

    [Fact]
    public async Task Requests_on_the_same_text_share_its_parse_and_an_edit_parses_again()
    {
        using var workspace = new AdhocWorkspace();
        var path = Path.Combine(Path.GetTempPath(), "codalus-xaml-" + Guid.NewGuid().ToString("N"), "Main.axaml");
        var text = SourceText.From(Text);
        var first = await XamlContext.CreateAsync(workspace.CurrentSolution, path, text, TestContext.Current.CancellationToken);
        var second = await XamlContext.CreateAsync(workspace.CurrentSolution, path, text, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Same(first.Document, second!.Document);
        Assert.Equal(Text, first.TextString);

        var edited = await XamlContext.CreateAsync(workspace.CurrentSolution, path, text.WithChanges(new TextChange(new TextSpan(0, 0), " ")), TestContext.Current.CancellationToken);
        Assert.NotSame(first.Document, edited!.Document);

        Assert.Equal(first.Document.Root!.DescendantsAndSelf().Select(e => e.Name), first.Document.Elements.Select(e => e.Name));
        Assert.Equal(["Window", "Grid", "Grid.RowDefinitions", "RowDefinition", "TextBlock", "StackPanel", "Button"], first.Document.Elements.Select(e => e.LocalName));
        Assert.Equal("x", first.Document.Elements[1].Attributes[0].Prefix);
        Assert.Null(first.Document.Elements[0].Prefix);
    }

    [Fact]
    public void A_second_root_is_not_among_the_elements()
    {
        var document = XamlDocument.Parse("<A><B/></A><C/>");
        Assert.Equal(["A", "B"], document.Elements.Select(e => e.Name));
        Assert.NotEmpty(document.Errors);
    }
}
