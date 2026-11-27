using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// A C# file's first colors from its syntax (keywords, literals, comments) when the client takes them and the full
/// answer isn't there in time; the next answer is the full one, and no later one is partial.
/// </summary>
public sealed class SyntacticTokensTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-syntactic-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Source = """
        namespace App;
        // a comment
        public class Greeter
        {
            public string Greet(int count)
            {
                if (count > 1) { return "many\tgreetings"; }
                return $"one {count}";
            }
        }
        """;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App", "obj"));
        File.WriteAllText(Path.Combine(_root, "App", "Greeter.cs"), Source);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private async Task<LanguageHostClient> StartAsync(TimeSpan? wait)
    {
        // The syntax's answer and the full one race once the wait is over (3d8232ae): a full answer that landed first
        // failed these tests (macOS CI, run 45702937714). Held back, the syntax's comes first, as on a project still
        // compiling.
        var launch = ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly) with
        {
            Environment = wait is null ? null : new Dictionary<string, string> { ["CODALUS_LSHOST_HOLD_COLD_FULL_MS"] = "5000" },
        };
        var client = new LanguageHostClient("C# language service", launch, policy: new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) })
        {
            SyntacticTokensWait = wait,
        };
        await client.StartAsync(Ct);
        var file = Path.Combine(_root, "App", "Greeter.cs");
        await client.DidOpenAsync(new OpenDocumentState(DocumentUris.FromPath(file), "csharp", 1, Source), Ct);
        var output = Path.Combine(_root, "App", "obj", "App.dll");
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll");
        string[] arguments = ["/target:library", $"/out:{output}", .. framework.Select(p => $"/reference:{p}"), "Greeter.cs"];
        await client.UpdateProjectsAsync([new ProjectData(Path.Combine(_root, "App", "App.csproj"), "App", "C#", "net10.0", arguments, output, null, [])], Ct, loading: false);
        return client;
    }

    private static string[] Types(LanguageHostClient client, SemanticTokens tokens) =>
        [.. tokens.Data.Chunk(5).Select(t => client.Legend!.TokenTypes[t[3]]).Distinct()];

    [Fact]
    public async Task The_first_answer_is_the_syntax_then_the_full_answer_and_never_partial_again()
    {
        await using var client = await StartAsync(TimeSpan.Zero);
        var uri = DocumentUris.FromPath(Path.Combine(_root, "App", "Greeter.cs"));
        var range = new Protocol.Range(new Position(0, 0), new Position(11, 0));

        var first = await client.SemanticTokensAsync(uri, range, Ct);
        Assert.Equal("syntactic", first.ResultId);
        var types = Types(client, first);
        Assert.Contains("keyword", types);
        Assert.Contains("controlKeyword", types);
        Assert.Contains("comment", types);
        Assert.Contains("string", types);
        Assert.Contains("stringEscape", types);
        Assert.Contains("number", types);
        Assert.DoesNotContain("class", types); // identifiers wait for the semantic answer

        var full = await client.SemanticTokensAsync(uri, range, Ct);
        Assert.Null(full.ResultId);
        Assert.Contains("class", Types(client, full));
        Assert.Contains("parameter", Types(client, full));

        // What the syntax gave is what the full answer gives for the same tokens.
        var fullTokens = Decode(full.Data).ToDictionary(t => (t.Line, t.Start));
        foreach (var token in Decode(first.Data))
        {
            Assert.True(fullTokens.TryGetValue((token.Line, token.Start), out var same), $"{token} not in the full answer");
            Assert.Equal(client.Legend!.TokenTypes[same.Type], client.Legend.TokenTypes[token.Type]);
        }

        await client.DidChangeTextAsync(uri, 2, () => Source.Replace("many", "several", StringComparison.Ordinal), Ct);
        Assert.Null((await client.SemanticTokensAsync(uri, range, Ct)).ResultId);
    }

    [Fact]
    public async Task An_edit_after_the_syntactic_answer_gets_the_full_answer_of_the_new_text()
    {
        // The cold request keeps computing its full answer for the request after it (M66); an edit in between makes
        // that answer stale, so the request after the edit computes its own.
        await using var client = await StartAsync(TimeSpan.Zero);
        var uri = DocumentUris.FromPath(Path.Combine(_root, "App", "Greeter.cs"));
        var range = new Protocol.Range(new Position(0, 0), new Position(11, 0));
        Assert.Equal("syntactic", (await client.SemanticTokensAsync(uri, range, Ct)).ResultId);

        var edited = Source.Replace("count", "numberOfGreetings", StringComparison.Ordinal);
        await client.DidChangeTextAsync(uri, 2, () => edited, Ct);
        var full = await client.SemanticTokensAsync(uri, range, Ct);
        Assert.Null(full.ResultId);

        var lines = edited.Split('\n');
        var parameters = Decode(full.Data).Where(t => client.Legend!.TokenTypes[t.Type] == "parameter").ToList();
        Assert.NotEmpty(parameters);
        Assert.All(parameters, t => Assert.Equal("numberOfGreetings", lines[t.Line].Substring(t.Start, t.Length)));
    }

    [Fact]
    public async Task A_client_that_does_not_ask_for_them_gets_only_full_answers()
    {
        await using var client = await StartAsync(null);
        var uri = DocumentUris.FromPath(Path.Combine(_root, "App", "Greeter.cs"));
        var tokens = await client.SemanticTokensAsync(uri, null, Ct);
        Assert.Null(tokens.ResultId);
    }

    private static IEnumerable<(int Line, int Start, int Length, int Type)> Decode(int[] data)
    {
        var (line, start) = (0, 0);
        foreach (var t in data.Chunk(5))
        {
            line += t[0];
            start = t[0] == 0 ? start + t[1] : t[1];
            yield return (line, start, t[2], t[3]);
        }
    }
}
