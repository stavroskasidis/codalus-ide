using Daedalus.LanguageServices.Client;
using Daedalus.LanguageServices.Protocol;
using TextEdit = Daedalus.LanguageServices.Protocol.TextEdit;

namespace Daedalus.LanguageServices.Host.Tests;

/// <summary>End-to-end tests: the real host process, the AOT-safe client, and the SpikeSolution fixture.</summary>
public sealed class HostIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private LanguageHostClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(RepoPaths.HostAssembly), $"Host not built: {RepoPaths.HostAssembly}");
        _client = new LanguageHostClient(HostLaunchInfo.FromPath(RepoPaths.HostAssembly));
        await _client.StartAsync(Timeout());
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    [Fact]
    public void Initialize_returns_token_legend()
    {
        Assert.NotNull(_client.Legend);
        Assert.Equal(TokenTypes.All, _client.Legend!.TokenTypes);
    }

    [Fact]
    public async Task OpenSolution_returns_projects_folders_and_documents()
    {
        var result = await _client.OpenSolutionAsync(RepoPaths.SpikeSolution, Timeout());

        Assert.Equal(SolutionNodeKind.Solution, result.Root.Kind);
        Assert.Equal(["Spike.App", "Spike.Core", "Spike.Utilities"], result.Root.Children.Select(p => p.Name));

        var core = result.Root.Children.Single(p => p.Name == "Spike.Core");
        var inventory = Assert.Single(core.Children, n => n.Kind == SolutionNodeKind.Folder);
        Assert.Equal("Inventory", inventory.Name);
        Assert.Equal(["InventoryStore.cs", "Product.cs"], inventory.Children.Select(d => d.Name));
    }

    [Fact]
    public async Task SemanticTokens_classify_symbols_semantically()
    {
        await _client.OpenSolutionAsync(RepoPaths.SpikeSolution, Timeout());
        var path = RepoPaths.Sample("src", "Spike.Core", "Inventory", "Product.cs");
        var text = await File.ReadAllTextAsync(path, Timeout());
        await _client.DidOpenAsync(path, text, version: 1, Timeout());

        var tokens = await _client.GetSemanticTokensAsync(path, Timeout());
        var decoded = Decode(tokens.Data, text);

        Assert.Equal(1, tokens.Version);
        Assert.Contains(decoded, t => t.Text == "Product" && t.Type == "record");
        Assert.Contains(decoded, t => t.Text == "Category" && t.Type == "enum");
        Assert.Contains(decoded, t => t.Text == "Hardware" && t.Type == "enumMember");
        Assert.Contains(decoded, t => t.Text == "Empty" && t.Type == "field" && (t.Modifiers & TokenModifiers.Static) != 0);
        Assert.Contains(decoded, t => t.Text == "StockLevel" && t.Type == "recordStruct");
        Assert.Contains(decoded, t => t.Type == "xmlDocComment");
    }

    [Fact]
    public async Task DidChange_updates_tokens_and_version()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daedalus-{Guid.NewGuid():N}.cs");
        const string Original = "class A { int _x; }";
        await _client.DidOpenAsync(path, Original, version: 1, Timeout());

        // Insert a static method; the identifier must be classified once the edit lands.
        const string Inserted = " static void Run() { } ";
        await _client.DidChangeAsync(path, version: 2, [new TextEdit(Original.Length - 1, 0, Inserted)], Timeout());
        var tokens = await _client.GetSemanticTokensAsync(path, Timeout());

        var newText = Original.Insert(Original.Length - 1, Inserted);
        var decoded = Decode(tokens.Data, newText);
        Assert.Equal(2, tokens.Version);
        Assert.Contains(decoded, t => t.Text == "Run" && t.Type == "method" && (t.Modifiers & TokenModifiers.Static) != 0);
        Assert.Contains(decoded, t => t.Text == "_x" && t.Type == "field");
    }

    [Fact]
    public async Task SemanticTokensRange_only_returns_requested_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daedalus-{Guid.NewGuid():N}.cs");
        var text = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"class C{i} {{ }}"));
        await _client.DidOpenAsync(path, text, version: 1, Timeout());

        var tokens = await _client.GetSemanticTokensRangeAsync(path, startLine: 40, endLine: 42, Timeout());
        var decoded = Decode(tokens.Data, text);

        Assert.All(decoded, t => Assert.InRange(t.Line, 40, 41));
        Assert.Contains(decoded, t => t.Text == "C40" && t.Type == "class");
        Assert.Contains(decoded, t => t.Text == "C41" && t.Type == "class");
    }

    [Fact]
    public async Task Host_crash_is_reported_and_state_is_restored_after_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daedalus-{Guid.NewGuid():N}.cs");
        const string Text = "class Survivor { }";
        _client.DocumentTextProvider = p => p == path ? (Text, 7) : null;
        await _client.DidOpenAsync(path, Text, version: 7, Timeout());

        var crashed = new TaskCompletionSource<HostCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.HostCrashed += (_, e) => crashed.TrySetResult(e);
        _client.HostRestarted += (_, _) => restarted.TrySetResult();

        var originalPid = _client.HostProcessId!.Value;
        System.Diagnostics.Process.GetProcessById(originalPid).Kill();

        var crash = await crashed.Task.WaitAsync(Timeout());
        Assert.True(crash.WillRestart);
        await restarted.Task.WaitAsync(Timeout());
        Assert.NotEqual(originalPid, _client.HostProcessId);

        var tokens = await _client.GetSemanticTokensAsync(path, Timeout());
        Assert.Equal(7, tokens.Version);
        Assert.Contains(Decode(tokens.Data, Text), t => t.Text == "Survivor" && t.Type == "class");
    }

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private sealed record DecodedToken(int Line, int Character, string Text, string Type, int Modifiers);

    private static List<DecodedToken> Decode(int[] data, string text)
    {
        var lines = text.Split('\n');
        var result = new List<DecodedToken>();
        int line = 0, character = 0;
        for (var i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            var tokenText = lines[line].TrimEnd('\r').Substring(character, data[i + 2]);
            result.Add(new DecodedToken(line, character, tokenText, TokenTypes.All[data[i + 3]], data[i + 4]));
        }

        return result;
    }
}
