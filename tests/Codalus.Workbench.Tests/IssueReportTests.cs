using Codalus.Platform;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Feedback;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Report a Problem and Suggest a Feature (M51): what a report attaches (redacted: secrets and the home folder), the log's
/// limit, and the new issue's address (the form, its field ids and label per kind, the escaping, the length limit with
/// the logs moved to a file and a note in their place, and the user's text cut only as a last resort).
/// </summary>
public sealed class IssueReportTests : IDisposable
{
    private const string Home = @"C:\Users\alex";

    private static readonly IssueEnvironment s_environment =
        new("0.8.0", PlatformKind.Windows, "Microsoft Windows 10.0.26300", "X64", ".NET 11.0.0 (X64)") { Sdk = "11.0.100" };

    private static readonly string[] s_problemFields = ["template", "labels", "title", "what", "repro", "version", "os", "sdk", "logs"];

    private static readonly string[] s_featureFields = ["template", "labels", "title", "problem", "proposal"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-issue-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IssueDraft Problem(string? logs = "[build] restored\n[git] status") =>
        new(IssueKind.Problem, "Build hangs", "The build never ends.", "1. Open BuildAndRun\n2. Build", s_environment) { Logs = logs };

    private static IssueDraft Feature() =>
        new(IssueKind.Feature, "Dark title bar", "The title bar stays light.", "Follow the theme.", s_environment);

    private static Dictionary<string, string> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void Secrets_and_the_home_folder_are_redacted_in_what_is_attached()
    {
        const string Log = "[git] token=ghp_abcdefghijklmnopqrstuvwxyz0123456789 in C:\\Users\\alex\\src\\app\n"
            + "[setup] password=hunter2; settings at c:/users/ALEX/AppData/Roaming/Codalus\n"
            + "[build] C:\\Users\\alexandra\\other stays\n"
            + "[home] C:\\Users\\alex";

        var redacted = IssueReport.Redact(Log, Home, ignoreCase: true);

        Assert.DoesNotContain("ghp_", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.Contains(@"in ~\src\app", redacted, StringComparison.Ordinal);
        Assert.Contains("settings at ~/AppData/Roaming/Codalus", redacted, StringComparison.Ordinal); // the other slash, and case ignored
        Assert.Contains(@"C:\Users\alexandra\other stays", redacted, StringComparison.Ordinal); // a whole path segment only
        Assert.EndsWith("[home] ~", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_home_folder_keeps_its_case_where_the_file_system_does_and_a_root_home_redacts_nothing()
    {
        Assert.Equal("~/src and /HOME/alex/src", IssueReport.ReplaceHome("/home/alex/src and /HOME/alex/src", "/home/alex/", ignoreCase: false));
        Assert.Equal("/usr/lib", IssueReport.ReplaceHome("/usr/lib", "/", ignoreCase: false));
        Assert.Equal("text", IssueReport.ReplaceHome("text", null, ignoreCase: false));
    }

    [Fact]
    public void A_problem_attaches_the_logs_last_lines_and_an_empty_log_attaches_none()
    {
        var log = string.Join("\r\n", Enumerable.Range(1, 500).Select(i => $"line {i}")) + "\r\n";

        var tail = IssueReport.LogTail(log, Home)!;

        var lines = tail.Split('\n');
        Assert.Equal(IssueReport.LogLines, lines.Length);
        Assert.Equal("line 301", lines[0]);
        Assert.Equal("line 500", lines[^1]);
        Assert.Equal("a\nb", IssueReport.LogTail("x\na\nb", Home, lines: 2));
        Assert.Null(IssueReport.LogTail("  \n", Home));
        Assert.Null(IssueReport.LogTail(null, Home));
    }

    [Fact]
    public void The_attachments_are_the_version_os_runtime_sdk_and_the_logs()
    {
        var text = IssueReport.AttachmentsText(Problem());

        Assert.Contains("Codalus IDE: 0.8.0", text, StringComparison.Ordinal);
        Assert.Contains("OS: Windows, Microsoft Windows 10.0.26300 (X64)", text, StringComparison.Ordinal);
        Assert.Contains(".NET runtime: .NET 11.0.0 (X64)", text, StringComparison.Ordinal);
        Assert.Contains(".NET SDK: 11.0.100", text, StringComparison.Ordinal);
        Assert.Contains("the last 2 lines", text, StringComparison.Ordinal);
        Assert.EndsWith("[build] restored\n[git] status", text, StringComparison.Ordinal);

        Assert.DoesNotContain("--- Codalus IDE log", IssueReport.AttachmentsText(Problem(logs: null)), StringComparison.Ordinal);
        Assert.Contains(".NET SDK: not known", IssueReport.AttachmentsText(Problem() with { Environment = s_environment with { Sdk = null } }), StringComparison.Ordinal);

        // A feature request attaches only the version and the OS.
        Assert.Equal("Codalus IDE 0.8.0 on Windows (Microsoft Windows 10.0.26300, X64)", IssueReport.AttachmentsText(Feature()));
    }

    [Fact]
    public void A_problem_opens_the_bug_report_form_with_each_field_by_its_id()
    {
        var link = IssueReport.Build(Problem(), "codalus-log.txt");

        Assert.False(link.LogsInFile);
        Assert.StartsWith("https://github.com/stavroskasidis/codalus-ide/issues/new?template=bug_report.yml&labels=bug&title=", link.Url.AbsoluteUri, StringComparison.Ordinal);
        var query = Query(link.Url);
        Assert.Equal(s_problemFields, query.Keys);
        Assert.Equal("Build hangs", query["title"]);
        Assert.Equal("The build never ends.", query["what"]);
        Assert.Equal("1. Open BuildAndRun\n2. Build", query["repro"]);
        Assert.Equal("0.8.0", query["version"]);
        Assert.Equal("Windows", query["os"]); // the form's dropdown option
        Assert.Equal("11.0.100", query["sdk"]);
        Assert.Equal(IssueReport.AttachmentsText(Problem()), query["logs"]);

        // macOS and Linux are the dropdown's other options; an unknown SDK leaves its field out.
        Assert.Equal("macOS", Query(IssueReport.Build(Problem() with { Environment = s_environment with { Platform = PlatformKind.MacOS } }, "x").Url)["os"]);
        var linux = Query(IssueReport.Build(Problem() with { Environment = s_environment with { Platform = PlatformKind.Linux, Sdk = null } }, "x").Url);
        Assert.Equal("Linux", linux["os"]);
        Assert.False(linux.ContainsKey("sdk"));
    }

    [Fact]
    public void A_feature_opens_the_feature_request_form_with_the_version_and_os_under_the_problem()
    {
        var link = IssueReport.Build(Feature(), "codalus-log.txt");

        var query = Query(link.Url);
        Assert.Equal(s_featureFields, query.Keys);
        Assert.Equal("feature_request.yml", query["template"]);
        Assert.Equal("enhancement", query["labels"]);
        Assert.Equal("The title bar stays light.\n\nCodalus IDE 0.8.0 on Windows (Microsoft Windows 10.0.26300, X64)", query["problem"]);
        Assert.Equal("Follow the theme.", query["proposal"]);
        Assert.DoesNotContain("proposal", Query(IssueReport.Build(Feature() with { Details = "" }, "x").Url).Keys);
    }

    [Fact]
    public void Values_are_escaped_and_line_endings_are_newlines()
    {
        var draft = Problem() with { Title = "a&b=c #1 ?x+y 100% é", Description = "first\r\nsecond" };

        var url = IssueReport.Build(draft, "x").Url;

        Assert.Contains("title=a%26b%3Dc%20%231%20%3Fx%2By%20100%25%20%C3%A9&", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("what=first%0Asecond&", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("a&b=c #1 ?x+y 100% é", Query(url)["title"]);
    }

    [Fact]
    public void Logs_too_long_for_the_link_go_to_a_file_with_a_note_in_their_place()
    {
        var logs = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"[build] a long line of the IDE's log number {i}, with some more words"));
        var draft = Problem(logs);

        var link = IssueReport.Build(draft, "codalus-log-20261026-101500.txt");

        Assert.True(link.LogsInFile);
        Assert.False(link.Shortened);
        Assert.True(link.Url.AbsoluteUri.Length <= IssueReport.MaxUrlLength, $"{link.Url.AbsoluteUri.Length} characters");
        var query = Query(link.Url);
        Assert.StartsWith(IssueReport.EnvironmentText(s_environment), query["logs"], StringComparison.Ordinal);
        Assert.EndsWith(IssueReport.LogsNote("codalus-log-20261026-101500.txt"), query["logs"], StringComparison.Ordinal);
        Assert.Contains("codalus-log-20261026-101500.txt", query["logs"], StringComparison.Ordinal);
        Assert.Equal("The build never ends.", query["what"]); // the user's text stays whole

        // The file has everything the link left out.
        var path = IssueReport.SaveLogs(_root, draft, "codalus-log-20261026-101500.txt");
        Assert.Equal(Path.Combine(_root, "codalus-log-20261026-101500.txt"), path);
        var saved = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.Contains("number 1,", saved, StringComparison.Ordinal);
        Assert.Contains("number 200,", saved, StringComparison.Ordinal);
        Assert.StartsWith("Codalus IDE: 0.8.0", saved, StringComparison.Ordinal);
        Assert.Equal("codalus-log-20261026-101500.txt", IssueReport.LogFileName(new DateTime(2026, 10, 26, 10, 15, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void Text_too_long_even_without_the_logs_is_cut_to_fit_and_copy_keeps_all_of_it()
    {
        var steps = string.Concat(Enumerable.Repeat("Click the button, then wait for the window — ", 400));
        var draft = Problem(logs: null) with { Details = steps };

        var link = IssueReport.Build(draft, "x");

        Assert.True(link.Shortened);
        Assert.False(link.LogsInFile);
        Assert.True(link.Url.AbsoluteUri.Length <= IssueReport.MaxUrlLength);
        var repro = Query(link.Url)["repro"];
        Assert.StartsWith("Click the button", repro, StringComparison.Ordinal);
        Assert.EndsWith("(cut to fit the link)", repro, StringComparison.Ordinal);
        Assert.Equal("The build never ends.", Query(link.Url)["what"]);

        var markdown = IssueReport.Markdown(draft);
        Assert.Contains(steps.Trim(), markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_markdown_has_the_forms_sections()
    {
        var markdown = IssueReport.Markdown(Problem());

        Assert.StartsWith("# Build hangs\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### What happened\n\nThe build never ends.", markdown, StringComparison.Ordinal);
        Assert.Contains("### Steps to reproduce\n\n1. Open BuildAndRun", markdown, StringComparison.Ordinal);
        Assert.Contains("### Operating system\n\nWindows", markdown, StringComparison.Ordinal);
        Assert.Contains("```text\nCodalus IDE: 0.8.0", markdown, StringComparison.Ordinal);
        Assert.Contains("### Proposal\n\nFollow the theme.", IssueReport.Markdown(Feature()), StringComparison.Ordinal);
    }
}
