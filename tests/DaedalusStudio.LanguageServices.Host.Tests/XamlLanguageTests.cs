using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>samples/Avalonia in the real build and language hosts.</summary>
public sealed class AvaloniaLanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("Avalonia", "Avalonia.slnx", 2);
}

/// <summary>
/// M19a (ADR 0046): Avalonia XAML in the C# language host, on samples/Avalonia: completion from Roslyn's view of the
/// project (Avalonia's types from metadata, the app's controls and view models from source), compiled binding paths,
/// live diagnostics with no false alarms on the fixture, hover, Go to Definition, x:Name rename into the code-behind,
/// formatting and the logical tree the previewer selects with.
/// </summary>
public sealed class XamlLanguageTests(AvaloniaLanguageFixture avalonia) : IClassFixture<AvaloniaLanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string MainWindow => avalonia.File("AvaloniaApp", "Views", "MainWindow.axaml");

    private string TodoItemView => avalonia.File("AvaloniaApp", "Views", "TodoItemView.axaml");

    private string AppStyles => avalonia.File("AvaloniaApp", "Styles", "AppStyles.axaml");

    private string App => avalonia.File("AvaloniaApp", "App.axaml");

    private string Generic => avalonia.File("AvaloniaControls", "Themes", "Generic.axaml");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    /// <summary>Opens the file, or puts back its text from disk when an earlier test edited it.</summary>
    private async Task FreshAsync(string path)
    {
        var disk = await File.ReadAllTextAsync(path, Ct);
        string current;
        try
        {
            current = avalonia.Text(path).Text;
        }
        catch (KeyNotFoundException)
        {
            await avalonia.OpenAsync(path);
            return;
        }

        if (current != disk)
        {
            await avalonia.EditAsync(path, current, disk);
        }
    }

    private async Task<IReadOnlyList<CompletionItem>> CompleteAsync(string path, string marker, int delta)
    {
        var list = await avalonia.Client.CompletionAsync(Uri(path), avalonia.At(path, marker, delta), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        return list.Items;
    }

    private async Task<IReadOnlyList<string>> LabelsAsync(string path, string marker, int delta) =>
        [.. (await CompleteAsync(path, marker, delta)).Select(i => i.Label)];

    /// <summary>The file's XAML diagnostics once they are published for its current version.</summary>
    private async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string path, Func<IReadOnlyList<Diagnostic>, bool>? until = null)
    {
        var version = avalonia.Text(path).Version;
        IReadOnlyList<Diagnostic> current = [];
        int? seen = null;
        var wait = LanguageFixture.WaitUntilAsync(() =>
        {
            var file = avalonia.Languages.Diagnostics.Get(path, "csharp");
            current = file?.Items.Where(d => d.Source == "XAML").ToList() ?? [];
            seen = file?.Version;
            return file is not null && file.Version == version && (until is null || until(current));
        }, TimeSpan.FromSeconds(60));
        try
        {
            await wait;
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No XAML diagnostics for {Path.GetFileName(path)} version {version} (last {seen}): {string.Join("; ", current.Select(d => d.Message))}\n{string.Join('\n', avalonia.Log.TakeLast(30))}");
        }

        return current;
    }

    [Fact]
    public async Task The_fixtures_xaml_has_no_false_alarms()
    {
        // Each file with one planted error: once that is reported, nothing else may be.
        foreach (var path in new[] { MainWindow, TodoItemView, AppStyles, App, Generic })
        {
            await FreshAsync(path);
            await avalonia.EditAsync(path, "xmlns=\"https://github.com/avaloniaui\"", "xmlns=\"https://github.com/avaloniaui\" Bogus=\"1\"");
            var diagnostics = await DiagnosticsAsync(path, d => d.Any(x => x.Message.Contains("'Bogus'", StringComparison.Ordinal)));
            Assert.True(diagnostics.Count == 1, $"{Path.GetFileName(path)}: {string.Join("; ", diagnostics.Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Message}"))}");
            await FreshAsync(path);
        }
    }
    [Fact]
    public async Task Elements_attributes_and_values_complete()
    {
        await FreshAsync(MainWindow);
        await avalonia.EditAsync(MainWindow, "<ListBox x:Name=\"Tasks\"", "<Bu />\n    <Button HorizontalAlignment=\"\" IsEnabled=\"\" Background=\"\" />\n    <ListBox x:Name=\"Tasks\"");

        // Element names: Avalonia's types, the library's control and the app's view through their prefixes, and the
        // parent's property elements.
        var elements = await LabelsAsync(MainWindow, "<Bu />", 3);
        Assert.Contains("Button", elements);
        Assert.Contains("controls:RatingControl", elements);
        Assert.Contains("views:TodoItemView", elements);
        Assert.Contains("DockPanel.LastChildFill", elements);
        Assert.DoesNotContain("Control", elements.Where(e => e == "Panel.Children")); // sanity: a list, not a crash

        // Attributes: properties, events, attached properties, directives.
        var attributes = await CompleteAsync(MainWindow, "<Button HorizontalAlignment", "<Button ".Length);
        var labels = attributes.Select(a => a.Label).ToList();
        Assert.Contains("Content", labels);
        Assert.Contains("Click", labels);
        Assert.Contains("DockPanel.Dock", labels);
        Assert.Contains("Grid.Row", labels);
        Assert.Contains("x:Name", labels);
        var content = attributes.First(a => a.Label == "Content");
        Assert.Equal("Content=\"$0\"", content.TextEdit?.NewText);
        Assert.Equal(InsertTextFormat.Snippet, content.InsertTextFormat);

        // Values: an enum, a boolean, named colors.
        Assert.Contains("Center", await LabelsAsync(MainWindow, "HorizontalAlignment=\"\"", "HorizontalAlignment=\"".Length));
        Assert.Contains("True", await LabelsAsync(MainWindow, "IsEnabled=\"\"", "IsEnabled=\"".Length));
        Assert.Contains("CornflowerBlue", await LabelsAsync(MainWindow, "Background=\"\"", "Background=\"".Length));

        // x:DataType: the view models through their prefix.
        Assert.Contains("vm:MainWindowViewModel", await LabelsAsync(MainWindow, "x:DataType=\"vm:MainWindowViewModel\"", "x:DataType=\"".Length));

        // The library's control: its settable properties, without those already set or read-only.
        var rating = await LabelsAsync(MainWindow, "<controls:RatingControl ", "<controls:RatingControl ".Length);
        Assert.Contains("Foreground", rating);
        Assert.DoesNotContain("Maximum", rating);
        Assert.DoesNotContain("Stars", rating);
    }

    [Fact]
    public async Task Markup_extensions_resources_and_compiled_binding_paths_complete()
    {
        await FreshAsync(MainWindow);
        await avalonia.EditAsync(MainWindow, "Text=\"{Binding Count}\"", "Text=\"{Binding Count}\" Tag=\"{\" Foreground=\"{StaticResource }\" Width=\"{Binding Items[0].}\" MinWidth=\"{Binding Count, Mode=}\"");

        var extensions = await LabelsAsync(MainWindow, "Tag=\"{\"", "Tag=\"{".Length);
        Assert.Contains("Binding", extensions);
        Assert.Contains("StaticResource", extensions);
        Assert.Contains("x:Static", extensions);

        // Resources of the app and of the library it merges.
        var resources = await LabelsAsync(MainWindow, "{StaticResource }", "{StaticResource ".Length);
        Assert.Contains("AccentBrush", resources);
        Assert.Contains("CardSpacing", resources);
        Assert.Contains("StarBrush", resources);

        // The data type's members (x:DataType="vm:MainWindowViewModel"), with their documentation, and through an indexer.
        var members = await CompleteAsync(MainWindow, "{Binding Greeting}", "{Binding ".Length);
        Assert.Contains(members, m => m.Label == "Count");
        Assert.Contains(members, m => m.Label == "Increment");
        var greeting = Assert.Single(members, m => m.Label == "Greeting");
        Assert.Contains("The text at the top of the window.", greeting.Documentation?.Value, StringComparison.Ordinal);
        var items = await LabelsAsync(MainWindow, "{Binding Items[0].}", "{Binding Items[0].".Length);
        Assert.Contains("Title", items);
        Assert.Contains("Priority", items);
        Assert.Contains("OneWay", await LabelsAsync(MainWindow, "Mode=}", "Mode=".Length));

        // In the data template, the item's type.
        await FreshAsync(TodoItemView);
        Assert.Contains("IsDone", await LabelsAsync(TodoItemView, "{Binding Title}", "{Binding ".Length));
    }

    [Fact]
    public async Task Styles_complete_selectors_classes_and_setters()
    {
        await FreshAsync(AppStyles);
        await avalonia.EditAsync(AppStyles, "<Style Selector=\"TextBlock.task\">", "<Style Selector=\"TextBlock.\"><Setter Property=\"FontWeight\" Value=\"\" /></Style>\n  <Style Selector=\"Button:\" />\n  <Style Selector=\"TextBlock.task\">");
        var classes = await LabelsAsync(AppStyles, "Selector=\"TextBlock.\"", "Selector=\"TextBlock.".Length);
        Assert.Contains("title", classes);
        Assert.Contains("task", classes);
        Assert.Contains("pointerover", await LabelsAsync(AppStyles, "Selector=\"Button:\"", "Selector=\"Button:".Length));
        Assert.Contains("FontSize", await LabelsAsync(AppStyles, "<Setter Property=\"FontWeight\"", "<Setter Property=\"".Length));
        Assert.Contains("Bold", await LabelsAsync(AppStyles, "Value=\"\" />", "Value=\"".Length));
    }

    [Fact]
    public async Task Errors_are_reported_live()
    {
        await FreshAsync(TodoItemView);
        await avalonia.EditAsync(TodoItemView, "<CheckBox IsChecked=\"{Binding IsDone}\" />",
            "<CheckBox IsChecked=\"{Binding IsDoen}\" />\n    <Buton />\n    <TextBlock Colour=\"Red\" />\n    <StackPanel Orientation=\"Sideways\" />\n    <TextBlock Text=\"{Binding Priority.Missing}\" />\n    <ContentControl><ContentControl.ContentTemplate><DataTemplate><TextBlock xml:space=\"preserve\" Text=\"{Binding Anything}\" /></DataTemplate></ContentControl.ContentTemplate></ContentControl>");
        var diagnostics = await DiagnosticsAsync(TodoItemView, d => d.Count >= 4);
        Assert.Contains(diagnostics, d => d.Code == "XAML0006" && d.Message.Contains("'IsDoen'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0003" && d.Message.Contains("'Buton'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0004" && d.Message.Contains("'Colour'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0005" && d.Message.Contains("'Sideways'", StringComparison.Ordinal));

        // Not errors: a binding in a data template without a data type (unknown), the xml: prefix.
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Anything", StringComparison.Ordinal) || d.Code == "XAML0002");
        var path = Assert.Single(diagnostics, d => d.Code == "XAML0006" && d.Message.Contains("'IsDoen'", StringComparison.Ordinal));
        Assert.Equal(avalonia.At(TodoItemView, "IsDoen").Character, path.Range.Start.Character);

        // A syntax error.
        await avalonia.EditAsync(TodoItemView, "<Buton />", "<Buton");
        Assert.Contains(await DiagnosticsAsync(TodoItemView, d => d.Any(x => x.Code == "XAML0001")), d => d.Code == "XAML0001");

        // Fixed: none left.
        await avalonia.EditAsync(TodoItemView, "{Binding IsDoen}", "{Binding IsDone}");
        await avalonia.EditAsync(TodoItemView, "\n    <Buton\n    <TextBlock Colour=\"Red\" />\n    <StackPanel Orientation=\"Sideways\" />\n    <TextBlock Text=\"{Binding Priority.Missing}\" />", string.Empty);
        await avalonia.EditAsync(TodoItemView, "<TextBlock xml:space=\"preserve\" Text=\"{Binding Anything}\" />", "<TextBlock xml:space=\"preserve\" Text=\"Anything\" />");
        await LanguageFixture.WaitUntilAsync(() => avalonia.Languages.Diagnostics.Get(TodoItemView, "csharp") is null, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Hover_and_definition_reach_types_members_resources_and_styles()
    {
        await FreshAsync(MainWindow);
        var hover = await avalonia.Client.HoverAsync(Uri(MainWindow), avalonia.At(MainWindow, "<Button x:Name", 2), Ct);
        Assert.Contains("class Avalonia.Controls.Button", hover?.Contents.Value, StringComparison.Ordinal);
        var path = await avalonia.Client.HoverAsync(Uri(MainWindow), avalonia.At(MainWindow, "{Binding Greeting}", "{Binding Gr".Length), Ct);
        Assert.Contains("string MainWindowViewModel.Greeting", path?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("The text at the top of the window.", path?.Contents.Value, StringComparison.Ordinal);
        var attached = await avalonia.Client.HoverAsync(Uri(MainWindow), avalonia.At(MainWindow, "DockPanel.Dock=\"Top\"", 12), Ct);
        Assert.Contains("DockPanel.DockProperty", attached?.Contents.Value, StringComparison.Ordinal);

        // Definitions: a view model property, the library's control, a resource, a style class, the code-behind.
        var greeting = Assert.Single(await avalonia.Client.DefinitionAsync(Uri(MainWindow), avalonia.At(MainWindow, "{Binding Greeting}", "{Binding Gr".Length), Ct));
        Assert.EndsWith("MainWindowViewModel.cs", greeting.Uri, StringComparison.Ordinal);
        var rating = Assert.Single(await avalonia.Client.DefinitionAsync(Uri(MainWindow), avalonia.At(MainWindow, "controls:RatingControl", "controls:Ra".Length), Ct));
        Assert.EndsWith("RatingControl.cs", rating.Uri, StringComparison.Ordinal);
        var resource = Assert.Single(await avalonia.Client.DefinitionAsync(Uri(MainWindow), avalonia.At(MainWindow, "{StaticResource CardSpacing}", "{StaticResource Ca".Length), Ct));
        Assert.EndsWith("App.axaml", resource.Uri, StringComparison.Ordinal);
        var style = Assert.Single(await avalonia.Client.DefinitionAsync(Uri(MainWindow), avalonia.At(MainWindow, "Classes=\"title\"", "Classes=\"ti".Length), Ct));
        Assert.EndsWith("AppStyles.axaml", style.Uri, StringComparison.Ordinal);
        var codeBehind = await avalonia.Client.DefinitionAsync(Uri(MainWindow), avalonia.At(MainWindow, "x:Class=\"AvaloniaApp.Views.MainWindow\"", "x:Class=\"Av".Length), Ct);
        Assert.Contains(codeBehind, l => l.Uri.EndsWith("MainWindow.axaml.cs", StringComparison.Ordinal));

        // Avalonia's own types decompile (or come from Source Link) like C#'s.
        var button = await avalonia.Client.DefinitionAsync(Uri(MainWindow), avalonia.At(MainWindow, "<Button x:Name", 2), Ct);
        Assert.NotEmpty(button);
    }

    [Fact]
    public async Task Renaming_an_x_name_follows_bindings_and_the_code_behind()
    {
        await FreshAsync(MainWindow);
        var codeBehind = avalonia.File("AvaloniaApp", "Views", "MainWindow.axaml.cs");
        await avalonia.EditAsync(MainWindow, "Text=\"{Binding Count}\"", "Text=\"{Binding #GreetingText.Text}\"");
        var prepare = await avalonia.Client.PrepareRenameAsync(Uri(MainWindow), avalonia.At(MainWindow, "x:Name=\"GreetingText\"", "x:Name=\"Gr".Length), Ct);
        Assert.Equal("GreetingText", prepare?.Placeholder);

        var edit = await avalonia.Client.RenameAsync(Uri(MainWindow), avalonia.At(MainWindow, "x:Name=\"GreetingText\"", "x:Name=\"Gr".Length), "Headline", Ct);
        Assert.NotNull(edit);
        var xaml = Assert.Single(edit.DocumentChanges, c => c.TextDocument.Uri == Uri(MainWindow));
        Assert.Equal(2, xaml.Edits.Length); // x:Name and #GreetingText
        var cs = Assert.Single(edit.DocumentChanges, c => c.TextDocument.Uri == Uri(codeBehind));
        Assert.Single(cs.Edits);
        Assert.All(edit.DocumentChanges.SelectMany(c => c.Edits), e => Assert.Equal("Headline", e.NewText));
    }

    [Fact]
    public async Task Formatting_indents_elements_and_attributes()
    {
        await FreshAsync(TodoItemView);
        await avalonia.EditAsync(TodoItemView, "  <StackPanel Orientation=\"Horizontal\" Spacing=\"6\">\n    <CheckBox", "  <StackPanel Orientation=\"Horizontal\" Spacing=\"6\">   \n<Border\n Padding=\"2\">\n          <TextBlock />\n</Border>\n    <CheckBox");
        var edits = await avalonia.Client.FormatAsync(Uri(TodoItemView), null, new FormattingOptions(2, true), Ct);
        var text = Apply(avalonia.Text(TodoItemView).Text, edits);
        Assert.Contains("  <StackPanel Orientation=\"Horizontal\" Spacing=\"6\">\n    <Border\n      Padding=\"2\">\n      <TextBlock />\n    </Border>\n    <CheckBox", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        await avalonia.EditAsync(TodoItemView, avalonia.Text(TodoItemView).Text, text);
        Assert.Empty(await avalonia.Client.FormatAsync(Uri(TodoItemView), null, new FormattingOptions(2, true), Ct)); // formatted text stays
    }

    [Fact]
    public async Task The_logical_tree_is_what_the_previewer_selects()
    {
        await FreshAsync(MainWindow);
        var tree = await avalonia.Client.XamlLogicalTreeAsync(Uri(MainWindow), Ct);
        var root = Assert.Single(tree!.Nodes);
        Assert.Equal("MainWindow", root.Type);
        var dock = Assert.Single(root.Children);
        Assert.Equal("DockPanel", dock.Type);
        Assert.Equal(["TextBlock", "StackPanel", "ListBox"], dock.Children.Select(c => c.Type));
        Assert.Equal("GreetingText", dock.Children[0].Name);
        Assert.Equal(["Button", "TextBlock", "RatingControl"], dock.Children[1].Children.Select(c => c.Type));
        Assert.Empty(dock.Children[2].Children); // the item template isn't the logical tree

        // A styles file previews its Design.PreviewWith.
        await FreshAsync(AppStyles);
        var styles = await avalonia.Client.XamlLogicalTreeAsync(Uri(AppStyles), Ct);
        Assert.Equal("StackPanel", Assert.Single(styles!.Nodes).Type);
    }

    private static string Apply(string text, TextEdit[] edits)
    {
        var lines = text.Split('\n');
        int Offset(Position p) => lines.Take(p.Line).Sum(l => l.Length + 1) + p.Character;
        foreach (var edit in edits.OrderByDescending(e => Offset(e.Range.Start)))
        {
            var start = Offset(edit.Range.Start);
            text = text[..start] + edit.NewText + text[Offset(edit.Range.End)..];
        }

        return text;
    }
}
