using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Feedback;

namespace Codalus.Shell.Tests;

/// <summary>
/// Help → Report a Problem and Suggest a Feature (M51), headless, with the browser and the file manager faked: the
/// commands are on the Help menu and in the palette; the dialog shows exactly what is attached (the version, the OS, the
/// runtime, the SDK and the log's last lines, redacted); Open in Browser stays off until there is a title and a
/// description, then opens the prefilled form and closes; logs too long for the link go to a file that is shown; a
/// feature request attaches only the version and the OS; Escape cancels. Nothing leaves the machine.
/// </summary>
public sealed class FeedbackDialogTests
{
    private const string Secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly string[] s_commands = ["help.reportProblem", "help.suggestFeature"];

    private static readonly string[] s_osOptions = ["Windows", "macOS", "Linux"];

    private static readonly string[] s_buttons = ["Open in Browser", "Copy", "Cancel"];

    private sealed record Opened(List<Uri> Urls, List<string> Revealed);

    /// <summary>The browser, the file manager and <c>dotnet --version</c> replaced.</summary>
    private static Opened Fake(ShellFixture f)
    {
        var opened = new Opened([], []);
        f.Shell.OpenExternalUrl = opened.Urls.Add;
        f.Shell.RevealPath = opened.Revealed.Add;
        f.Shell.FindSdkVersion = _ => Task.FromResult<string?>("11.0.100-test");
        return opened;
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static async Task<FeedbackView> OpenAsync(ShellFixture f, string command)
    {
        await f.RunAsync(command);
        await ShellFixture.PumpUntil(() => f.Shell.Feedback.IsOpen && f.Window.GetVisualDescendants().OfType<FeedbackView>().Any(v => v.IsEffectivelyVisible));
        return f.Window.GetVisualDescendants().OfType<FeedbackView>().Single();
    }

    private static Button FooterButton(FeedbackView view, string label) =>
        Named<ItemsControl>(view, "FooterButtons").GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == label);

    /// <summary>Clicks a command button as a pointer would: only when it's enabled.</summary>
    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"{button.Content} is disabled");
        button.Command!.Execute(button.CommandParameter);
    }

    private static Dictionary<string, string> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [AvaloniaFact]
    public async Task The_commands_are_on_the_Help_menu_after_About_and_in_the_palette()
    {
        await using var f = ShellFixture.Create();

        var help = f.Shell.Workbench.Menus.GetItems("menuBar/help").Select(i => i.Command).ToList();
        var about = help.IndexOf("help.about");
        Assert.True(about >= 0);
        Assert.Equal(s_commands, help.Skip(about + 1).Take(2));
        Assert.True(help.IndexOf("update.check") > help.IndexOf("help.suggestFeature"));
        foreach (var id in s_commands)
        {
            Assert.True(f.Shell.Workbench.Commands.Descriptors.Single(d => d.Id == id).ShowInPalette, id);
        }

        Assert.Equal("Report a Problem…", f.Shell.Workbench.Commands.Descriptors.Single(d => d.Id == "help.reportProblem").Title);
    }

    [AvaloniaFact]
    public async Task Report_a_Problem_shows_what_is_attached_redacted_and_opens_the_prefilled_bug_report()
    {
        await using var f = ShellFixture.Create();
        var opened = Fake(f);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        f.Shell.Workbench.Log.AppendLine($"[git] push with token={Secret} from {Path.Combine(home, "src", "app")}");
        f.Shell.Workbench.Log.AppendLine("[build] the last line of the log");

        var view = await OpenAsync(f, "help.reportProblem");
        var dialog = f.Shell.Feedback;
        await ShellFixture.PumpUntil(() => dialog.Preview.Contains("11.0.100-test", StringComparison.Ordinal), describe: () => dialog.Preview);

        Assert.Equal("Report a Problem", Named<TextBlock>(view, "TitleText").Text);
        var preview = Named<SelectableTextBlock>(view, "PreviewText").Text!;
        Assert.Contains($"Codalus IDE: {ShellInfo.Version}", preview, StringComparison.Ordinal);
        Assert.Contains(".NET runtime: ", preview, StringComparison.Ordinal);
        Assert.Contains(".NET SDK: 11.0.100-test", preview, StringComparison.Ordinal);
        Assert.Contains("[build] the last line of the log", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, preview, StringComparison.Ordinal);
        Assert.Contains("token=" + SecretRedactor.Placeholder, preview, StringComparison.Ordinal);
        Assert.DoesNotContain(home, preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("~", "src", "app"), preview, StringComparison.Ordinal);
        var include = Named<CheckBox>(view, "IncludeLogs");
        Assert.True(include.IsEffectivelyVisible);
        Assert.True(include.IsChecked);
        Assert.False(dialog.HasNote);

        // Open in Browser waits for a title and a description.
        var open = FooterButton(view, "Open in Browser");
        Assert.False(open.IsEffectivelyEnabled);
        Named<TextBox>(view, "TitleBox").Text = "Push fails";
        ShellFixture.Pump();
        Assert.False(open.IsEffectivelyEnabled);
        Named<TextBox>(view, "DescriptionBox").Text = "Push says the remote rejected it.";
        Named<TextBox>(view, "DetailsBox").Text = "1. Commit\n2. Push";
        ShellFixture.Pump();
        Assert.True(open.IsEffectivelyEnabled);
        Assert.Empty(opened.Urls); // nothing left the machine yet

        Click(open);
        await ShellFixture.PumpUntil(() => !dialog.IsOpen);

        var url = Assert.Single(opened.Urls);
        Assert.StartsWith(IssueReport.NewIssueUrl + "?template=bug_report.yml&labels=bug&title=Push%20fails&", url.AbsoluteUri, StringComparison.Ordinal);
        var query = Query(url);
        Assert.Equal("Push says the remote rejected it.", query["what"]);
        Assert.Equal("1. Commit\n2. Push", query["repro"]);
        Assert.Equal(ShellInfo.Version, query["version"]);
        Assert.Equal("11.0.100-test", query["sdk"]);
        Assert.Contains(query["os"], s_osOptions);
        Assert.Contains("[build] the last line of the log", query["logs"], StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Empty(opened.Revealed);
    }

    [AvaloniaFact]
    public async Task Without_the_logs_box_only_the_environment_is_attached_and_Escape_cancels()
    {
        await using var f = ShellFixture.Create();
        var opened = Fake(f);
        f.Shell.Workbench.Log.AppendLine("[build] a line that stays home");

        var view = await OpenAsync(f, "help.reportProblem");
        var dialog = f.Shell.Feedback;
        Assert.Contains("a line that stays home", dialog.Preview, StringComparison.Ordinal);
        Named<CheckBox>(view, "IncludeLogs").IsChecked = false;
        ShellFixture.Pump();

        Assert.DoesNotContain("a line that stays home", Named<SelectableTextBlock>(view, "PreviewText").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a line that stays home", IssueReport.Build(dialog.Draft, "x").Url.AbsoluteUri, StringComparison.Ordinal);

        Named<TextBox>(view, "TitleBox").Focus();
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => !dialog.IsOpen);
        Assert.Empty(opened.Urls);
    }

    [AvaloniaFact]
    public async Task Logs_too_long_for_the_link_are_saved_to_a_file_that_is_shown_after_the_browser_opens()
    {
        await using var f = ShellFixture.Create();
        var opened = Fake(f);
        for (var i = 1; i <= 300; i++)
        {
            f.Shell.Workbench.Log.AppendLine($"[build] line {i} of a long log, long enough that the link can't carry two hundred of them");
        }

        var view = await OpenAsync(f, "help.reportProblem");
        var dialog = f.Shell.Feedback;
        dialog.IssueTitle = "Slow build";
        dialog.Description = "The build takes minutes.";
        ShellFixture.Pump();

        Assert.True(dialog.HasNote);
        Assert.True(Named<TextBlock>(view, "NoteText").IsEffectivelyVisible);
        Assert.Contains("line 300 of", dialog.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("line 100 of", dialog.Preview, StringComparison.Ordinal); // the last 200 lines only

        Click(FooterButton(view, "Open in Browser"));
        await ShellFixture.PumpUntil(() => !dialog.IsOpen);

        var url = Assert.Single(opened.Urls);
        Assert.True(url.AbsoluteUri.Length <= IssueReport.MaxUrlLength, $"{url.AbsoluteUri.Length} characters");
        var path = Assert.Single(opened.Revealed);
        Assert.Equal(f.Shell.FeedbackFolder, Path.GetDirectoryName(path));
        Assert.StartsWith(f.UserData, path, StringComparison.Ordinal);
        Assert.Contains(IssueReport.LogsNote(Path.GetFileName(path)), Query(url)["logs"], StringComparison.Ordinal);
        var saved = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("line 101 of", saved, StringComparison.Ordinal);
        Assert.Contains("line 300 of", saved, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Suggest_a_Feature_attaches_only_the_version_and_the_os_and_copy_has_the_whole_report()
    {
        await using var f = ShellFixture.Create();
        var opened = Fake(f);
        f.Shell.Workbench.Log.AppendLine("[build] not for a feature request");

        var view = await OpenAsync(f, "help.suggestFeature");
        var dialog = f.Shell.Feedback;

        Assert.Equal("Suggest a Feature", Named<TextBlock>(view, "TitleText").Text);
        Assert.False(Named<CheckBox>(view, "IncludeLogs").IsEffectivelyVisible);
        Assert.Equal($"Codalus IDE {ShellInfo.Version} on ", dialog.Preview[..($"Codalus IDE {ShellInfo.Version} on ".Length)]);
        Assert.DoesNotContain("not for a feature request", dialog.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', dialog.Preview);

        Named<TextBox>(view, "TitleBox").Text = "Pin tool windows";
        Named<TextBox>(view, "DescriptionBox").Text = "Tool windows close when I click away.";
        Named<TextBox>(view, "DetailsBox").Text = "A pin button on each.";
        ShellFixture.Pump();

        await dialog.CopyAsync();
        await ShellFixture.PumpUntil(() => Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(f.Window.Clipboard!).Result?.StartsWith("# Pin tool windows", StringComparison.Ordinal) == true);
        Assert.True(dialog.IsOpen); // Copy keeps the dialog

        Click(FooterButton(view, "Open in Browser"));
        await ShellFixture.PumpUntil(() => !dialog.IsOpen);
        var query = Query(Assert.Single(opened.Urls));
        Assert.Equal("feature_request.yml", query["template"]);
        Assert.Equal("enhancement", query["labels"]);
        Assert.Equal("Pin tool windows", query["title"]);
        Assert.StartsWith("Tool windows close when I click away.\n\nCodalus IDE ", query["problem"], StringComparison.Ordinal);
        Assert.Equal("A pin button on each.", query["proposal"]);
        Assert.False(query.ContainsKey("logs"));
        Assert.Empty(opened.Revealed);
    }

    [AvaloniaFact]
    public async Task The_buttons_follow_the_os_order_and_a_second_run_returns_the_open_dialog()
    {
        await using var f = ShellFixture.Create();
        Fake(f);

        await OpenAsync(f, "help.reportProblem");
        var dialog = f.Shell.Feedback;
        var labels = dialog.Buttons.Select(b => b.Label).ToList();
        Assert.Equal(DialogViewModel.PrimaryLastOnThisOs ? s_buttons.Reverse() : s_buttons, labels);

        _ = f.Shell.SuggestFeatureAsync();
        Assert.Equal(IssueKind.Problem, dialog.Kind);
        dialog.Cancel();
        Assert.False(dialog.IsOpen);
    }
}
