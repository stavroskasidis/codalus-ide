using System.Reflection;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The generic LSP bridge (ADR 0016) against a scripted server that answers like servers written for VS Code: every
/// union shape arrives in the narrowed form the editor uses.
/// </summary>
public sealed class LspBridgeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-lsp-" + Guid.NewGuid().ToString("N"));
    private readonly LanguageRegistry _languages = new();
    private readonly LanguageServiceRegistry _servers = new();
    private readonly SolutionService _solutions = new(() => null);
    private LanguageService _service = null!;

    private static string Metadata(string key) =>
        typeof(LspBridgeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ServerPath => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), "net10.0", "codalus-lsp-conformance.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(ServerPath), $"Conformance server not built: {ServerPath}");
        Directory.CreateDirectory(_root);
        _languages.Register(new LanguageDefinition("conf", "Conformance") { Extensions = [".conf"] });
        _servers.Register(new LanguageServiceDescriptor("conformance", "Conformance", [], null, () => ProcessLaunchInfo.FromPath(ServerPath))
        {
            Languages = ["conf"],
            SettingsSection = "conformance",
        });
        _service = new LanguageService(_servers, _solutions, languages: _languages)
        {
            SettingsProvider = section => section == "conformance" ? JsonDocument.Parse("""{ "mode": "strict" }""").RootElement.Clone() : null,
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    [Fact]
    public async Task A_vs_code_style_server_works_through_the_bridge()
    {
        var ct = Timeout();
        var path = Path.Combine(_root, "settings.conf");
        var text = "alpha = 1\nbroken\n";
        await File.WriteAllTextAsync(path, text, ct);
        Assert.Equal("conf", _service.LanguageIdOf(path));
        Assert.True(await _service.OpenAsync(new LanguageDocument(path, "conf", () => (text, 1))));
        var uri = DocumentUris.FromPath(path);
        var client = (await _service.ClientAsync("conf"))!;

        // Capabilities: options objects mean "supported"; sync is full; rename without prepare.
        var capabilities = client.Capabilities!;
        Assert.True(capabilities.HoverProvider);
        Assert.True(capabilities.DefinitionProvider);
        Assert.True(capabilities.DocumentFormattingProvider);
        Assert.True(capabilities.WorkspaceSymbolProvider);
        Assert.True(capabilities.RenameProvider);
        Assert.False(capabilities.PrepareRenameProvider);
        Assert.Equal(TextDocumentSyncKind.Full, capabilities.TextDocumentSync);
        Assert.Empty(capabilities.CompletionProvider!.TriggerCharacters);
        Assert.True(capabilities.SemanticTokensProvider!.Full);
        Assert.Equal(["property", "string", "comment"], capabilities.SemanticTokensProvider.Legend.TokenTypes);

        // Diagnostics: numeric codes become strings.
        await WaitUntil(() => _service.Diagnostics.For(path).Any(d => d.Code == "42"), ct);

        // Completion: an array becomes a list; plain documentation becomes Markdown; insert/replace becomes a range;
        // snippets keep their tab stops (the editor's snippet engine inserts them); the server's workspace/configuration
        // request was answered.
        CompletionList completion = null!;
        await WaitUntil(() =>
        {
            completion = client.CompletionAsync(uri, new Position(1, 3), null, ct).GetAwaiter().GetResult();
            return completion.Items.Any(i => i.Label == "setting" && i.Detail?.Contains("strict", StringComparison.Ordinal) == true);
        }, ct);
        var alpha = completion.Items.Single(i => i.Label == "alpha");
        Assert.Equal("The first key.", alpha.Documentation?.Value);
        Assert.Equal(new Position(1, 0), alpha.TextEdit!.Range.Start);
        var beta = completion.Items.Single(i => i.Label == "beta");
        Assert.Equal(("beta(${1:value})$0", InsertTextFormat.Snippet), (beta.InsertText, beta.InsertTextFormat));

        // Hover: marked strings become Markdown.
        var hover = await client.HoverAsync(uri, new Position(0, 1), ct);
        Assert.Contains("```conf\nalpha = 1\n```", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("A **marked** string.", hover.Contents.Value, StringComparison.Ordinal);

        // Signature help: offset labels become substrings; missing active signature defaults to 0.
        var help = await client.SignatureHelpAsync(uri, new Position(0, 2), null, ct);
        Assert.Equal(["first", "second"], help!.Signatures[0].Parameters.Select(p => p.Label));
        Assert.Equal("The second.", help.Signatures[0].Parameters[1].Documentation?.Value);
        Assert.Equal(0, help.ActiveSignature);

        // Locations: links and a single location become arrays.
        var definition = Assert.Single(await client.DefinitionAsync(uri, new Position(1, 1), ct));
        Assert.Equal(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 5)), definition.Range);
        Assert.Single(await client.ReferencesAsync(uri, new Position(0, 1), includeDeclaration: true, ct));

        // Rename: a `changes` map becomes document changes.
        var rename = await client.RenameAsync(uri, new Position(0, 1), "gamma", ct);
        var change = Assert.Single(rename!.DocumentChanges);
        Assert.Equal(uri, change.TextDocument.Uri);
        Assert.Equal("gamma", Assert.Single(change.Edits).NewText);

        // Code actions: the bare command is dropped (and the numeric codes in the action's diagnostics still deserialize).
        var action = Assert.Single(await client.CodeActionsAsync(uri, new LanguageServices.Protocol.Range(new Position(1, 0), new Position(1, 6)), [], ct));
        Assert.Equal("quickfix", action.Kind);
        Assert.Single(action.Edit!.DocumentChanges);

        // Workspace symbols with only a URI get a range; formatting, folding and semantic tokens pass through.
        var symbol = Assert.Single(await client.WorkspaceSymbolsAsync("al", ct));
        Assert.Equal(new Position(0, 0), symbol.Location.Range.Start);
        Assert.Single(await client.FormatAsync(uri, null, new FormattingOptions(4, true), ct));
        Assert.Single(await client.FoldingRangesAsync(uri, ct));
        Assert.Equal(10, (await client.SemanticTokensAsync(uri, null, ct)).Data.Length);

        // A full-sync server gets the whole text on edits: fixing the line clears the diagnostic.
        text = "alpha = 1\nbroken = 2\n";
        await _service.ChangeAsync(path, 2, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(1, 6), new Position(1, 6)), " = 2")]);
        await WaitUntil(() => !_service.Diagnostics.For(path).Any(), ct);
    }
}
