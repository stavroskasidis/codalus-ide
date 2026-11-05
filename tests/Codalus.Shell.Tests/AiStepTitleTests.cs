using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;

namespace Codalus.Shell.Tests;

/// <summary>How an external agent's step shows its title (ADR 0092): MCP calls by their tool, long scripts by their first line.</summary>
public sealed class AiStepTitleTests
{
    [Theory]
    [InlineData("mcp__codalus__find_references Greeter.Greet", "find_references Greeter.Greet")]
    [InlineData("mcp__codalus__build", "build")]
    [InlineData("mcp__github__create_issue", "github: create_issue")]
    [InlineData("mcp__", "mcp__")]
    [InlineData("Read Program.cs", "Read Program.cs")]
    public void An_mcp_call_shows_its_tool(string title, string shown) => Assert.Equal(shown, AiStepItem.McpTitle(title));

    [Fact]
    public void A_script_shows_its_first_line_and_the_whole_of_it_in_the_details()
    {
        var step = new AiAgentStep("execute", "$r = Invoke-RestMethod -Uri $url\n$r.value | Format-Table", null) { Id = "1", Kind = "execute", Status = "completed" };
        var item = AiStepItem.FromAcp(step);
        Assert.Equal("$r = Invoke-RestMethod -Uri $url …", item.Title);
        Assert.StartsWith("$r = Invoke-RestMethod -Uri $url\n$r.value | Format-Table", item.Details, StringComparison.Ordinal);
    }
}
