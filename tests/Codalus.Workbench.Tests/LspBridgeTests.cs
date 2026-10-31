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

    /// <summary>While this file is missing, the server doesn't answer <c>initialize</c> (<c>--initialize-after</c>).</summary>
    private string Gate => Path.Combine(_root, "initialize.gate");
    private readonly LanguageRegistry _languages = new();
    private readonly LanguageServiceRegistry _servers = new();
    private readonly SolutionService _solutions = new(() => null);
    private LanguageService _service = null!;

    private static string Metadata(string key) =>
        typeof(LspBridgeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ServerPath => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(ServerPath), $"Conformance server not built: {ServerPath}");
        Directory.CreateDirectory(_root);
        _languages.Register(new LanguageDefinition("conf", "Conformance") { Extensions = [".conf"] });
        File.WriteAllText(Gate, ""); // the server answers initialize at once, unless a test takes the gate away
        _servers.Register(new LanguageServiceDescriptor("conformance", "Conformance", [], null, () => ProcessLaunchInfo.FromPath(ServerPath, "--initialize-after", Gate))
        {
            Languages = ["conf"],
            SettingsSection = "conformance",
        });
        _service = new LanguageService(_servers, _solutions, languages: _languages)
        {
            SettingsProvider = section => section == "conformance" ? JsonElement.Parse("""{ "mode": "strict" }""") : null,
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

    /// <summary>
    /// A completion list already in the narrowed shape is returned as it is (checked in place, without the node round
    /// trip that cost 2 ms of a 4 ms SQL completion); one item that isn't sends the whole list through normalization.
    /// </summary>
    /// <summary>
    /// A service disposed while its host starts (the window closed before <c>initialize</c> was answered) ends the host.
    /// A cancelled start leaves its process running, and the service didn't dispose the client then: every shell test
    /// that closed its window while the C# host started left one (~150 MB), and a full run of them ran out of memory.
    /// </summary>
    [Fact]
    public async Task A_service_disposed_while_its_host_starts_ends_the_host()
    {
        File.Delete(Gate);
        var launcher = new RecordingLauncher();
        var servers = new LanguageServiceRegistry();
        servers.Register(new LanguageServiceDescriptor("conformance", "Conformance", [], null, () => ProcessLaunchInfo.FromPath(ServerPath, "--initialize-after", Gate) with { Launcher = launcher })
        {
            Languages = ["conf"],
        });
        var service = new LanguageService(servers, _solutions, languages: _languages);
        var starting = service.ClientAsync("conf");
        var host = await launcher.Started.Task.WaitAsync(Timeout());
        try
        {
            await service.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            var ended = await Task.WhenAny(host.Exited, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(ended == host.Exited, $"The host (pid {host.Id}) still runs 30 s after its service was disposed.");
        }
        finally
        {
            host.Kill();
        }
    }

    /// <summary>Starts processes on this machine and hands the test the first one.</summary>
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public TaskCompletionSource<IHostProcess> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
        {
            var process = await LocalProcessLauncher.Instance.StartAsync(launch, cancellationToken).ConfigureAwait(false);
            Started.TrySetResult(process);
            return process;
        }
    }

    [Fact]
    public void A_narrowed_completion_list_passes_unchanged_and_any_other_is_normalized()
    {
        const string Edit = """{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"a"}""";
        static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        static CompletionList Normalize(JsonElement list) =>
            LanguageServices.Client.LspNormalizer.Result(Methods.Completion, list).Deserialize(ProtocolJsonContext.Default.CompletionList)!;

        var narrowed = Parse($$$"""
            {"isIncomplete":false,"items":[
              {"label":"a","kind":5,"textEdit":{{{Edit}}}},
              {"label":"b","documentation":{"kind":"markdown","value":"**b**"}},
              {"label":"c","documentation":null,"textEdit":{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"replace":{"start":{"line":0,"character":0},"end":{"line":0,"character":2}},"newText":"c"}}]}
            """);
        var result = LanguageServices.Client.LspNormalizer.Result(Methods.Completion, narrowed);
        Assert.Equal(narrowed.GetRawText(), result.GetRawText());
        Assert.Equal(["a", "b", "c"], Normalize(narrowed).Items.Select(i => i.Label));

        // Each shape that needs a change: plain-string documentation, MarkupContent without a kind, command links,
        // an insert/replace edit without a range, an array instead of a list, a list without isIncomplete.
        var plain = Normalize(Parse($$"""{"isIncomplete":false,"items":[{"label":"a","textEdit":{{Edit}}},{"label":"b","documentation":"Plain."}]}"""));
        Assert.Equal(new MarkupContent("markdown", "Plain."), plain.Items[1].Documentation);
        var kindless = Normalize(Parse("""{"isIncomplete":false,"items":[{"label":"a","documentation":{"value":"Value."}}]}"""));
        Assert.Equal(new MarkupContent("markdown", "Value."), kindless.Items[0].Documentation);
        var command = Normalize(Parse("""{"isIncomplete":false,"items":[{"label":"a","documentation":{"kind":"markdown","value":"Docs [Open](command:open)"}}]}"""));
        Assert.Equal("Docs", command.Items[0].Documentation?.Value);
        var insertReplace = Normalize(Parse("""{"isIncomplete":false,"items":[{"label":"a","textEdit":{"insert":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"replace":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"a"}}]}"""));
        Assert.Equal(3, insertReplace.Items[0].TextEdit!.Range.End.Character);
        var array = Normalize(Parse("""[{"label":"a"}]"""));
        Assert.Equal(("a", false), (Assert.Single(array.Items).Label, array.IsIncomplete));
        var noFlag = LanguageServices.Client.LspNormalizer.Result(Methods.Completion, Parse("""{"items":[{"label":"a"}]}"""));
        Assert.False(noFlag.GetProperty("isIncomplete").GetBoolean());
    }

    /// <summary>
    /// Document symbols and inlay hints in the narrowed shape (our own servers') are returned as they are, checked in place:
    /// the node round trip made about 100 MB of strings and copies of a 100k-line file's 4.8 MB of symbols. The shapes that
    /// need a change still get it.
    /// </summary>
    [Fact]
    public void Narrowed_document_symbols_and_inlay_hints_pass_unchanged_and_other_shapes_are_normalized()
    {
        static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        static string Range(int start, int end) => $$$"""{"start":{"line":{{{start}}},"character":0},"end":{"line":{{{end}}},"character":0}}""";

        var symbols = Parse($$$"""
            [{"name":"C","kind":5,"range":{{{Range(0, 9)}}},"selectionRange":{{{Range(0, 0)}}},"location":null,
              "children":[{"name":"M","kind":6,"range":{{{Range(2, 3)}}},"selectionRange":{{{Range(2, 2)}}}}]}]
            """);
        Assert.Equal(symbols.GetRawText(), LanguageServices.Client.LspNormalizer.Result(Methods.DocumentSymbol, symbols).GetRawText());

        // SymbolInformation[] (a location each, flat) is nested by range.
        var information = Parse($$$"""
            [{"name":"C","kind":5,"location":{"uri":"file:///a.cs","range":{{{Range(0, 9)}}}}},
             {"name":"M","kind":6,"containerName":"C","location":{"uri":"file:///a.cs","range":{{{Range(2, 3)}}}}}]
            """);
        var nested = LanguageServices.Client.LspNormalizer.Result(Methods.DocumentSymbol, information).Deserialize(ProtocolJsonContext.Default.DocumentSymbolArray)!;
        var type = Assert.Single(nested);
        Assert.Equal(("C", "M"), (type.Name, Assert.Single(type.Children!).Name));

        var hints = Parse("""[{"position":{"line":0,"character":5},"label":": int","kind":1,"tooltip":{"kind":"plaintext","value":"The type."}}]""");
        Assert.Equal(hints.GetRawText(), LanguageServices.Client.LspNormalizer.Result(Methods.InlayHint, hints).GetRawText());

        // Label parts are joined; a plain-string tooltip becomes MarkupContent.
        var parts = Parse("""[{"position":{"line":0,"character":5},"label":[{"value":": "},{"value":"int"}],"tooltip":"The type."}]""");
        var hint = Assert.Single(LanguageServices.Client.LspNormalizer.Result(Methods.InlayHint, parts).Deserialize(ProtocolJsonContext.Default.InlayHintArray)!);
        Assert.Equal((": int", new MarkupContent("plaintext", "The type.")), (hint.Label, hint.Tooltip));
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
        await WaitUntil(() => _service.Diagnostics.For(path).Count == 0, ct);
    }

    // ------------------------------------------------------------------ document sync through the writer loop (M57)

    /// <summary>A service whose conformance server logs each document notification to <paramref name="log"/>.</summary>
    private LanguageService LoggingService(string log, bool incremental)
    {
        var servers = new LanguageServiceRegistry();
        string[] args = incremental ? ["--log", log, "--incremental"] : ["--log", log];
        servers.Register(new LanguageServiceDescriptor("conformance", "Conformance", [], null, () => ProcessLaunchInfo.FromPath(ServerPath, args))
        {
            Languages = ["conf"],
        });
        return new LanguageService(servers, _solutions, languages: _languages);
    }

    /// <summary>The log's lines up to the last hover, once the hover request came back (the server handles notifications first).</summary>
    private static async Task<string[]> LogAsync(LanguageService service, string path, string log, CancellationToken ct)
    {
        var client = (await service.ClientAsync("conf"))!;
        await client.HoverAsync(DocumentUris.FromPath(path), new Position(0, 0), ct);
        return await File.ReadAllLinesAsync(log, ct);
    }

    /// <summary>
    /// A server that syncs whole texts gets them read from the document's snapshot when written, and a text still queued
    /// is replaced by a newer one: it ends on the final text, the versions only increase, and all of it comes before the
    /// request that follows the edits.
    /// </summary>
    [Fact]
    public async Task Whole_texts_end_on_the_final_text_with_increasing_versions_before_the_next_request()
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "full.log");
        await using var service = LoggingService(log, incremental: false);
        var path = Path.Combine(_root, "big.conf");
        var line = "key = " + new string('v', 50) + "\n";
        var current = (Text: string.Concat(Enumerable.Repeat(line, 10_000)), Version: 1);
        var gate = new object();
        (string, int) Snapshot()
        {
            lock (gate)
            {
                return current;
            }
        }

        Assert.True(await service.OpenAsync(new LanguageDocument(path, "conf", Snapshot)));
        var changes = new List<Task>();
        for (var version = 2; version <= 200; version++)
        {
            lock (gate)
            {
                current = ("v" + version + " = 1\n" + current.Text, version);
            }

            // As the editor does: queued, not awaited, one after another.
            changes.Add(service.ChangeAsync(path, version, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "v" + version + " = 1\n")]));
        }

        var lines = await LogAsync(service, path, log, ct);
        await Task.WhenAll(changes);
        var versions = lines.TakeWhile(l => l != "hover").Where(l => l.StartsWith("didChange ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(versions);
        Assert.Equal(versions.Order().Distinct(), versions); // strictly increasing
        Assert.Equal(200, versions[^1]);
        Assert.Equal(current.Text, await File.ReadAllTextAsync(log + ".text", ct));
        TestContext.Current.TestOutputHelper?.WriteLine($"{versions.Count} whole texts sent for 199 changes");
    }

    /// <summary>
    /// A whole text carries its own change's text, never a later one: a request queued between two changes reads the first
    /// change's text (M57 review: it read the document's latest snapshot when written, so the next key's text, sent as its
    /// version, reached the server before a completion computed on the change before it).
    /// </summary>
    [Fact]
    public async Task A_request_between_two_changes_reads_the_whole_text_of_the_change_before_it()
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "between.log");
        await using var service = LoggingService(log, incremental: false);
        var path = Path.Combine(_root, "between.conf");
        var uri = DocumentUris.FromPath(path);
        using var gate = new ManualResetEventSlim(initialState: true);
        var current = ("a = 1\n", 1);
        var currentGate = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (string, int) Latest()
        {
            if (!gate.IsSet)
            {
                entered.TrySetResult(); // the writer reads the document's latest text (what this test guards against)
            }

            gate.Wait(TimeSpan.FromSeconds(30));
            lock (currentGate)
            {
                return current;
            }
        }

        void Edit(string text, int version)
        {
            lock (currentGate)
            {
                current = (text, version);
            }
        }

        Assert.True(await service.OpenAsync(new LanguageDocument(path, "conf", Latest)));
        var client = (await service.ClientAsync("conf"))!;

        // The writer is held in the v2 text, so the hover and v3 are queued behind it, and the document has moved to v3.
        gate.Reset();
        Edit("b = 2\n", 2);
        var change2 = service.ChangeAsync(path, 2, [new TextDocumentContentChangeEvent(null, "b = 2\n")], () =>
        {
            entered.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(30));
            return "b = 2\n";
        });
        await entered.Task.WaitAsync(ct);
        var hover = client.HoverAsync(uri, new Position(0, 0), ct);
        Edit("c = 3\n", 3);
        var change3 = service.ChangeAsync(path, 3, [new TextDocumentContentChangeEvent(null, "c = 3\n")], () => "c = 3\n");
        gate.Set();

        await hover;
        await Task.WhenAll(change2, change3);
        Assert.Equal("b = 2\n", await File.ReadAllTextAsync(log + ".text", ct)); // what the hover read
        var lines = await LogAsync(service, path, log, ct);
        Assert.Equal(["didOpen 1 6", "didChange 2 6 1", "hover", "didChange 3 6 1", "hover"], lines);
        Assert.Equal("c = 3\n", await File.ReadAllTextAsync(log + ".text", ct));
    }

    /// <summary>An incremental server gets each version's edits, one change notification per version, before the next request.</summary>
    [Fact]
    public async Task An_incremental_server_gets_one_change_per_version_before_the_next_request()
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "incremental.log");
        await using var service = LoggingService(log, incremental: true);
        var path = Path.Combine(_root, "edits.conf");
        Assert.True(await service.OpenAsync(new LanguageDocument(path, "conf", () => ("a = 1\n", 1))));
        for (var version = 2; version <= 101; version++)
        {
            _ = service.ChangeAsync(path, version, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "x")]);
        }

        var lines = await LogAsync(service, path, log, ct);
        var changes = lines.TakeWhile(l => l != "hover").Where(l => l.StartsWith("didChange ", StringComparison.Ordinal)).ToList();
        Assert.Equal(Enumerable.Range(2, 100).Select(v => $"didChange {v} {6 + v - 1} 1"), changes);
        Assert.Equal(new string('x', 100) + "a = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
    }

    /// <summary>
    /// A document's server and its linter (ADR 0027) each get every change in order: all of a change's notifications are
    /// queued before any is awaited (M57: the linter's waited for the server's write, so the next change could overtake
    /// it, and a version skipped as already sent would leave the linter with the wrong text).
    /// </summary>
    [Fact]
    public async Task A_server_and_its_linter_each_get_every_change_in_order()
    {
        var ct = Timeout();
        var serverLog = Path.Combine(_root, "server.log");
        var linterLog = Path.Combine(_root, "linter.log");
        var servers = new LanguageServiceRegistry();
        servers.Register(new LanguageServiceDescriptor("conformance", "Conformance", [], null, () => ProcessLaunchInfo.FromPath(ServerPath, "--log", serverLog, "--incremental"))
        {
            Languages = ["conf"],
        });
        servers.Register(new LanguageServiceDescriptor("conformance-lint", "Conformance linter", [], null, () => ProcessLaunchInfo.FromPath(ServerPath, "--log", linterLog, "--incremental"))
        {
            Languages = ["conf"],
            IsLinter = true,
        });
        await using var service = new LanguageService(servers, _solutions, languages: _languages);
        var path = Path.Combine(_root, "linted.conf");
        Assert.True(await service.OpenAsync(new LanguageDocument(path, "conf", () => ("a = 1\n", 1))));
        await WaitUntil(() => service.RunningLinters(path).Count == 1 && File.Exists(linterLog), ct);

        var changes = new List<Task>();
        for (var version = 2; version <= 101; version++)
        {
            changes.Add(service.ChangeAsync(path, version, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "x")]));
        }

        await Task.WhenAll(changes);
        var uri = DocumentUris.FromPath(path);
        await (await service.ClientAsync("conf"))!.HoverAsync(uri, new Position(0, 0), ct);
        await service.RunningLinters(path)[0].HoverAsync(uri, new Position(0, 0), ct);
        var expected = Enumerable.Range(2, 100).Select(v => $"didChange {v} {6 + v - 1} 1").ToList();
        foreach (var log in new[] { serverLog, linterLog })
        {
            var lines = await File.ReadAllLinesAsync(log, ct);
            Assert.Equal(expected, lines.TakeWhile(l => l != "hover").Where(l => l.StartsWith("didChange ", StringComparison.Ordinal)));
            Assert.Equal(new string('x', 100) + "a = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
        }
    }

    /// <summary>
    /// A didOpen reads the document when it is written, so it can carry edits queued after it: those, at or below the
    /// version it sent, are skipped, whatever the server syncs (M57: the server applied them twice, or went back).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_change_at_or_below_the_opened_version_reaches_the_server(bool incremental)
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "open.log");
        await using var service = LoggingService(log, incremental);
        var path = Path.Combine(_root, "opened.conf");
        var current = ("abc = 1\n", 5); // the editor's snapshot when the didOpen was written: edits 2 to 5 were in it
        Assert.True(await service.OpenAsync(new LanguageDocument(path, "conf", () => current)));
        for (var version = 3; version <= 5; version++)
        {
            await service.ChangeAsync(path, version, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "stale")]);
        }

        current = ("!abc = 1\n", 6);
        await service.ChangeAsync(path, 6, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "!")]);

        var lines = await LogAsync(service, path, log, ct);
        Assert.Equal(["didOpen 5 8", "didChange 6 9 1", "hover"], lines);
        Assert.Equal("!abc = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
    }

    /// <summary>
    /// A didOpen sends the snapshot taken when it was queued: a request queued behind it reads that text, and the edit
    /// made after the request follows it (M57 integration: the didOpen read the editor's latest snapshot when written, so a
    /// hover queued before the next key read that key's text, and the key's own change was skipped as already sent).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_request_queued_behind_a_didOpen_reads_the_text_it_was_queued_with(bool incremental)
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "queued-open.log");
        await using var service = LoggingService(log, incremental);
        Assert.True(await service.OpenAsync(new LanguageDocument(Path.Combine(_root, "warm.conf"), "conf", () => ("w = 0\n", 1)))); // the host is up
        var client = (await service.ClientAsync("conf"))!;
        var path = Path.Combine(_root, "queued.conf");
        var uri = DocumentUris.FromPath(path);
        using var gate = new ManualResetEventSlim(initialState: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = ("b = 1\n", 1);
        var currentGate = new object();
        string Held(string text)
        {
            entered.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(30));
            return text;
        }

        (string, int) Latest()
        {
            Held(string.Empty); // the writer reads the latest text: held too, so the edit below is in it
            lock (currentGate)
            {
                return current;
            }
        }

        // The writer is held making the didOpen's text; the hover, then the next key's change, are queued behind it.
        var opening = service.OpenAsync(new LanguageDocument(path, "conf", Latest, () => (() => Held("b = 1\n"), 1)));
        await entered.Task.WaitAsync(ct);
        var hover = client.HoverAsync(uri, new Position(0, 0), ct);
        lock (currentGate)
        {
            current = ("!b = 1\n", 2);
        }

        var change = service.ChangeAsync(path, 2, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "!")], () => "!b = 1\n");
        gate.Set();

        Assert.True(await opening);
        await hover;
        await change;
        Assert.Equal("b = 1\n", await File.ReadAllTextAsync(log + ".text", ct)); // what the hover read
        var lines = (await LogAsync(service, path, log, ct)).Skip(1).ToArray(); // after warm.conf's didOpen
        Assert.Equal(["didOpen 1 6", "hover", "didChange 2 7 1", "hover"], lines);
        Assert.Equal("!b = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
    }

    /// <summary>
    /// A change skipped because its document wasn't open yet, newer than the snapshot a didOpen was queued with (the editor's
    /// thread queued the change between the snapshot and the didOpen), makes the didOpen send the latest text: the edit is
    /// never lost.
    /// </summary>
    [Fact]
    public async Task A_didOpen_queued_after_a_newer_change_sends_the_latest_text()
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "late-open.log");
        await using var service = LoggingService(log, incremental: true);
        Assert.True(await service.OpenAsync(new LanguageDocument(Path.Combine(_root, "warm.conf"), "conf", () => ("w = 0\n", 1))));
        var client = (await service.ClientAsync("conf"))!;
        var path = Path.Combine(_root, "late.conf");
        var uri = DocumentUris.FromPath(path);

        // The change reaches the client before the document is open on its connection: skipped there, and remembered.
        await client.DidChangeAsync(uri, 2, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "!")], ct);
        await client.DidOpenAsync(uri, "conf", () => ("!b = 1\n", 2), ct, (() => "b = 1\n", 1));

        var lines = (await LogAsync(service, path, log, ct)).Skip(1).ToArray();
        Assert.Equal(["didOpen 2 7", "hover"], lines);
        Assert.Equal("!b = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
    }

    /// <summary>
    /// After a crash, each open document is opened again as a document opened now is: an edit made while the replay runs
    /// (the editor's thread typing while earlier documents are written), skipped because the document wasn't open on the
    /// new server yet, still reaches it (M57 review: the replay read every text when it began and sent that, so the server
    /// went on applying incremental edits to a text one edit behind the editor's).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_edit_made_while_a_restarted_server_reopens_documents_reaches_it(bool snapshot)
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "replay.log");
        await using var service = LoggingService(log, incremental: true);
        var path = Path.Combine(_root, "replayed.conf");
        var current = ("b = 1\n", 1);
        var currentGate = new object();
        var armed = false;
        Task? edit = null;
        var bang = new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "!");

        // The replay's first read of the document is followed at once by the next key, on the replay's thread: its change
        // is queued before anything the replay queues after the read.
        (string, int) Read()
        {
            (string, int) read;
            lock (currentGate)
            {
                read = current;
                if (!armed)
                {
                    return read;
                }

                armed = false;
                current = ("!b = 1\n", 2);
            }

            edit = service.ChangeAsync(path, 2, [bang]);
            return read;
        }

        var document = snapshot
            ? new LanguageDocument(path, "conf", Read, () =>
            {
                var (text, version) = Read();
                return (() => text, version);
            })
            : new LanguageDocument(path, "conf", Read);
        Assert.True(await service.OpenAsync(document));
        Assert.Equal(["didOpen 1 6", "hover"], await LogAsync(service, path, log, ct));

        await RestartAsync(service, () =>
        {
            lock (currentGate)
            {
                armed = true;
            }
        }, ct);
        Assert.NotNull(edit);
        await edit;
        lock (currentGate)
        {
            current = ("!!b = 1\n", 3);
        }

        await service.ChangeAsync(path, 3, [bang]);
        await LogAsync(service, path, log, ct);
        Assert.Equal("!!b = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
    }

    /// <summary>
    /// A document closed while a restarted server reopens documents isn't opened there: the replay began with it open,
    /// but its close went out first, and an open after it would leave the server with a document no edit or close reaches.
    /// </summary>
    [Fact]
    public async Task A_document_closed_while_a_restarted_server_reopens_documents_is_not_opened_there()
    {
        var ct = Timeout();
        var log = Path.Combine(_root, "replay-closed.log");
        await using var service = LoggingService(log, incremental: true);
        var path = Path.Combine(_root, "closed-in-replay.conf");
        var armed = 0;
        Task? close = null;
        (string, int) Read()
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                close = service.CloseAsync(path); // its didClose is queued before anything the replay queues after the read
            }

            return ("b = 1\n", 1);
        }

        Assert.True(await service.OpenAsync(new LanguageDocument(path, "conf", Read, () =>
        {
            var (text, version) = Read();
            return (() => text, version);
        })));
        var other = Path.Combine(_root, "open-in-replay.conf");
        Assert.True(await service.OpenAsync(new LanguageDocument(other, "conf", () => ("o = 1\n", 1))));
        await RestartAsync(service, () => Volatile.Write(ref armed, 1), ct);
        Assert.NotNull(close);
        await close;

        await LogAsync(service, other, log, ct); // the server has handled everything the replay sent
        Assert.Equal("o = 1\n", await File.ReadAllTextAsync(log + ".text", ct));
        await LogAsync(service, path, log, ct);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(log + ".text", ct)); // the server doesn't have it
        Assert.False(service.IsOpen(path));
    }

    /// <summary>Kills a service's host after <paramref name="before"/>, and returns once it has restarted and reopened its documents.</summary>
    private static async Task RestartAsync(LanguageService service, Action before, CancellationToken ct)
    {
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRestarted(object? sender, string language) => restarted.TrySetResult();
        service.HostRestarted += OnRestarted;
        try
        {
            var pid = (await service.ClientAsync("conf"))!.ProcessId!.Value;
            before();
            using (var host = System.Diagnostics.Process.GetProcessById(pid))
            {
                host.Kill();
            }

            await restarted.Task.WaitAsync(ct);
        }
        finally
        {
            service.HostRestarted -= OnRestarted;
        }
    }

    /// <summary>
    /// A document closed while its server starts is never opened there: its editor has let go of the text by then, and
    /// a late open would leave the server with text that no edit or close corrects (M47: the C# host got an empty
    /// Shapes.cs from Code Search's preview, so the file's types were in neither the index nor workspace symbols).
    /// </summary>
    [Fact]
    public async Task A_document_closed_while_its_server_starts_is_not_opened_there()
    {
        var ct = Timeout();
        var closed = Path.Combine(_root, "closed.conf");
        var released = false;
        File.Delete(Gate); // the server stays in its start until the document is closed (WSL, 2026-10-28: it started first)
        var opening = _service.OpenAsync(new LanguageDocument(closed, "conf", () => released ? ("gone", 2) : ("alpha = 1", 1)));
        Assert.False(opening.IsCompleted); // the server is starting
        await _service.CloseAsync(closed);
        released = true;
        File.WriteAllText(Gate, "");
        Assert.True(await opening);

        // The server handles notifications in order: once the next document's diagnostics are back, those of an open of
        // the closed one would be too.
        var next = Path.Combine(_root, "next.conf");
        Assert.True(await _service.OpenAsync(new LanguageDocument(next, "conf", () => ("broken", 1))));
        await WaitUntil(() => _service.Diagnostics.For(next).Any(d => d.Code == "42"), ct);
        Assert.Empty(_service.Diagnostics.For(closed));
        Assert.False(_service.IsOpen(closed));
    }
}
