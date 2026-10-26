using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Workbench.Ai;

namespace Codalus.Workbench.Feedback;

/// <summary>What Help → Report a Problem and Suggest a Feature open (M51): the repository's bug report or feature request form.</summary>
public enum IssueKind
{
    Problem,
    Feature,
}

/// <summary>
/// The facts a report attaches about this installation: the IDE's version (<c>codalus --version</c>'s), the OS (the bug
/// report form's dropdown option and the OS's own description), its architecture, the .NET runtime and, when it was
/// found, the .NET SDK.
/// </summary>
public sealed record IssueEnvironment(string Version, PlatformKind Platform, string OsDescription, string Architecture, string Runtime)
{
    /// <summary>The .NET SDK <c>dotnet --version</c> named, or null while it isn't known.</summary>
    public string? Sdk { get; init; }

    /// <summary>The bug report form's <c>os</c> option: Windows, macOS or Linux.</summary>
    public string OsName => Platform switch
    {
        PlatformKind.Windows => "Windows",
        PlatformKind.MacOS => "macOS",
        _ => "Linux",
    };

    /// <summary>This process's environment, without the SDK (finding it runs <c>dotnet</c>).</summary>
    public static IssueEnvironment Current(string version) => new(
        version,
        PlatformInfo.Current,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
}

/// <summary>A report as the dialog has it: the user's text and what is attached.</summary>
public sealed record IssueDraft(IssueKind Kind, string Title, string Description, string Details, IssueEnvironment Environment)
{
    /// <summary>The redacted tail of the IDE's log, attached to a problem when its box is checked; null otherwise.</summary>
    public string? Logs { get; init; }
}

/// <summary>The new issue's address, and whether the logs were left out of it, to be saved to a file and attached.</summary>
public sealed record IssueLink(Uri Url, bool LogsInFile)
{
    /// <summary>The user's text was cut to keep the address under <see cref="IssueReport.MaxUrlLength"/>.</summary>
    public bool Shortened { get; init; }
}

/// <summary>
/// Report a Problem and Suggest a Feature (M51, the maintainer's decision of 2026-10-24): a new issue on the repository,
/// prefilled through the issue forms' query parameters (<c>template</c>, <c>title</c> and each field by its id) and
/// opened in the browser for the user to submit. Nothing is sent from here. The attachments are redacted
/// (<see cref="SecretRedactor"/>, and the home folder as <c>~</c>); logs too long for the address are left for a file the
/// user attaches, and a line in the form's logs field says so.
/// </summary>
public static class IssueReport
{
    public const string Repository = "stavroskasidis/codalus-ide";

    public const string NewIssueUrl = "https://github.com/" + Repository + "/issues/new";

    /// <summary>The longest address opened: browsers and GitHub take about 8 KB; a longer one could be cut or refused.</summary>
    public const int MaxUrlLength = 8000;

    /// <summary>How many of the log's last lines a problem attaches.</summary>
    public const int LogLines = 200;

    /// <summary>The issue form (<c>.github/ISSUE_TEMPLATE</c>) for <paramref name="kind"/>.</summary>
    public static string Template(IssueKind kind) => kind == IssueKind.Problem ? "bug_report.yml" : "feature_request.yml";

    /// <summary>The label the form gives the issue.</summary>
    public static string Label(IssueKind kind) => kind == IssueKind.Problem ? "bug" : "enhancement";

    // ------------------------------------------------------------------ attachments

    /// <summary>
    /// <paramref name="text"/> with its secrets replaced (<see cref="SecretRedactor"/>) and the home folder written as
    /// <c>~</c> (with either slash, and ignoring case where the file system does).
    /// </summary>
    public static string Redact(string text, string? home, bool? ignoreCase = null) =>
        ReplaceHome(SecretRedactor.Redact(text).Text, home, ignoreCase ?? !OperatingSystem.IsLinux());

    /// <summary>The home folder in <paramref name="text"/> as <c>~</c>: a whole path segment only, so <c>/home/al</c> leaves <c>/home/alex</c>.</summary>
    public static string ReplaceHome(string text, string? home, bool ignoreCase)
    {
        var trimmed = home?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(text) || trimmed is not { Length: >= 3 })
        {
            return text; // "/" or "C:" would be every path
        }

        var variants = new[] { trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\') }.Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length);
        var pattern = "(?:" + string.Join('|', variants.Select(Regex.Escape)) + @")(?![\w.\-])";
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return Regex.Replace(text, pattern, "~", options, TimeSpan.FromSeconds(2));
    }

    /// <summary>The last <paramref name="lines"/> lines of <paramref name="log"/>, redacted; null when the log is empty.</summary>
    public static string? LogTail(string? log, string? home, int lines = LogLines, bool? ignoreCase = null)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return null;
        }

        // From the end: a long session's log is megabytes, and only its last lines are wanted.
        var text = log.TrimEnd('\r', '\n');
        var start = 0;
        var at = text.Length;
        for (var i = 0; i < lines; i++)
        {
            var newline = at > 0 ? text.LastIndexOf('\n', at - 1) : -1;
            if (newline < 0)
            {
                start = 0;
                break;
            }

            at = newline;
            start = newline + 1;
        }

        return Redact(text[start..].ReplaceLineEndings("\n"), home, ignoreCase);
    }

    /// <summary>The environment as the report shows it, a line each.</summary>
    public static string EnvironmentText(IssueEnvironment environment) =>
        $"Codalus IDE: {environment.Version}\n"
        + $"OS: {environment.OsName}, {environment.OsDescription} ({environment.Architecture})\n"
        + $".NET runtime: {environment.Runtime}\n"
        + $".NET SDK: {environment.Sdk ?? "not known"}";

    /// <summary>Everything attached besides the user's own text, exactly as it is sent: the environment, then the logs if any.</summary>
    public static string AttachmentsText(IssueDraft draft) =>
        draft.Kind == IssueKind.Feature ? FeatureEnvironment(draft.Environment)
        : draft.Logs is not { Length: > 0 } logs ? EnvironmentText(draft.Environment)
        : EnvironmentText(draft.Environment) + "\n\n" + LogHeader(logs) + "\n" + logs;

    private static string LogHeader(string logs) =>
        $"--- Codalus IDE log, the last {logs.Split('\n').Length} lines (secrets and the home folder redacted) ---";

    /// <summary>A feature request's one line about the installation (its form has no fields for it).</summary>
    private static string FeatureEnvironment(IssueEnvironment environment) =>
        $"Codalus IDE {environment.Version} on {environment.OsName} ({environment.OsDescription}, {environment.Architecture})";

    // ------------------------------------------------------------------ the address

    /// <summary>The form's fields for <paramref name="draft"/>, in order; <paramref name="logsNote"/> replaces the logs when given.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(IssueDraft draft, string? logsNote = null)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("template", Template(draft.Kind)),
            new("labels", Label(draft.Kind)),
            new("title", draft.Title),
        };
        if (draft.Kind == IssueKind.Problem)
        {
            fields.Add(new("what", draft.Description));
            fields.Add(new("repro", draft.Details));
            fields.Add(new("version", draft.Environment.Version));
            fields.Add(new("os", draft.Environment.OsName));
            if (draft.Environment.Sdk is { Length: > 0 } sdk)
            {
                fields.Add(new("sdk", sdk));
            }

            var attached = logsNote is null ? AttachmentsText(draft) : EnvironmentText(draft.Environment) + "\n\n" + logsNote;
            fields.Add(new("logs", attached));
        }
        else
        {
            fields.Add(new("problem", draft.Description.Length > 0 ? draft.Description + "\n\n" + FeatureEnvironment(draft.Environment) : FeatureEnvironment(draft.Environment)));
            fields.Add(new("proposal", draft.Details));
        }

        return [.. fields.Where(f => f.Key is "template" or "labels" or "title" || f.Value.Length > 0)];
    }

    /// <summary>The new issue's address with <paramref name="fields"/> as its query, each value escaped.</summary>
    public static string Url(IEnumerable<KeyValuePair<string, string>> fields) =>
        NewIssueUrl + "?" + string.Join('&', fields.Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value.ReplaceLineEndings("\n"))));

    /// <summary>The logs field's line when the logs are in a file (<paramref name="fileName"/>, saved in the logs folder).</summary>
    public static string LogsNote(string fileName) =>
        $"The log was too long for the link. Codalus IDE saved it as {fileName} (in its logs folder): drag that file here to attach it.";

    /// <summary>The name of the file the logs go to when they don't fit.</summary>
    public static string LogFileName(DateTime now) => $"codalus-log-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt";

    /// <summary>
    /// The new issue's address, at most <see cref="MaxUrlLength"/> characters: the logs move to <paramref name="logFileName"/>
    /// (with <see cref="LogsNote"/> in their place) when they don't fit, and then the user's longest text is cut if it
    /// still doesn't (Copy has all of it).
    /// </summary>
    public static IssueLink Build(IssueDraft draft, string logFileName)
    {
        var url = Url(Fields(draft));
        if (url.Length <= MaxUrlLength)
        {
            return new IssueLink(new Uri(url), LogsInFile: false);
        }

        var logsInFile = draft.Kind == IssueKind.Problem && draft.Logs is { Length: > 0 };
        var note = logsInFile ? LogsNote(logFileName) : null;
        url = Url(Fields(draft, note));
        if (url.Length <= MaxUrlLength)
        {
            return new IssueLink(new Uri(url), logsInFile);
        }

        var shortened = draft;
        foreach (var field in new[] { "details", "description", "title" })
        {
            shortened = Shorten(shortened, field, note);
            url = Url(Fields(shortened, note));
            if (url.Length <= MaxUrlLength)
            {
                break;
            }
        }

        return new IssueLink(new Uri(url), logsInFile) { Shortened = true };
    }

    private const string Cut = "\n\n[…] (cut to fit the link)";

    /// <summary><paramref name="draft"/> with <paramref name="field"/> cut to the longest that fits (none when nothing does).</summary>
    private static IssueDraft Shorten(IssueDraft draft, string field, string? note)
    {
        var text = Get(draft);
        if (text.Length == 0)
        {
            return draft;
        }

        // The longest prefix that fits, by bisection (an escaped character's length varies).
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Url(Fields(With(draft, Prefix(text, mid)), note)).Length <= MaxUrlLength)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return With(draft, low == 0 ? string.Empty : Prefix(text, low));

        string Get(IssueDraft d) => field switch
        {
            "details" => d.Details,
            "description" => d.Description,
            _ => d.Title,
        };

        IssueDraft With(IssueDraft d, string value) => field switch
        {
            "details" => d with { Details = value },
            "description" => d with { Description = value },
            _ => d with { Title = value },
        };
    }

    /// <summary>The first <paramref name="length"/> characters and the cut's marker, never splitting a surrogate pair.</summary>
    private static string Prefix(string text, int length)
    {
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length] + Cut;
    }

    // ------------------------------------------------------------------ the file and the clipboard

    /// <summary>Writes the attachments (the environment and the logs) to <paramref name="fileName"/> in <paramref name="folder"/>; returns its path.</summary>
    public static string SaveLogs(string folder, IssueDraft draft, string fileName)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, AttachmentsText(draft).ReplaceLineEndings() + Environment.NewLine);
        return path;
    }

    /// <summary>The whole report as Markdown, as the form's sections would show it (Copy): nothing is cut.</summary>
    public static string Markdown(IssueDraft draft)
    {
        var text = new StringBuilder().Append("# ").Append(draft.Title).Append("\n\n");
        if (draft.Kind == IssueKind.Problem)
        {
            Section("What happened", draft.Description);
            Section("Steps to reproduce", draft.Details);
            Section("Codalus IDE version", draft.Environment.Version);
            Section("Operating system", draft.Environment.OsName);
            Section(".NET SDK version", draft.Environment.Sdk ?? string.Empty);
            Section("Logs and crash reports", "```text\n" + AttachmentsText(draft) + "\n```");
        }
        else
        {
            Section("The problem", draft.Description + "\n\n" + FeatureEnvironment(draft.Environment));
            Section("Proposal", draft.Details);
        }

        return text.ToString().TrimEnd() + "\n";

        void Section(string heading, string body)
        {
            if (body.Trim().Length > 0)
            {
                text.Append("### ").Append(heading).Append("\n\n").Append(body.Trim()).Append("\n\n");
            }
        }
    }
}
