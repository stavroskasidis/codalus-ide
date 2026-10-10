using DaedalusStudio.Workbench.Languages;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>M16d (ADR 0027): the HTML of a Razor file, for HTML and CSS in .razor and .cshtml files.</summary>
public sealed class RazorHtmlProjectionTests
{
    /// <summary>The projection, checked to keep every offset and line break.</summary>
    private static string Project(string razor)
    {
        var html = RazorHtmlProjection.Project(razor);
        Assert.Equal(razor.Length, html.Length);
        for (var i = 0; i < razor.Length; i++)
        {
            Assert.True(html[i] == razor[i] || html[i] == ' ', $"offset {i}: '{razor[i]}' became '{html[i]}'");
            Assert.True(razor[i] is not '\r' and not '\n' || html[i] == razor[i], $"offset {i}: a line break was blanked");
        }

        return html;
    }

    /// <summary>The projection's non-blank text, one space between pieces, so tests read what the HTML server sees.</summary>
    private static string Squeeze(string html) => string.Join(' ', html.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void A_component_keeps_its_markup_and_loses_its_csharp_and_directives()
    {
        const string razor = """
            @page "/counter"
            @using System.Text
            @inject NavigationManager Navigation

            <PageTitle>Counter</PageTitle>

            <h1 class="title">Counter</h1>

            <p role="status">Current count: @currentCount</p>

            <button class="btn btn-primary" @onclick="IncrementCount" @onclick:preventDefault>Click me</button>
            <input @bind-Value="name" @bind-Value:event="oninput" />

            @code {
                private int currentCount = 0;
                private string name = "<b>not markup</b>";

                private void IncrementCount()
                {
                    currentCount++;
                }
            }
            """;
        Assert.Equal(
            """<PageTitle>Counter</PageTitle> <h1 class="title">Counter</h1> <p role="status">Current count: </p> <button class="btn btn-primary" >Click me</button> <input />""",
            Squeeze(Project(razor)));
    }

    [Fact]
    public void Statements_lose_their_csharp_and_keep_the_elements_in_their_blocks()
    {
        const string razor = """
            @if (show)
            {
                <p class="note">@message</p>
            }
            else if (other)
            {
                <span>other</span>
            }
            else
            {
                @:Plain @text line
            }
            <ul>
            @foreach (var item in items.Where(i => i.Count > 0))
            {
                var label = item.Name + "<li>";
                <li @key="item.Id">@item.Name (@item.Count)</li>
            }
            </ul>
            @{
                var total = items.Sum(i => i.Count);
                <text>Total: @total</text>
            }
            """;
        Assert.Equal(
            """<p class="note"> </p> <span>other</span> Plain line <ul> <li > ( )</li> </ul> Total:""",
            Squeeze(Project(razor)));
    }

    [Fact]
    public void Comments_escapes_expressions_and_styles()
    {
        const string razor = """
            @* a Razor comment with <b>markup</b> *@
            <!-- an HTML comment stays -->
            <div class="card @extra" style="width: @(percent)%">Mail me@example.com</div>
            <p>@DateTime.Now.ToString("t") and @(1 + 2) and @await LoadAsync()</p>
            <style>
                @@media (max-width: 600px) { .card { color: red; } }
            </style>
            """;
        var html = Project(razor);
        Assert.Equal(
            """<!-- an HTML comment stays --> <div class="card " style="width: %">Mail me@example.com</div> <p> and and </p> <style> @media (max-width: 600px) { .card { color: red; } } </style>""",
            Squeeze(html));
    }

    [Fact]
    public void A_cshtml_section_keeps_its_markup()
    {
        const string razor = """
            @model IndexModel
            @section Scripts {
                <script src="~/js/site.js"></script>
            }
            <a asp-page="/Privacy">Privacy</a>
            """;
        Assert.Equal("""<script src="~/js/site.js"></script> <a asp-page="/Privacy">Privacy</a>""", Squeeze(Project(razor)));
    }

    [Fact]
    public void Markup_is_where_the_last_character_before_the_caret_was_kept()
    {
        const string razor = "<div class=\"a\">\n    \n</div>\n@code {\n    int x = \n}\n";
        var html = Project(razor);
        Assert.True(RazorHtmlProjection.IsMarkup(razor, html, razor.IndexOf("class", StringComparison.Ordinal)));
        Assert.True(RazorHtmlProjection.IsMarkup(razor, html, razor.IndexOf("\n    \n", StringComparison.Ordinal) + 5));
        Assert.False(RazorHtmlProjection.IsMarkup(razor, html, razor.IndexOf("x = ", StringComparison.Ordinal) + 4));
        Assert.False(RazorHtmlProjection.IsMarkup(razor, html, razor.IndexOf("int", StringComparison.Ordinal)));
        Assert.True(RazorHtmlProjection.IsMarkup(razor, html, 0));
    }
}
