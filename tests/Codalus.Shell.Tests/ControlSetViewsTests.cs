using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h: the views redesigned on the control set (docs/design/controls/FindResults.dc.html, HttpResponse.dc.html,
/// WcfTestClient.dc.html). Find Results groups by folder and file with counts, switches between Tree and List with a
/// segmented control, draws each match's line in the editor's colors with the match on Shell.Match.Background, and walks
/// the results with F8 and Shift+F8; the HTTP Response's status is a chip in its status's color, with Body, Headers and
/// Request as tabs, Pretty or Raw, and a link to the request; the WCF Test Client's request is a typed grid (required
/// marks, an enum's combo box, a list's items added and removed as [0], [1]…), its operations carry their signatures,
/// and a fault is an error chip.
/// </summary>
public sealed class ControlSetViewsTests
{
    private static T? Visible<T>(Visual root, Func<T, bool>? where = null) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && (where?.Invoke(c) ?? true));

    /// <summary>With <c>CODALUS_UI_SNAPSHOTS</c>, the window in Harbor Light, Classic Dark and Harbor High contrast, saved there.</summary>
    private static void Snapshots(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var before = window.RequestedThemeVariant;
        foreach (var variant in new[] { Theming.ThemeService.HarborLight, Avalonia.Styling.ThemeVariant.Dark, Theming.ThemeService.HarborHighContrast })
        {
            window.RequestedThemeVariant = variant;
            for (var i = 0; i < 3; i++)
            {
                ShellFixture.Pump();
            }

            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(folder, $"{name}-{variant.Key}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.RequestedThemeVariant = before;
        ShellFixture.Pump();
    }

    private static IBrush? Resource(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) ? value as IBrush : null;

    [AvaloniaFact]
    public async Task Find_results_group_by_folder_and_file_switch_tree_and_list_color_the_code_and_walk_with_F8()
    {
        await using var f = ShellFixture.Create();
        var folder = Directory.CreateTempSubdirectory("codalus-find-views-").FullName;
        try
        {
            var service = Path.Combine(folder, "Service", "ICalculatorService.cs");
            var client = Path.Combine(folder, "Client", "Program.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(service)!);
            Directory.CreateDirectory(Path.GetDirectoryName(client)!);
            await File.WriteAllTextAsync(service, "namespace Calc;\n\npublic interface ICalculatorService\n{\n    string Greet(Person person);\n}\n\npublic sealed class Person\n{\n}\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(client, "var person = new Person();\nSystem.Console.WriteLine(person);\n", TestContext.Current.CancellationToken);

            var find = f.Shell.FindReplace;
            var results = f.Shell.FindResults;
            find.Query = "Person";
            find.MatchCase = true;
            find.WholeWord = true;
            find.LookIn = folder;
            await find.FindAllAsync();
            var page = results.CurrentPage;
            Assert.Equal("Find all ", page.TitleVerb);
            Assert.Equal("\"Person\"", page.TitleTerm);
            Assert.StartsWith(", Match case, Whole word, ", page.TitleRest, StringComparison.Ordinal);
            Assert.Equal(3, page.MatchCount);

            // Path and file: folders below the folder searched, then their files, each with its count.
            page.FilesGroupBy = FindResultsGrouping.PathAndFile;
            var headers = page.Rows.Where(r => r.IsHeader).ToList();
            Assert.Equal([("Client", 1, true), ("Program.cs", 1, false), ("Service", 2, true), ("ICalculatorService.cs", 2, false)], headers.Select(h => (h.Label, h.Count, h.IsFolder)));
            Assert.All(page.Rows.Where(r => !r.IsHeader), r => Assert.Equal(1, r.Span));
            Assert.All(headers, h => Assert.Equal(4, h.Span));

            // The window at the design's size (the docked pane in the test window is too short for the rows).
            var view = new FindResultsView { DataContext = results };
            var host = new Window { Width = 1100, Height = 540, Content = view };
            host.Show();
            await ShellFixture.PumpUntil(() => Visible<CodeLine>(view) is not null);

            // The query line and the counts.
            var query = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "QueryText");
            Assert.Contains(query.Inlines!.OfType<Run>(), r => r.Text == "\"Person\"" && r.FontWeight == FontWeight.SemiBold);
            var counts = Visible<CountsText>(view)!;
            Assert.Contains(counts.Inlines!.OfType<Run>(), r => r.Text == "3" && r.FontWeight == FontWeight.SemiBold);

            // The code in the editor's colors, the match on Shell.Match.Background in bold.
            var line = Visible<CodeLine>(view, c => c.Source == "public sealed class Person")!;
            var theme = CodeColors.ThemeFor(line.ActualThemeVariant);
            Assert.Equal(TokenTypes.IndexOf("keyword"), line.TokenAt("public sealed class Person".IndexOf("class", StringComparison.Ordinal)));
            var runs = line.Inlines!.OfType<Run>().ToList();
            Assert.Same(theme.Token(TokenTypes.IndexOf("keyword")), runs.Single(r => r.Text == "class").Foreground);
            var match = runs.Single(r => r.Text == "Person");
            Assert.Equal(FontWeight.Bold, match.FontWeight);
            Assert.Same(Resource(line, "Shell.Match.Background"), match.Background);

            Snapshots(host, "find-results");

            // Selected, the line takes the selection's text and the match is underlined.
            page.SelectedRow = page.Rows.First(r => r.Code == "public sealed class Person");
            await ShellFixture.PumpUntil(() => line.IsPlain);
            var plain = line.Inlines!.OfType<Run>().Single(r => r.Text == "Person");
            Assert.Null(plain.Background);
            Assert.Equal(TextDecorations.Underline, plain.TextDecorations);

            // Tree or List: the segmented control switches to List View (full paths, no headers) and back.
            var segments = Visible<SegmentedControl>(view, s => s.Name == "ViewSwitch")!;
            segments.SelectedIndex = 1;
            Assert.True(page.IsListView);
            Assert.All(page.Rows, r => Assert.False(r.IsHeader));
            Assert.Contains(page.Rows, r => r.FileName == service);
            page.ViewIndex = 0;
            Assert.Equal(0, segments.SelectedIndex);
            Assert.Contains(page.Rows, r => r.IsHeader);

            // F8 and Shift+F8 walk the matches and open them; Enter opens the selected one.
            page.SelectedRow = null;
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "List");
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8, Source = list });
            var first = page.Rows.First(r => !r.IsHeader);
            Assert.Equal(first, page.SelectedRow);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == first.Path);
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8, Source = list });
            Assert.Equal(page.Rows.Where(r => !r.IsHeader).ElementAt(1), page.SelectedRow);
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8, KeyModifiers = KeyModifiers.Shift, Source = list });
            Assert.Equal(first, page.SelectedRow);

            // Kept results: the pages show as tabs, each with its count as a badge.
            page.KeepResults = true;
            find.Query = "Greet";
            await find.FindAllAsync();
            await ShellFixture.PumpUntil(() => Visible<TabStrip>(view, t => t.Name == "PageTabs") is { ItemCount: 2 });
            var tabs = Visible<TabStrip>(view, t => t.Name == "PageTabs")!;
            ShellFixture.Pump();
            var badges = tabs.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("badge") && b.IsEffectivelyVisible)
                .Select(b => (b.Child as TextBlock)?.Text).ToList();
            Assert.Equal(["3", "1"], badges);
            host.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task The_http_response_shows_its_status_as_a_chip_with_tabs_pretty_or_raw_and_a_link_to_the_request()
    {
        await using var f = ShellFixture.Create();
        await using var server = new MockHttpServer(request => request.Path switch
        {
            "/missing" => MockHttpResponse.NotFound(),
            "/moved" => new MockHttpResponse(304, [], "text/plain"),
            _ => MockHttpResponse.Json("""{"id":1,"title":"Write the fixture"}"""),
        });
        var folder = Directory.CreateTempSubdirectory("codalus-http-views-").FullName;
        try
        {
            var file = Path.Combine(folder, "Api.http");
            var text = $"GET {server.BaseUrl}/todos\n\n###\nGET {server.BaseUrl}/missing\n\n###\nGET {server.BaseUrl}/moved\n";
            await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
            var http = f.Shell.HttpResponse;
            f.Shell.Layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId, focus: false);

            await f.Shell.SendHttpRequestAsync(file, text, 1);
            await ShellFixture.PumpUntil(() => http.History.Count == 1);
            await ShellFixture.PumpUntil(() => Visible<HttpResponseView>(f.Window) is { } v && v.BodyText.Length > 0);
            var view = Visible<HttpResponseView>(f.Window)!;

            // The status chip, the version, the time and size, and the request with its link.
            Border Chip() => (Border)view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StatusChip").GetVisualParent()!;
            Assert.Equal("200 OK", http.StatusText);
            Assert.Contains("success", Chip().Classes);
            Assert.Equal("HTTP/1.1", http.Version);
            Assert.EndsWith(" ms", http.Elapsed, StringComparison.Ordinal);
            Assert.Equal("36 B", http.Size);
            Assert.Equal("GET", http.Method);
            Assert.Equal("Api.http, line 1", http.Source);
            Assert.StartsWith("application/json", http.ContentType, StringComparison.Ordinal);
            Assert.NotNull(Visible<Button>(view, b => b.Classes.Contains("link") && Equals(b.Content, "Api.http, line 1")));

            // Pretty (formatted) or Raw (as received).
            Assert.Contains("\n", view.BodyText, StringComparison.Ordinal);
            http.BodyViewIndex = 1;
            Assert.Equal("""{"id":1,"title":"Write the fixture"}""", view.BodyText);
            http.BodyViewIndex = 0;

            // Body, Headers (a badge with the count) and Request as tabs.
            var tabs = Visible<TabStrip>(view, t => t.Name == "Tabs")!;
            Assert.Equal(3, tabs.ItemCount);
            Assert.True(http.HeaderCount > 0);
            Assert.NotNull(tabs.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("badge") && (b.Child as TextBlock)?.Text == http.HeaderCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            tabs.SelectedIndex = 1;
            Assert.True(http.IsHeadersTab);
            ShellFixture.Pump();
            Assert.NotNull(Visible<TextBlock>(view, t => t.Text == "Content-Type"));
            tabs.SelectedIndex = 2;
            Assert.StartsWith($"GET {server.BaseUrl}/todos", http.RequestText, StringComparison.Ordinal);
            tabs.SelectedIndex = 0;

            // A client error and a redirect take their colors; the history's rows carry method and status chips.
            await f.Shell.SendHttpRequestAsync(file, text, 4);
            await ShellFixture.PumpUntil(() => http.History.Count == 2 && http.StatusText == "404 Not Found");
            ShellFixture.Pump();
            Assert.Contains("error", Chip().Classes);
            await f.Shell.SendHttpRequestAsync(file, text, 7);
            await ShellFixture.PumpUntil(() => http.History.Count == 3 && http.Selected?.Exchange.Response?.StatusCode == 304);
            ShellFixture.Pump();
            Assert.Contains("warning", Chip().Classes);
            Assert.Equal([HttpStatusKind.Redirect, HttpStatusKind.Error, HttpStatusKind.Success], http.History.Select(h => h.StatusKind));
            Assert.All(http.History, h => Assert.Equal("GET", h.Method));
            Assert.Equal(["/moved", "/missing", "/todos"], http.History.Select(h => h.Path));
            var history = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "HistoryList");
            ShellFixture.Pump();
            var chips = history.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).ToList();
            Assert.Contains(chips, c => c.Classes.Contains("square") && c.Classes.Contains("info"));
            Assert.Contains(chips, c => c.Classes.Contains("warning") && !c.Classes.Contains("square"));
            var httpHost = new Window { Width = 1280, Height = 640, Content = new HttpResponseView { DataContext = http } };
            httpHost.Show();
            Snapshots(httpHost, "http-response");
            httpHost.Close();

            // The link opens the .http file at the request.
            http.Selected = http.History.Last();
            http.OpenRequestCommand.Execute(null);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == file);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task The_wcf_test_clients_typed_grid_marks_required_fields_lists_enums_and_items_and_shows_a_fault_as_a_chip()
    {
        var wsdl = Path.Combine(ShellFixture.RepoRoot, "samples", "Wcf", "contracts", "CalculatorService.wsdl");
        MockHttpServer? server = null;
        server = new MockHttpServer(request => request.Method == "GET"
            ? MockHttpResponse.Text(File.ReadAllText(wsdl).Replace("http://localhost:5280/", server!.BaseUrl + "/", StringComparison.Ordinal), contentType: "text/xml; charset=utf-8")
            : request.Body.Contains("<Divide", StringComparison.Ordinal)
                ? MockHttpResponse.Text(File.ReadAllText(Recorded("response-divide11.xml")), 500, "text/xml; charset=utf-8")
                : MockHttpResponse.Text(File.ReadAllText(Recorded("response-greet11.xml")), 200, "text/xml; charset=utf-8"));
        await using (server)
        {
            using var client = new WcfTestClient();
            var wcf = new WcfTestClientViewModel(client, server.BaseUrl + "/CalculatorService.svc", () => Task.FromResult<IReadOnlyList<DiscoveredService>>([]), () => Task.FromResult<string?>(null));
            await wcf.LoadAsync();
            Assert.True(wcf.Error is null, wcf.Error);
            Assert.True(wcf.IsLoaded);

            // The operations with their signatures.
            Assert.Equal(
                ["Add(double a, double b) → double", "Divide(double a, double b) → double", "Greet(Person person) → string", "FindPeople(string prefix, int max) → Person[]"],
                wcf.OperationRows.Select(o => o.Signature));

            var view = new WcfTestClientView { DataContext = wcf };
            var window = new Window { Width = 1100, Height = 760, Content = view };
            window.Show();
            try
            {
                wcf.Select("Greet");
                ShellFixture.Pump();
                Assert.Same(wcf.SelectedOperationRow, wcf.OperationRows[2]);

                // The typed grid: a value type that can't be nil is required, a string isn't; the enum is a combo box.
                var age = wcf.Row("person.Age")!;
                Assert.True(age.IsRequired);
                Assert.Equal("int", age.Type);
                Assert.False(wcf.Row("person.Name")!.IsRequired);
                Assert.True(wcf.Row("person.Name")!.CanNil);
                Assert.Equal("Person: 4 members", wcf.Row("person")!.Summary);
                ShellFixture.Pump();
                var combo = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && c.DataContext == wcf.Row("person.Mood"));
                Assert.Equal(["Calm", "Happy", "Grumpy"], combo.Items.Cast<string>());
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.DataContext == age && t.Inlines?.OfType<Run>().Any(r => r.Text == "*") == true);

                // A list is one row (string[], its count); items are added as [0], [1] and removed with their own buttons.
                var tags = wcf.Row("person.Tags")!;
                Assert.Equal("string[]", tags.Type);
                Assert.Equal("0 items", tags.Summary);
                tags.AddCommand.Execute(null);
                wcf.Row("person.Tags")!.AddCommand.Execute(null);
                Assert.Equal(["[0]", "[1]"], wcf.Rows.Where(r => r.Index >= 0).Select(r => r.Label));
                Assert.Equal("2 items", wcf.Row("person.Tags")!.Summary);
                ShellFixture.Pump();
                Assert.Equal(2, view.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && ButtonIcon.GetKind(b) == "trash"));
                Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && ButtonIcon.GetKind(b) == "add");
                wcf.Rows.First(r => r.Label == "[1]").RemoveCommand.Execute(null);
                Assert.Equal(["[0]"], wcf.Rows.Where(r => r.Index >= 0).Select(r => r.Label));

                // A group collapses with its chevron.
                wcf.Row("person")!.ToggleCommand.Execute(null);
                Assert.Single(wcf.Rows);
                wcf.Rows[0].ToggleCommand.Execute(null);
                Assert.Equal(6, wcf.Rows.Count);

                // Form or XML, as a segmented control.
                var request = view.GetVisualDescendants().OfType<SegmentedControl>().First(s => s.IsEffectivelyVisible);
                request.SelectedIndex = 1;
                Assert.True(wcf.IsXmlTab);
                Assert.Contains("<Greet", wcf.RequestXml, StringComparison.Ordinal);
                request.SelectedIndex = 0;

                // A response: a success chip and the time; a fault: an error chip with its code, the reason, the status.
                wcf.Row("person.Name")!.Value = "Ada";
                await wcf.SendAsync();
                ShellFixture.Pump();
                Assert.True(wcf.IsResponseOk);
                Assert.Equal("200 OK", wcf.ResponseChip);
                Border Chip() => (Border)view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ResponseChipText").GetVisualParent()!.GetVisualParent()!;
                Assert.Contains("success", Chip().Classes);
                Assert.NotNull(Visible<XmlNodeText>(view, t => t.NodeName == "GreetResult"));
                Snapshots(window, "wcf-test-client");
                wcf.Select("Divide");
                wcf.Row("a")!.Value = "10";
                wcf.Row("b")!.Value = "0";
                await wcf.SendAsync();
                ShellFixture.Pump();
                Assert.True(wcf.IsFault);
                Assert.Equal("Fault s:Client", wcf.ResponseChip);
                Assert.Equal("Division by zero", wcf.ResponseReason);
                Assert.StartsWith("500 · ", wcf.ResponseMeta, StringComparison.Ordinal);
                Assert.Contains("error", Chip().Classes);
                Assert.True(wcf.Row("a")!.IsRequired);

                Snapshots(window, "wcf-test-client-fault");

                // Tree or XML for the response.
                wcf.ResponseView = 1;
                ShellFixture.Pump();
                Assert.NotNull(Visible<TextBox>(view, t => t.Text?.Contains("<s:Fault>", StringComparison.Ordinal) == true || t.Text?.Contains("faultcode", StringComparison.Ordinal) == true));
            }
            finally
            {
                window.Close();
            }
        }

        static string Recorded(string name) => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "Wcf", name);
    }
}
