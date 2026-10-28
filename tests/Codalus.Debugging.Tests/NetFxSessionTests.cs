using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Threading.Channels;
using Codalus.Debugging.Host.Mono;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>samples/Debugging/NetFxDebuggee copied to a temp folder and built once: a .NET Framework 4.8 console app (it builds on every OS).</summary>
public sealed class NetFxDebuggeeFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-netfx-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "NetFxDebuggee");

    public string Program => Path.Combine(Folder, "Program.cs");

    /// <summary>The built <c>NetFxDebuggee.exe</c>, or null when the build failed (<see cref="BuildOutput"/> says why).</summary>
    public string? Exe { get; private set; }

    public string BuildOutput { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        var source = Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Debugging", "NetFxDebuggee");
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)));
        }

        File.Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        var start = new ProcessStartInfo("dotnet", ["build", Folder, "-nologo", "-v", "quiet"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        BuildOutput = await output + await error;
        var exe = Path.Combine(Folder, "bin", "Debug", "net48", "NetFxDebuggee.exe");
        Exe = process.ExitCode == 0 && File.Exists(exe) ? exe : null;
    }

    public void RequireExe() => Assert.True(Exe is not null, "NetFxDebuggee didn't build:\n" + BuildOutput);

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
}

/// <summary>
/// M43b (ADR 0076): the desktop-CLR adapter's DAP layer on every OS, against a fake ICorDebug and the real metadata and
/// portable PDB of the net48 debuggee: breakpoints bound as modules load (conditions, function breakpoints), stacks,
/// locals and members, evaluation with func-eval, setting values, stepping (hidden lines, Just My Code), exceptions
/// with the type filter, async methods' hoisted variables, pause, and the end. Plus the module reader, and the real
/// adapter process off Windows.
/// </summary>
public sealed class NetFxSessionTests(NetFxDebuggeeFixture fixture) : IClassFixture<NetFxDebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    // ------------------------------------------------------------------ the module reader

    [Fact]
    public void The_net48_debuggees_metadata_and_portable_pdb_are_read()
    {
        fixture.RequireExe();
        using var info = ClrModuleInfo.Open(fixture.Exe!);
        Assert.Equal("NetFxDebuggee", info.AssemblyName);
        Assert.True(info.HasSymbols, info.SymbolStatus);
        Assert.Equal("Symbols loaded.", info.SymbolStatus);
        Assert.False(info.IsOptimized);
        Assert.True(DesktopClr.IsManagedExecutable(fixture.Exe!));
        Assert.Null(DesktopClr.BitnessProblem(fixture.Exe!, debugger64: true));
        Assert.False(DesktopClr.IsManagedExecutable(typeof(NetFxSessionTests).Assembly.Location));

        // A line with code binds in its method; a blank line binds on the next line with code.
        var sum = DebuggeeFixture.Line(fixture.Program, "netfx-sum");
        var document = info.MatchDocument(fixture.Program)!;
        Assert.NotNull(document);
        var (method, offset, line) = Assert.Single(info.Locations(document, sum));
        Assert.Equal(sum, line);
        Assert.Equal("NetFxDebuggee.Cart.Total", info.MethodDisplayName(method));
        Assert.Equal(sum, info.LineAt(method, offset)?.StartLine);
        var lines = File.ReadAllLines(fixture.Program);
        var blank = Array.FindIndex(lines, sum, l => l.Trim().Length == 0) + 1;
        var (_, _, after) = Assert.Single(info.Locations(document, blank));
        Assert.True(after > blank, $"A breakpoint on the blank line {blank} binds on a later line, not {after}.");
        Assert.Contains(info.Locals(method, offset), l => l.Name == "sum");
        Assert.Contains(info.Locals(method, offset), l => l.Name == "item");
        Assert.NotNull(info.RangeAt(method, offset));

        // Types, fields, properties, enums and attributes.
        var cart = info.FindType("NetFxDebuggee.Cart");
        Assert.NotEqual(0, cart);
        Assert.Contains(info.Fields(cart), f => f.Name == "<Owner>k__BackingField" && !f.IsStatic);
        Assert.Contains(info.Properties(cart), p => p is { Name: "Count", GetterToken: not 0, IndexParameters: 0, TypeName: "int" });
        Assert.Equal(["int"], info.Method(Assert.Single(info.FindMethods("Cart.Twice"))).ParameterTypes);
        var mood = info.FindType("NetFxDebuggee.Mood");
        Assert.True(info.IsEnum(mood));
        Assert.True(info.TypeHasAttribute(mood, "System.FlagsAttribute"));
        Assert.Equal("mscorlib", info.BaseType(cart)?.Assembly);
        Assert.False(info.IsNonUserCode(method));
    }

    [Fact]
    public void A_pdb_that_isnt_portable_is_reported_and_not_read()
    {
        fixture.RequireExe();
        var folder = Path.Combine(Path.GetTempPath(), "codalus-netfx-pdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "NetFxDebuggee.exe");
            File.Copy(fixture.Exe!, exe);
            File.WriteAllBytes(Path.Combine(folder, "NetFxDebuggee.pdb"), [.. "Microsoft C/C++ MSF 7.00\r\n\x1a"u8, .. new byte[512]]);
            using var info = ClrModuleInfo.Open(exe);
            Assert.False(info.HasSymbols);
            Assert.DoesNotContain("loaded", info.SymbolStatus, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the session

    /// <summary>The session behind a real DapServer, a DAP client on in-memory pipes, and the fake process.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly DapServer _server;

        public Harness(FakeProcess process)
        {
            Process = process;
            Debugger = new FakeDebugger(process);
            var toServer = new Pipe();
            var toClient = new Pipe();
            _server = new DapServer(toServer.Reader.AsStream(), toClient.Writer.AsStream());
            Session = new NetFxDebugSession(_server, TextWriter.Null, Debugger);
            _server.Handler = Session.HandleAsync;
            _ = _server.RunAsync(_stop.Token);
            Client = new DapConnection(toClient.Reader.AsStream(), toServer.Writer.AsStream());
            Client.EventReceived += e => Events.Writer.WriteAsync(e).AsTask();
            Client.Start();
        }

        public FakeProcess Process { get; }

        public FakeDebugger Debugger { get; }

        public NetFxDebugSession Session { get; }

        public DapConnection Client { get; }

        public Channel<DapEvent> Events { get; } = Channel.CreateUnbounded<DapEvent>();

        public Task<JsonElement> RequestAsync<T>(string command, T arguments, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
            Client.RequestAsync(command, arguments, info, Ct);

        public Task<TResult> RequestAsync<T, TResult>(string command, T arguments, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> result) =>
            Client.RequestAsync(command, arguments, info, result, Ct);

        public async Task<JsonElement> NextAsync(string name)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var e = await Events.Reader.ReadAsync(timeout.Token);
                if (e.Name == name)
                {
                    return e.Body;
                }
            }
        }

        public async Task<Variable[]> VariablesAsync(int reference) =>
            (await RequestAsync("variables", new VariablesArguments(reference), s_json.VariablesArguments, s_json.VariablesResponse)).Variables;

        public async Task<string> EvaluateAsync(string expression, int frame) =>
            (await RequestAsync("evaluate", new EvaluateArguments(expression, frame, "watch"), s_json.EvaluateArguments, s_json.EvaluateResponse)).Result;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Client.DisposeAsync();
            _server.Dispose();
            _stop.Dispose();
        }
    }

    private static int FieldToken(ClrModuleInfo info, int type, string name) => info.Fields(type).Single(f => f.Name == name).Token;

    private static int Getter(ClrModuleInfo info, int type, string name) => info.Properties(type).Single(p => p.Name == name).GetterToken;

    [Fact]
    public async Task Breakpoints_stacks_variables_evaluation_steps_exceptions_and_the_end_through_dap()
    {
        fixture.RequireExe();
        using var info = ClrModuleInfo.Open(fixture.Exe!);
        var corelibPath = typeof(object).Assembly.Location;
        using var corelib = ClrModuleInfo.Open(corelibPath);
        var module = new FakeModule(fixture.Exe!);
        var mscorlib = new FakeModule(corelibPath);
        var process = new FakeProcess();
        await using var dap = new Harness(process);

        var capabilities = await dap.RequestAsync("initialize", new InitializeArguments("codalus", "Codalus IDE", "clr"), s_json.InitializeArguments, s_json.Capabilities);
        Assert.True(capabilities.SupportsConditionalBreakpoints);
        Assert.True(capabilities.SupportsFunctionBreakpoints);
        Assert.True(capabilities.SupportsExceptionFilterOptions);
        Assert.Contains(capabilities.ExceptionBreakpointFilters!, f => f.Filter == "all");

        await dap.RequestAsync("launch", new NetFxDebugArguments(fixture.Exe, ["one"], fixture.Folder), s_json.NetFxDebugArguments);
        var processEvent = await dap.NextAsync(DapEvents.Process);
        await dap.NextAsync(DapEvents.Initialized);
        Assert.Equal(4242, processEvent.GetProperty("systemProcessId").GetInt32());
        Assert.Equal(["one"], dap.Debugger.Launched!.Args!);

        // Breakpoints before the module loads: pending, then bound when it does.
        var sum = DebuggeeFixture.Line(fixture.Program, "netfx-sum");
        var set = await dap.RequestAsync("setBreakpoints", new SetBreakpointsArguments(new Source("Program.cs", fixture.Program), [new SourceBreakpoint(sum, Condition: "sum > 100"), new SourceBreakpoint(sum)]), s_json.SetBreakpointsArguments, s_json.SetBreakpointsResponse);
        Assert.All(set.Breakpoints, b => Assert.False(b.Verified));
        var functions = await dap.RequestAsync("setFunctionBreakpoints", new SetFunctionBreakpointsArguments([new FunctionBreakpoint("Cart.Twice")]), s_json.SetFunctionBreakpointsArguments, s_json.SetBreakpointsResponse);
        Assert.False(Assert.Single(functions.Breakpoints).Verified);
        await dap.RequestAsync("setExceptionBreakpoints", new SetExceptionBreakpointsArguments(["user-unhandled"], [new ExceptionFilterOptions("all", "System.InvalidOperationException")]), s_json.SetExceptionBreakpointsArguments);
        await dap.RequestAsync("configurationDone", new EmptyArguments(), s_json.EmptyArguments);

        process.Send(new ClrModuleLoaded(module));
        process.Send(new ClrModuleLoaded(mscorlib));
        await process.WaitForContinuesAsync(2, Ct);
        var bound = await dap.NextAsync(DapEvents.Breakpoint);
        Assert.True(bound.GetProperty("breakpoint").GetProperty("verified").GetBoolean());
        var document = info.MatchDocument(fixture.Program)!;
        var (total, offset, _) = info.Locations(document, sum).Single();
        var twice = info.FindMethods("Cart.Twice").Single();
        lock (process.Breakpoints)
        {
            Assert.Equal(2, process.Breakpoints.Count(b => b.Method == total && b.Offset == offset)); // one per source breakpoint
            Assert.Contains(process.Breakpoints, b => b.Method == twice);
        }

        Assert.Contains(process.UserCode, u => ReferenceEquals(u.Module, module) && u.UserCode);
        Assert.Contains(process.UserCode, u => ReferenceEquals(u.Module, mscorlib) && !u.UserCode);

        // The program: a Cart with its fields, an Item, locals by the PDB's slots.
        var cartType = info.FindType("NetFxDebuggee.Cart");
        var itemType = info.FindType("NetFxDebuggee.Item");
        var moodType = info.FindType("NetFxDebuggee.Mood");
        var cart = FakeValue.Object(module, cartType)
            .With(FieldToken(info, cartType, "<Owner>k__BackingField"), FakeValue.String("Ada"))
            .With(FieldToken(info, cartType, "_items"), FakeValue.Null())
            .With(FieldToken(info, cartType, "_codes"), FakeValue.Array(ClrType.Named("System.Int32"), FakeValue.Of(7), FakeValue.Of(8), FakeValue.Of(9)))
            .With(FieldToken(info, cartType, "<Mood>k__BackingField"), FakeValue.Object(module, moodType, valueType: true).With(FieldToken(info, moodType, "value__"), FakeValue.Of(3)));
        var item = FakeValue.Object(module, itemType)
            .With(FieldToken(info, itemType, "<Name>k__BackingField"), FakeValue.String("Tea"))
            .With(FieldToken(info, itemType, "<Price>k__BackingField"), FakeValue.Of(3));
        var sumValue = FakeValue.Of(0);
        var thread = new FakeThread(7, process); // its values are neutered when the process runs, a func-eval included, as ICorDebug's are
        var frame = new FakeFrame(thread, module, total, offset);
        frame.Arguments.Add(cart);
        foreach (var (slot, name) in info.Locals(total, offset))
        {
            frame.Locals[slot] = name switch { "sum" => sumValue, "item" => item, _ => FakeValue.Null() };
        }

        thread.FrameList.Add(frame);
        thread.FrameList.Add(new FakeFrame(thread, null, 0, 0) { Description = "[Native to Managed Transition]" });
        process.ThreadList.Add(thread);
        var count = Getter(info, cartType, "Count");
        process.OnCall = (_, method, arguments) => method == count ? FakeValue.Of(2) : method == twice ? FakeValue.Of((int)arguments[1].Primitive! * 2) : throw new ClrException("unexpected call");

        // The conditional breakpoint's condition is false: the program goes on; the other one stops.
        var conditional = process.Breakpoints.First(b => b.Method == total);
        var continues = process.Continues;
        process.Send(new ClrBreakpointHit(thread, conditional));
        await process.WaitForContinuesAsync(continues + 1, Ct);
        Assert.Equal(continues + 1, process.Continues);
        var plain = process.Breakpoints.Last(b => b.Method == total);
        process.Send(new ClrBreakpointHit(thread, plain));
        var stopped = await dap.NextAsync(DapEvents.Stopped);
        Assert.Equal("breakpoint", stopped.GetProperty("reason").GetString());
        Assert.Equal(7, stopped.GetProperty("threadId").GetInt32());
        Assert.Equal(set.Breakpoints[1].Id, stopped.GetProperty("hitBreakpointIds")[0].GetInt32());

        var threads = await dap.RequestAsync("threads", new EmptyArguments(), s_json.EmptyArguments, s_json.ThreadsResponse);
        Assert.Equal("Thread 7", Assert.Single(threads.Threads).Name);
        var stack = await dap.RequestAsync("stackTrace", new StackTraceArguments(7), s_json.StackTraceArguments, s_json.StackTraceResponse);
        var top = stack.StackFrames[0];
        Assert.Equal("NetFxDebuggee.Cart.Total", top.Name);
        Assert.Equal((Path.GetFullPath(fixture.Program), sum), (Path.GetFullPath(top.Source!.Path!), top.Line));
        Assert.Null(top.PresentationHint);
        Assert.Equal(("[Native to Managed Transition]", "label"), (stack.StackFrames[1].Name, stack.StackFrames[1].PresentationHint));

        // Locals, this, its members (auto-properties by name, a getter through func-eval, an enum, an array).
        var scope = Assert.Single((await dap.RequestAsync("scopes", new ScopesArguments(top.Id), s_json.ScopesArguments, s_json.ScopesResponse)).Scopes);
        var locals = await dap.VariablesAsync(scope.VariablesReference);
        Assert.Equal("0", locals.Single(v => v.Name == "sum").Value);
        Assert.Equal("int", locals.Single(v => v.Name == "sum").Type);
        var self = locals.Single(v => v.Name == "this");
        Assert.Equal("{NetFxDebuggee.Cart}", self.Value);
        var members = await dap.VariablesAsync(self.VariablesReference);
        Assert.Equal("\"Ada\"", members.Single(v => v.Name == "Owner").Value);
        Assert.Equal("this.Owner", members.Single(v => v.Name == "Owner").EvaluateName);
        Assert.Equal("2", members.Single(v => v.Name == "Count").Value);
        Assert.Equal("Busy | Loud", members.Single(v => v.Name == "Mood").Value);
        Assert.Equal("null", members.Single(v => v.Name == "_items").Value);
        var codes = members.Single(v => v.Name == "_codes");
        Assert.Equal("{int[3]}", codes.Value);

        // Count's getter ran (a func-eval neuters the values read before it): what's expanded now is read again.
        Assert.Equal(["7", "8", "9"], (await dap.VariablesAsync(codes.VariablesReference)).Select(v => v.Value));
        Assert.Contains(await dap.VariablesAsync(locals.Single(v => v.Name == "item").VariablesReference), v => v is { Name: "Name", Value: "\"Tea\"" });

        // Evaluation: members, operators, indexers, calls with overloads (func-eval), static fields, errors.
        Assert.Equal("3", await dap.EvaluateAsync("item.Price", top.Id));
        Assert.Equal("3", await dap.EvaluateAsync("Owner.Length", top.Id));
        Assert.Equal("4", await dap.EvaluateAsync("Twice(Count)", top.Id));
        Assert.Equal("5", await dap.EvaluateAsync("item.Price + Count", top.Id)); // item.Price read before Count's getter ran
        Assert.Equal("8", await dap.EvaluateAsync("Twice(Twice(Count))", top.Id));
        Assert.Equal("10", await dap.EvaluateAsync("this.Twice(5)", top.Id));
        Assert.Equal("9", await dap.EvaluateAsync("_codes[1] + 1", top.Id));
        Assert.Equal("true", await dap.EvaluateAsync("Count > 1 && Owner == \"Ada\"", top.Id));
        Assert.Equal("\"Ada!\"", await dap.EvaluateAsync("Owner + \"!\"", top.Id));
        Assert.Equal("0.5", await dap.EvaluateAsync("(long)sum + 0.5", top.Id));
        Assert.Equal("Busy | Loud", await dap.EvaluateAsync("Mood", top.Id));
        Assert.Equal("3", await dap.EvaluateAsync("(int)Mood", top.Id));
        Assert.Equal("101 'e'", await dap.EvaluateAsync("item.Name[1]", top.Id));
        process.StaticFields[FieldToken(info, info.FindType("NetFxDebuggee.Program"), "s_ticks")] = FakeValue.Of(5);
        Assert.Equal("5", await dap.EvaluateAsync("Program.s_ticks", top.Id));
        Assert.Equal("5", await dap.EvaluateAsync("NetFxDebuggee.Program.s_ticks", top.Id));
        var int32 = corelib.FindType("System.Int32");
        process.StaticFields[FieldToken(info, info.FindType("NetFxDebuggee.Program"), "s_ticks")] = FakeValue.Object(mscorlib, int32, valueType: true).With(FieldToken(corelib, int32, "m_value"), FakeValue.Of(5));
        Assert.Equal("5", await dap.EvaluateAsync("Program.s_ticks", top.Id)); // a boxed primitive that comes as its struct
        Assert.Equal("6", await dap.EvaluateAsync("Program.s_ticks + 1", top.Id));
        Assert.Contains("doesn't exist", (await Assert.ThrowsAsync<DapException>(() => dap.EvaluateAsync("nothingHere", top.Id))).Message, StringComparison.Ordinal);
        Assert.Contains("method 'Twice'", (await Assert.ThrowsAsync<DapException>(() => dap.EvaluateAsync("Twice(\"x\")", top.Id))).Message, StringComparison.Ordinal);
        Assert.Contains("outside the bounds", (await Assert.ThrowsAsync<DapException>(() => dap.EvaluateAsync("_codes[3]", top.Id))).Message, StringComparison.Ordinal);
        var evaluated = await dap.RequestAsync("evaluate", new EvaluateArguments("this", top.Id, "watch"), s_json.EvaluateArguments, s_json.EvaluateResponse);
        Assert.Contains(await dap.VariablesAsync(evaluated.VariablesReference), v => v.Name == "Owner");

        // Setting a local.
        var changed = await dap.RequestAsync("setVariable", new SetVariableArguments(scope.VariablesReference, "sum", "40 + 2"), s_json.SetVariableArguments, s_json.SetVariableResponse);
        Assert.Equal("42", changed.Value);
        Assert.Equal(42, sumValue.Primitive);
        await Assert.ThrowsAsync<DapException>(() => dap.RequestAsync("setVariable", new SetVariableArguments(scope.VariablesReference, "item", "1"), s_json.SetVariableArguments, s_json.SetVariableResponse));

        // Step over: the line's range; a hidden sequence point goes on by itself; the next line's start stops.
        await dap.RequestAsync("next", new ThreadArguments(7), s_json.ThreadArguments);
        ClrStep first;
        lock (process.Steps)
        {
            first = Assert.Single(process.Steps);
        }

        Assert.Equal(ClrStepKind.Over, first.Kind);
        Assert.Equal(info.RangeAt(total, offset), Assert.Single(first.Ranges));
        Assert.True(first.JustMyCode);
        var points = info.SequencePoints(total).OrderBy(p => p.Offset).ToList();
        var hidden = points.FirstOrDefault(p => p.IsHidden && p.Offset > offset);
        var next = points.First(p => !p.IsHidden && p.Offset > offset);
        if (hidden.Document is not null && hidden.StartLine != 0)
        {
            thread.FrameList[0] = new FakeFrame(thread, module, total, hidden.Offset) { };
            process.Send(new ClrStepCompleted(thread));
            await WaitUntilAsync(() => process.Steps.Count == 2);
            Assert.Equal(ClrStepKind.Over, process.Steps[1].Kind);
        }

        thread.FrameList[0] = new FakeFrame(thread, module, total, next.Offset);
        process.Send(new ClrStepCompleted(thread));
        var stepped = await dap.NextAsync(DapEvents.Stopped);
        Assert.Equal("step", stepped.GetProperty("reason").GetString());

        // Stepping into code without symbols (Just My Code): out again.
        await dap.RequestAsync("stepIn", new ThreadArguments(7), s_json.ThreadArguments);
        var stepsBefore = process.Steps.Count;
        thread.FrameList[0] = new FakeFrame(thread, mscorlib, corelib.FindMethods("Object.ToString").First(), 0);
        process.Send(new ClrStepCompleted(thread));
        await WaitUntilAsync(() => process.Steps.Count == stepsBefore + 1);
        Assert.Equal(ClrStepKind.Out, process.Steps[^1].Kind);
        thread.FrameList[0] = new FakeFrame(thread, module, total, next.Offset);
        process.Send(new ClrStepCompleted(thread));
        Assert.Equal("step", (await dap.NextAsync(DapEvents.Stopped)).GetProperty("reason").GetString());

        // An async method's MoveNext: its hoisted variables and parameter are the method's.
        var moveNext = info.FindMethods("MoveNext").Single(m => info.TypeFullName(info.DeclaringType(m)).Contains("ComputeAsync", StringComparison.Ordinal));
        var machineType = info.DeclaringType(moveNext);
        var machine = FakeValue.Object(module, machineType);
        foreach (var field in info.Fields(machineType))
        {
            machine.With(field.Token, field.Name switch { "value" => FakeValue.Of(5), _ when field.Name.StartsWith("<factor>", StringComparison.Ordinal) => FakeValue.Of(2), _ => FakeValue.Of(0) });
        }

        var asyncLine = DebuggeeFixture.Line(fixture.Program, "netfx-async");
        var asyncOffset = info.Locations(document, asyncLine).Single().Offset;
        var asyncFrame = new FakeFrame(thread, module, moveNext, asyncOffset);
        asyncFrame.Arguments.Add(machine);
        thread.FrameList.Insert(0, asyncFrame);
        await dap.RequestAsync("continue", new ThreadArguments(7), s_json.ThreadArguments, s_json.ContinueResponse);
        process.Send(new ClrBreakpointHit(thread, plain));
        await dap.NextAsync(DapEvents.Stopped);
        stack = await dap.RequestAsync("stackTrace", new StackTraceArguments(7), s_json.StackTraceArguments, s_json.StackTraceResponse);
        Assert.Equal(asyncLine, stack.StackFrames[0].Line);
        var asyncLocals = await dap.VariablesAsync(Assert.Single((await dap.RequestAsync("scopes", new ScopesArguments(stack.StackFrames[0].Id), s_json.ScopesArguments, s_json.ScopesResponse)).Scopes).VariablesReference);
        Assert.Equal("5", asyncLocals.Single(v => v.Name == "value").Value);
        Assert.Equal("2", asyncLocals.Single(v => v.Name == "factor").Value);
        Assert.DoesNotContain(asyncLocals, v => v.Name.StartsWith('<'));
        Assert.Equal("10", await dap.EvaluateAsync("value * factor", stack.StackFrames[0].Id));
        thread.FrameList.RemoveAt(0);

        // Exceptions: the filtered type stops (with its message and stack trace), another doesn't, nor a first chance outside the user's code.
        await dap.RequestAsync("continue", new ThreadArguments(7), s_json.ThreadArguments, s_json.ContinueResponse);
        var exceptionType = corelib.FindType("System.InvalidOperationException");
        var exceptionBase = corelib.FindType("System.Exception");
        var stackTraceGetter = Getter(corelib, exceptionBase, "StackTrace");
        process.OnCall = (_, method, _) => method == stackTraceGetter ? FakeValue.String("   at NetFxDebuggee.Cart.Checkout()") : throw new ClrException("unexpected call");
        var other = FakeValue.Object(mscorlib, corelib.FindType("System.ArgumentException")).With(FieldToken(corelib, exceptionBase, "_message"), FakeValue.String("no"));
        thread.Exception = other;
        continues = process.Continues;
        process.Send(new ClrExceptionThrown(thread, ClrExceptionStage.UserFirstChance));
        await process.WaitForContinuesAsync(continues + 1, Ct);
        var innerException = FakeValue.Object(mscorlib, corelib.FindType("System.ArgumentException")).With(FieldToken(corelib, exceptionBase, "_message"), FakeValue.String("The key is lost.")).With(FieldToken(corelib, exceptionBase, "_innerException"), FakeValue.Null());
        thread.Exception = FakeValue.Object(mscorlib, exceptionType).With(FieldToken(corelib, exceptionBase, "_message"), FakeValue.String("The till is closed.")).With(FieldToken(corelib, exceptionBase, "_innerException"), innerException);
        var userFrame = thread.FrameList[0];
        thread.FrameList[0] = new FakeFrame(thread, mscorlib, corelib.FindMethods("Object.ToString").First(), 0); // thrown in the framework
        process.Send(new ClrExceptionThrown(thread, ClrExceptionStage.FirstChance));
        await process.WaitForContinuesAsync(continues + 2, Ct);
        Assert.Equal(continues + 2, process.Continues);
        thread.FrameList[0] = userFrame;
        process.Send(new ClrExceptionThrown(thread, ClrExceptionStage.UserFirstChance));
        var exception = await dap.NextAsync(DapEvents.Stopped);
        Assert.Equal("exception", exception.GetProperty("reason").GetString());
        var exceptionInfo = await dap.RequestAsync("exceptionInfo", new ThreadArguments(7), s_json.ThreadArguments, s_json.ExceptionInfoResponse);
        Assert.Equal("System.InvalidOperationException", exceptionInfo.ExceptionId);
        Assert.Equal("The till is closed.", exceptionInfo.Description);
        Assert.Equal("   at NetFxDebuggee.Cart.Checkout()", exceptionInfo.Details?.StackTrace);
        var inner = Assert.Single(exceptionInfo.Details!.InnerException!); // read after StackTrace's getter ran
        Assert.Equal(("System.ArgumentException", "The key is lost."), (inner.FullTypeName, inner.Message));

        // Pause while running; Debugger.Log's output; the end.
        await dap.RequestAsync("continue", new ThreadArguments(7), s_json.ThreadArguments, s_json.ContinueResponse);
        await dap.RequestAsync("pause", new ThreadArguments(7), s_json.ThreadArguments);
        Assert.Equal("pause", (await dap.NextAsync(DapEvents.Stopped)).GetProperty("reason").GetString());
        Assert.Equal(1, process.Pauses);
        await dap.RequestAsync("continue", new ThreadArguments(7), s_json.ThreadArguments, s_json.ContinueResponse);
        process.Send(new ClrLogMessage("netfx-log\n"));
        Assert.Equal("netfx-log\n", (await dap.NextAsync(DapEvents.Output)).GetProperty("output").GetString());
        dap.Debugger.Output!("stdout", "Ada: 5\n");
        Assert.Equal("Ada: 5\n", (await dap.NextAsync(DapEvents.Output)).GetProperty("output").GetString());
        process.Send(new ClrExited(3));
        Assert.Equal(3, (await dap.NextAsync(DapEvents.Exited)).GetProperty("exitCode").GetInt32());
        await dap.NextAsync(DapEvents.Terminated);
        await dap.Session.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Attach_and_detach_leave_the_process_running()
    {
        fixture.RequireExe();
        var process = new FakeProcess();
        await using var dap = new Harness(process);
        await dap.RequestAsync("initialize", new InitializeArguments("codalus", "Codalus IDE", "clr"), s_json.InitializeArguments, s_json.Capabilities);
        await dap.RequestAsync("attach", new NetFxDebugArguments(ProcessId: 4242, Request: "attach"), s_json.NetFxDebugArguments);
        Assert.Equal(4242, dap.Debugger.Attached);
        await dap.RequestAsync("configurationDone", new EmptyArguments(), s_json.EmptyArguments);
        await dap.RequestAsync("disconnect", new DisconnectArguments(), s_json.DisconnectArguments);
        Assert.True(process.Detached);
        Assert.False(process.Terminated);
        await dap.Session.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Disconnect_ends_a_launched_program()
    {
        var process = new FakeProcess();
        await using var dap = new Harness(process);
        await dap.RequestAsync("initialize", new InitializeArguments("codalus", "Codalus IDE", "clr"), s_json.InitializeArguments, s_json.Capabilities);
        await dap.RequestAsync("launch", new NetFxDebugArguments("app.exe"), s_json.NetFxDebugArguments);
        await dap.RequestAsync("configurationDone", new EmptyArguments(), s_json.EmptyArguments);
        await dap.RequestAsync("disconnect", new DisconnectArguments(), s_json.DisconnectArguments);
        Assert.True(process.Terminated);
        await dap.NextAsync(DapEvents.Terminated);

        // Requests that need a stopped program say it's running.
        await using var running = new Harness(new FakeProcess());
        await running.RequestAsync("launch", new NetFxDebugArguments("app.exe"), s_json.NetFxDebugArguments);
        Assert.Contains("running", (await Assert.ThrowsAsync<DapException>(() => running.RequestAsync("next", new ThreadArguments(1), s_json.ThreadArguments))).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the real adapter and the debug service

    private static DebugAdapterStartInfo Adapter() =>
        new("dotnet", [Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll"), DesktopClr.AdapterArgument], AppContext.BaseDirectory);

    [Fact]
    public async Task The_adapter_process_answers_and_says_what_it_cant_launch()
    {
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        Assert.True(adapter.Capabilities.SupportsConfigurationDoneRequest);
        Assert.Contains(adapter.Capabilities.ExceptionBreakpointFilters!, f => f.Filter == "user-unhandled" && f.Default == true);
        var missing = Path.Combine(Path.GetTempPath(), "codalus-no-such-program.exe");
        var error = await Assert.ThrowsAsync<DapException>(() => adapter.LaunchAsync(new NetFxDebugArguments(missing), s_json.NetFxDebugArguments, Ct));
        Assert.Contains(OperatingSystem.IsWindows() ? "doesn't exist" : "only on Windows", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win-x64", "dbgshim.dll")]
    [InlineData("linux-x64", "libdbgshim.so")]
    [InlineData("linux-arm64", "libdbgshim.so")]
    [InlineData("osx-arm64", "libdbgshim.dylib")]
    public void The_host_carries_ClrDebug_and_dbgshim_for_each_package_platform(string rid, string library)
    {
        // CoreCLR's debugger adapter (M54, ADR 0086) runs on every OS: ClrDebug ships everywhere again (it was left out
        // of the Linux and macOS packages in 0.3.0), and dbgshim comes for the package's platform.
        var deps = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codalus-debughost.deps.json")))!;
        var libraries = deps["targets"]!.AsObject().Single().Value!.AsObject();
        Assert.Contains(libraries, l => l.Key.StartsWith("ClrDebug/", StringComparison.Ordinal) && l.Value!["runtime"] is not null);
        var shim = libraries.Single(l => l.Key.StartsWith($"Microsoft.Diagnostics.DbgShim.{rid}/", StringComparison.Ordinal)).Value!;
        var asset = Assert.Single(shim["runtimeTargets"]!.AsObject());
        Assert.Equal($"runtimes/{rid}/native/{library}", asset.Key);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", library)));
    }

    [Fact]
    public async Task The_debug_service_sends_dotnet_framework_programs_and_processes_to_the_desktop_clr_adapter()
    {
        // netcoredbg isn't there (null): the launch and the attach go to codalus-debughost --netfx-dap instead.
        await using var debug = new Codalus.Workbench.Debugging.DebugService(() => null)
        {
            DesktopClrAdapter = Adapter,
            IsDesktopClrProgram = program => program.EndsWith("codalus-no-such-program.exe", StringComparison.Ordinal),
            IsDesktopClrProcess = id => id == 1,
        };
        var missing = Path.Combine(Path.GetTempPath(), "codalus-no-such-program.exe");
        var plan = new Codalus.Workbench.Build.LaunchPlan(missing, string.Empty, Path.GetTempPath(), new Dictionary<string, string>(), null);
        var launch = await Assert.ThrowsAnyAsync<Exception>(() => debug.LaunchAsync(plan, Ct));
        Assert.DoesNotContain("netcoredbg", launch.Message, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "doesn't exist" : "only on Windows", launch.Message, StringComparison.Ordinal);
        Assert.False(debug.IsActive);

        var attach = await Assert.ThrowsAnyAsync<Exception>(() => debug.AttachAsync(1, Ct));
        Assert.DoesNotContain("netcoredbg", attach.Message, StringComparison.Ordinal);

        // Other programs still go to netcoredbg.
        var other = await Assert.ThrowsAnyAsync<Exception>(() => debug.LaunchAsync(plan with { FileName = Path.Combine(Path.GetTempPath(), "app") }, Ct));
        Assert.Contains("netcoredbg", other.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attach_to_process_tells_dotnet_framework_processes_by_one_snapshot_of_their_modules()
    {
        // The desktop CLR: clr.dll, or CLR 2's mscorwks.dll; CoreCLR wins when it's there too.
        Assert.Equal("clr.dll", DesktopClr.RuntimeOf(["ntdll.dll", "mscoree.dll", "CLR.DLL", "clrjit.dll"]));
        Assert.Equal("mscorwks.dll", DesktopClr.RuntimeOf(["mscorwks.dll"]));
        Assert.Equal("clr.dll", DesktopClr.RuntimeOf(["mscorwks.dll", "clr.dll"]));
        Assert.Null(DesktopClr.RuntimeOf(["clr.dll", "coreclr.dll"]));
        Assert.Null(DesktopClr.RuntimeOf(["ntdll.dll", "kernel32.dll"]));

        // One snapshot decides .NET and .NET Framework together: a desktop-CLR process is always .NET (Windows only).
        Assert.Equal((true, true), Codalus.Workbench.Debugging.ProcessList.Classify("NetFxDebuggee", ["ntdll.dll", "mscoree.dll", "clr.dll"], windows: true));
        Assert.Equal((true, false), Codalus.Workbench.Debugging.ProcessList.Classify("app", ["ntdll.dll", "coreclr.dll", "clr.dll"], windows: true));
        Assert.Equal((true, false), Codalus.Workbench.Debugging.ProcessList.Classify("app", ["libc.so.6", "libcoreclr.so"], windows: false));
        Assert.Equal((false, false), Codalus.Workbench.Debugging.ProcessList.Classify("NetFxDebuggee", ["clr.dll"], windows: false));
        Assert.Equal((false, false), Codalus.Workbench.Debugging.ProcessList.Classify("notepad", ["ntdll.dll", "user32.dll"], windows: true));
        Assert.Equal((true, false), Codalus.Workbench.Debugging.ProcessList.Classify("dotnet", null, windows: false));
        Assert.Equal((false, false), Codalus.Workbench.Debugging.ProcessList.Classify("LegacyApp", null, windows: true));

        // The entry keeps it so, and its runtime follows.
        var legacy = new Codalus.Workbench.Debugging.ProcessEntry(4242, "LegacyApp", null, IsDotnet: false, IsDesktopClr: true);
        Assert.True(legacy.IsDotnet);
        Assert.Equal(".NET Framework", legacy.Runtime);
        Assert.Equal(".NET", new Codalus.Workbench.Debugging.ProcessEntry(5150, "app", null, true).Runtime);
        Assert.Null(new Codalus.Workbench.Debugging.ProcessEntry(6000, "notepad", null, false).Runtime);

        // This test's own process, read the way Attach to Process reads every process (unknown on macOS).
        var own = Codalus.Platform.ProcessModules.Names(Environment.ProcessId);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Null(own);
        }
        else
        {
            Assert.NotNull(own);
            Assert.Contains(own, m => string.Equals(m, OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(own.Count, own.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal((true, false), Codalus.Workbench.Debugging.ProcessList.Classify("testhost", own, OperatingSystem.IsWindows()));
            Assert.Null(DesktopClr.LoadedRuntime(Environment.ProcessId));
        }

        Assert.Null(Codalus.Platform.ProcessModules.Names(int.MaxValue)); // no such process

        // The list leaves this process out and puts .NET first.
        var list = Codalus.Workbench.Debugging.ProcessList.Get();
        Assert.DoesNotContain(list, p => p.Id == Environment.ProcessId);
        Assert.All(list, p => Assert.True(p.IsDotnet || !p.IsDesktopClr));
        Assert.Equal(list.OrderByDescending(p => p.IsDotnet).Select(p => p.Id), list.Select(p => p.Id));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(condition());
    }
}
