using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h: the review of the control set's views (PR #52). Running Services lists without reloading; F5 continues a
/// stopped debugger; the HTTP Response's and the WCF Test Client's splitters; the live regions on the text that changes;
/// the Headers tab's name; the body's tools reachable in a narrow window; the response tree's text selectable and only
/// the selected node plain; the Find Results header trimmed as a whole; the chevrons' names; an overflowed text button
/// named by its label; Send Again from a file that isn't open.
/// </summary>
public sealed class ControlSetViewsReviewTests
{
    private static string Wsdl => Path.Combine(ShellFixture.RepoRoot, "samples", "Wcf", "contracts", "CalculatorService.wsdl");

    private static string Recorded(string name) => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "Wcf", name);

    /// <summary>A loopback service that serves the samples' WSDL and answers Greet and Divide as recorded.</summary>
    private static MockHttpServer WcfService()
    {
        MockHttpServer? server = null;
        server = new MockHttpServer(request => request.Method == "GET"
            ? MockHttpResponse.Text(File.ReadAllText(Wsdl).Replace("http://localhost:5280/", server!.BaseUrl + "/", StringComparison.Ordinal), contentType: "text/xml; charset=utf-8")
            : request.Body.Contains("<Divide", StringComparison.Ordinal)
                ? MockHttpResponse.Text(File.ReadAllText(Recorded("response-divide11.xml")), 500, "text/xml; charset=utf-8")
                : MockHttpResponse.Text(File.ReadAllText(Recorded("response-greet11.xml")), 200, "text/xml; charset=utf-8"));
        return server;
    }

    private static Window Host(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        ShellFixture.Pump();
        ShellFixture.Pump();
        return window;
    }

    [AvaloniaFact]
    public async Task Running_services_lists_the_services_without_reloading_the_one_shown_and_its_form()
    {
        await using var a = WcfService();
        await using var b = WcfService();
        var found = new List<DiscoveredService> { new("A", a.BaseUrl + "/CalculatorService.svc?wsdl"), new("B", b.BaseUrl + "/CalculatorService.svc?wsdl") };
        using var client = new WcfTestClient();
        var wcf = new WcfTestClientViewModel(client, null, () => Task.FromResult<IReadOnlyList<DiscoveredService>>(found), () => Task.FromResult<string?>(null));

        // B chosen from the menu, its form filled.
        await wcf.LoadDiscoveredAsync(found[1]);
        Assert.Equal(found[1].MetadataUrl, wcf.Address);
        wcf.Select("Greet");
        wcf.Row("person.Name")!.Value = "Ada";

        // Opening the menu again only lists them: B and its form stay.
        await wcf.DiscoverAsync(selectFirst: false);
        Assert.Equal(2, wcf.Discovered.Count);
        Assert.Equal(found[1].MetadataUrl, wcf.Address);
        Assert.Equal("Ada", wcf.Row("person.Name")!.Value);
        Assert.StartsWith("Endpoints: 2.", wcf.Status, StringComparison.Ordinal);

        // The command still loads the first.
        await wcf.DiscoverAsync();
        await ShellFixture.PumpUntil(() => !wcf.IsBusy && wcf.Address == found[0].MetadataUrl);
    }

    [AvaloniaFact]
    public async Task F5_invokes_in_the_wcf_test_client_but_continues_a_stopped_debugger()
    {
        await using var f = ShellFixture.Create();
        var context = f.Shell.Workbench.Context;
        string? CommandForF5() => f.Shell.Workbench.Keybindings.Peek(KeyChord.Parse("f5", PlatformInfo.Current)).Rule?.Command;

        context.Set("focusedView", "wcfTestClient");
        Assert.Equal("wcf.invoke", CommandForF5());
        context.Set("debugStopped", true);
        context.Set("debugRunning", false);
        Assert.Equal("debug.start", CommandForF5()); // Continue
        context.Set("debugStopped", false);
        context.Set("debugRunning", true);
        Assert.Equal("wcf.invoke", CommandForF5()); // the service runs under the debugger: invoke it
        context.Set("debugRunning", false);
        context.Set("focusedView", "textFile");
        Assert.Equal("debug.start", CommandForF5());
    }

    [AvaloniaFact]
    public async Task The_http_response_keeps_a_history_width_the_user_drags_and_its_body_tools_reachable_when_narrow()
    {
        await using var f = ShellFixture.Create();
        await using var server = new MockHttpServer(_ => MockHttpResponse.Json("""{"id":1}"""));
        var file = Path.Combine(f.UserData, "Api.http");
        var text = $"GET {server.BaseUrl}/todos\n";
        await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
        await f.Shell.SendHttpRequestAsync(file, text, 1);
        await ShellFixture.PumpUntil(() => f.Shell.HttpResponse.History.Count == 1);

        var view = new HttpResponseView { DataContext = f.Shell.HttpResponse };
        var window = Host(view, 1000, 500);
        try
        {
            // A splitter between the response and the history; the history starts at 360 px.
            Assert.Single(view.GetVisualDescendants().OfType<GridSplitter>(), s => s.Name == "HistorySplitter");
            Assert.Equal(360, view.HistoryColumnWidth);

            // Dragged, it keeps the user's width when the window changes; it narrows only to leave the response room.
            view.DragHistoryTo(250);
            window.Width = 1200;
            ShellFixture.Pump();
            Assert.Equal(250, view.HistoryColumnWidth);
            window.Width = 280;
            ShellFixture.Pump();
            Assert.True(view.HistoryColumnWidth <= 224, $"{view.HistoryColumnWidth}");

            // Narrow, Pretty or Raw and Word Wrap move into the overflow menu instead of disappearing.
            window.Width = 600;
            ShellFixture.Pump();
            ShellFixture.Pump();
            var tools = view.GetVisualDescendants().OfType<ToolbarPanel>().Single(t => t.Name == "BodyTools");
            var bodyView = view.GetVisualDescendants().OfType<SegmentedControl>().Single(s => s.Name == "BodyView");
            Assert.True(tools.IsEffectivelyVisible);
            Assert.True(bodyView.IsEffectivelyVisible || tools.Overflowed.Contains(bodyView), "Pretty or Raw is shown or in the overflow menu");
            window.Width = 1280;
            ShellFixture.Pump();
            ShellFixture.Pump();
            Assert.True(bodyView.IsEffectivelyVisible);
            Assert.Empty(tools.Overflowed);

            // The status is announced from its text; the Headers tab is named.
            var status = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StatusChip");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            Assert.Equal("200 OK", ControlAutomationPeer.CreatePeerForElement(status).GetName());
            var headers = view.GetVisualDescendants().OfType<TabStripItem>().ElementAt(1);
            Assert.Equal("Headers", ControlAutomationPeer.CreatePeerForElement(headers).GetName());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Send_again_reads_a_closed_http_file_and_reports_one_it_cant_read()
    {
        await using var f = ShellFixture.Create();
        await using var server = new MockHttpServer(_ => MockHttpResponse.Json("""{"id":1}"""));
        var file = Path.Combine(f.UserData, "Again.http");
        var text = $"GET {server.BaseUrl}/todos\n";
        await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
        var http = f.Shell.HttpResponse;
        await f.Shell.SendHttpRequestAsync(file, text, 1);
        await ShellFixture.PumpUntil(() => http.History.Count == 1);

        Assert.True(http.SendAgainCommand.CanExecute(null));
        http.SendAgainCommand.Execute(null);
        await ShellFixture.PumpUntil(() => http.History.Count == 2);
        Assert.Equal(2, server.Requests.Count);

        File.Delete(file);
        http.SendAgainCommand.Execute(null);
        for (var i = 0; i < 20; i++)
        {
            ShellFixture.Pump();
        }

        Assert.Equal(2, http.History.Count);
    }

    [AvaloniaFact]
    public async Task The_wcf_test_client_resizes_announces_its_result_and_keeps_the_tree_selectable_and_colored()
    {
        await using var server = WcfService();
        using var client = new WcfTestClient();
        var wcf = new WcfTestClientViewModel(client, server.BaseUrl + "/CalculatorService.svc", () => Task.FromResult<IReadOnlyList<DiscoveredService>>([]), () => Task.FromResult<string?>(null));
        await wcf.LoadAsync();
        var view = new WcfTestClientView { DataContext = wcf };
        var window = Host(view, 1100, 760);
        try
        {
            // A splitter between the operations and the request, and no cap on the operations' width.
            Assert.Single(view.GetVisualDescendants().OfType<GridSplitter>());
            var grid = view.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 3 && g.ColumnDefinitions[1].Width.Value == 12);
            Assert.True(double.IsPositiveInfinity(grid.ColumnDefinitions[0].MaxWidth));

            // The chevrons say what they do.
            wcf.Select("Greet");
            var person = wcf.Row("person")!;
            Assert.Equal("Collapse person", person.ToggleName);
            ShellFixture.Pump();
            var chevron = view.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == person && b.Command == person.ToggleCommand);
            Assert.Equal("Collapse person", ToolTip.GetTip(chevron));
            Assert.Equal("Collapse person", AutomationProperties.GetName(chevron));
            person.ToggleCommand.Execute(null);
            Assert.Equal("Expand person", wcf.Row("person")!.ToggleName);
            wcf.Row("person")!.ToggleCommand.Execute(null);

            // The result is announced from the texts that change.
            wcf.Row("person.Name")!.Value = "Ada";
            await wcf.SendAsync();
            ShellFixture.Pump();
            var chip = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResponseChipText");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(chip));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResponseReasonText")));
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting((Border)chip.GetVisualParent()!.GetVisualParent()!));

            // The tree's text can be selected and copied; selecting a node plains only that node.
            ShellFixture.Pump();
            var tree = view.GetVisualDescendants().OfType<TreeView>().Single();
            var nodes = tree.GetVisualDescendants().OfType<XmlNodeText>().ToList();
            var result = nodes.Single(n => n.NodeName == "GreetResult");
            Assert.IsAssignableFrom<SelectableTextBlock>(result);
            result.SelectAll();
            Assert.Equal("GreetResult = Hello Ada, 36 and happy (math)!", result.SelectedText);
            tree.SelectedItem = wcf.ResponseTree[0];
            ShellFixture.Pump();
            Assert.True(nodes.Single(n => n.NodeName == "GreetResponse").IsPlain);
            Assert.False(result.IsPlain);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_counts_line_names_itself_with_its_text_and_raises_a_name_change_for_its_live_region()
    {
        var counts = new CountsText { Source = "Matching lines: 3" };
        var window = Host(counts, 400, 100);
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(counts);
            Assert.Equal("Matching lines: 3", peer.GetName());
            var changes = new List<object?>();
            peer.PropertyChanged += (_, e) =>
            {
                if (e.Property == AutomationElementIdentifiers.NameProperty)
                {
                    changes.Add(e.NewValue);
                }
            };
            counts.Source = "Matching lines: 4";
            Assert.Equal(["Matching lines: 4"], changes);
            Assert.Equal("Matching lines: 4", peer.GetName());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Find_results_trims_a_long_search_as_a_whole_with_the_search_in_its_tooltip()
    {
        await using var f = ShellFixture.Create();
        var folder = Directory.CreateTempSubdirectory("codalus-find-review-").FullName;
        try
        {
            var query = "very_long_identifier_" + new string('x', 120);
            await File.WriteAllTextAsync(Path.Combine(folder, "A.cs"), $"var {query} = 1;\n", TestContext.Current.CancellationToken);
            f.Shell.FindReplace.Query = query;
            f.Shell.FindReplace.LookIn = folder;
            await f.Shell.FindReplace.FindAllAsync();
            var view = new FindResultsView { DataContext = f.Shell.FindResults };
            var window = Host(view, 500, 400);
            try
            {
                var text = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QueryText");
                Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, text.TextTrimming);
                Assert.Equal(f.Shell.FindResults.Title, ToolTip.GetTip(text));
                Assert.True(text.Bounds.Width <= 500, $"{text.Bounds.Width}");
                Assert.Contains(text.Inlines!.OfType<Run>(), r => r.Text == $"\"{query}\"");
                Assert.DoesNotContain(PolishTests.Clipped(window), p => p.Contains("very_long", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public void An_overflowed_text_button_is_named_by_its_label_not_its_tooltip()
    {
        var button = new Button { Content = "Modify Find" };
        ToolTip.SetTip(button, "Show Find and Replace with this search");
        var entry = Assert.IsType<MenuItem>(ToolbarPanel.Entry(button, button));
        Assert.Equal("Modify Find", entry.Header);
        var icon = new Button();
        ToolTip.SetTip(icon, "Refresh");
        Assert.Equal("Refresh", Assert.IsType<MenuItem>(ToolbarPanel.Entry(icon, icon)).Header);
    }
}
