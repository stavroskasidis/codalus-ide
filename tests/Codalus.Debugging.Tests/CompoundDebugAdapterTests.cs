using System.Text.Json;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M16d (ADR 0027): a compound session (a server and its page) through <see cref="CompoundDebugAdapter"/>, on fake
/// adapters: ids route requests, breakpoints merge, stops wait their turn, and one adapter's end ends the session.
/// </summary>
public sealed class CompoundDebugAdapterTests
{
    /// <summary>A part's thread 1 in the session: its own id times the parts, plus its index.</summary>
    private const int M = CompoundDebugAdapter.MaxParts;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CompoundDebugAdapter Compound, FakeAdapter Server, FakeAdapter Browser, List<DapEvent> Events)> CreateAsync()
    {
        var server = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var browser = new FakeAdapter([new DapThread(1, "localhost:5000/counter")]);
        var compound = new CompoundDebugAdapter(server, "Server");
        var events = new List<DapEvent>();
        compound.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            return Task.CompletedTask;
        };
        await compound.AddAsync(browser, "Browser", _ => Task.CompletedTask, Ct);
        return (compound, server, browser, events);
    }

    private static T[] Events<T>(List<DapEvent> events, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        lock (events)
        {
            return [.. events.Where(e => e.Name == name).Select(e => e.Body.Deserialize(type)!)];
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Ids_send_each_request_to_the_adapter_it_came_from()
    {
        var (compound, server, browser, _) = await CreateAsync();
        var threads = await compound.ThreadsAsync(Ct);
        Assert.Equal([new DapThread(M, "Main Thread"), new DapThread(M + 1, "Browser: localhost:5000/counter")], threads);

        var frames = (await compound.StackTraceAsync(M + 1, 20, Ct)).StackFrames;
        Assert.Equal(1, browser.Requests.Count(r => r == "stackTrace 1"));
        Assert.Equal(7 * CompoundDebugAdapter.MaxParts + 1, frames[0].Id);

        var scopes = await compound.ScopesAsync(frames[0].Id, Ct);
        Assert.Contains("scopes 7", browser.Requests);
        Assert.Equal(9 * CompoundDebugAdapter.MaxParts + 1, scopes[0].VariablesReference);
        var variables = await compound.VariablesAsync(scopes[0].VariablesReference, Ct);
        Assert.Contains("variables 9", browser.Requests);
        Assert.Equal(0, variables[0].VariablesReference); // none stays none

        await compound.StackTraceAsync(M, 20, Ct);
        Assert.Contains("stackTrace 1", server.Requests);
        Assert.Equal(await compound.EvaluateAsync("x", 7 * CompoundDebugAdapter.MaxParts, "watch", Ct), new EvaluateResponse("x", VariablesReference: 0));
        Assert.Contains("evaluate x 7", server.Requests);
    }

    [Fact]
    public async Task A_breakpoint_is_bound_when_either_adapter_binds_it_and_joining_adapters_get_it()
    {
        var server = new FakeAdapter([]) { Binds = false };
        var compound = new CompoundDebugAdapter(server, "Server");
        var events = new List<DapEvent>();
        compound.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            return Task.CompletedTask;
        };

        var result = await compound.SetBreakpointsAsync("/app/Counter.razor", [new SourceBreakpoint(12)], Ct);
        Assert.False(result[0].Verified);
        var id = result[0].Id!.Value;

        // The browser joins, gets the breakpoint and binds it: the session's breakpoint is bound.
        var browser = new FakeAdapter([]);
        await compound.AddAsync(browser, "Browser", _ => Task.CompletedTask, Ct);
        Assert.Contains("setBreakpoints /app/Counter.razor 12", browser.Requests);
        var changed = Events(events, DapEvents.Breakpoint, DapJsonContext.Default.BreakpointEvent).Last();
        Assert.Equal(id, changed.Breakpoint.Id);
        Assert.True(changed.Breakpoint.Verified);

        // An adapter's later event about its own id is reported under the session's id.
        await server.RaiseAsync(DapEvents.Breakpoint, new BreakpointEvent("changed", new Breakpoint(server.LastBreakpointIds[0], Verified: true)), DapJsonContext.Default.BreakpointEvent);
        Assert.Equal(id, Events(events, DapEvents.Breakpoint, DapJsonContext.Default.BreakpointEvent).Last().Breakpoint.Id);

        // Both get the next change of the file.
        var again = await compound.SetBreakpointsAsync("/app/Counter.razor", [new SourceBreakpoint(12), new SourceBreakpoint(20)], Ct);
        Assert.Equal(2, again.Length);
        Assert.All(again, b => Assert.True(b.Verified));
        Assert.Contains("setBreakpoints /app/Counter.razor 12,20", server.Requests);
        Assert.Contains("setBreakpoints /app/Counter.razor 12,20", browser.Requests);
    }

    [Fact]
    public async Task A_stop_waits_while_another_one_shows_and_shows_when_it_resumes()
    {
        var (compound, server, browser, events) = await CreateAsync();
        await server.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        await browser.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        Assert.Equal([M], Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent).Select(s => s.ThreadId));

        await compound.ContinueAsync(M, Ct);
        Assert.Contains("continue 1", server.Requests);
        Assert.DoesNotContain("continue 1", browser.Requests);
        await WaitAsync(() => Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent).Length == 2);
        Assert.Equal([M, M + 1], Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent).Select(s => s.ThreadId));
    }

    [Fact]
    public async Task Break_all_pauses_every_adapter_and_continue_resumes_every_paused_one()
    {
        var (compound, server, browser, events) = await CreateAsync();
        await compound.PauseAsync(0, Ct);
        Assert.Contains("pause 0", server.Requests);
        Assert.Contains("pause 0", browser.Requests);
        await server.RaiseAsync(DapEvents.Stopped, new StoppedEvent("pause", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        await browser.RaiseAsync(DapEvents.Stopped, new StoppedEvent("pause", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        Assert.Single(Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent));

        await compound.ContinueAsync(M, Ct);
        Assert.Contains("continue 1", server.Requests);
        Assert.Contains("continue 1", browser.Requests);
        await Task.Delay(100, Ct);
        Assert.Single(Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent)); // nothing left to show
    }

    [Fact]
    public async Task When_one_adapter_ends_the_others_are_ended_and_the_session_ends_once()
    {
        var (compound, server, browser, events) = await CreateAsync();
        await browser.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        Assert.Contains("disconnect True", server.Requests);
        await server.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        Assert.Single(events, e => e.Name == DapEvents.Terminated);
        Assert.True(compound.Closed.IsCompleted);
    }

    [Fact]
    public async Task An_adapter_that_cannot_join_leaves_the_session_running()
    {
        var server = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var compound = new CompoundDebugAdapter(server, "Server");
        var browser = new FakeAdapter([new DapThread(1, "page")]);
        await Assert.ThrowsAsync<DapException>(() => compound.AddAsync(browser, "Browser", _ => throw new DapException("launch", "no browser"), Ct));
        Assert.Equal(1, compound.PartCount);
        Assert.Equal([new DapThread(M, "Main Thread")], await compound.ThreadsAsync(Ct));
        Assert.False(compound.Closed.IsCompleted);
        Assert.True(browser.Disposed);
    }

    [Fact]
    public async Task Independent_programs_end_one_by_one_and_the_session_ends_with_the_last()
    {
        // M14c: multiple startup projects, each program on its own adapter.
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var second = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var compound = new CompoundDebugAdapter(first, "Api", independent: true);
        var events = new List<DapEvent>();
        var parts = new List<CompoundPartEvent>();
        compound.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            return Task.CompletedTask;
        };
        compound.PartChanged += (_, e) =>
        {
            lock (parts)
            {
                parts.Add(e);
            }
        };
        await compound.AddAsync(second, "Worker", _ => Task.CompletedTask, Ct);
        Assert.Equal(["Api", "Worker"], compound.PartNames);
        Assert.Equal([new DapThread(M, "Api: Main Thread"), new DapThread(M + 1, "Worker: Main Thread")], await compound.ThreadsAsync(Ct));

        // Each program's process is reported; the second's exit becomes a line in the Debug Console.
        await first.RaiseAsync(DapEvents.Process, new ProcessEvent("api", 100), DapJsonContext.Default.ProcessEvent);
        await second.RaiseAsync(DapEvents.Process, new ProcessEvent("worker", 200), DapJsonContext.Default.ProcessEvent);
        Assert.Equal([100, 200], parts.Select(p => p.ProcessId));
        Assert.Single(events, e => e.Name == DapEvents.Process); // the primary's, for the session's process id

        await second.RaiseAsync(DapEvents.Exited, new ExitedEvent(3), DapJsonContext.Default.ExitedEvent);
        await second.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        Assert.Contains(Events(events, DapEvents.Output, DapJsonContext.Default.OutputEvent), o => o.Output.Contains("[Worker] The program has exited with code 3", StringComparison.Ordinal));
        Assert.DoesNotContain("disconnect True", first.Requests); // the other program goes on
        Assert.DoesNotContain(events, e => e.Name == DapEvents.Terminated);
        Assert.False(compound.Closed.IsCompleted);
        Assert.Contains(parts, p => p is { Name: "Worker", Ended: true, ExitCode: 3 });
        Assert.Equal([new DapThread(M, "Api: Main Thread")], await compound.ThreadsAsync(Ct));

        await first.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        Assert.Single(events, e => e.Name == DapEvents.Terminated);
        Assert.True(compound.Closed.IsCompleted);
    }

    [Fact]
    public async Task A_reserved_place_keeps_the_session_open_when_the_first_program_ends_before_the_next_joins()
    {
        // Found on a loaded Windows runner: the first program exited before the second's adapter was ready, the session
        // ended, and the second's start was cancelled.
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var second = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var compound = new CompoundDebugAdapter(first, "Partner", independent: true);
        compound.Reserve(1);
        var events = new List<DapEvent>();
        compound.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            return Task.CompletedTask;
        };

        await first.RaiseAsync(DapEvents.Terminated, new ExitedEvent(7), DapJsonContext.Default.ExitedEvent);
        Assert.DoesNotContain(events, e => e.Name == DapEvents.Terminated);
        Assert.False(compound.Closed.IsCompleted);

        await compound.AddAsync(second, "Debuggee", _ => Task.CompletedTask, Ct);
        await compound.ReleaseAsync();
        Assert.False(compound.Closed.IsCompleted); // the second program runs

        await second.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        Assert.Single(events, e => e.Name == DapEvents.Terminated);
        Assert.True(compound.Closed.IsCompleted);
    }

    [Fact]
    public async Task Releasing_the_last_reserved_place_ends_a_session_whose_programs_all_ended()
    {
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var compound = new CompoundDebugAdapter(first, "Partner", independent: true);
        compound.Reserve(1);
        await first.RaiseAsync(DapEvents.Terminated, new ExitedEvent(7), DapJsonContext.Default.ExitedEvent);
        Assert.False(compound.Closed.IsCompleted);

        await compound.ReleaseAsync(); // the second program couldn't start
        Assert.True(compound.Closed.IsCompleted);
    }

    [Fact]
    public async Task An_independent_programs_stop_still_waits_for_the_one_shown()
    {
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var second = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var compound = new CompoundDebugAdapter(first, "Api", independent: true);
        var events = new List<DapEvent>();
        compound.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            return Task.CompletedTask;
        };
        await compound.AddAsync(second, "Worker", _ => Task.CompletedTask, Ct);
        await first.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        await second.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        Assert.Equal([M], Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent).Select(s => s.ThreadId));

        // The shown program ends: the waiting stop shows.
        await first.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        await WaitAsync(() => Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent).Length == 2);
        Assert.Equal(M + 1, Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent)[1].ThreadId);
    }

    private static bool Requested(FakeAdapter adapter, string request)
    {
        lock (adapter.Requests)
        {
            return adapter.Requests.Contains(request);
        }
    }

    [Fact]
    public async Task A_program_still_starting_holds_up_neither_the_threads_nor_another_programs_stop()
    {
        // netcoredbg answers configurationDone once the program's runtime started (up to its 5 seconds), and every
        // request waits behind it: the first program's stop, which lists the threads, waited for the second's start.
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var configured = new TaskCompletionSource();
        var second = new FakeAdapter([new DapThread(1, "Main Thread")]) { ConfigurationDoneGate = configured.Task };
        await using var compound = new CompoundDebugAdapter(first, "App", independent: true);
        var joining = compound.AddAsync(second, "Worker", _ => Task.CompletedTask, Ct);
        await WaitAsync(() => Requested(second, "configurationDone"));

        Assert.Equal([new DapThread(M, "App: Main Thread")], await compound.ThreadsAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(Requested(second, "threads"));

        // A program that stopped while starting (an early breakpoint) lists its threads, as one that joined does.
        await second.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        Assert.Equal(2, (await compound.ThreadsAsync(Ct)).Length);
        configured.SetResult();
        await joining;
        Assert.Equal([new DapThread(M, "App: Main Thread"), new DapThread(M + 1, "Worker: Main Thread")], await compound.ThreadsAsync(Ct));
    }

    [Fact]
    public async Task Break_all_leaves_out_a_program_still_starting_and_pauses_the_others()
    {
        // A program joining the session (a later startup project, or one whose start is made again in its place) isn't
        // attached yet, and netcoredbg's Break All waits until it is (DebugAdapter.PauseAsync, up to a minute). Waiting
        // for it held up the next program's pause, and stopped the joining one once attached, after the session may have
        // gone on.
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var configured = new TaskCompletionSource();
        var attached = new TaskCompletionSource();
        var joining = new FakeAdapter([new DapThread(1, "Main Thread")]) { ConfigurationDoneGate = configured.Task, PauseGate = attached.Task };
        var last = new FakeAdapter([new DapThread(1, "Main Thread")]);
        await using var compound = new CompoundDebugAdapter(first, "App", independent: true);
        var log = new List<string>();
        compound.Log += (_, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };
        var adding = compound.AddAsync(joining, "Worker", _ => Task.CompletedTask, Ct);
        await WaitAsync(() => Requested(joining, "configurationDone"));
        await compound.AddAsync(last, "Api", _ => Task.CompletedTask, Ct);

        await compound.PauseAsync(0, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(Requested(first, "pause 0"));
        Assert.True(Requested(last, "pause 0"));
        Assert.False(Requested(joining, "pause 0"));
        lock (log)
        {
            Assert.Contains("[Worker] Not paused: it's still starting.", log);
        }

        // Once it joined, Break All pauses it too.
        configured.SetResult();
        attached.SetResult();
        await adding;
        await compound.PauseAsync(0, Ct);
        Assert.True(Requested(joining, "pause 0"));
    }

    [Fact]
    public async Task Break_all_and_the_threads_leave_out_a_program_whose_attach_goes_on_after_its_configuration()
    {
        // A program in the terminal or a console window is attached to only after configurationDone (DebugService's held
        // launch), and that attach can wait a minute more for netcoredbg (DebugAdapter.AttachAsync). Break All meanwhile
        // waited for it, then stopped it once attached, after the user may have continued the others.
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var launched = new TaskCompletionSource();
        var attached = new TaskCompletionSource();
        var joining = new FakeAdapter([new DapThread(1, "Main Thread")]) { PauseGate = attached.Task };
        await using var compound = new CompoundDebugAdapter(first, "App", independent: true);
        var log = new List<string>();
        compound.Log += (_, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };
        var adding = compound.AddAsync(joining, "Worker", _ => launched.Task, Ct);
        await WaitAsync(() => Requested(joining, "configurationDone"));

        await compound.PauseAsync(0, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(Requested(first, "pause 0"));
        Assert.False(Requested(joining, "pause 0"));
        lock (log)
        {
            Assert.Contains("[Worker] Not paused: it's still starting.", log);
        }

        Assert.Equal([new DapThread(M, "App: Main Thread")], await compound.ThreadsAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(Requested(joining, "threads"));

        // Once its attach is answered, it has joined: Break All pauses it too.
        launched.SetResult();
        attached.SetResult();
        await adding;
        await compound.PauseAsync(0, Ct);
        Assert.True(Requested(joining, "pause 0"));
    }

    [Fact]
    public async Task Break_all_waits_for_the_first_program_to_be_attached_to()
    {
        // The first program starts with the session, and no other program has joined yet: as in a session of one program,
        // Break All waits until netcoredbg has attached to it (DebugAdapter.PauseAsync, up to a minute), then stops it.
        var attached = new TaskCompletionSource();
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]) { PauseGate = attached.Task };
        await using var compound = new CompoundDebugAdapter(first, "App", independent: true);

        var pausing = compound.PauseAsync(0, Ct);
        await Task.Delay(100, Ct);
        Assert.False(pausing.IsCompleted);

        attached.SetResult();
        await pausing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(Requested(first, "pause 0"));
    }

    [Fact]
    public async Task Break_all_goes_on_past_a_program_whose_adapter_ended_meanwhile()
    {
        // A program ending as Break All reaches it: its adapter's connection closes before the session sees it end.
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]) { PauseError = new DapConnectionLostException("The debug adapter ended.") };
        var second = new FakeAdapter([new DapThread(1, "Main Thread")]);
        await using var compound = new CompoundDebugAdapter(first, "App", independent: true);
        var log = new List<string>();
        compound.Log += (_, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };
        await compound.AddAsync(second, "Worker", _ => Task.CompletedTask, Ct);

        await compound.PauseAsync(0, Ct);

        Assert.True(Requested(second, "pause 0"));
        lock (log)
        {
            Assert.Contains("[App] Pause failed: The debug adapter ended.", log);
        }
    }

    [Fact]
    public async Task A_start_made_again_keeps_the_programs_place_and_drops_the_abandoned_attempt()
    {
        var first = new FakeAdapter([new DapThread(1, "Main Thread")]);
        var timedOut = new FakeAdapter([new DapThread(1, "Main Thread")]) { ConfigurationDoneError = "Failed command 'configurationDone' : 0x80004005" };
        var started = new FakeAdapter([new DapThread(1, "Main Thread")]);
        await using var compound = new CompoundDebugAdapter(first, "App", independent: true);
        var events = new List<DapEvent>();
        compound.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            return Task.CompletedTask;
        };
        var changes = new List<CompoundPartEvent>();
        compound.PartChanged += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e);
            }
        };
        await compound.SetBreakpointsAsync("/app/Worker.cs", [new SourceBreakpoint(3)], Ct);

        var adapters = new Queue<FakeAdapter>([timedOut, started]);
        var attempts = new List<(Exception Error, int Attempt)>();
        await compound.AddAsync(
            "Worker",
            _ => Task.FromResult<IDebugAdapter>(adapters.Dequeue()),
            _ => Task.CompletedTask,
            (adapter, error, attempt) =>
            {
                attempts.Add((error, attempt));
                return Task.FromResult(true);
            },
            Ct);

        // One failed attempt, undone: disposed, its end and events no longer the session's.
        var (failure, number) = Assert.Single(attempts);
        Assert.Equal(1, number);
        Assert.Contains("0x80004005", failure.Message, StringComparison.Ordinal);
        Assert.True(timedOut.Disposed);
        await timedOut.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1), DapJsonContext.Default.StoppedEvent);
        Assert.Empty(Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent));
        Assert.DoesNotContain(changes, c => c.Ended);

        // The second attempt took the program's place (index 1) and got the session's breakpoints.
        Assert.Equal(["App", "Worker"], compound.PartNames);
        Assert.True(Requested(started, "setBreakpoints /app/Worker.cs 3"));
        Assert.Equal([new DapThread(M, "App: Main Thread"), new DapThread(M + 1, "Worker: Main Thread")], await compound.ThreadsAsync(Ct));
        await started.RaiseAsync(DapEvents.Stopped, new StoppedEvent("breakpoint", ThreadId: 1, HitBreakpointIds: [started.LastBreakpointIds[0]]), DapJsonContext.Default.StoppedEvent);
        var stop = Assert.Single(Events(events, DapEvents.Stopped, DapJsonContext.Default.StoppedEvent));
        Assert.Equal(M + 1, stop.ThreadId);
        Assert.Equal([1], stop.HitBreakpointIds!);
    }

    /// <summary>An adapter that records its requests: thread 1, frame 7, scope reference 9.</summary>
    private sealed class FakeAdapter(DapThread[] threads) : IDebugAdapter
    {
        private readonly TaskCompletionSource _closed = new();
        private int _nextBreakpoint = 100;

        public List<string> Requests { get; } = [];

        public bool Binds { get; init; } = true;

        /// <summary><c>configurationDone</c> answers once this completes (netcoredbg waits for the program's runtime).</summary>
        public Task ConfigurationDoneGate { get; init; } = Task.CompletedTask;

        /// <summary><c>configurationDone</c> fails with this message (netcoredbg didn't see the runtime start in time).</summary>
        public string? ConfigurationDoneError { get; init; }

        /// <summary>A pause answers once this completes (netcoredbg's Break All waits until it has attached).</summary>
        public Task PauseGate { get; init; } = Task.CompletedTask;

        /// <summary>A pause fails with this.</summary>
        public Exception? PauseError { get; init; }

        public int[] LastBreakpointIds { get; private set; } = [];

        public bool Disposed { get; private set; }

        public Capabilities Capabilities { get; } = new();

        public Task Initialized => Task.CompletedTask;

        public Task Closed => _closed.Task;

        public event Func<DapEvent, Task>? EventReceived;

        public event EventHandler<string>? Log;

        public Task RaiseAsync<T>(string name, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
            EventReceived?.Invoke(new DapEvent(name, JsonSerializer.SerializeToElement(body, type))) ?? Task.CompletedTask;

        private Task Record(string request)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            Log?.Invoke(this, request);
            return Task.CompletedTask;
        }

        public async Task ConfigurationDoneAsync(CancellationToken cancellationToken)
        {
            await Record("configurationDone");
            await ConfigurationDoneGate.WaitAsync(cancellationToken);
            if (ConfigurationDoneError is { } error)
            {
                throw new DapException("configurationDone", error);
            }
        }

        public async Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken)
        {
            await Record($"setBreakpoints {path} {string.Join(",", breakpoints.Select(b => b.Line))}");
            LastBreakpointIds = [.. breakpoints.Select(_ => _nextBreakpoint++)];
            return [.. breakpoints.Select((b, i) => new Breakpoint(LastBreakpointIds[i], Binds, Line: b.Line))];
        }

        public Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken) =>
            Task.FromResult<Breakpoint[]>([.. breakpoints.Select(_ => new Breakpoint(Verified: Binds))]);

        public Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null) =>
            Record("setExceptionBreakpoints " + string.Join(",", filters));

        public Task ContinueAsync(int threadId, CancellationToken cancellationToken) => Record($"continue {threadId}");

        public Task NextAsync(int threadId, CancellationToken cancellationToken) => Record($"next {threadId}");

        public Task StepInAsync(int threadId, CancellationToken cancellationToken) => Record($"stepIn {threadId}");

        public Task StepOutAsync(int threadId, CancellationToken cancellationToken) => Record($"stepOut {threadId}");

        public async Task PauseAsync(int threadId, CancellationToken cancellationToken)
        {
            await PauseGate.WaitAsync(cancellationToken);
            if (PauseError is { } error)
            {
                throw error;
            }

            await Record($"pause {threadId}");
        }

        public async Task<DapThread[]> ThreadsAsync(CancellationToken cancellationToken)
        {
            await Record("threads");
            return threads;
        }

        public async Task<StackTraceResponse> StackTraceAsync(int threadId, int levels, CancellationToken cancellationToken)
        {
            await Record($"stackTrace {threadId}");
            return new StackTraceResponse([new StackFrame(7, "Counter.OnInitialized", new Source("Counter.razor", "/app/Counter.razor"), 12, 1)]);
        }

        public async Task<Scope[]> ScopesAsync(int frameId, CancellationToken cancellationToken)
        {
            await Record($"scopes {frameId}");
            return [new Scope("Locals", 9)];
        }

        public async Task<Variable[]> VariablesAsync(int variablesReference, CancellationToken cancellationToken)
        {
            await Record($"variables {variablesReference}");
            return [new Variable("count", "1", "int")];
        }

        public async Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken)
        {
            await Record($"evaluate {expression} {frameId}");
            return new EvaluateResponse(expression);
        }

        public Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken) =>
            Task.FromResult(new SetVariableResponse(value));

        public Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken cancellationToken) =>
            Task.FromResult(new SetExpressionResponse(value));

        public Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExceptionInfoResponse("Error"));

        public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken)
        {
            await Record($"disconnect {terminateDebuggee}");
            _closed.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
