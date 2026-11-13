namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// M60's tools for agents: Hot Reload (Apply Code Changes) and profiling of the program being run or debugged. Each runs
/// or changes the program, so each is an action (<c>ai.agent.ideActions</c>).
/// </summary>
public static class IdeRuntimeTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } =
    [
        IdeTools.Action("apply_code_changes", "Apply the code changes to the program that runs, without restarting it (Hot Reload, Apply Code Changes): the program being debugged (the Codalus debugger applies the editors' text, saved or not) or one started with Hot Reload (dotnet watch: unsaved files are saved). Returns whether they were applied, or why not: compile errors, or an edit that needs a restart (a rude edit).", "{}"),
        IdeTools.Action("profile_start", "Start a CPU profile (the Performance Profiler's CPU Usage) of the program being debugged or run, or, when nothing runs or a project is given, launch the startup project (or that one) under the profiler from its start. Then make the program do the work to measure and call profile_stop; the profile also stops when the program ends.", """{"project":{"type":"string","description":"A project to launch under the profiler (it builds first), instead of profiling the program that runs."}}"""),
        IdeTools.Action("profile_stop", "Stop the CPU profile (or take the one that stopped when its program ended), open its report in Codalus IDE, and return a summary: the hot path and the top functions by self (exclusive) and total (inclusive) time, with their source lines.", """{"top":{"type":"integer","description":"How many functions in each list (default 15, at most 50)."}}"""),
        IdeTools.Action("memory_snapshot", "Take a GC heap snapshot of the program being run or debugged (a full garbage collection first) and return the types holding the most memory (their objects' count and size) and, for the largest, what keeps them alive: the path of referrers to a GC root.", """{"top":{"type":"integer","description":"How many types (default 20, at most 100)."}}"""),
    ];
}
