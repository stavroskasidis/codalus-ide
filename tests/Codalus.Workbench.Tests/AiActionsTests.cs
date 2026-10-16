using Codalus.Workbench.Ai;
using Codalus.Workbench.Testing;

namespace Codalus.Workbench.Tests;

/// <summary>M28b's rules: cleaning a completion, reading a next-edit suggestion, splitting diffs, and test frameworks.</summary>
public sealed class AiActionsTests
{
    [Fact]
    public void A_completion_loses_fences_repeats_and_the_text_after_the_caret()
    {
        // Plain.
        Assert.Equal("a + b;", AiCompletions.Clean("a + b;", "return ", "\n}"));

        // Fenced, and repeating the current line's start.
        Assert.Equal("a + b;", AiCompletions.Clean("```csharp\n    return a + b;\n```", "int Add(int a, int b)\n{\n    return ", "\n}"));

        // Repeating the text after the caret.
        Assert.Equal("Console.WriteLine(\"hi\");", AiCompletions.Clean("Console.WriteLine(\"hi\");\n}", "    ", "\n}"));

        // The cursor marker echoed back, and an empty reply.
        Assert.Equal("x", AiCompletions.Clean("<|cursor|>x", "", ""));
        Assert.Null(AiCompletions.Clean("   \n ", "a", "b"));

        // At most MaxLines lines.
        var many = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line{i};"));
        Assert.Equal(AiCompletions.MaxLines, AiCompletions.Clean(many, "", "")!.Split('\n').Length);
    }

    [Fact]
    public void A_next_edit_suggestion_must_be_among_the_shown_lines_and_change_something()
    {
        string[] lines = ["int count = 0;", "count++;", "Print(count);"];
        var suggestion = AiNextEdit.Parse("""Sure: {"startLine": 2, "endLine": 3, "newText": "total++;\nPrint(total);"}""", lines, 1, 3);
        Assert.Equal(new AiEditSuggestion(2, 3, "count++;\nPrint(count);", "total++;\nPrint(total);"), suggestion);

        Assert.Null(AiNextEdit.Parse("""{"none": true}""", lines, 1, 3));
        Assert.Null(AiNextEdit.Parse("""{"startLine": 5, "newText": "x"}""", lines, 1, 3));
        Assert.Null(AiNextEdit.Parse("""{"startLine": 2, "newText": "count++;"}""", lines, 1, 3));
        Assert.Null(AiNextEdit.Parse("no idea", lines, 1, 3));
        Assert.Null(AiNextEdit.Parse("{broken", lines, 1, 3));
    }

    [Fact]
    public void Diffs_split_by_file_and_replies_unfence()
    {
        var diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-a\n+b\ndiff --git a/.env b/.env\n+SECRET=1\n";
        var sections = AiActions.SplitDiff(diff).ToList();
        Assert.Equal(["src/A.cs", ".env"], sections.Select(s => s.Path));
        Assert.Equal(diff, string.Concat(sections.Select(s => s.Text)));
        Assert.Empty(AiActions.SplitDiff(""));

        Assert.Equal("Add the greeter\n", AiActions.Unfence("```\nAdd the greeter\n```"));
        Assert.Equal("Add the greeter", AiActions.Unfence("Add the greeter"));
        Assert.Equal((1, 9), AiActions.FixRegion(new string[30], 3, radius: 6));
        Assert.Equal((24, 30), AiActions.FixRegion(new string[30], 28, radius: 4));
    }

    [Fact]
    public void The_test_framework_comes_from_the_test_projects()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-frameworks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string Project(string name, string body)
            {
                var path = Path.Combine(folder, name + ".csproj");
                File.WriteAllText(path, body);
                return path;
            }

            var nunit = Project("N", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="NUnit" Version="4.0.0" /></ItemGroup></Project>""");
            var mstest = Project("M", """<Project Sdk="MSTest.Sdk/3.6.0"></Project>""");
            var tunit = Project("T", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="TUnit" Version="0.1.0" /></ItemGroup></Project>""");
            var none = Project("X", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            Assert.Equal("NUnit", TestFrameworks.Detect([none, nunit]));
            Assert.Equal("MSTest", TestFrameworks.Detect([mstest]));
            Assert.Equal("TUnit", TestFrameworks.Detect([tunit]));
            Assert.Equal("xUnit", TestFrameworks.Detect([none, Path.Combine(folder, "missing.csproj")]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
