using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>AI Chat's @-mentions (ADR 0092): only what a question mentions goes with it, and @ lists what can.</summary>
public sealed class AiMentionsTests
{
    [Fact]
    public void A_question_sends_only_what_it_mentions()
    {
        Assert.True(AiMentions.Parse("Why does this fail?").IsEmpty);

        var choice = AiMentions.Parse("Fix @errors in @file, see @src/App/Program.cs and @Lib/Shapes.cs. Not an @ alone, nor mail@example.");
        Assert.Equal((false, true, false, true, false, false), (choice.Selection, choice.CurrentFile, choice.OpenFiles, choice.Errors, choice.BuildOutput, choice.Solution));
        Assert.Equal(["src/App/Program.cs", "Lib/Shapes.cs"], choice.Files);

        var all = AiMentions.Parse("@SELECTION @open @build @solution");
        Assert.Equal((true, false, true, false, true, true), (all.Selection, all.CurrentFile, all.OpenFiles, all.Errors, all.BuildOutput, all.Solution));
        Assert.Empty(all.Files);
    }

    [Fact]
    public void At_lists_the_ides_parts_then_the_files_whose_names_start_with_it()
    {
        string[] files = ["src/App/Program.cs", "src/App/Errors/ErrorPage.cs", "tests/App.Tests/ProgramTests.cs", "README.md"];
        Assert.Equal(["selection", "file", "open", "errors", "build", "solution"], AiMentions.Suggest("", files).Select(s => s.Name));
        Assert.All(AiMentions.Suggest("", files), s => Assert.True(s.IsMention));

        Assert.Equal(["errors", "src/App/Errors/ErrorPage.cs"], AiMentions.Suggest("err", files).Select(s => s.Name));
        Assert.Equal(["src/App/Program.cs", "tests/App.Tests/ProgramTests.cs"], AiMentions.Suggest("Program", files).Select(s => s.Name));
        Assert.Equal("@errors — the Error List", AiMentions.Suggest("errors", files).First().Text);
    }
}
