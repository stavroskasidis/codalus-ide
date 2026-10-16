using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Ai.Agent;

/// <summary>What agent mode needs from the shell: files as the editor has them, previews, approvals, diagnostics and builds.</summary>
public interface IAgentHost
{
    /// <summary>A file's text: the editor's when it is open (unsaved changes included), else the disk's; null when it doesn't exist.</summary>
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Shows a change in the preview (a replaced text, or a new file when <paramref name="oldText"/> is null) and applies it
    /// when the user chooses Apply. True when it was applied.
    /// </summary>
    Task<bool> ProposeChangeAsync(string path, string? oldText, string newText, CancellationToken cancellationToken);

    /// <summary>Asks the user to allow something (a command, an MCP tool call). True when allowed.</summary>
    Task<bool> ApproveAsync(string title, string message, IReadOnlyList<string> details, CancellationToken cancellationToken);

    /// <summary>The Error List's errors and warnings, of one file or all.</summary>
    Task<string> DiagnosticsAsync(string? path, CancellationToken cancellationToken);

    /// <summary>The end of the last build's output.</summary>
    Task<string> BuildOutputAsync(CancellationToken cancellationToken);

    /// <summary>Builds the solution and returns the result and its errors and warnings.</summary>
    Task<string> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>A tool's answer to the model: text, or an error the model can act on.</summary>
public sealed record AgentToolResult(string Text, bool IsError = false, string? Summary = null)
{
    public static AgentToolResult Error(string message) => new(message, IsError: true, Summary: message);
}

/// <summary>A tool that isn't built in (an MCP server's): its definition, and how to call it.</summary>
public sealed record ExternalAgentTool(ChatToolDto Definition, string Source, Func<JsonElement, CancellationToken, Task<string>> Call);

/// <summary>
/// Agent mode's tools (M28c, ADR 0063). Reading tools run without asking but only inside the workspace and never on
/// excluded files, and what they return is redacted; changes go through the preview; commands and external tools ask
/// first. Every call is logged by the session, not here.
/// </summary>
public sealed partial class AgentTools(AiService ai, IAgentHost host, string workspaceRoot, IReadOnlyList<ExternalAgentTool>? external = null)
{
    /// <summary>The most characters a tool returns.</summary>
    public const int MaxResult = 20_000;

    /// <summary>The most matches <c>search_text</c> returns.</summary>
    public const int MaxMatches = 100;

    private readonly string _root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private readonly IReadOnlyList<ExternalAgentTool> _external = external ?? [];

    /// <summary>How long a command may run.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public string Root => _root;

    /// <summary>The tools offered to the model.</summary>
    public IReadOnlyList<ChatToolDto> Definitions =>
    [
        Tool("read_file", "Read a text file of the workspace. Returns its lines numbered from 1.", """{"type":"object","properties":{"path":{"type":"string","description":"The file, relative to the workspace root."}},"required":["path"]}"""),
        Tool("list_files", "List the files and folders of a workspace folder (not ignored ones), up to 2 levels deep.", """{"type":"object","properties":{"path":{"type":"string","description":"The folder, relative to the workspace root; empty for the root."}}}"""),
        Tool("search_text", "Search the workspace's text files for a regular expression. Returns path:line: text for each match.", """{"type":"object","properties":{"pattern":{"type":"string","description":"A .NET regular expression."},"glob":{"type":"string","description":"Only files whose name matches, e.g. *.cs."}},"required":["pattern"]}"""),
        Tool("get_diagnostics", "The errors and warnings the IDE shows, of one file or all.", """{"type":"object","properties":{"path":{"type":"string","description":"A file relative to the workspace root; empty for all."}}}"""),
        Tool("get_build_output", "The end of the last build's output.", """{"type":"object","properties":{}}"""),
        Tool("build", "Build the solution and return the result with its errors and warnings.", """{"type":"object","properties":{}}"""),
        Tool("edit_file", "Replace an exact piece of text in a file once. The user sees the change in a preview and applies or rejects it.", """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string","description":"The exact text to replace; it must occur once."},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}"""),
        Tool("create_file", "Create a new file with the given content. The user sees it in a preview and applies or rejects it.", """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""),
        Tool("run_command", "Run a shell command in a workspace folder, after the user allows it. Returns the exit code and output.", """{"type":"object","properties":{"command":{"type":"string"},"cwd":{"type":"string","description":"The folder, relative to the workspace root; empty for the root."}},"required":["command"]}"""),
        .. _external.Select(t => t.Definition),
    ];

    private static ChatToolDto Tool(string name, string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new ChatToolDto(name, description, document.RootElement.Clone());
    }

    /// <summary>Runs a call. Failures become error results for the model, never exceptions (except cancellation).</summary>
    public async Task<AgentToolResult> RunAsync(ChatToolCallDto call, CancellationToken cancellationToken)
    {
        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments);
            args = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return AgentToolResult.Error("The arguments aren't valid JSON.");
        }

        try
        {
            return call.Name switch
            {
                "read_file" => await ReadFileAsync(Arg(args, "path"), cancellationToken).ConfigureAwait(false),
                "list_files" => await ListFilesAsync(Arg(args, "path"), cancellationToken).ConfigureAwait(false),
                "search_text" => await SearchAsync(Arg(args, "pattern"), Arg(args, "glob"), cancellationToken).ConfigureAwait(false),
                "get_diagnostics" => Text(await host.DiagnosticsAsync(OptionalPath(Arg(args, "path")), cancellationToken).ConfigureAwait(false), "diagnostics"),
                "get_build_output" => Text(await host.BuildOutputAsync(cancellationToken).ConfigureAwait(false), "build output"),
                "build" => Text(await host.BuildAsync(cancellationToken).ConfigureAwait(false), "build"),
                "edit_file" => await EditAsync(Arg(args, "path"), Arg(args, "old_text"), Arg(args, "new_text"), cancellationToken).ConfigureAwait(false),
                "create_file" => await CreateAsync(Arg(args, "path"), Arg(args, "content"), cancellationToken).ConfigureAwait(false),
                "run_command" => await RunCommandAsync(Arg(args, "command"), Arg(args, "cwd"), cancellationToken).ConfigureAwait(false),
                _ when _external.FirstOrDefault(t => t.Definition.Name == call.Name) is { } tool => await ExternalAsync(tool, args, cancellationToken).ConfigureAwait(false),
                _ => AgentToolResult.Error($"There is no tool '{call.Name}'."),
            };
        }
        catch (AgentToolException ex)
        {
            return AgentToolResult.Error(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
        {
            return AgentToolResult.Error(ex.Message);
        }
    }

    // ------------------------------------------------------------------ paths

    /// <summary>A path inside the workspace (relative to it, or absolute under it); anything else is refused.</summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AgentToolException("A path is needed.");
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(full))
        {
            throw new AgentToolException($"'{path}' is outside the workspace.");
        }

        // A link that leads out of the workspace is outside it too.
        for (var dir = full; dir.Length > _root.Length; dir = Path.GetDirectoryName(dir)!)
        {
            var info = new FileInfo(dir);
            if (info.Exists ? info.LinkTarget is not null : new DirectoryInfo(dir) is { Exists: true, LinkTarget: not null })
            {
                var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName : new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is null || !IsInside(Path.GetFullPath(target)))
                {
                    throw new AgentToolException($"'{path}' leads outside the workspace.");
                }
            }
        }

        return full;
    }

    private bool IsInside(string full) =>
        full.Equals(_root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(_root + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private string? OptionalPath(string? path) => string.IsNullOrWhiteSpace(path) ? null : Resolve(path);

    private string Relative(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

    private async Task EnsureAllowedAsync(string full, CancellationToken cancellationToken)
    {
        if (await ai.Exclusions.ExclusionReasonAsync(full, _root, cancellationToken).ConfigureAwait(false) is { } reason)
        {
            throw new AgentToolException($"'{Relative(full)}' is excluded from AI ({reason}).");
        }
    }

    // ------------------------------------------------------------------ reading

    private async Task<AgentToolResult> ReadFileAsync(string? path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        await EnsureAllowedAsync(full, cancellationToken).ConfigureAwait(false);
        if (await host.ReadTextAsync(full, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return AgentToolResult.Error($"'{Relative(full)}' doesn't exist.");
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var numbered = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            numbered.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(lines[i]).Append('\n');
        }

        return Text(numbered.ToString(), Relative(full));
    }

    private async Task<AgentToolResult> ListFilesAsync(string? path, CancellationToken cancellationToken)
    {
        var folder = string.IsNullOrWhiteSpace(path) ? _root : Resolve(path);
        if (!Directory.Exists(folder))
        {
            return AgentToolResult.Error($"'{Relative(folder)}' isn't a folder.");
        }

        var sb = new StringBuilder();
        await ListAsync(folder, 0).ConfigureAwait(false);
        return Text(sb.Length == 0 ? "(empty)" : sb.ToString(), string.IsNullOrWhiteSpace(path) ? "the workspace" : Relative(folder));

        async Task ListAsync(string dir, int depth)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Name is ".git" or ".vs" or "bin" or "obj" or "node_modules" || await ai.Exclusions.IsExcludedAsync(entry.FullName, _root, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // A link is listed, never followed: it may lead outside the workspace.
                var link = entry.LinkTarget is not null;
                sb.Append(' ', depth * 2).Append(entry.Name).Append(entry is DirectoryInfo ? "/" : "").Append(link ? " (link)" : "").Append('\n');
                if (entry is DirectoryInfo && !link && depth < 1 && sb.Length < MaxResult)
                {
                    await ListAsync(entry.FullName, depth + 1).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<AgentToolResult> SearchAsync(string? pattern, string? glob, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return AgentToolResult.Error("A pattern is needed.");
        }

        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var name = glob is { Length: > 0 } ? new Regex("^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase) : null;
        var matches = new StringBuilder();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(file);
            if (relative.Split('/').Any(part => part is ".git" or ".vs" or "bin" or "obj" or "node_modules") || (name is not null && !name.IsMatch(Path.GetFileName(file)))
                || new FileInfo(file).Length > 2_000_000 || await ai.Exclusions.IsExcludedAsync(file, _root, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var text = await host.ReadTextAsync(file, cancellationToken).ConfigureAwait(false) ?? "";
            if (text.Contains('\0', StringComparison.Ordinal))
            {
                continue; // binary
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && count < MaxMatches; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    count++;
                    matches.Append(relative).Append(':').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(lines[i].TrimEnd('\r').Trim()).Append('\n');
                }
            }

            if (count >= MaxMatches)
            {
                matches.Append("… (more matches not shown)\n");
                break;
            }
        }

        return Text(count == 0 ? "No matches." : matches.ToString(), $"'{pattern}'");
    }

    // ------------------------------------------------------------------ changing

    private async Task<AgentToolResult> EditAsync(string? path, string? oldText, string? newText, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        await EnsureAllowedAsync(full, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(oldText) || newText is null)
        {
            return AgentToolResult.Error("old_text and new_text are needed.");
        }

        if (await host.ReadTextAsync(full, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return AgentToolResult.Error($"'{Relative(full)}' doesn't exist: use create_file.");
        }

        // The file may use CRLF while the model wrote LF.
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var find = crlf ? oldText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : oldText;
        var first = text.IndexOf(find, StringComparison.Ordinal);
        if (first < 0)
        {
            return AgentToolResult.Error("old_text wasn't found in the file. Read the file again and copy the text exactly.");
        }

        if (text.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0)
        {
            return AgentToolResult.Error("old_text occurs more than once: include more lines around it.");
        }

        var replacement = crlf ? newText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : newText;
        return await host.ProposeChangeAsync(full, find, replacement, cancellationToken).ConfigureAwait(false)
            ? new AgentToolResult($"Applied the change to {Relative(full)}.", Summary: "applied")
            : new AgentToolResult($"The user rejected the change to {Relative(full)}. Don't make it again; ask what they want instead.", IsError: true, Summary: "rejected");
    }

    private async Task<AgentToolResult> CreateAsync(string? path, string? content, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        await EnsureAllowedAsync(full, cancellationToken).ConfigureAwait(false);
        if (await host.ReadTextAsync(full, cancellationToken).ConfigureAwait(false) is not null)
        {
            return AgentToolResult.Error($"'{Relative(full)}' exists: use edit_file.");
        }

        return await host.ProposeChangeAsync(full, null, content ?? "", cancellationToken).ConfigureAwait(false)
            ? new AgentToolResult($"Created {Relative(full)}.", Summary: "created")
            : new AgentToolResult($"The user rejected creating {Relative(full)}.", IsError: true, Summary: "rejected");
    }

    // ------------------------------------------------------------------ commands and external tools

    private async Task<AgentToolResult> RunCommandAsync(string? command, string? cwd, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return AgentToolResult.Error("A command is needed.");
        }

        var folder = string.IsNullOrWhiteSpace(cwd) ? _root : Resolve(cwd);
        if (!await host.ApproveAsync("Run this command?", "The AI agent wants to run a command in " + (folder == _root ? "the workspace" : Relative(folder)) + ":", [command], cancellationToken).ConfigureAwait(false))
        {
            return new AgentToolResult("The user didn't allow the command. Don't run it again; ask what they want instead.", IsError: true, Summary: "not allowed");
        }

        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = folder;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new AgentToolException("The command couldn't start.");
        process.StandardInput.Close();
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxResult * 2)
                {
                    output.Append(line).Append('\n');
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return AgentToolResult.Error($"The command didn't finish within {CommandTimeout.TotalSeconds:0} seconds and was stopped. Output so far:\n{Cut(output.ToString())}");
        }

        process.WaitForExit(); // the output's end
        var text = string.Create(CultureInfo.InvariantCulture, $"Exit code {process.ExitCode}.\n{output}");
        return new AgentToolResult(Cut(SecretRedactor.Redact(text).Text), process.ExitCode != 0, string.Create(CultureInfo.InvariantCulture, $"exit code {process.ExitCode}"));
    }

    private async Task<AgentToolResult> ExternalAsync(ExternalAgentTool tool, JsonElement args, CancellationToken cancellationToken)
    {
        if (!await host.ApproveAsync($"Call {tool.Definition.Name}?", $"The AI agent wants to call the tool '{tool.Definition.Name}' of the MCP server '{tool.Source}' with:", [args.GetRawText()], cancellationToken).ConfigureAwait(false))
        {
            return new AgentToolResult("The user didn't allow the call.", IsError: true, Summary: "not allowed");
        }

        try
        {
            return Text(await tool.Call(args, cancellationToken).ConfigureAwait(false), tool.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentToolResult.Error($"{tool.Source}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ results

    private static AgentToolResult Text(string text, string summary) => new(Cut(SecretRedactor.Redact(text).Text), Summary: summary);

    private static string Cut(string text) => text.Length > MaxResult ? text[..MaxResult] + "\n[… cut: the rest wasn't sent]" : text;

    private static string? Arg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A tool call the agent can't do: the message goes back to the model.</summary>
public sealed class AgentToolException(string message) : Exception(message);
