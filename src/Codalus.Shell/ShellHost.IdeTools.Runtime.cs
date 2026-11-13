using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Debugging;
using Codalus.Diagnostics.Protocol;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Shell;

/// <summary>
/// M60's tools for agents: the shell's side of <see cref="IdeRuntimeTools"/>. Apply Code Changes on the debug session's
/// Hot Reload (M55) or on <c>dotnet watch</c> (M15b), and the Performance Profiler's CPU Usage and Memory Usage (M18,
/// <c>codalus-diaghost</c>) summed up as text: the hot path, the top functions, the largest types and what holds them.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>How long apply_code_changes waits for <c>dotnet watch</c> to say how an apply went.</summary>
    private static readonly TimeSpan s_watchApplyWait = TimeSpan.FromSeconds(60);

    /// <summary>The profile profile_start started, kept after it stops by itself (its program ended) for profile_stop.</summary>
    private ProfilingRun? _agentProfiling;

    private async Task<IdeToolResult> RuntimeToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var result = tool switch
        {
            "apply_code_changes" => await ApplyCodeChangesToolAsync(root, cancellationToken).ConfigureAwait(true),
            "profile_start" => await ProfileStartToolAsync(args, cancellationToken).ConfigureAwait(true),
            "profile_stop" => await ProfileStopToolAsync(args, root, cancellationToken).ConfigureAwait(true),
            "memory_snapshot" => await MemorySnapshotToolAsync(args, cancellationToken).ConfigureAwait(true),
            _ => throw new McpException($"There is no tool '{tool}'."),
        };
        return result with { Text = result.Text.ReplaceLineEndings("\n") };
    }

    // ------------------------------------------------------------------ Hot Reload

    private async Task<IdeToolResult> ApplyCodeChangesToolAsync(string? root, CancellationToken cancellationToken)
    {
        if (HotReloadProgram is null && DebugEdits.IsActive)
        {
            // The language host compiles the edits: it reads the files the code-change tools just wrote first.
            await SyncAgentWritesAsync(cancellationToken).ConfigureAwait(true);
            return await ApplyDebugEditsToolAsync(root, cancellationToken).ConfigureAwait(true);
        }

        if (HotReloadProgram is { HotReload: { } session } program)
        {
            return await ApplyWatchedEditsToolAsync(program, session, cancellationToken).ConfigureAwait(true);
        }

        return IdeToolResult.Error(Debugger.IsActive
            ? "The debugger in use (netcoredbg) can't apply code changes while it debugs. Stop the program (stop_run) and debug_start it again to run the new code; the Codalus debugger (the debug.adapter setting) applies changes."
            : "No program takes code changes now. Start it with debug_start (the debugger applies changes to it), or with Hot Reload in the IDE, then apply_code_changes after an edit.");
    }

    /// <summary>The debug session's Hot Reload: the editors' text, saved or not; a rude edit is reported, never asked about.</summary>
    private async Task<IdeToolResult> ApplyDebugEditsToolAsync(string? root, CancellationToken cancellationToken)
    {
        // A session that just started has no Hot Reload yet (the language host loads the solution first): wait for it.
        // "Nothing to apply" is asked twice more a second apart: an agent writes files on disk, and the language host
        // reads a changed file once its watcher has been quiet for 200 ms.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var unchanged = 0;
        HotReloadOutcome outcome;
        while (true)
        {
            // Not cancelled midway: an update half applied would leave the session between two versions.
            outcome = (await ApplyDebugEditsAsync(ask: false, CancellationToken.None).ConfigureAwait(true)).Outcome;
            var again = outcome.Kind == HotReloadOutcome.Unavailable ? DateTime.UtcNow < deadline
                : outcome.Kind == HotReloadOutcome.None && unchanged++ < 2;
            if (!again || !DebugEdits.IsActive)
            {
                break;
            }

            await Task.Delay(outcome.Kind == HotReloadOutcome.None ? 1000 : 500, cancellationToken).ConfigureAwait(true);
        }

        string Diagnostics()
        {
            var sb = new StringBuilder();
            foreach (var d in outcome.Diagnostics.Take(50))
            {
                sb.Append("- ").Append(d.Id).Append(": ").Append(d.Message);
                if (d.Location is { } location && DocumentUris.ToPath(location.Uri) is { } file && IsInWorkspace(file, root))
                {
                    sb.Append(" (").Append(Relative(file, root)).Append(':').Append((location.Range.Start.Line + 1).ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        switch (outcome.Kind)
        {
            case HotReloadOutcome.Applied:
            {
                var sb = new StringBuilder("The code changes were applied to the program being debugged; it runs them from now on");
                sb.Append(Debugger.State == DebugState.Stopped ? " (it is stopped: debug_continue or debug_step goes on in the new code)." : ".");
                switch (outcome.Browser)
                {
                    case { Unreachable: true }:
                        sb.Append(" The edit changed code that runs in the browser, which gets it after a restart.");
                        break;
                    case { Errors.Count: > 0 } failed:
                        sb.Append(" The browser couldn't apply it: ").Append(string.Join("; ", failed.Errors)).Append('.');
                        break;
                    case { Pages: > 0 }:
                        sb.Append(" The browser's page took it too.");
                        break;
                }

                foreach (var error in outcome.HandlerErrors)
                {
                    sb.Append("\nThe program's update handlers failed: ").Append(error);
                }

                return new(sb.ToString());
            }

            case HotReloadOutcome.None:
                return new("There were no code changes to apply: the program already runs the code as it is.");
            case HotReloadOutcome.Errors:
                return IdeToolResult.Error("Not applied: the changes don't compile. Fix these, then apply_code_changes again:\n" + Diagnostics());
            case HotReloadOutcome.Rude:
                return IdeToolResult.Error("Not applied: the changes can't be applied while the program runs (a rude edit), so the program goes on with its old code. Restart it to run them (stop_run, then debug_start):\n" + Diagnostics());
            case HotReloadOutcome.Failed:
                return IdeToolResult.Error("Not applied: " + (outcome.Message?.Contains("CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED", StringComparison.Ordinal) == true
                    ? "the program wasn't started to take code changes (it was attached to). Start it with debug_start instead."
                    : outcome.Message ?? "the debugger refused them."));
            default:
                return IdeToolResult.Error("Hot Reload isn't ready for this debug session yet (the language service is still loading the solution); try again shortly.");
        }
    }

    /// <summary><c>dotnet watch</c>: unsaved documents are saved, and its next report says how the apply went.</summary>
    private async Task<IdeToolResult> ApplyWatchedEditsToolAsync(RunningProgram program, HotReloadSession session, CancellationToken cancellationToken)
    {
        if (!session.Started.IsCompleted)
        {
            try
            {
                await Task.WhenAny(session.Started, program.Exited).WaitAsync(s_hotReloadStartWait, cancellationToken).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                // Its output didn't say (another SDK's wording): saving is still the trigger.
            }
        }

        if (!_models.Values.Any(m => m.IsModified && !m.IsReadOnly))
        {
            return new($"Nothing is unsaved: dotnet watch applies saved files as they change. Its last report: {WatchStateText(session.State)}{(session.Message is { Length: > 0 } m ? " (" + m + ")" : "")}. get_run_output has its output.");
        }

        var reported = new TaskCompletionSource<HotReloadEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, HotReloadEvent e)
        {
            if (e.State is not (HotReloadState.Applying or HotReloadState.Starting))
            {
                reported.TrySetResult(e);
            }
        }

        session.Changed += OnChanged;
        try
        {
            await SaveAllAsync().ConfigureAwait(true);
            var e = await reported.Task.WaitAsync(s_watchApplyWait, cancellationToken).ConfigureAwait(true);
            return e.State switch
            {
                HotReloadState.Watching when e.Applied is { } applied => new(string.Create(CultureInfo.InvariantCulture, $"The code changes were saved and dotnet watch applied them to {program.Name} in {applied.TotalMilliseconds:0} ms.")),
                HotReloadState.Watching => new($"The code changes were saved; dotnet watch: {e.Message}"),
                HotReloadState.Restarting => IdeToolResult.Error($"The changes couldn't be applied while {program.Name} runs (a rude edit): dotnet watch is restarting it with them. {e.Message}"),
                HotReloadState.Errors => IdeToolResult.Error($"Not applied: the changes don't compile ({e.Message}). get_run_output has dotnet watch's errors."),
                HotReloadState.Exited => IdeToolResult.Error($"{program.Name} exited; dotnet watch starts it again on the next change. {e.Message}"),
                _ => IdeToolResult.Error($"Not applied: {e.Message}"),
            };
        }
        catch (TimeoutException)
        {
            return IdeToolResult.Error(string.Create(CultureInfo.InvariantCulture, $"The changes were saved, but dotnet watch didn't report within {s_watchApplyWait.TotalSeconds:0} s; get_run_output has its output."));
        }
        finally
        {
            session.Changed -= OnChanged;
        }
    }

    private static string WatchStateText(HotReloadState state) => state switch
    {
        HotReloadState.Starting => "starting",
        HotReloadState.Watching => "watching",
        HotReloadState.Applying => "applying",
        HotReloadState.Errors => "compile errors",
        HotReloadState.Restarting => "restarting (a rude edit)",
        HotReloadState.Exited => "the app exited",
        _ => "not applied",
    };

    // ------------------------------------------------------------------ the program to profile

    /// <summary>
    /// The program an agent's profiling tools look at: the one being debugged, else the newest one run (under
    /// <c>dotnet watch</c>, the app it started, not the watcher); null when none runs.
    /// </summary>
    private (int ProcessId, string Name, bool Debugged)? AgentProgram()
    {
        if (Debugger.IsActive && Debugger.Processes.LastOrDefault(p => p is { Id: not null, Ended: false, Target: null }) is { Id: { } id } debugged)
        {
            return (id, debugged.Name, true);
        }

        foreach (var program in Runs.Running.Reverse())
        {
            if (program.HasExited)
            {
                continue;
            }

            if (program.HotReload is null)
            {
                return (program.ProcessId, program.Name, false);
            }

            if (program.HotReload.AppProcessId is { } app)
            {
                return (app, program.Name, false);
            }
        }

        return null;
    }

    /// <summary>The newest notification since <paramref name="before"/> notifications: why a profiler call failed.</summary>
    private string? NotificationSince(int before) =>
        Workbench.Notifications.Items is { } items && items.Count > before ? items[^1].Message : null;

    // ------------------------------------------------------------------ CPU profiling

    private async Task<IdeToolResult> ProfileStartToolAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (ActiveProfiling is { } active)
        {
            return IdeToolResult.Error($"A profile of {active.Target} is collecting already: profile_stop it first.");
        }

        if (!Profiling.IsAvailable)
        {
            return IdeToolResult.Error("The diagnostics host (codalus-diaghost) isn't installed with this IDE, so it can't profile.");
        }

        var project = Str(args, "project") is { Length: > 0 } name ? ProjectPath(name) : null;
        var target = project is null ? AgentProgram() : null;
        if (target is { Debugged: true } && Debugger.State == DebugState.Stopped)
        {
            return IdeToolResult.Error("The program is stopped under the debugger, so it would record nothing: debug_continue first (or remove its breakpoints).");
        }

        if (target is null)
        {
            project ??= Runs.StartupProject ?? throw new IdeToolException("Nothing runs, and there is no startup project to launch: give a project (get_solution lists them).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var before = Workbench.Notifications.Items.Count;
        var run = target is { } program
            ? await StartProfilingAsync(new ProfilerRequest([ProfilerTools.Cpu], Launch: false, program.ProcessId), program.Name).ConfigureAwait(true)
            : await StartProfilingAsync(new ProfilerRequest([ProfilerTools.Cpu], Launch: true, null), launchProject: project).ConfigureAwait(true);
        if (run is null)
        {
            return IdeToolResult.Error("Profiling couldn't start" + (NotificationSince(before) is { } why ? ": " + why : " (the build failed or the workspace isn't trusted; get_build_output has the build)."));
        }

        _agentProfiling = run;
        var how = target is null
            ? $"Launched {run.Target} (process {run.ProcessId.ToString(CultureInfo.InvariantCulture)}) under the profiler, from its start"
            : $"Profiling {run.Target} (process {run.ProcessId.ToString(CultureInfo.InvariantCulture)})";
        return new(how + ", for CPU usage. Make it do the work to measure (run_tests, browser_*, its input), then call profile_stop for the summary. The profile stops by itself when the program ends.");
    }

    private async Task<IdeToolResult> ProfileStopToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if ((ActiveProfiling ?? _agentProfiling) is not { } run)
        {
            return IdeToolResult.Error("No profile is collecting: profile_start first.");
        }

        // Stopping waits for the trace to be written (a stopped one: the session it saved), then opens its report.
        var session = await run.StopAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
        if (ReferenceEquals(_agentProfiling, run))
        {
            _agentProfiling = null;
        }

        if (session?.TracePath is not { } path || !File.Exists(path))
        {
            return IdeToolResult.Error($"The profile of {run.Target} recorded nothing (the program may have ended before the profiler reached it).");
        }

        var top = Math.Clamp(Int(args, "top") ?? 15, 1, 50);
        var trace = await Profiling.OpenTraceAsync(path, cancellationToken).ConfigureAwait(true);
        await using (trace.ConfigureAwait(true))
        {
            return new(await CpuSummaryTextAsync(trace, session, top, root, cancellationToken).ConfigureAwait(true));
        }
    }

    /// <summary>The CPU profile as text: its totals, the hot path (the widest call at each level), the top functions.</summary>
    private async Task<string> CpuSummaryTextAsync(CpuTrace trace, ProfilerSession session, int top, string? root, CancellationToken cancellationToken)
    {
        var summary = trace.Summary;
        var filter = new CpuFilter();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"CPU profile of {session.ProcessName} (process {session.ProcessId}): {summary.DurationMilliseconds / 1000:0.0} s, {summary.Samples} samples in managed code ({summary.ExternalSamples} in native code or waiting), {summary.Threads.Count(t => t.Samples > 0)} threads with samples.");
        sb.AppendLine().AppendLine("Its report is open in Codalus IDE.");
        var roots = await trace.TopDownAsync(filter, -1, 0, 50, cancellationToken).ConfigureAwait(true);
        var total = roots.Total;
        if (total == 0)
        {
            return sb.AppendLine("No samples in managed code: the program waited or ran native code the whole time.").ToString();
        }

        string Percent(int samples) => (100.0 * samples / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        // The hot path: from the widest root, the widest child at each level while it holds most of its parent.
        sb.AppendLine().AppendLine("Hot path (inclusive % of the samples, the widest call at each level):");
        var at = roots.Nodes.OrderByDescending(n => n.Inclusive).FirstOrDefault();
        for (var depth = 0; at is not null && depth < 40; depth++)
        {
            sb.Append(' ', 2 + depth).Append(Percent(at.Inclusive)).Append("  ").Append(at.Name);
            if (at.Exclusive > 0)
            {
                sb.Append("  (self ").Append(Percent(at.Exclusive)).Append(')');
            }

            sb.AppendLine();
            if (!at.HasChildren)
            {
                break;
            }

            var children = await trace.TopDownAsync(filter, at.Id, 0, 50, cancellationToken).ConfigureAwait(true);
            var next = children.Nodes.OrderByDescending(n => n.Inclusive).FirstOrDefault();
            at = next is not null && next.Inclusive * 2 >= at.Inclusive ? next : null;
        }

        // The functions' source lines come once the host has read the symbols, after the trace opened: up to 30 s.
        var symbols = DateTime.UtcNow.AddSeconds(30);
        while (!(await trace.FilesAsync(filter, cancellationToken).ConfigureAwait(true)).Resolved && DateTime.UtcNow < symbols)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
        }

        var functions = (await trace.FunctionsAsync(filter, null, 0, 500, cancellationToken).ConfigureAwait(true)).Functions;
        async Task<string> LineAsync(CpuFunction function)
        {
            var text = new StringBuilder().Append(function.Name);
            if (function.File is { } file && IsInWorkspace(file, root) && !await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
            {
                text.Append(" (").Append(Relative(file, root));
                if (function.Line is { } line)
                {
                    text.Append(':').Append(line.ToString(CultureInfo.InvariantCulture));
                }

                text.Append(')');
            }
            else if (function.Module is { Length: > 0 } module)
            {
                text.Append(" [").Append(module).Append(']');
            }

            return text.ToString();
        }

        sb.AppendLine().AppendLine("Top functions by self time (exclusive, then inclusive):");
        foreach (var function in functions.Where(f => f.Exclusive > 0).Take(top))
        {
            sb.Append("  ").Append(Percent(function.Exclusive)).Append("  ").Append(Percent(function.Inclusive)).Append("  ").AppendLine(await LineAsync(function).ConfigureAwait(true));
        }

        sb.AppendLine().AppendLine("Top functions by total time (inclusive, then exclusive):");
        foreach (var function in functions.OrderByDescending(f => f.Inclusive).ThenByDescending(f => f.Exclusive).Take(top))
        {
            sb.Append("  ").Append(Percent(function.Inclusive)).Append("  ").Append(Percent(function.Exclusive)).Append("  ").AppendLine(await LineAsync(function).ConfigureAwait(true));
        }

        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ memory

    private async Task<IdeToolResult> MemorySnapshotToolAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!Profiling.IsAvailable)
        {
            return IdeToolResult.Error("The diagnostics host (codalus-diaghost) isn't installed with this IDE, so it can't take a heap snapshot.");
        }

        if (AgentProgram() is not { } program)
        {
            return IdeToolResult.Error("No program runs: start it with run_project or debug_start first.");
        }

        if (program.Debugged && Debugger.State == DebugState.Stopped)
        {
            // The snapshot's full garbage collection needs the program's threads to run.
            return IdeToolResult.Error("The program is stopped under the debugger, and a heap snapshot needs it running: debug_continue first.");
        }

        var top = Math.Clamp(Int(args, "top") ?? 20, 1, 100);
        var folder = Path.Combine(Path.GetTempPath(), "codalus-heap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var file = Path.Combine(folder, "heap.nettrace");
            await Profiling.TakeHeapSnapshotAsync(program.ProcessId, file, timeout.Token).ConfigureAwait(true);
            var snapshot = await Profiling.OpenHeapSnapshotAsync(file, timeout.Token).ConfigureAwait(true);
            await using (snapshot.ConfigureAwait(true))
            {
                return new(await HeapSummaryTextAsync(snapshot, program.Name, program.ProcessId, top, timeout.Token).ConfigureAwait(true));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IdeToolResult.Error("The heap snapshot took more than 5 minutes; it was abandoned.");
        }
        finally
        {
            TryDeleteFolder(folder);
        }
    }

    /// <summary>The largest types of a snapshot, and for the first few the heaviest path of referrers to a GC root.</summary>
    private static async Task<string> HeapSummaryTextAsync(HeapSnapshot snapshot, string name, int processId, int top, CancellationToken cancellationToken)
    {
        var summary = snapshot.Summary;
        var types = await snapshot.TypesAsync(null, null, 0, top, cancellationToken).ConfigureAwait(true);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Heap of {name} (process {processId}) after a full garbage collection: {summary.Objects} objects, {HeapBytes(summary.Bytes)}, {summary.Types} types, {summary.Roots} roots.");
        sb.AppendLine().AppendLine().AppendLine("Largest types (the size of their own objects):");
        foreach (var type in types.Types)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {HeapBytes(type.Bytes),10}  {type.Count,9} objects  {type.Name}").AppendLine();
        }

        if (types.More > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  (and {types.More} smaller types)").AppendLine();
        }

        // What keeps the largest alive: the heaviest referrer at each step, up to a root.
        var shown = 0;
        foreach (var type in types.Types)
        {
            if (shown == 3)
            {
                break;
            }

            List<int> path = [type.Id];
            var chain = new List<string>();
            var reachedRoot = false;
            for (var depth = 0; depth < 12; depth++)
            {
                // The heaviest referrer not on the path already (a delegate's invocation list refers back to delegates).
                var referrers = await snapshot.ReferrersAsync(path, 0, 20, cancellationToken).ConfigureAwait(true);
                if (referrers.Referrers.Where(r => r.Id < 0 || !path.Contains(r.Id)).OrderByDescending(r => r.Bytes).FirstOrDefault() is not { } heaviest)
                {
                    break;
                }

                chain.Add(heaviest.Id < 0 ? "[root: " + heaviest.Name + "]" : heaviest.Name);
                if (heaviest.Id < 0)
                {
                    reachedRoot = true;
                    break;
                }

                if (!heaviest.HasChildren)
                {
                    break;
                }

                path.Add(heaviest.Id);
            }

            if (chain.Count == 0)
            {
                continue;
            }

            if (shown++ == 0)
            {
                sb.AppendLine().AppendLine("What keeps the largest alive (the heaviest path of referrers, toward a GC root):");
            }

            sb.Append("  ").Append(type.Name).Append(" ← ").Append(string.Join(" ← ", chain)).AppendLine(reachedRoot ? "" : " …");
        }

        return sb.ToString().TrimEnd();
    }

    private static string HeapBytes(long size) => size switch
    {
        >= 1 << 30 => (size / (double)(1 << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1 << 20 => (size / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1 << 10 => (size / (double)(1 << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => size.ToString(CultureInfo.InvariantCulture) + " B",
    };
}
