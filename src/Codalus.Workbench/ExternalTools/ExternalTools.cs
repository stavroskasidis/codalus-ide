using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Workbench.Output;

namespace Codalus.Workbench.ExternalTools;

/// <summary>
/// A user-defined command of the Tools menu (ADR 0033). <see cref="Arguments"/> and <see cref="InitialDirectory"/> may use
/// macros (<c>$(ItemPath)</c>…). With <see cref="UseOutputWindow"/> its output goes to the Output window; with
/// <see cref="PromptForArguments"/> the arguments are shown for editing first. <see cref="Id"/> is stable, so the tool's
/// command (<c>externalTools.run.&lt;id&gt;</c>) can have a key binding.
/// </summary>
public sealed record ExternalTool(string Id, string Title, string Command, string Arguments = "", string InitialDirectory = "", bool UseOutputWindow = true, bool PromptForArguments = false)
{
    public const string CommandPrefix = "externalTools.run.";

    public string CommandId => CommandPrefix + Id;

    /// <summary>A new id from a title: lower-case letters and digits, made unique among <paramref name="existing"/>.</summary>
    public static string NewId(string title, IEnumerable<string> existing)
    {
        var stem = new string([.. title.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        stem = stem.Length == 0 ? "tool" : stem;
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        var id = stem;
        for (var i = 2; taken.Contains(id); i++)
        {
            id = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return id;
    }
}

/// <summary>A macro of external tools' arguments and initial directory, for the editor's macro list.</summary>
public sealed record ToolMacro(string Name, string Description);

/// <summary>External tools in <c>&lt;user config&gt;/externalTools.json</c>, and their macros.</summary>
public static partial class ExternalToolStore
{
    public const string FileName = "externalTools.json";

    /// <summary>The macros the shell fills in (plugins add more, plugin API 0.2.8).</summary>
    public static IReadOnlyList<ToolMacro> Macros { get; } =
    [
        new("ItemPath", "The full path of the current file (the active document, or the Solution Explorer's selection)."),
        new("ItemDir", "The folder of the current file, ending with a separator."),
        new("ItemFileName", "The current file's name without its extension."),
        new("ItemExt", "The current file's extension."),
        new("ProjectDir", "The current project's folder, ending with a separator."),
        new("ProjectFileName", "The current project's file name."),
        new("ProjectPath", "The current project's full path."),
        new("SolutionDir", "The solution's (or the open folder's) folder, ending with a separator."),
        new("SolutionFileName", "The solution's file name."),
        new("TargetPath", "The current project's build output (the assembly)."),
        new("TargetDir", "The folder of the build output, ending with a separator."),
        new("TargetName", "The build output's name without its extension."),
        new("TargetExt", "The build output's extension."),
        new("BinDir", "The current project's bin folder, ending with a separator."),
        new("CurLine", "The caret's line in the active editor (1-based)."),
        new("CurCol", "The caret's column in the active editor (1-based)."),
        new("CurText", "The word at the caret."),
        new("SelText", "The active editor's selected text."),
    ];

    public static IReadOnlyList<ExternalTool> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var tools = new List<ExternalTool>();
            foreach (var item in (root?["tools"] as JsonArray ?? root as JsonArray ?? []).OfType<JsonObject>())
            {
                var title = item["title"]?.GetValue<string>();
                var command = item["command"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var id = item["id"]?.GetValue<string>() is { Length: > 0 } given ? given : ExternalTool.NewId(title, tools.Select(t => t.Id));
                tools.Add(new ExternalTool(
                    id,
                    title,
                    command,
                    item["arguments"]?.GetValue<string>() ?? string.Empty,
                    item["initialDirectory"]?.GetValue<string>() ?? string.Empty,
                    item["useOutputWindow"]?.GetValue<bool>() ?? true,
                    item["promptForArguments"]?.GetValue<bool>() ?? false));
            }

            return [.. tools.DistinctBy(t => t.Id)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            return [];
        }
    }

    public static void Save(string path, IReadOnlyList<ExternalTool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["id"] = tool.Id,
                ["title"] = tool.Title,
                ["command"] = tool.Command,
                ["arguments"] = tool.Arguments,
                ["initialDirectory"] = tool.InitialDirectory,
                ["useOutputWindow"] = tool.UseOutputWindow,
                ["promptForArguments"] = tool.PromptForArguments,
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, new JsonObject { ["tools"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Replaces <c>$(Name)</c> with <paramref name="resolve"/>'s value; a macro it doesn't know stays as written.</summary>
    public static string Expand(string text, Func<string, string?> resolve) =>
        Macro().Replace(text, m => resolve(m.Groups["name"].Value) ?? m.Value);

    /// <summary>The macro names a text uses.</summary>
    public static IReadOnlyList<string> MacrosIn(string text) => [.. Macro().Matches(text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_.]*)\)")]
    private static partial Regex Macro();
}

/// <summary>Runs an external tool (its macros already expanded).</summary>
public static class ExternalToolRunner
{
    /// <summary>
    /// Starts the tool. With an output channel, its output goes there and the task completes with its exit code; without,
    /// it starts on its own (a GUI tool, or a console of its own) and the task completes once it started (null).
    /// </summary>
    public static async Task<int?> RunAsync(string command, string arguments, string? directory, OutputChannel? channel, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = channel is not null,
            RedirectStandardOutput = channel is not null,
            RedirectStandardError = channel is not null,
            StandardOutputEncoding = channel is null ? null : Encoding.UTF8,
            StandardErrorEncoding = channel is null ? null : Encoding.UTF8,
        };
        if (directory is { Length: > 0 } && Directory.Exists(directory))
        {
            start.WorkingDirectory = directory;
        }

        channel?.AppendLine($"> {command} {arguments}".TrimEnd());
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{command} did not start.");
        if (channel is null)
        {
            return null;
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                channel.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                channel.AppendErrorLine(e.Data); // in the error color (M34h)
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessTree.Kill(process);
            throw;
        }

        process.WaitForExit(); // the last lines
        channel.AppendLine($"{Path.GetFileName(command)} exited with code {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        return process.ExitCode;
    }
}
