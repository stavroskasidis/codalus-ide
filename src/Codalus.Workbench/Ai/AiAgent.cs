using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Ai;

/// <summary>What the agent's tools need from the IDE: the user's approvals, the editor's text, problems and builds.</summary>
public interface IAiAgentHost
{
    /// <summary>The workspace's folder, or null with none open.</summary>
    string? Root { get; }

    /// <summary>Whether the workspace is trusted: commands and builds run only then.</summary>
    bool Trusted { get; }

    /// <summary>A file's text as the editor has it (unsaved changes included), or null when it isn't open.</summary>
    string? OpenText(string path);

    /// <summary>A file's problems, or every file's with <paramref name="path"/> null, one per line.</summary>
    string Problems(string? path);

    /// <summary>Shows the change in the refactoring preview; returns whether the user applied it.</summary>
    Task<bool> ProposeEditAsync(string path, string? oldText, string newText, CancellationToken cancellationToken);

    /// <summary>Asks the user whether <paramref name="command"/> may run.</summary>
    Task<bool> ApproveCommandAsync(string command, CancellationToken cancellationToken);

    /// <summary>Asks the user whether an MCP server's tool may run with these arguments.</summary>
    Task<AiApproval> ApproveToolAsync(string server, string tool, string arguments, CancellationToken cancellationToken);

    /// <summary>Builds the solution; returns the outcome and its errors.</summary>
    Task<string> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>The user's answer to a tool call.</summary>
public enum AiApproval
{
    Deny,
    Once,
    Session,
}

/// <summary>A tool of the agent: what the model sees, and how it runs.</summary>
public sealed record AiAgentTool(AiToolDto Definition, Func<JsonObject, CancellationToken, Task<string>> Invoke);

/// <summary>What the agent did, for the chat: text as it streams, and each tool call with its result.</summary>
public abstract record AiAgentProgress
{
    public sealed record Text(string Piece) : AiAgentProgress;

    public sealed record Calling(string Tool, string Summary) : AiAgentProgress;

    public sealed record Result(string Tool, string Summary) : AiAgentProgress;
}

/// <summary>
/// Agent mode (M28d, ADR 0063): a loop of at most <see cref="MaxSteps"/> steps. Each sends the conversation with the
/// tools; the model answers with text, tool calls or both, and the calls' results go back. It ends when an answer has
/// no tool call, the user stops it, or the steps run out.
/// </summary>
public sealed class AiAgent(AiAgent.Chat chat, IReadOnlyList<AiAgentTool> tools)
{
    public const int MaxSteps = 25;

    /// <summary>One request: the messages and tools, each event handed on as it comes.</summary>
    public delegate Task Chat(IReadOnlyList<AiMessageDto> messages, IReadOnlyList<AiToolDto> tools, Action<AiEventParams> onEvent, CancellationToken cancellationToken);

    public const string SystemPrompt =
        "You are the agent in Codalus IDE, an IDE for .NET, working in the user's workspace through tools. Read the code you need "
        + "before changing it, and keep changes small and focused. edit_file replaces exact text, so quote it exactly as read_file "
        + "showed it (without the line numbers). The user reviews every edit and approves every command; a rejected one stays "
        + "rejected, so don't retry it unchanged. When done, say briefly what you changed.";

    /// <summary>Runs the loop on <paramref name="messages"/> (extended in place); returns the final answer's text.</summary>
    public async Task<string> RunAsync(List<AiMessageDto> messages, Action<AiAgentProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(progress);
        var definitions = tools.Select(t => t.Definition).ToList();
        for (var step = 0; step < MaxSteps; step++)
        {
            var text = new StringBuilder();
            var calls = new List<AiToolCallDto>();
            await chat(messages, definitions, e =>
            {
                if (e.Text is { Length: > 0 } piece)
                {
                    text.Append(piece);
                    progress(new AiAgentProgress.Text(piece));
                }

                if (e.ToolCall is { } call)
                {
                    calls.Add(call);
                }
            }, cancellationToken).ConfigureAwait(false);

            messages.Add(new AiMessageDto("assistant", text.ToString()) { ToolCalls = calls.Count > 0 ? [.. calls] : null });
            if (calls.Count == 0)
            {
                return text.ToString();
            }

            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress(new AiAgentProgress.Calling(call.Name, Summarize(call.Arguments)));
                var result = await InvokeAsync(call, cancellationToken).ConfigureAwait(false);
                progress(new AiAgentProgress.Result(call.Name, FirstLine(result)));
                messages.Add(new AiMessageDto("tool", result) { ToolCallId = call.Id });
            }
        }

        return $"Stopped after {MaxSteps} steps.";
    }

    private async Task<string> InvokeAsync(AiToolCallDto call, CancellationToken cancellationToken)
    {
        if (tools.FirstOrDefault(t => t.Definition.Name == call.Name) is not { } tool)
        {
            return $"There is no tool named {call.Name}.";
        }

        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(call.Arguments.Length > 0 ? call.Arguments : "{}") as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            return "The arguments aren't valid JSON: " + ex.Message;
        }

        try
        {
            return await tool.Invoke(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException)
        {
            return $"{call.Name} failed: {ex.Message}";
        }
    }

    /// <summary>The arguments for the chat: their values, short.</summary>
    internal static string Summarize(string arguments)
    {
        try
        {
            if (JsonNode.Parse(arguments) is JsonObject node)
            {
                var parts = node.Select(p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : p.Value?.ToJsonString() ?? string.Empty)
                    .Select(s => FirstLine(s));
                return string.Join(", ", parts);
            }
        }
        catch (JsonException)
        {
        }

        return FirstLine(arguments);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 100 ? line[..100] + "…" : text.Contains('\n', StringComparison.Ordinal) ? line + " …" : line;
    }
}

/// <summary>
/// The agent's own tools (ADR 0063): reading, searching and problems always; editing, commands and builds only in a
/// trusted workspace. Paths stay under the workspace's folder, and the data boundary (<see cref="AiContext"/>) refuses
/// secrets and ignored files to the agent as it does to the chat.
/// </summary>
public sealed class AiAgentTools(IAiAgentHost host, AiContext context)
{
    public const int OutputLimit = 8000;
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private const int ReadLimit = 60_000;
    private static readonly HashSet<string> s_skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", ".codalus", ".idea" };

    public IReadOnlyList<AiAgentTool> Create()
    {
        var list = new List<AiAgentTool>
        {
            Tool("list_files", "Lists the files and folders under a folder of the workspace (bin, obj and .git are left out).",
                """{"type":"object","properties":{"path":{"type":"string","description":"A folder relative to the workspace; empty for its root."},"recursive":{"type":"boolean"}}}""",
                ListFilesAsync),
            Tool("read_file", "Reads a file of the workspace, with line numbers.",
                """{"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"}},"required":["path"]}""",
                ReadFileAsync),
            Tool("search_text", "Finds a literal text in the workspace's files; returns file:line: text for each match.",
                """{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string","description":"A folder to search in; empty for the whole workspace."}},"required":["query"]}""",
                SearchTextAsync),
            Tool("get_problems", "The errors and warnings of a file, or of every file when no path is given.",
                """{"type":"object","properties":{"path":{"type":"string"}}}""",
                (args, _) => Task.FromResult(GetProblems(args))),
        };
        if (host.Root is not null && host.Trusted)
        {
            list.Add(Tool("edit_file", "Changes a file: replaces old_text (exactly as it is in the file, once) with new_text, or writes the whole file when old_text is omitted. The user reviews the change first.",
                """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","new_text"]}""",
                EditFileAsync));
            list.Add(Tool("run_command", "Runs a shell command in the workspace's folder, once the user approves it; returns its output and exit code. Two minutes at most.",
                """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""",
                RunCommandAsync));
            list.Add(Tool("build", "Builds the solution; returns whether it succeeded and its errors.",
                """{"type":"object","properties":{}}""",
                (_, ct) => host.BuildAsync(ct)));
        }

        return list;
    }

    private static AiAgentTool Tool(string name, string description, string schema, Func<JsonObject, CancellationToken, Task<string>> invoke) =>
        new(new AiToolDto(name, description, schema), invoke);

    private static string? String(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue value ? value.TryGetValue<int>(out var number) ? number : value.TryGetValue<double>(out var d) ? (int)d : null : null;

    /// <summary>A path of the workspace, or why it isn't one.</summary>
    private (string? Path, string? Error) Resolve(string? relative)
    {
        if (host.Root is not { } root)
        {
            return (null, "No workspace is open.");
        }

        var full = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));
        var back = Path.GetRelativePath(root, full);
        return back.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(back)
            ? (null, $"{relative} is outside the workspace.")
            : (full, null);
    }

    private string Relative(string path) => Path.GetRelativePath(host.Root!, path).Replace('\\', '/');

    private static bool Skipped(string name) => s_skipped.Contains(name);

    private Task<string> ListFilesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var (folder, error) = Resolve(String(args, "path"));
        if (folder is null)
        {
            return Task.FromResult(error!);
        }

        if (!Directory.Exists(folder))
        {
            return Task.FromResult($"{String(args, "path")} isn't a folder.");
        }

        var recursive = args["recursive"] is JsonValue r && r.TryGetValue<bool>(out var b) && b;
        var lines = new List<string>();
        void Walk(string dir, int depth)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lines.Count >= 500)
                {
                    return;
                }

                if (entry is DirectoryInfo directory)
                {
                    if (Skipped(directory.Name))
                    {
                        continue;
                    }

                    lines.Add(Relative(directory.FullName) + "/");
                    if (recursive && depth < 8)
                    {
                        Walk(directory.FullName, depth + 1);
                    }
                }
                else if (!AiContext.IsSecret(entry.Name))
                {
                    lines.Add(Relative(entry.FullName));
                }
            }
        }

        Walk(folder, 0);
        return Task.FromResult(lines.Count == 0 ? "(empty)" : string.Join('\n', lines) + (lines.Count >= 500 ? "\n(more not listed)" : string.Empty));
    }

    private async Task<string> ReadFileAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var (path, error) = Resolve(String(args, "path"));
        if (path is null)
        {
            return error!;
        }

        if (await context.RefusalAsync(path, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var text = host.OpenText(path) ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null);
        if (text is null)
        {
            return $"{String(args, "path")} doesn't exist.";
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Math.Clamp(Int(args, "start_line") ?? 1, 1, Math.Max(1, lines.Length));
        var end = Math.Clamp(Int(args, "end_line") ?? lines.Length, start, lines.Length);
        var result = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            if (result.Length > ReadLimit)
            {
                result.Append(System.Globalization.CultureInfo.InvariantCulture, $"(cut at line {i - 1} of {lines.Length}: read the rest with start_line)\n");
                break;
            }

            result.Append(i).Append(": ").Append(lines[i - 1]).Append('\n');
        }

        return result.ToString();
    }

    private async Task<string> SearchTextAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var query = String(args, "query");
        if (string.IsNullOrEmpty(query))
        {
            return "Give a query.";
        }

        var (folder, error) = Resolve(String(args, "path"));
        if (folder is null)
        {
            return error!;
        }

        var matches = new List<string>();
        var refusedFiles = 0;
        foreach (var file in Files(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= 100)
            {
                break;
            }

            string text;
            try
            {
                if (new FileInfo(file).Length > 2_000_000)
                {
                    continue;
                }

                text = host.OpenText(file) ?? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            if (!text.Contains(query, StringComparison.Ordinal) || text.Contains('\0', StringComparison.Ordinal))
            {
                continue;
            }

            if (await context.RefusalAsync(file, cancellationToken).ConfigureAwait(false) is not null)
            {
                refusedFiles++;
                continue;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && matches.Count < 100; i++)
            {
                if (lines[i].Contains(query, StringComparison.Ordinal))
                {
                    var line = lines[i].Trim();
                    matches.Add($"{Relative(file)}:{i + 1}: {(line.Length > 200 ? line[..200] + "…" : line)}");
                }
            }
        }

        var note = refusedFiles > 0 ? $"\n({refusedFiles} matching file(s) are secret or ignored, and not shown.)" : string.Empty;
        return (matches.Count == 0 ? "No matches." : string.Join('\n', matches)) + note;
    }

    private static IEnumerable<string> Files(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var dir))
        {
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                dirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.Where(f => !AiContext.IsSecret(f)))
            {
                yield return file;
            }

            foreach (var sub in dirs.Where(d => !Skipped(Path.GetFileName(d))).Reverse())
            {
                pending.Push(sub);
            }
        }
    }

    private string GetProblems(JsonObject args)
    {
        if (String(args, "path") is not { Length: > 0 } relative)
        {
            return host.Problems(null) is { Length: > 0 } all ? all : "No problems.";
        }

        var (path, error) = Resolve(relative);
        return path is null ? error! : host.Problems(path) is { Length: > 0 } problems ? problems : "No problems in " + relative + ".";
    }

    private async Task<string> EditFileAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var relative = String(args, "path");
        var (path, error) = Resolve(relative);
        if (path is null)
        {
            return error!;
        }

        if (await context.RefusalAsync(path, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var newText = String(args, "new_text") ?? string.Empty;
        var oldText = String(args, "old_text");
        if (oldText is { Length: > 0 })
        {
            var current = host.OpenText(path) ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null);
            if (current is null)
            {
                return $"{relative} doesn't exist.";
            }

            // Files may use CRLF while the model quotes with LF.
            if (!current.Contains(oldText, StringComparison.Ordinal) && current.Contains("\r\n", StringComparison.Ordinal))
            {
                oldText = oldText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                newText = newText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            }

            var first = current.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0)
            {
                return $"old_text wasn't found in {relative}: read the file again and quote it exactly.";
            }

            if (current.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
            {
                return $"old_text appears more than once in {relative}: quote more of it so it's unique.";
            }
        }

        return await host.ProposeEditAsync(path, oldText is { Length: > 0 } ? oldText : null, newText, cancellationToken).ConfigureAwait(false)
            ? $"The user applied the edit to {relative}."
            : $"The user rejected the edit to {relative}.";
    }

    private async Task<string> RunCommandAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (String(args, "command") is not { Length: > 0 } command)
        {
            return "Give a command.";
        }

        if (!await host.ApproveCommandAsync(command, cancellationToken).ConfigureAwait(false))
        {
            return "The user didn't allow the command to run.";
        }

        return await RunAsync(command, host.Root!, CommandTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="command"/> through the system's shell; returns its output (cut) and exit code.</summary>
    internal static async Task<string> RunAsync(string command, string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = folder;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.CreateNoWindow = true;
        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.Append(line).Append('\n');
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return "The command couldn't start: " + ex.Message;
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        string ending;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            ending = $"Exit code {process.ExitCode}.";
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            ending = $"Stopped after {timeout.TotalMinutes:0} minutes.";
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        return Cut(text) + ending;
    }

    /// <summary>The start and the end of a long output, where the command and its errors are.</summary>
    internal static string Cut(string text) =>
        text.Length <= OutputLimit ? text : text[..2000] + $"\n… ({text.Length - OutputLimit} characters left out) …\n" + text[^(OutputLimit - 2000)..];
}
