using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Ai;

/// <summary>Something a chat message carries: a file, the selection, diagnostics or build errors.</summary>
/// <param name="Kind"><c>selection</c>, <c>file</c>, <c>diagnostics</c> or <c>build</c>.</param>
/// <param name="Label">What the chat shows for it.</param>
/// <param name="Content">The text sent.</param>
/// <param name="Path">The file it comes from, when it does.</param>
public sealed record AiAttachment(string Kind, string Label, string Content, string? Path = null);

/// <summary>
/// What an AI request may carry (M28a, ADR 0062). Only what the user attaches is sent, and never a secret or an ignored
/// file: files that <c>.gitignore</c> excludes (asked of git) or that <c>.codalusignore</c> lists (gitignore syntax), and
/// secrets by name (user secrets, <c>.env</c>, keys and certificates, Production settings), whatever the user attaches.
/// </summary>
public sealed partial class AiContext(string? workspaceRoot, Func<string, CancellationToken, Task<bool>>? gitIgnored = null)
{
    /// <summary>What the IDE tells every model first.</summary>
    public const string SystemPrompt =
        "You are the assistant in Codalus IDE, an IDE for .NET. Answer concisely. When you change code, give the complete changed "
        + "code in one fenced block with its language, so the user can apply it; when only part of a file was given, change that part.";

    private readonly Func<string, CancellationToken, Task<bool>> _gitIgnored = gitIgnored ?? GitCheckIgnoreAsync;
    private readonly Lazy<IReadOnlyList<(Regex Pattern, bool Negated)>> _codalusIgnore = new(() => ReadIgnoreFile(workspaceRoot));

    /// <summary>Whether a file is a secret by its name, whoever asks.</summary>
    public static bool IsSecret(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || extension is ".pfx" or ".p12" or ".key" or ".pem" or ".snk" or ".jks" or ".keystore"
            || name.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_rsa", StringComparison.Ordinal) || name.Equals("id_ed25519", StringComparison.Ordinal);
    }

    /// <summary>Why <paramref name="path"/> may not be sent, or null when it may.</summary>
    public async Task<string?> RefusalAsync(string path, CancellationToken cancellationToken)
    {
        if (IsSecret(path))
        {
            return $"{Path.GetFileName(path)} looks like a secret, and secrets are never sent.";
        }

        if (workspaceRoot is not null && Path.GetRelativePath(workspaceRoot, path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal)
            && IsCodalusIgnored(relative.Replace('\\', '/')))
        {
            return $"{Path.GetFileName(path)} is listed in .codalusignore.";
        }

        if (await _gitIgnored(path, cancellationToken).ConfigureAwait(false))
        {
            return $"{Path.GetFileName(path)} is ignored by git (.gitignore), so it isn't sent.";
        }

        return null;
    }

    /// <summary>An attachment of a file's text (or part of it), or why it can't be attached.</summary>
    public async Task<(AiAttachment? Attachment, string? Refused)> AttachAsync(string kind, string path, string content, string label, CancellationToken cancellationToken) =>
        await RefusalAsync(path, cancellationToken).ConfigureAwait(false) is { } refused
            ? (null, refused)
            : (new AiAttachment(kind, label, content, path), null);

    /// <summary>
    /// The messages of a request: the system prompt, the conversation so far, and the new question with its attachments
    /// before it (each file in a fenced block named by its path).
    /// </summary>
    public static IReadOnlyList<AiMessageDto> Messages(IReadOnlyList<AiMessageDto> history, IReadOnlyList<AiAttachment> attachments, string question)
    {
        var text = new StringBuilder();
        foreach (var attachment in attachments)
        {
            text.Append(attachment.Label).Append(":\n```").Append(Language(attachment.Path)).Append('\n').Append(attachment.Content.TrimEnd()).Append("\n```\n\n");
        }

        text.Append(question);
        return [new AiMessageDto("system", SystemPrompt), .. history, new AiMessageDto("user", text.ToString())];
    }

    private static string Language(string? path) => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".razor" or ".cshtml" => "razor", ".xaml" or ".axaml" or ".csproj" or ".props" or ".targets" or ".xml" => "xml",
        ".json" => "json", ".js" => "javascript", ".ts" => "typescript", ".css" => "css", ".html" => "html", ".sql" => "sql",
        ".fs" => "fsharp", ".vb" => "vb", ".md" => "markdown", ".yml" or ".yaml" => "yaml", _ => string.Empty,
    };

    private bool IsCodalusIgnored(string relative)
    {
        var ignored = false;
        foreach (var (pattern, negated) in _codalusIgnore.Value)
        {
            if (pattern.IsMatch(relative))
            {
                ignored = !negated;
            }
        }

        return ignored;
    }

    /// <summary>The workspace's <c>.codalusignore</c> in gitignore syntax: <c>#</c> comments, <c>!</c>, <c>*</c>, <c>**</c>, <c>?</c>, a leading or trailing <c>/</c>.</summary>
    private static List<(Regex, bool)> ReadIgnoreFile(string? root)
    {
        var file = root is null ? null : Path.Combine(root, ".codalusignore");
        if (file is null || !File.Exists(file))
        {
            return [];
        }

        var rules = new List<(Regex, bool)>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var negated = line.StartsWith('!');
            if (negated)
            {
                line = line[1..];
            }

            var anchored = line.StartsWith('/') || line.TrimEnd('/').Contains('/', StringComparison.Ordinal);
            var directory = line.EndsWith('/');
            line = line.Trim('/');
            var body = Regex.Escape(line).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal).Replace(@"\*\*", ".*", StringComparison.Ordinal)
                .Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal);
            var pattern = (anchored ? "^" : "^(?:.*/)?") + body + (directory ? "/" : "(?:$|/)");
            rules.Add((new Regex(pattern, RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase)), negated));
        }

        return rules;
    }

    /// <summary><c>git check-ignore -q</c> in the file's folder: exit code 0 means ignored; anything else (no repository, no git) doesn't.</summary>
    private static async Task<bool> GitCheckIgnoreAsync(string path, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CODALUS_GIT") is { Length: > 0 } git ? git : "git")
            {
                WorkingDirectory = folder, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "check-ignore", "-q", "--", path })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
