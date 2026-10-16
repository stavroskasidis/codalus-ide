using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// samples/WindowsDesktop's WPF app (WpfApp.slnx) in the real build and language hosts, on every OS: on Linux and macOS
/// the build host sets <c>EnableWindowsTargeting</c>, so the net11.0-windows project restores and builds at design time.
/// </summary>
public sealed class WpfLanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("WindowsDesktop", "WpfApp.slnx", 1);

    /// <summary>The design-time build after the restore (WPF's references, its XAML as additional files, the markup compiler's fields), and the host seeing WPF's types.</summary>
    protected override async Task ReadyAsync()
    {
        static bool Restored(Codalus.Projects.Protocol.CompilerInputsInfo inputs) =>
            inputs.Arguments.Any(a => a.Contains("PresentationFramework.dll", StringComparison.OrdinalIgnoreCase))
            && inputs.Arguments.Any(a => a.StartsWith("/additionalfile:", StringComparison.OrdinalIgnoreCase) && a.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
            && inputs.Arguments.Any(a => a.Contains("MainWindow.g.", StringComparison.OrdinalIgnoreCase));
        try
        {
            await WaitUntilAsync(() => Solutions.CompilerInputs.Count(i => Restored(i.Value)) == 1
                                       && Languages.Status("csharp") is { State: Protocol.WorkspaceStates.Ready, Projects: 1 });
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("The WPF app's design-time build has no WPF references, XAML files or generated code: "
                + string.Join("; ", Solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error} restored={Restored(i.Value)}"))
                + "\n" + string.Join('\n', Log.TakeLast(40)));
        }

        var window = File("WpfApp", "MainWindow.xaml");
        var seen = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!seen && watch.Elapsed < 2 * Timeout)
        {
            var text = await System.IO.File.ReadAllTextAsync(window);
            var list = await Client.CompletionAsync(Protocol.DocumentUris.FromPath(window), PositionOf(text, text.IndexOf("<ListBox", StringComparison.Ordinal) + 1), new Protocol.CompletionContext(Protocol.CompletionTriggerKind.Invoked, null), CancellationToken.None);
            seen = list.Items.Any(i => i.Label == "Button");
            if (!seen)
            {
                await Task.Delay(2000);
            }
        }

        Assert.True(seen, $"The language host doesn't see WPF's types in MainWindow.xaml after {watch.Elapsed.TotalSeconds:0} s.\n" + string.Join('\n', Log.TakeLast(40)));
    }
}

/// <summary>
/// M40a (ADR 0069): WPF XAML in the C# language host, the M19 engine with WPF's dialect: namespaces from
/// <c>[XmlnsDefinition]</c>, dependency and attached properties by convention, attached events, WPF's markup
/// extensions, styles, triggers and setters, resources through merged dictionaries and App.xaml, binding paths from
/// <c>d:DataContext</c> (warnings: WPF's bindings aren't compiled), hover, Go to Definition, <c>x:Name</c> rename and
/// formatting. Roslyn only, so it runs on every OS.
/// </summary>
public sealed class WpfXamlTests(WpfLanguageFixture wpf) : IClassFixture<WpfLanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string MainWindow => wpf.File("WpfApp", "MainWindow.xaml");

    private string App => wpf.File("WpfApp", "App.xaml");

    private string Colors => wpf.File("WpfApp", "Themes", "Colors.xaml");

    private string Styles => wpf.File("WpfApp", "Themes", "Styles.xaml");

    private string Rating => wpf.File("WpfApp", "Controls", "RatingControl.xaml");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    private async Task FreshAsync(string path)
    {
        var disk = await File.ReadAllTextAsync(path, Ct);
        string current;
        try
        {
            current = wpf.Text(path).Text;
        }
        catch (KeyNotFoundException)
        {
            await wpf.OpenAsync(path);
            return;
        }

        if (current != disk)
        {
            await wpf.EditAsync(path, current, disk);
        }
    }

    private async Task<IReadOnlyList<CompletionItem>> CompleteAsync(string path, string marker, int delta)
    {
        var list = await wpf.Client.CompletionAsync(Uri(path), wpf.At(path, marker, delta), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        return list.Items;
    }

    private async Task<IReadOnlyList<string>> LabelsAsync(string path, string marker, int delta) =>
        [.. (await CompleteAsync(path, marker, delta)).Select(i => i.Label)];

    private async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string path, Func<IReadOnlyList<Diagnostic>, bool> until)
    {
        var version = wpf.Text(path).Version;
        IReadOnlyList<Diagnostic> current = [];
        try
        {
            await LanguageFixture.WaitUntilAsync(() =>
            {
                var file = wpf.Languages.Diagnostics.Get(path, "csharp");
                current = file?.Items.Where(d => d.Source == "XAML").ToList() ?? [];
                return file is not null && file.Version == version && until(current);
            }, TimeSpan.FromSeconds(60));
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No matching XAML diagnostics for {Path.GetFileName(path)} version {version}: {string.Join("; ", current.Select(d => $"{d.Code} {d.Message}"))}\n{string.Join('\n', wpf.Log.TakeLast(30))}");
        }

        return current;
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Severity} {d.Message}"));

    [Fact]
    public async Task The_fixtures_xaml_has_no_false_alarms()
    {
        // Each file with one planted error: once that is reported, nothing else may be.
        foreach (var path in new[] { MainWindow, App, Colors, Styles, Rating })
        {
            await FreshAsync(path);
            await wpf.EditAsync(path, "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Bogus=\"1\"");
            var diagnostics = await DiagnosticsAsync(path, d => d.Any(x => x.Message.Contains("'Bogus'", StringComparison.Ordinal)));
            Assert.True(diagnostics.Count == 1, $"{Path.GetFileName(path)}: {Describe(diagnostics)}");
            await FreshAsync(path);
        }
    }

    [Fact]
    public async Task Errors_and_warnings_are_reported_where_they_are()
    {
        await FreshAsync(MainWindow);

        // An unknown type and member are errors; a binding path the data context lacks and an unknown resource are
        // warnings (WPF reports them at run time).
        await wpf.EditAsync(MainWindow, "<ListBox Grid.Row=\"1\"", "<ListBx Grid.Row=\"1\"");
        await wpf.EditAsync(MainWindow, "HorizontalContentAlignment=\"Stretch\" />", "HorizontalContentAlignment=\"Stretch\" />\n        <Button Contnt=\"x\" />");
        await wpf.EditAsync(MainWindow, "Text=\"{Binding NewTitle,", "Text=\"{Binding NewTitel,");
        await wpf.EditAsync(MainWindow, "Style=\"{StaticResource AccentButton}\"", "Style=\"{StaticResource AccentButon}\"");
        var diagnostics = await DiagnosticsAsync(MainWindow, d => d.Count >= 4);
        Assert.Contains(diagnostics, d => d is { Code: "XAML0003", Severity: DiagnosticSeverity.Error } && d.Message.Contains("'ListBx'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d is { Code: "XAML0004", Severity: DiagnosticSeverity.Error } && d.Message.Contains("'Contnt'", StringComparison.Ordinal));
        var path = Assert.Single(diagnostics, d => d.Code == "XAML0006");
        Assert.Equal(DiagnosticSeverity.Warning, path.Severity);
        Assert.Contains("NewTitel", path.Message, StringComparison.Ordinal);
        Assert.Contains("MainViewModel", path.Message, StringComparison.Ordinal);
        var resource = Assert.Single(diagnostics, d => d.Code == "XAML0007");
        Assert.Equal(DiagnosticSeverity.Warning, resource.Severity);
        Assert.Contains("AccentButon", resource.Message, StringComparison.Ordinal);
        Assert.True(diagnostics.Count == 4, Describe(diagnostics));

        // A data template's paths go against its DataType; an ElementName's against the element.
        await FreshAsync(MainWindow);
        await wpf.EditAsync(MainWindow, "IsChecked=\"{Binding IsDone,", "IsChecked=\"{Binding IsDun,");
        await wpf.EditAsync(MainWindow, "{Binding ActualWidth, ElementName=NewTitleBox}", "{Binding ActualWidht, ElementName=NewTitleBox}");
        diagnostics = await DiagnosticsAsync(MainWindow, d => d.Count >= 2);
        Assert.Contains(diagnostics, d => d.Message.Contains("'IsDun' isn't a property of TodoItem", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'ActualWidht' isn't a property of TextBox", StringComparison.Ordinal));
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));

        // A setter's property must be its style's target type's; a trigger's too.
        await FreshAsync(MainWindow);
        await FreshAsync(Styles);
        await wpf.EditAsync(Styles, "<Setter Property=\"Foreground\" Value=\"White\" />", "<Setter Property=\"Forground\" Value=\"White\" />");
        await wpf.EditAsync(Styles, "<Trigger Property=\"IsMouseOver\"", "<Trigger Property=\"IsMouseOva\"");
        diagnostics = await DiagnosticsAsync(Styles, d => d.Count >= 2);
        Assert.Contains(diagnostics, d => d.Code == "XAML0004" && d.Message.Contains("'Button' has no property 'Forground'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0004" && d.Message.Contains("'IsMouseOva'", StringComparison.Ordinal));
        await FreshAsync(Styles);
    }

    [Fact]
    public async Task Elements_attributes_and_values_complete()
    {
        await FreshAsync(MainWindow);
        await wpf.EditAsync(MainWindow, "<ListBox Grid.Row=\"1\"", "<Bu />\n        <Button HorizontalAlignment=\"\" Visibility=\"\" Background=\"\" FontWeight=\"\" Cursor=\"\" />\n        <ListBox Grid.Row=\"1\"");

        var elements = await LabelsAsync(MainWindow, "<Bu />", 3);
        Assert.Contains("Button", elements);
        Assert.Contains("StackPanel", elements);
        Assert.Contains("controls:RatingControl", elements);
        Assert.Contains("Grid.RowDefinitions", elements);

        // Properties, events, attached properties (WPF's and the app's own), attached events, directives.
        var attributes = await LabelsAsync(MainWindow, "<Button HorizontalAlignment", "<Button ".Length);
        Assert.Contains("Content", attributes);
        Assert.Contains("Click", attributes);
        Assert.Contains("Command", attributes);
        Assert.Contains("Grid.Row", attributes);
        Assert.Contains("DockPanel.Dock", attributes);
        Assert.Contains("ToolTipService.ShowDuration", attributes);
        Assert.Contains("controls:Highlight.IsOn", attributes);
        Assert.Contains("x:Name", attributes);
        Assert.Contains("d:DataContext", attributes);
        Assert.DoesNotContain("x:DataType", attributes);

        // Values: enums, named colors, WPF's static classes of values.
        Assert.Contains("Stretch", await LabelsAsync(MainWindow, "HorizontalAlignment=\"\"", "HorizontalAlignment=\"".Length));
        Assert.Contains("Collapsed", await LabelsAsync(MainWindow, "Visibility=\"\"", "Visibility=\"".Length));
        Assert.Contains("CornflowerBlue", await LabelsAsync(MainWindow, "Background=\"\"", "Background=\"".Length));
        Assert.Contains("Bold", await LabelsAsync(MainWindow, "FontWeight=\"\"", "FontWeight=\"".Length));
        Assert.Contains("Hand", await LabelsAsync(MainWindow, "Cursor=\"\"", "Cursor=\"".Length));

        // The user control: its dependency properties.
        var rating = await LabelsAsync(MainWindow, "<controls:RatingControl Value", "<controls:RatingControl ".Length);
        Assert.Contains("Foreground", rating);

        // xmlns: WPF's namespace and the project's as clr-namespace:.
        var namespaces = await LabelsAsync(MainWindow, "xmlns:vm=\"clr-namespace:WpfApp.ViewModels\"", "xmlns:vm=\"".Length);
        Assert.Contains("http://schemas.microsoft.com/winfx/2006/xaml/presentation", namespaces);
        Assert.Contains("clr-namespace:WpfApp.Controls", namespaces);
        Assert.DoesNotContain("https://github.com/avaloniaui", namespaces);
    }

    [Fact]
    public async Task Markup_extensions_resources_and_binding_paths_complete()
    {
        await FreshAsync(MainWindow);
        await wpf.EditAsync(MainWindow, "Margin=\"0,8\" ItemsSource", "Margin=\"0,8\" Tag=\"{\" Foreground=\"{StaticResource }\" MinWidth=\"{Binding Items.}\" Width=\"{Binding NewTitle, Mode=}\" Height=\"{Binding NewTitle, UpdateSourceTrigger=}\" ItemsSource");

        var extensions = await LabelsAsync(MainWindow, "Tag=\"{\"", "Tag=\"{".Length);
        Assert.Contains("Binding", extensions);
        Assert.Contains("StaticResource", extensions);
        Assert.Contains("DynamicResource", extensions);
        Assert.Contains("RelativeSource", extensions);
        Assert.Contains("x:Static", extensions);
        Assert.DoesNotContain("CompiledBinding", extensions);

        // Resources: the window's own, App.xaml's and the dictionaries it merges.
        var resources = await LabelsAsync(MainWindow, "{StaticResource }", "{StaticResource ".Length);
        Assert.Contains("AccentBrush", resources);
        Assert.Contains("AccentButton", resources);
        Assert.Contains("PagePadding", resources);
        Assert.Contains("TodoTemplate", resources);

        // Binding paths from d:DataContext's design instance (properties only), through a collection.
        var members = await LabelsAsync(MainWindow, "Text=\"{Binding NewTitle,", "Text=\"{Binding ".Length);
        Assert.Contains("Items", members);
        Assert.Contains("NewTitle", members);
        Assert.Contains("AddCommand", members);
        Assert.Contains("RemainingCount", members);
        Assert.DoesNotContain("Add", members);
        Assert.Contains("Count", await LabelsAsync(MainWindow, "{Binding Items.}", "{Binding Items.".Length));
        Assert.Contains("IsDone", await LabelsAsync(MainWindow, "IsChecked=\"{Binding IsDone", "IsChecked=\"{Binding ".Length));
        Assert.Contains("ActualWidth", await LabelsAsync(MainWindow, "{Binding ActualWidth, ElementName", "{Binding ".Length));
        Assert.Contains("TwoWay", await LabelsAsync(MainWindow, "Mode=}", "Mode=".Length));
        Assert.Contains("PropertyChanged", await LabelsAsync(MainWindow, "UpdateSourceTrigger=}", "UpdateSourceTrigger=".Length));
        Assert.Contains("vm:MainViewModel", await LabelsAsync(MainWindow, "DesignInstance Type=vm:MainViewModel", "DesignInstance Type=".Length));

        // A user control's ancestor binding and its own element name.
        await FreshAsync(Rating);
        Assert.Contains("Maximum", await LabelsAsync(Rating, "{Binding Maximum, RelativeSource", "{Binding ".Length));
        Assert.Contains("Value", await LabelsAsync(Rating, "{Binding Value, ElementName=Root}", "{Binding ".Length));
        Assert.Contains("local:RatingControl", await LabelsAsync(Rating, "AncestorType={x:Type local:RatingControl}", "AncestorType={x:Type ".Length));
    }

    [Fact]
    public async Task Styles_setters_triggers_and_templates_complete()
    {
        await FreshAsync(Styles);
        await wpf.EditAsync(Styles, "<Setter Property=\"Padding\" Value=\"12,4\" />", "<Setter Property=\"Padding\" Value=\"12,4\" />\n        <Setter Property=\"\" Value=\"\" />\n        <Setter Property=\"HorizontalAlignment\" Value=\"\" />");
        var properties = await LabelsAsync(Styles, "<Setter Property=\"\"", "<Setter Property=\"".Length);
        Assert.Contains("Background", properties);
        Assert.Contains("Content", properties);
        Assert.Contains("Center", await LabelsAsync(Styles, "<Setter Property=\"HorizontalAlignment\" Value=\"\"", "<Setter Property=\"HorizontalAlignment\" Value=\"".Length));
        Assert.Contains("IsPressed", await LabelsAsync(Styles, "<Trigger Property=\"IsMouseOver\"", "<Trigger Property=\"".Length));
        Assert.Contains("True", await LabelsAsync(Styles, "<Trigger Property=\"IsMouseOver\" Value=\"True\"", "<Trigger Property=\"IsMouseOver\" Value=\"".Length));
        Assert.Contains("Padding", await LabelsAsync(Styles, "{TemplateBinding Background}", "{TemplateBinding ".Length));

        // A template trigger's setter names an element of the template: its properties are that element's.
        Assert.Contains("Chrome", await LabelsAsync(Styles, "<Setter TargetName=\"Chrome\"", "<Setter TargetName=\"".Length));
        Assert.Contains("CornerRadius", await LabelsAsync(Styles, "<Setter TargetName=\"Chrome\" Property=\"Opacity\"", "<Setter TargetName=\"Chrome\" Property=\"".Length));

        // App.xaml: the first window and the merged dictionaries are the project's XAML files.
        await FreshAsync(App);
        Assert.Contains("MainWindow.xaml", await LabelsAsync(App, "StartupUri=\"MainWindow.xaml\"", "StartupUri=\"".Length));
        Assert.Contains("Themes/Colors.xaml", await LabelsAsync(App, "Source=\"Themes/Colors.xaml\"", "Source=\"".Length));
        await FreshAsync(Styles);
    }

    [Fact]
    public async Task Hover_and_definition_reach_wpf_types_the_code_and_resources()
    {
        await FreshAsync(MainWindow);
        var hover = await wpf.Client.HoverAsync(Uri(MainWindow), wpf.At(MainWindow, "<ListBox Grid", 2), Ct);
        Assert.Contains("class System.Windows.Controls.ListBox", hover?.Contents.Value, StringComparison.Ordinal);
        var attached = await wpf.Client.HoverAsync(Uri(MainWindow), wpf.At(MainWindow, "DockPanel.Dock=\"Right\"", 12), Ct);
        Assert.Contains("Dock", attached?.Contents.Value, StringComparison.Ordinal);

        var path = Assert.Single(await wpf.Client.DefinitionAsync(Uri(MainWindow), wpf.At(MainWindow, "{Binding NewTitle,", "{Binding Ne".Length), Ct));
        Assert.EndsWith("MainViewModel.cs", path.Uri, StringComparison.Ordinal);
        var control = Assert.Single(await wpf.Client.DefinitionAsync(Uri(MainWindow), wpf.At(MainWindow, "<controls:RatingControl", "<controls:Ra".Length), Ct));
        Assert.EndsWith("RatingControl.xaml.cs", control.Uri, StringComparison.Ordinal);
        var resource = Assert.Single(await wpf.Client.DefinitionAsync(Uri(MainWindow), wpf.At(MainWindow, "{StaticResource AccentButton}", "{StaticResource Ac".Length), Ct));
        Assert.EndsWith("Styles.xaml", resource.Uri, StringComparison.Ordinal);
        var codeBehind = Assert.Single(await wpf.Client.DefinitionAsync(Uri(MainWindow), wpf.At(MainWindow, "x:Class=\"WpfApp.MainWindow\"", "x:Class=\"Wp".Length), Ct));
        Assert.EndsWith("MainWindow.xaml.cs", codeBehind.Uri, StringComparison.Ordinal); // not the markup compiler's MainWindow.g.cs
        var handler = Assert.Single(await wpf.Client.DefinitionAsync(Uri(MainWindow), wpf.At(MainWindow, "Click=\"OnAddClicked\"", "Click=\"On".Length), Ct));
        Assert.EndsWith("MainWindow.xaml.cs", handler.Uri, StringComparison.Ordinal);
        var design = Assert.Single(await wpf.Client.DefinitionAsync(Uri(MainWindow), wpf.At(MainWindow, "Type=vm:MainViewModel", "Type=vm:Ma".Length), Ct));
        Assert.EndsWith("MainViewModel.cs", design.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_an_x_name_follows_element_names_and_the_code_behind()
    {
        await FreshAsync(MainWindow);
        var codeBehind = wpf.File("WpfApp", "MainWindow.xaml.cs");
        var prepare = await wpf.Client.PrepareRenameAsync(Uri(MainWindow), wpf.At(MainWindow, "x:Name=\"NewTitleBox\"", "x:Name=\"Ne".Length), Ct);
        Assert.Equal("NewTitleBox", prepare?.Placeholder);

        var edit = await wpf.Client.RenameAsync(Uri(MainWindow), wpf.At(MainWindow, "x:Name=\"NewTitleBox\"", "x:Name=\"Ne".Length), "TitleInput", Ct);
        Assert.NotNull(edit);
        var xaml = Assert.Single(edit.DocumentChanges, c => c.TextDocument.Uri == Uri(MainWindow));
        Assert.Equal(2, xaml.Edits.Length); // x:Name and ElementName
        var cs = Assert.Single(edit.DocumentChanges, c => c.TextDocument.Uri == Uri(codeBehind));
        Assert.Single(cs.Edits);
        Assert.DoesNotContain(edit.DocumentChanges, c => c.TextDocument.Uri.EndsWith(".g.cs", StringComparison.Ordinal) || c.TextDocument.Uri.EndsWith(".g.i.cs", StringComparison.Ordinal));
        Assert.All(edit.DocumentChanges.SelectMany(c => c.Edits), e => Assert.Equal("TitleInput", e.NewText));
    }

    [Fact]
    public async Task Formatting_keeps_the_fixture_and_indents_what_is_off()
    {
        foreach (var path in new[] { Colors, Rating })
        {
            await FreshAsync(path);
            Assert.Empty(await wpf.Client.FormatAsync(Uri(path), null, new FormattingOptions(4, true), Ct));
        }

        await wpf.EditAsync(Colors, "    <SolidColorBrush x:Key=\"DoneBrush\"", "<SolidColorBrush x:Key=\"DoneBrush\"");
        var edits = await wpf.Client.FormatAsync(Uri(Colors), null, new FormattingOptions(4, true), Ct);
        var edit = Assert.Single(edits);
        Assert.Equal("    ", edit.NewText);
        await FreshAsync(Colors);
    }
}
