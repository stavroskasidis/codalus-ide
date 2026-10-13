using Codalus.Plugins.Protocol;
using Codalus.Workbench.Ai;

namespace Codalus.Workbench.Tests;

/// <summary>The AI data boundary (M28a, ADR 0062): what a request may carry, and how it is laid out.</summary>
public sealed class AiContextTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-aictx-").FullName;

    [Theory]
    [InlineData("secrets.json")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("signing.pfx")]
    [InlineData("server.key")]
    [InlineData("cert.pem")]
    [InlineData("Strong.snk")]
    [InlineData("appsettings.Production.json")]
    public void Secrets_are_refused_by_name(string name)
    {
        Assert.True(AiContext.IsSecret(Path.Combine(_root, name)));
    }

    [Fact]
    public void Ordinary_files_are_not_secrets()
    {
        Assert.False(AiContext.IsSecret(Path.Combine(_root, "Program.cs")));
        Assert.False(AiContext.IsSecret(Path.Combine(_root, "appsettings.json")));
        Assert.False(AiContext.IsSecret(Path.Combine(_root, "appsettings.Development.json")));
    }

    [Fact]
    public async Task Codalusignore_and_git_ignored_files_are_refused_with_the_reason()
    {
        File.WriteAllText(Path.Combine(_root, ".codalusignore"), "# not for AI\nData/\n*.generated.cs\n!Keep.generated.cs\n/Root.txt\n");
        var ignoredByGit = Path.Combine(_root, "bin", "App.dll.config");
        var context = new AiContext(_root, (path, _) => Task.FromResult(path == ignoredByGit));
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await context.RefusalAsync(Path.Combine(_root, "Program.cs"), ct));
        Assert.Contains(".codalusignore", await context.RefusalAsync(Path.Combine(_root, "Data", "Seed.cs"), ct), StringComparison.Ordinal);
        Assert.Contains(".codalusignore", await context.RefusalAsync(Path.Combine(_root, "src", "Model.generated.cs"), ct), StringComparison.Ordinal);
        Assert.Null(await context.RefusalAsync(Path.Combine(_root, "src", "Keep.generated.cs"), ct)); // negated
        Assert.Contains(".codalusignore", await context.RefusalAsync(Path.Combine(_root, "Root.txt"), ct), StringComparison.Ordinal);
        Assert.Null(await context.RefusalAsync(Path.Combine(_root, "src", "Root.txt"), ct)); // anchored to the root
        Assert.Contains(".gitignore", await context.RefusalAsync(ignoredByGit, ct), StringComparison.Ordinal);
        Assert.Contains("secret", await context.RefusalAsync(Path.Combine(_root, ".env"), ct), StringComparison.Ordinal);

        var (attachment, refused) = await context.AttachAsync("file", Path.Combine(_root, ".env"), "API_KEY=x", ".env", ct);
        Assert.Null(attachment);
        Assert.NotNull(refused);
    }

    [Fact]
    public void A_request_has_the_system_prompt_the_history_and_the_attachments_before_the_question()
    {
        var messages = AiContext.Messages(
            [new AiMessageDto("user", "earlier"), new AiMessageDto("assistant", "answer")],
            [new AiAttachment("selection", "The selection in Program.cs (lines 3–4)", "var x = 1;", "/w/Program.cs")],
            "Why?");

        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal(AiContext.SystemPrompt, messages[0].Content);
        Assert.Equal("The selection in Program.cs (lines 3–4):\n```csharp\nvar x = 1;\n```\n\nWhy?", messages[^1].Content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
