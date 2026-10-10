using DaedalusStudio.Workbench.Languages;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>M16d (ADR 0027): what scoped CSS (.razor.css) completes in its selectors.</summary>
public sealed class ScopedCssTests
{
    private static (ScopedCssCompletion Kind, int WordStart) At(string css, string marker)
    {
        var offset = css.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, marker);
        return ScopedCss.At(css.Replace("|", string.Empty, StringComparison.Ordinal), offset);
    }

    [Fact]
    public void Selectors_complete_class_names_ids_and_deep_but_declarations_and_comments_do_not()
    {
        Assert.Equal(ScopedCssCompletion.ClassName, At(".car| { color: red; }", "|").Kind);
        Assert.Equal((ScopedCssCompletion.ClassName, 1), At(".car| { color: red; }", "|"));
        Assert.Equal(ScopedCssCompletion.Id, At(".card { }\n#err|", "|").Kind);
        Assert.Equal(ScopedCssCompletion.PseudoElement, At(".card ::de|", "|").Kind);
        Assert.Equal(ScopedCssCompletion.None, At(".card :ho|", "|").Kind); // a pseudo-class: the CSS server's
        Assert.Equal(ScopedCssCompletion.None, At(".card { margin: .5|rem; }", "|").Kind);
        Assert.Equal(ScopedCssCompletion.None, At(".card { color: #ff|0; }", "|").Kind);
        Assert.Equal(ScopedCssCompletion.None, At("/* .comm| */", "|").Kind);

        // Rules inside @media are selectors again.
        Assert.Equal(ScopedCssCompletion.ClassName, At("@media (max-width: 600px) {\n    .car| { }\n}", "|").Kind);
        Assert.Equal(ScopedCssCompletion.None, At("@media (max-width: 600px) {\n    .card { width: 1.5| }\n}", "|").Kind);
    }

    [Fact]
    public void A_components_class_names_and_ids_come_from_its_markup_not_its_csharp()
    {
        const string component = """
            <div id="main" class="card card-wide @extra">
                <span class='note'>@message</span>
                <p class="@(active ? "on" : "off") lead">text</p>
            </div>
            @code {
                string html = "<b class=\"fake\">";
            }
            """;
        var (classes, ids) = ScopedCss.Names(component);
        Assert.Equal(["card", "card-wide", "lead", "note"], classes);
        Assert.Equal(["main"], ids);
        Assert.True(ScopedCss.IsScoped("/app/Components/Counter.razor.css"));
        Assert.True(ScopedCss.IsScoped("/app/Pages/Index.cshtml.css"));
        Assert.False(ScopedCss.IsScoped("/app/wwwroot/app.css"));
        Assert.Equal("/app/Components/Counter.razor", ScopedCss.Component("/app/Components/Counter.razor.css"));
    }
}
