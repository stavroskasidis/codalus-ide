using System.Text.Json;
using Codalus.Debugging.Symbols;
using Codalus.Tests;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M58 (ADR 0094): symbol paths and symbol servers under the Codalus debugger, on a copy of samples/Debugging's program
/// whose PDB isn't next to it. A PDB in a symbol path is read as the module loads, so breakpoints bind from the
/// start; a symbol server (a mock one here) is asked only for what the settings let it be, or on Load Symbols, and
/// what it sends is kept in the cache.
/// </summary>
public sealed class ClrDebuggerSymbolTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-symbols-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The program's output copied without its PDB, which goes to <paramref name="pdb"/>.</summary>
    private string ProgramWithoutSymbols(out string pdb, bool builtElsewhere = true)
    {
        var program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Dll)!))
        {
            File.Copy(file, Path.Combine(program, Path.GetFileName(file)));
        }

        var store = Directory.CreateDirectory(Path.Combine(_root, "store")).FullName;
        pdb = Path.Combine(store, "Debuggee.pdb");
        File.Move(Path.Combine(program, "Debuggee.pdb"), pdb);

        // As on another machine: the folder the build wrote the PDB to (the debug directory names it) isn't there. The
        // copy's debug directory names another folder of the same length instead.
        var dll = Path.Combine(program, "Debuggee.dll");
        if (!builtElsewhere)
        {
            return dll;
        }

        var bytes = File.ReadAllBytes(dll);
        var built = System.Text.Encoding.UTF8.GetBytes(Path.Combine(fixture.Folder, "obj"));
        var at = bytes.AsSpan().IndexOf(built);
        Assert.True(at > 0, "The module's debug directory doesn't name the build's obj folder.");
        bytes.AsSpan(at + built.Length - 3, 3).Fill((byte)'x');
        File.WriteAllBytes(dll, bytes);
        return dll;
    }

    private static async Task<(DebugAdapter Adapter, EventLog Events)> StartAsync(string dll, SymbolOptions? symbols, bool justMyCode, params string[] arguments)
    {
        // The mock server is on this machine: not through a proxy the environment may name.
        var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter() with { Environment = new Dictionary<string, string> { ["NO_PROXY"] = "127.0.0.1,localhost", ["no_proxy"] = "127.0.0.1,localhost" } }, Ct);
        var events = new EventLog(adapter);
        await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", dll, .. arguments], Path.GetDirectoryName(dll), JustMyCode: justMyCode, Type: "coreclr", Symbols: symbols), s_json.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (adapter, events);
    }

    private static IEnumerable<ModuleEvent> Modules(EventLog events, string name) =>
        events.All.Where(e => e.Name == DapEvents.Module).Select(e => DebugAdapter.Read(e.Body, s_json.ModuleEvent)!).Where(m => m.Module.Name == name);

    private static async Task<ModuleEvent> ModuleAsync(EventLog events, string name, Func<ModuleEvent, bool> match)
    {
        for (var i = 0; i < 600; i++)
        {
            if (Modules(events, name).FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"No such module event for {name}. Its events: {string.Join("; ", Modules(events, name).Select(m => m.Reason + ": " + m.Module.SymbolStatus))}");
        return null!;
    }

    [Fact]
    public async Task A_pdb_in_a_symbol_path_is_read_as_its_module_loads_so_breakpoints_bind_from_the_start()
    {
        var dll = ProgramWithoutSymbols(out var pdb);
        var (adapter, events) = await StartAsync(dll, new SymbolOptions { SearchPaths = [Path.GetDirectoryName(pdb)!] }, justMyCode: true);
        await using var _ = adapter;
        await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(DebuggeeFixture.Line(fixture.Program, "loop"))], Ct);
        await adapter.ConfigurationDoneAsync(Ct);

        // The first time through the loop: nothing ran past the breakpoint while symbols were looked for.
        var stop = await events.StoppedAsync();
        Assert.Equal("breakpoint", stop.Reason);
        var top = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct)).StackFrames[0];
        Assert.Equal("0", (await adapter.EvaluateAsync("i", top.Id, "watch", Ct)).Result);
        var module = Assert.Single(Modules(events, "Debuggee.dll"));
        Assert.Equal("new", module.Reason);
        Assert.StartsWith("Symbols loaded from ", module.Module.SymbolStatus, StringComparison.Ordinal);
        Assert.Equal(pdb, module.Module.SymbolFilePath);
        Assert.True(module.Module.IsUserCode);
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task A_program_copied_without_its_pdb_reads_the_one_still_where_the_build_wrote_it()
    {
        // The debug directory names the PDB in the build's obj folder, which is still there on the machine that built
        // it. It was reported as a Windows PDB, and the program had no symbols.
        var dll = ProgramWithoutSymbols(out _, builtElsewhere: false);
        var (adapter, events) = await StartAsync(dll, null, justMyCode: true);
        await using var session = adapter;
        await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(DebuggeeFixture.Line(fixture.Program, "first"))], Ct);
        await adapter.ConfigurationDoneAsync(Ct);
        Assert.Equal("breakpoint", (await events.StoppedAsync()).Reason);
        var module = Assert.Single(Modules(events, "Debuggee.dll"));
        Assert.Equal("Symbols loaded.", module.Module.SymbolStatus);
        Assert.EndsWith(Path.Combine("obj", "Debug", "net11.0", "Debuggee.pdb"), module.Module.SymbolFilePath, StringComparison.Ordinal);
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task Without_symbol_settings_a_module_without_a_pdb_has_no_symbols_and_load_symbols_says_what_to_set()
    {
        var dll = ProgramWithoutSymbols(out var moved);
        Assert.True(File.Exists(moved));
        var wait = Path.Combine(_root, "go.txt");
        var (adapter, events) = await StartAsync(dll, null, justMyCode: true, "--wait-for", wait);
        await using var session = adapter;
        await adapter.ConfigurationDoneAsync(Ct);
        var module = await ModuleAsync(events, "Debuggee.dll", m => m.Reason == "new");
        Assert.Equal("No symbols: there's no PDB.", module.Module.SymbolStatus);
        var refused = await Assert.ThrowsAsync<DapException>(() => adapter.LoadSymbolsAsync(module.Module.Id.GetString()!, Ct));
        Assert.Contains("No symbol paths or symbol servers are set", refused.Message, StringComparison.Ordinal);
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task A_symbol_server_is_asked_on_load_symbols_and_what_it_sends_is_cached_and_binds_breakpoints()
    {
        var dll = ProgramWithoutSymbols(out var pdb);
        var key = SymbolIdentity.Read(dll)!.Key;
        await using var server = new MockHttpServer(request => request.Path.EndsWith("/" + key, StringComparison.Ordinal) ? MockHttpResponse.File(pdb) : MockHttpResponse.NotFound());
        var cache = Path.Combine(_root, "cache");
        var wait = Path.Combine(_root, "go.txt");
        var options = new SymbolOptions { Servers = [server.BaseUrl.TrimEnd('/') + "/symbols"], CacheFolder = cache };

        // Just My Code on: the server isn't asked for anything by itself.
        var (adapter, events) = await StartAsync(dll, options, justMyCode: true, "--wait-for", wait);
        await using (adapter)
        {
            await adapter.ConfigurationDoneAsync(Ct);
            var module = await ModuleAsync(events, "Debuggee.dll", m => m.Reason == "new");
            Assert.Equal("No symbols: there's no PDB.", module.Module.SymbolStatus);
            await ModuleAsync(events, "System.Console.dll", m => m.Reason == "new");
            Assert.Empty(server.Requests);

            // Load Symbols: one request, with the PDB's checksum; the module's status says where its symbols came from.
            await adapter.LoadSymbolsAsync(module.Module.Id.GetString()!, Ct);
            var changed = await ModuleAsync(events, "Debuggee.dll", m => m.Reason == "changed");
            Assert.StartsWith("Symbols loaded from the cache (downloaded from 127.0.0.1", changed.Module.SymbolStatus, StringComparison.Ordinal);
            Assert.StartsWith(cache, changed.Module.SymbolFilePath, StringComparison.Ordinal);
            var request = Assert.Single(server.Requests);
            Assert.EndsWith("/symbols/" + key, request.Path, StringComparison.Ordinal);
            Assert.StartsWith("SHA256:", request.Header("SymbolChecksum"), StringComparison.Ordinal);

            // Its breakpoints bind now, and the program stops at one.
            var bound = await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(DebuggeeFixture.Line(fixture.Program, "loop"))], Ct);
            Assert.True(Assert.Single(bound).Verified);
            await File.WriteAllTextAsync(wait, string.Empty, Ct);
            var stop = await events.StoppedAsync();
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal(DebuggeeFixture.Line(fixture.Program, "loop"), (await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct)).StackFrames[0].Line);
            await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
        }

        // The next session reads it from the cache as the module loads: no request, and breakpoints bind from the start.
        var requests = server.Requests.Count;
        var (again, later) = await StartAsync(dll, options, justMyCode: true);
        await using (again)
        {
            await again.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(DebuggeeFixture.Line(fixture.Program, "first"))], Ct);
            await again.ConfigurationDoneAsync(Ct);
            Assert.Equal("breakpoint", (await later.StoppedAsync()).Reason);
            Assert.StartsWith("Symbols loaded from the cache", Assert.Single(Modules(later, "Debuggee.dll")).Module.SymbolStatus, StringComparison.Ordinal);
            Assert.Equal(requests, server.Requests.Count);
            await again.DisconnectAsync(terminateDebuggee: true, Ct);
        }
    }

    [Fact]
    public async Task Without_just_my_code_modules_are_looked_up_by_themselves_but_those_the_settings_leave_out()
    {
        var dll = ProgramWithoutSymbols(out var pdb);
        var key = SymbolIdentity.Read(dll)!.Key;
        await using var server = new MockHttpServer(request => request.Path.EndsWith("/" + key, StringComparison.Ordinal) ? MockHttpResponse.File(pdb) : MockHttpResponse.NotFound());
        var wait = Path.Combine(_root, "go.txt");
        var options = new SymbolOptions { Servers = [server.BaseUrl], CacheFolder = Path.Combine(_root, "cache"), ExcludedModules = ["System.*.dll", "Microsoft.*.dll"] };
        var (adapter, events) = await StartAsync(dll, options, justMyCode: false, "--wait-for", wait);
        await using var session = adapter;
        await adapter.ConfigurationDoneAsync(Ct);

        // The program's module by itself; the framework's, which the settings leave out, aren't asked for.
        var changed = await ModuleAsync(events, "Debuggee.dll", m => m.Reason == "changed");
        Assert.StartsWith("Symbols loaded from the cache (downloaded from 127.0.0.1", changed.Module.SymbolStatus, StringComparison.Ordinal);
        await ModuleAsync(events, "System.Console.dll", m => m.Reason == "new");
        Assert.All(server.Requests, r => Assert.Contains("debuggee.pdb", r.Path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Modules(events, "System.Console.dll"), m => m.Reason == "changed");
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }
}
