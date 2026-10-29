using System.Diagnostics;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Platform;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M55 (ADR 0086's phase 2), on every OS: Hot Reload while debugging under the Codalus debugger. The language host's
/// Hot Reload session (Roslyn's Edit and Continue service) turns an edit of the running program's source into deltas;
/// the adapter applies them (<c>codalus/applyChanges</c>) and binds breakpoints in the new version. Later calls run the
/// new code; a frame already in a changed method finishes in its old version (Hot Reload's semantics, not a remap). A
/// program that hasn't stopped yet takes edits too. A program attached to takes them when it runs with
/// <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>, and is refused with the runtime's reason without it. A rude edit comes
/// back as Roslyn's diagnostic with the project to restart. Visual Basic as C#.
/// </summary>
[Collection(HotReloadTests.Name)]
public sealed class HotReloadDebuggingTests(HotReloadFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private const string Body = "        var text = \"hello \" + i; // [message]\n        return text;";

    private HotReloadFixture.Source CSharp => fixture.CSharp;

    private static DebugAdapterStartInfo Adapter() => DebuggeeFixture.CodalusAdapter();

    [Fact]
    public async Task An_edit_applies_to_a_launched_program_and_a_breakpoint_binds_on_a_line_only_the_new_version_has()
    {
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var events = new EventLog(adapter);
        var session = "launch-" + Guid.NewGuid().ToString("N");
        try
        {
            await LaunchAsync(adapter, CSharp, [new SourceBreakpoint(CSharp.Line("call"))]);
            var stopped = await events.StoppedAsync();
            var thread = stopped.ThreadId!.Value;
            await StartAsync(adapter, session, CSharp);

            // Message(0) not called yet: the edit adds a line and a local, which a breakpoint stops on.
            await CSharp.EditAsync(Body, "        var text = \"hello \" + i; // [message]\n        var edited = text.Length; // [new]\n        return text + \" edited \" + edited;");
            await ApplyAsync(adapter, session);
            await adapter.SetBreakpointsAsync(CSharp.Path, [new SourceBreakpoint(CSharp.Line("new"))], Ct);
            await adapter.ContinueAsync(thread, Ct);
            var inNew = await events.StoppedAsync();
            var top = (await adapter.StackTraceAsync(inNew.ThreadId!.Value, 1, Ct)).StackFrames[0];
            Assert.Equal("Greeter.Message", top.Name);
            Assert.Equal(CSharp.Line("new"), top.Line);
            await adapter.NextAsync(inNew.ThreadId.Value, Ct);
            var stepped = await events.StoppedAsync();
            var after = (await adapter.StackTraceAsync(stepped.ThreadId!.Value, 1, Ct)).StackFrames[0];
            Assert.Equal("7", (await adapter.EvaluateAsync("edited", after.Id, "watch", Ct)).Result);

            await adapter.SetBreakpointsAsync(CSharp.Path, [], Ct);
            await adapter.ContinueAsync(stepped.ThreadId.Value, Ct);
            await WaitUntilAsync(() => events.Output.Contains("hello 0 edited 7", StringComparison.Ordinal), "the edited Message's output", events);

            // A second edit on the first, while the program runs.
            await CSharp.EditAsync("\" edited \"", "\" twice \"");
            await ApplyAsync(adapter, session);
            await WaitUntilAsync(() => events.Output.Contains(" twice ", StringComparison.Ordinal), "the second edit's output", events);
        }
        finally
        {
            await EndAsync(adapter, session);
        }
    }

    [Fact]
    public async Task An_edit_applies_to_a_program_that_hasnt_stopped_yet()
    {
        // mscordbi reads the module's metadata for the edit through an importer a stop's stack walk creates: before the
        // adapter asked for it first, this crashed the adapter.
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var events = new EventLog(adapter);
        var session = "running-" + Guid.NewGuid().ToString("N");
        try
        {
            await LaunchAsync(adapter, CSharp, []);
            await WaitUntilAsync(() => events.Output.Contains("hello 3", StringComparison.Ordinal), "the program's output", events);
            await StartAsync(adapter, session, CSharp);
            await CSharp.EditAsync("        return text;", "        return text + \" running\";");
            await ApplyAsync(adapter, session);
            await WaitUntilAsync(() => events.Output.Contains(" running", StringComparison.Ordinal), "the edited output", events);
        }
        finally
        {
            await EndAsync(adapter, session);
        }
    }

    [Fact]
    public async Task A_frame_already_in_the_changed_method_finishes_in_its_old_version()
    {
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var events = new EventLog(adapter);
        var session = "frame-" + Guid.NewGuid().ToString("N");
        try
        {
            await LaunchAsync(adapter, CSharp, [new SourceBreakpoint(CSharp.Line("message"))]);
            var stopped = await events.StoppedAsync();
            var thread = stopped.ThreadId!.Value;
            await StartAsync(adapter, session, CSharp);

            await CSharp.EditAsync("        return text;", "        return text + \" new\";");
            await ApplyAsync(adapter, session);

            // The stopped frame is still the old version's: its line, then its next line, and its old result.
            Assert.Equal(CSharp.Line("message"), (await adapter.StackTraceAsync(thread, 1, Ct)).StackFrames[0].Line);
            await adapter.SetBreakpointsAsync(CSharp.Path, [], Ct);
            await adapter.NextAsync(thread, Ct);
            await events.StoppedAsync();
            Assert.Equal(CSharp.Line("message") + 1, (await adapter.StackTraceAsync(thread, 1, Ct)).StackFrames[0].Line);
            await adapter.ContinueAsync(thread, Ct);
            await WaitUntilAsync(() => events.Output.Contains("hello 1 new", StringComparison.Ordinal), "the next call's new output", events);
            Assert.Contains("hello 0\n", events.Output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.DoesNotContain("hello 0 new", events.Output, StringComparison.Ordinal);
        }
        finally
        {
            await EndAsync(adapter, session);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_program_attached_to_takes_edits_only_when_it_runs_with_modifiable_assemblies(bool modifiable)
    {
        var go = Path.Combine(CSharp.Folder, "attach-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(DebuggeeFixture.DotnetHost, ["exec", CSharp.Dll, go])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = CSharp.Folder,
        };
        if (modifiable)
        {
            start.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
        }

        using var program = Process.Start(start)!;

        // Read in the background: Peek() on a redirected pipe waits for more output instead of saying there's none,
        // so a loop of it read until the program ended its loop, two minutes on (macOS, the codalus-mac session).
        var output = new System.Text.StringBuilder();
        var waiting = new TaskCompletionSource();
        program.OutputDataReceived += (_, e) =>
        {
            lock (output)
            {
                output.AppendLine(e.Data);
            }

            if (e.Data == "waiting")
            {
                waiting.TrySetResult();
            }
        };
        program.BeginOutputReadLine();
        var session = "attach-" + Guid.NewGuid().ToString("N");
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var events = new EventLog(adapter);
            await adapter.AttachAsync(new NetFxDebugArguments(ProcessId: program.Id, Type: "coreclr", Request: "attach"), s_json.NetFxDebugArguments, Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await adapter.ConfigurationDoneAsync(Ct);
            await WaitUntilAsync(() => events.All.Any(e => e.Name == DapEvents.Module && e.Body.ToString().Contains("HotReload.dll", StringComparison.Ordinal)), "the program's module", events);
            await StartAsync(adapter, session, CSharp);

            await CSharp.EditAsync("        return text;", "        return text + \" attached\";");
            var update = await fixture.Client.HotReloadUpdateAsync(session, Ct);
            Assert.Equal(HotReloadUpdateResult.Ready, update.Status);
            var result = Assert.Single((await ApplyChangesAsync(adapter, update)).Results);
            if (modifiable)
            {
                Assert.Equal(ModuleDeltaResult.Applied, result.Status);
                await fixture.Client.HotReloadCommitAsync(session, Ct);
                File.WriteAllText(go, string.Empty);
                await WaitUntilAsync(() =>
                {
                    lock (output)
                    {
                        return output.ToString().Contains("hello 0 attached", StringComparison.Ordinal);
                    }
                }, "the attached program's new output", events);
            }
            else
            {
                // Its modules were loaded before the debugger came, without the variable: the runtime says so.
                Assert.Equal(ModuleDeltaResult.Failed, result.Status);
                Assert.Contains("CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED", result.Message, StringComparison.Ordinal);
                await fixture.Client.HotReloadDiscardAsync(session, Ct);
            }
        }
        finally
        {
            await EndAsync(adapter, session);
            ProcessTree.Kill(program);
        }
    }

    [Fact]
    public async Task A_rude_edit_comes_back_with_its_diagnostic_and_the_project_to_restart()
    {
        var session = "rude-" + Guid.NewGuid().ToString("N");
        Assert.True(await fixture.Client.HotReloadStartAsync(session, [CSharp.Project], ["Baseline", "AddMethodToExistingType"], Ct));
        try
        {
            await CSharp.EditAsync("static class Greeter", "class Greeter : Exception");
            var update = await fixture.Client.HotReloadUpdateAsync(session, Ct);
            Assert.Empty(update.Updates);
            Assert.Contains(update.RudeEdits, d => d.Id.StartsWith("ENC", StringComparison.Ordinal) && d.Location is not null);
            Assert.Equal(CSharp.Project, Assert.Single(update.ProjectsToRestart), ignoreCase: !OperatingSystem.IsLinux());
            await fixture.Client.HotReloadDiscardAsync(session, Ct);
        }
        finally
        {
            await fixture.Client.HotReloadEndAsync(session, Ct);
            await fixture.RestoreAsync();
        }
    }

    [Fact]
    public async Task An_edit_applies_to_a_Visual_Basic_program()
    {
        var vb = fixture.VisualBasic;
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var events = new EventLog(adapter);
        var session = "vb-" + Guid.NewGuid().ToString("N");
        try
        {
            await LaunchAsync(adapter, vb, [new SourceBreakpoint(vb.Line("vb-call"))]);
            var stopped = await events.StoppedAsync();
            await StartAsync(adapter, session, vb);
            await vb.EditAsync("        Return text", "        Return text & \" from VB\"");
            await ApplyAsync(adapter, session);
            await adapter.SetBreakpointsAsync(vb.Path, [], Ct);
            await adapter.ContinueAsync(stopped.ThreadId!.Value, Ct);
            await WaitUntilAsync(() => events.Output.Contains("hello 0 from VB", StringComparison.Ordinal), "the edited output", events);
        }
        finally
        {
            await EndAsync(adapter, session);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task LaunchAsync(DebugAdapter adapter, HotReloadFixture.Source program, SourceBreakpoint[] breakpoints)
    {
        await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", program.Dll], program.Folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await adapter.SetBreakpointsAsync(program.Path, breakpoints, Ct);
        await adapter.ConfigurationDoneAsync(Ct);
    }

    private async Task StartAsync(DebugAdapter adapter, string session, HotReloadFixture.Source program)
    {
        var capabilities = await adapter.Connection.RequestAsync(Host.NetFx.NetFxDebugSession.HotReloadCapabilitiesCommand, JsonElement.Parse("{}"), s_json.JsonElement, s_json.HotReloadCapabilitiesResponse, Ct);
        Assert.Equal("Baseline", capabilities.Capabilities[0]);
        Assert.True(await fixture.Client.HotReloadStartAsync(session, [program.Project], capabilities.Capabilities, Ct), "the language host has the project");
    }

    /// <summary>The edits since the last commit: the language host's deltas, applied by the adapter, then committed.</summary>
    private async Task ApplyAsync(DebugAdapter adapter, string session)
    {
        var update = await fixture.Client.HotReloadUpdateAsync(session, Ct);
        Assert.True(update.Status == HotReloadUpdateResult.Ready && update.Updates.Length == 1,
            $"{update.Status}, {update.Updates.Length} update(s); rude edits: {string.Join("; ", update.RudeEdits.Select(d => d.Id + " " + d.Message))}; errors: {string.Join("; ", update.Errors.Select(d => d.Id + " " + d.Message))}");
        var result = Assert.Single((await ApplyChangesAsync(adapter, update)).Results);
        Assert.True(result.Status == ModuleDeltaResult.Applied, $"{result.Status}: {result.Message}");
        await fixture.Client.HotReloadCommitAsync(session, Ct);
    }

    private static Task<ApplyChangesResponse> ApplyChangesAsync(DebugAdapter adapter, HotReloadUpdateResult update) =>
        adapter.Connection.RequestAsync(Host.NetFx.NetFxDebugSession.ApplyChangesCommand,
            new ApplyChangesArguments([.. update.Updates.Select(u => new ModuleDelta(u.ModuleId, u.MetadataDelta, u.IlDelta, u.PdbDelta))]),
            s_json.ApplyChangesArguments, s_json.ApplyChangesResponse, Ct);

    private async Task EndAsync(DebugAdapter adapter, string session)
    {
        try
        {
            await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }

        try
        {
            await fixture.Client.HotReloadEndAsync(session, Ct);
        }
        finally
        {
            await fixture.RestoreAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, EventLog events)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), $"Timed out waiting for {what}. Output: {events.Output}");
            await Task.Delay(50, Ct);
        }
    }
}

/// <summary>The Hot Reload tests share one fixture (a build host and a language host) and run one at a time: they edit the same files.</summary>
[CollectionDefinition(Name)]
public sealed class HotReloadTests : ICollectionFixture<HotReloadFixture>
{
    public const string Name = "HotReload";
}
