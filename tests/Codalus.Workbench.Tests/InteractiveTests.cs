using System.Collections.Concurrent;
using System.Reflection;
using Codalus.Interactive.Protocol;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Interactive;

namespace Codalus.Workbench.Tests;

/// <summary>C# Interactive (ADR 0015) against the real <c>codalus-replhost</c>.</summary>
public sealed class InteractiveTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-repl-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<OutputParams> _output = new();
    private InteractiveService _service = null!;

    private static string RepoRoot => Metadata("CodalusRepoRoot");

    /// <summary><c>CODALUS_REPLHOST</c> (e.g. a published host), else the one built with the tests.</summary>
    internal static string HostPath =>
        Environment.GetEnvironmentVariable(InteractiveHostLaunch.EnvironmentVariable) is { Length: > 0 } host
            ? host
            : Path.Combine(RepoRoot, "src", "Codalus.Interactive.Host", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-replhost.dll");

    private static string Metadata(string key) =>
        typeof(InteractiveTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static CancellationToken Timeout(int seconds = 60) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Assert.True(File.Exists(HostPath), $"REPL host not built: {HostPath}");
        // Packages restored by #r "nuget:" go to a throwaway folder, not the user's global packages folder.
        var launch = ProcessLaunchInfo.FromPath(HostPath) with
        {
            Environment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(_root, ".packages") },
        };
        _service = new InteractiveService(() => launch, _root);
        _service.Output += (_, e) => _output.Enqueue(e);
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitForOutputAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The output did not arrive.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private string Printed(bool isError = false) => string.Concat(_output.Where(o => o.IsError == isError).Select(o => o.Text));

    [Fact]
    public async Task Submissions_share_state_print_values_and_capture_output()
    {
        var ct = Timeout();
        var first = await _service.ExecuteAsync("var numbers = new List<int> { 1, 2, 3 };", ct);
        Ok(first);
        Assert.Null(first.Value);

        var sum = await _service.ExecuteAsync("numbers.Sum() * 7", ct);
        Assert.True(sum.Success);
        Assert.Equal("42", sum.Value);

        // Strings are formatted as C# literals; void calls have no value.
        Assert.Equal("\"hi\"", (await _service.ExecuteAsync("\"hi\"", ct)).Value);
        var print = await _service.ExecuteAsync("Console.WriteLine($\"count {numbers.Count}\"); Console.Error.Write(\"oops\")", ct);
        Assert.True(print.Success);
        Assert.Null(print.Value);
        // Responses complete on the connection's read loop and notifications on its dispatch loop, so output sent
        // before the result can be handled just after it.
        await WaitForOutputAsync(() => Printed().ReplaceLineEndings("\n").Contains("count 3\n", StringComparison.Ordinal) && Printed(isError: true).Contains("oops", StringComparison.Ordinal));

        // Declarations persist too.
        Assert.True((await _service.ExecuteAsync("int Twice(int x) => x * 2;", ct)).Success);
        Assert.Equal("84", (await _service.ExecuteAsync("Twice(numbers.Sum() * 7)", ct)).Value);
    }

    [Fact]
    public async Task Compile_errors_and_exceptions_keep_the_state()
    {
        var ct = Timeout();
        Assert.True((await _service.ExecuteAsync("var a = 1;", ct)).Success);

        var compileError = await _service.ExecuteAsync("a + undefinedThing", ct);
        Assert.False(compileError.Success);
        Assert.Null(compileError.Error);
        var diagnostic = Assert.Single(compileError.Diagnostics, d => d.Severity == "error");
        Assert.Equal("CS0103", diagnostic.Id);
        Assert.Equal(4, diagnostic.Character);

        // Variables declared before the exception survive it.
        var thrown = await _service.ExecuteAsync("var b = 2; throw new InvalidOperationException(\"boom\");", ct);
        Assert.False(thrown.Success);
        Assert.Contains("InvalidOperationException", thrown.Error, StringComparison.Ordinal);
        Assert.Contains("boom", thrown.Error, StringComparison.Ordinal);
        Assert.Equal("3", (await _service.ExecuteAsync("a + b", ct)).Value);
    }

    [Fact]
    public async Task A_crashing_script_restarts_the_host_with_an_empty_state()
    {
        var ct = Timeout();
        var restarted = new TaskCompletionSource();
        var resets = 0;
        _service.HostRestarted += (_, _) => restarted.TrySetResult();
        _service.Reset += (_, _) => Interlocked.Increment(ref resets);
        Assert.True((await _service.ExecuteAsync("var kept = 5;", ct)).Success);
        var pid = _service.ProcessId;

        var exit = await _service.ExecuteAsync("Environment.Exit(3)", ct);
        Assert.False(exit.Success);
        await restarted.Task.WaitAsync(ct);

        // The next submission runs in a new process, without the old variables.
        await WaitUntil(() => _service.IsRunning && _service.ProcessId != pid && Volatile.Read(ref resets) >= 2, ct);
        var lost = await _service.ExecuteAsync("kept", ct);
        Assert.False(lost.Success);
        Assert.Contains(lost.Diagnostics, d => d.Id == "CS0103");
        Assert.Equal("2", (await _service.ExecuteAsync("1 + 1", ct)).Value);
    }

    [Fact]
    public async Task Reset_stops_a_runaway_submission()
    {
        var ct = Timeout();
        Assert.True((await _service.ExecuteAsync("var x = 1;", ct)).Success);
        var loop = _service.ExecuteAsync("while (true) { }", ct);
        await Task.Delay(500, ct);
        Assert.False(loop.IsCompleted);

        await _service.ResetAsync(null, ct);
        var result = await loop.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.False(result.Success);
        Assert.Contains((await _service.ExecuteAsync("x", ct)).Diagnostics, d => d.Id == "CS0103");
    }

    [Fact]
    public async Task References_usings_and_load_files_configure_the_state()
    {
        var ct = Timeout();
        var script = Path.Combine(_root, "helpers.csx");
        await File.WriteAllTextAsync(script, "static string Greet(string name) => $\"Hi {name}\";\n", ct);
        var rpc = typeof(JsonRpcConnection).Assembly.Location;

        await _service.ResetAsync(new ResetParams(_root, [rpc], ["Codalus.Rpc"], [script]), ct);
        Assert.Equal("\"Hi Ada\"", (await _service.ExecuteAsync("Greet(\"Ada\")", ct)).Value);
        Assert.Equal("\"SupervisionPolicy\"", (await _service.ExecuteAsync("nameof(SupervisionPolicy)", ct)).Value);

        // #r and #load work in submissions too, relative to the working directory.
        await File.WriteAllTextAsync(Path.Combine(_root, "more.csx"), "var loaded = 7;\n", ct);
        Assert.True((await _service.ExecuteAsync("#load \"more.csx\"", ct)).Success);
        Assert.Equal("7", (await _service.ExecuteAsync("loaded", ct)).Value);
        var missing = await _service.ExecuteAsync("#r \"does-not-exist.dll\"", ct);
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task Nuget_references_restore_packages_with_their_dependencies()
    {
        var ct = Timeout(240);
        var fixture = Path.Combine(_root, "Packages");
        CopyDirectory(Path.Combine(RepoRoot, "samples", "Packages"), fixture);
        await _service.ResetAsync(new ResetParams(fixture, [], [], []), ct);

        // 1.1.0 depends on Codalus.Sample.Text; both come from the fixture feed by its nuget.config.
        var reference = await _service.ExecuteAsync("#r \"nuget: Codalus.Sample.Greeting, 1.1.0\"", ct);
        Ok(reference);
        Assert.Equal("\"HELLO, ADA!\"", (await _service.ExecuteAsync("Codalus.Sample.Greeting.Greeter.Shout(\"Ada\")", ct)).Value);

        var unknown = await _service.ExecuteAsync("#r \"nuget: Codalus.Sample.DoesNotExist, 1.0.0\"", ct);
        Assert.False(unknown.Success);
        await WaitForOutputAsync(() => Printed(isError: true).Contains("DoesNotExist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editor_services_see_the_script_state()
    {
        var ct = Timeout();
        Assert.True((await _service.ExecuteAsync("var greeting = \"hello\"; record Point(int X, int Y);", ct)).Success);

        Assert.True(await _service.IsCompleteAsync("greeting.Length", ct));
        Assert.False(await _service.IsCompleteAsync("if (true) {", ct));
        Assert.False(await _service.IsCompleteAsync("var s = \"\"\"\nabc", ct));

        // Completion knows earlier submissions and members.
        var top = await _service.CompletionAsync("gree", 4, ct);
        Assert.Equal(0, top.Start);
        Assert.Contains(top.Items, i => i.Label == "greeting");
        var members = await _service.CompletionAsync("greeting.ToU", 12, ct);
        Assert.Equal(9, members.Start);
        Assert.Contains(members.Items, i => i.Label == "ToUpperInvariant" && i.Kind == CompletionItemKind.Method);
        Assert.DoesNotContain(members.Items, i => i.Label == "Format");
        var statics = await _service.CompletionAsync("string.", 7, ct);
        Assert.Contains(statics.Items, i => i.Label == "Format");
        var record = await _service.CompletionAsync("new Point(1, 2).", 16, ct);
        Assert.Contains(record.Items, i => i.Label == "X" && i.Kind == CompletionItemKind.Property);

        // Signature help: the overloads and the active parameter.
        var help = await _service.SignatureHelpAsync("Math.Max(1, ", 12, ct);
        Assert.NotNull(help);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Contains(help.Signatures, s => s.Label.Contains("Max(int val1, int val2)", StringComparison.Ordinal));
        Assert.Null(await _service.SignatureHelpAsync("greeting", 8, ct));

        // Tokens: keyword, local (a script variable), method, string, number, comment.
        var code = "var n = greeting.Length; // c\nConsole.WriteLine(\"x\" + 1);";
        var types = Decode(code, await _service.TokensAsync(code, ct));
        Assert.Equal("keyword", types["var"]);
        Assert.Equal("local", types["greeting"]);
        Assert.Equal("property", types["Length"]);
        Assert.Equal("comment", types["// c"]);
        Assert.Equal("class", types["Console"]);
        Assert.Equal("method", types["WriteLine"]);
        Assert.Equal("string", types["\"x\""]);
        Assert.Equal("number", types["1"]);
    }

    /// <summary>Asserts success, showing the error and diagnostics otherwise.</summary>
    private static void Ok(ExecuteResult result) =>
        Assert.True(result.Success, result.Error + string.Concat(result.Diagnostics.Select(d => $"\n{d.Id} ({d.Line},{d.Character}): {d.Message}")));

    private static Dictionary<string, string> Decode(string code, int[] data)
    {
        var lines = code.Split('\n');
        var result = new Dictionary<string, string>();
        int line = 0, character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            result.TryAdd(lines[line].Substring(character, data[i + 2]), TokenTypes.All[data[i + 3]]);
        }

        return result;
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
