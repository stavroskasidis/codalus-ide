using System.Text.Json;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;

namespace Codalus.Workbench.Tests;

/// <summary>
/// AI Chat's compact view of a turn (ADR 0092): its steps in one line, the files it changed before and after, and an
/// image going to an agent as an ACP image block.
/// </summary>
public sealed class AiTurnSummaryTests
{
    private static AiAgentStep Acp(string kind, string title, string status = "completed") =>
        new(kind, title, null) { Id = Guid.NewGuid().ToString("N"), Kind = kind, Status = status };

    [Fact]
    public void The_steps_fold_into_one_line_most_frequent_first()
    {
        Assert.Equal("", AiTurnSummary.Describe([]));
        var steps = new[]
        {
            Acp("read", "Read Program.cs"),
            Acp("read", "Read App.csproj"),
            Acp("read", "Read Startup.cs"),
            Acp("search", "grep Order"),
            Acp("execute", "dotnet build", "failed"),
            Acp("edit", "Edit Program.cs") with { Diffs = [new AiStepDiff("src/Program.cs", "a", "b", Reviewed: true)] },
        };
        Assert.Equal("Read 3 files · edited Program.cs · ran 1 command · searched once · 1 failed", AiTurnSummary.Describe(steps));

        // The built-in agent's tools, the IDE's among them, by their names.
        var builtIn = new[]
        {
            new AiAgentStep("find_references", "Order.Submit", "done"),
            new AiAgentStep("run_tests", "", "done"),
            new AiAgentStep("debug_step", "", "done"),
            new AiAgentStep("browser_screenshot", "", "done"),
        };
        Assert.Equal("Used the browser once · used the debugger once · ran 1 command · searched once", AiTurnSummary.Describe(builtIn));
    }

    [Fact]
    public void The_running_step_is_the_last_one_not_finished()
    {
        Assert.Equal("dotnet test", AiTurnSummary.Current([Acp("read", "Read a"), Acp("execute", "dotnet test", "in_progress")])?.Target);
        Assert.Null(AiTurnSummary.Current([Acp("read", "Read a")]));
        Assert.Equal("build", AiTurnSummary.Current([new AiAgentStep("read_file", "a.cs", "done"), new AiAgentStep("build", "build", null)])?.Target);
    }

    [Fact]
    public void Changed_files_are_before_the_first_change_and_after_the_last()
    {
        var steps = new[]
        {
            Acp("edit", "one") with { Diffs = [new AiStepDiff("a.cs", "v1", "v2", Reviewed: true)] },
            Acp("edit", "new") with { Diffs = [new AiStepDiff("b.cs", null, "created", Reviewed: false)] },
            Acp("edit", "two") with { Diffs = [new AiStepDiff("a.cs", "v2", "v3", Reviewed: true)] },
            Acp("edit", "failed", "failed") with { Diffs = [new AiStepDiff("c.cs", "x", "y", Reviewed: true)] },
            Acp("edit", "back") with { Diffs = [new AiStepDiff("d.cs", "same", "other", Reviewed: true)] },
            Acp("edit", "back again") with { Diffs = [new AiStepDiff("d.cs", "other", "same", Reviewed: true)] },
        };

        Assert.Equal(
            [new AiChangedFile("a.cs", "v1", "v3", true), new AiChangedFile("b.cs", null, "created", false)],
            AiTurnSummary.ChangedFiles(steps));
    }

    [Fact]
    public void An_image_goes_as_an_acp_image_block()
    {
        var block = AcpContentBlock.FromImage(new AiImageAttachment("iVBORw0KGgo=", "image/png", "Pasted image 1"));
        var json = JsonSerializer.SerializeToElement(block, AcpJsonContext.Default.AcpContentBlock);
        Assert.Equal(("image", "iVBORw0KGgo=", "image/png"), (json.GetProperty("type").GetString(), json.GetProperty("data").GetString(), json.GetProperty("mimeType").GetString()));
        Assert.False(json.TryGetProperty("text", out _));
    }
}
