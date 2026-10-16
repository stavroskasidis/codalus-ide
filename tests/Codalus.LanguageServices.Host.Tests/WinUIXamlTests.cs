using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// samples/WindowsDesktop's WinUI 3 app (WinUIApp.slnx) in the real build and language hosts, on every OS: the Windows
/// App SDK's packages restore everywhere with <c>EnableWindowsTargeting</c>, and off Windows the build host leaves out
/// the design-time pass of WinUI's XAML compiler (a Windows program), so the project loads with WinUI's references.
/// </summary>
public sealed class WinUILanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("WindowsDesktop", "WinUIApp.slnx", 1);

    /// <summary>The design-time build after the restore (WinUI's references, its XAML as additional files), and the host seeing WinUI's types.</summary>
    protected override async Task ReadyAsync()
    {
        static bool Restored(Codalus.Projects.Protocol.CompilerInputsInfo inputs) =>
            inputs.Arguments.Any(a => a.Contains("Microsoft.WinUI.dll", StringComparison.OrdinalIgnoreCase))
            && inputs.Arguments.Any(a => a.StartsWith("/additionalfile:", StringComparison.OrdinalIgnoreCase) && a.EndsWith("MainPage.xaml", StringComparison.OrdinalIgnoreCase));
        try
        {
            await WaitUntilAsync(() => Solutions.CompilerInputs.Count(i => Restored(i.Value)) == 1
                                       && Languages.Status("csharp") is { State: Protocol.WorkspaceStates.Ready, Projects: 1 });
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("The WinUI app's design-time build has no WinUI references or XAML files: "
                + string.Join("; ", Solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error} restored={Restored(i.Value)}"))
                + "\n" + string.Join('\n', Log.TakeLast(40)));
        }

        var page = File("WinUIApp", "MainPage.xaml");
        var seen = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!seen && watch.Elapsed < 2 * Timeout)
        {
            var text = await System.IO.File.ReadAllTextAsync(page);
            var list = await Client.CompletionAsync(Protocol.DocumentUris.FromPath(page), PositionOf(text, text.IndexOf("<ListView", StringComparison.Ordinal) + 1), new Protocol.CompletionContext(Protocol.CompletionTriggerKind.Invoked, null), CancellationToken.None);
            seen = list.Items.Any(i => i.Label == "Button");
            if (!seen)
            {
                await Task.Delay(2000);
            }
        }

        Assert.True(seen, $"The language host doesn't see WinUI's types in MainPage.xaml after {watch.Elapsed.TotalSeconds:0} s.\n" + string.Join('\n', Log.TakeLast(40)));
    }
}

/// <summary>
/// M44 (ADR 0074): WinUI 3 XAML in the C# language host, the M19 engine with WinUI's dialect: <c>Microsoft.UI.Xaml</c>'s
/// namespaces for the presentation namespace, the projection's dependency and attached properties, <c>x:Bind</c>
/// compiled against the page's class or a data template's <c>x:DataType</c> (errors, as WinUI's XAML compiler reports
/// them), <c>x:Load</c> and <c>x:DefaultBindMode</c>, <c>ThemeResource</c>, <c>StaticResource</c> and
/// <c>CustomResource</c>, and resources through <c>App.xaml</c>'s merged dictionaries (<c>ms-appx:///</c>). Roslyn only,
/// so it runs on every OS.
/// </summary>
public sealed class WinUIXamlTests(WinUILanguageFixture winui) : IClassFixture<WinUILanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string MainPage => winui.File("WinUIApp", "MainPage.xaml");

    private string MainWindow => winui.File("WinUIApp", "MainWindow.xaml");

    private string App => winui.File("WinUIApp", "App.xaml");

    private string Colors => winui.File("WinUIApp", "Themes", "Colors.xaml");

    private string Rating => winui.File("WinUIApp", "Controls", "RatingControl.xaml");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    private async Task FreshAsync(string path)
    {
        var disk = await File.ReadAllTextAsync(path, Ct);
        string current;
        try
        {
            current = winui.Text(path).Text;
        }
        catch (KeyNotFoundException)
        {
            await winui.OpenAsync(path);
            return;
        }

        if (current != disk)
        {
            await winui.EditAsync(path, current, disk);
        }
    }

    private async Task<IReadOnlyList<string>> LabelsAsync(string path, string marker, int delta)
    {
        var list = await winui.Client.CompletionAsync(Uri(path), winui.At(path, marker, delta), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        return [.. list.Items.Select(i => i.Label)];
    }

    private async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string path, Func<IReadOnlyList<Diagnostic>, bool> until)
    {
        var version = winui.Text(path).Version;
        IReadOnlyList<Diagnostic> current = [];
        try
        {
            await LanguageFixture.WaitUntilAsync(() =>
            {
                var file = winui.Languages.Diagnostics.Get(path, "csharp");
                current = file?.Items.Where(d => d.Source == "XAML").ToList() ?? [];
                return file is not null && file.Version == version && until(current);
            }, TimeSpan.FromSeconds(60));
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No matching XAML diagnostics for {Path.GetFileName(path)} version {version}: {string.Join("; ", current.Select(d => $"{d.Code} {d.Message}"))}\n{string.Join('\n', winui.Log.TakeLast(30))}");
        }

        return current;
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Severity} {d.Message}"));

    [Fact]
    public async Task The_fixtures_xaml_has_no_false_alarms()
    {
        // Each file with one planted error: once that is reported, nothing else may be (x:Bind paths, x:Load, theme
        // resources the framework brings, the projection's attached properties, ms-appx:/// dictionaries).
        foreach (var path in new[] { MainPage, MainWindow, App, Colors, Rating })
        {
            await FreshAsync(path);
            await winui.EditAsync(path, "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Bogus=\"1\"");
            var diagnostics = await DiagnosticsAsync(path, d => d.Any(x => x.Message.Contains("'Bogus'", StringComparison.Ordinal)));
            Assert.True(diagnostics.Count == 1, $"{Path.GetFileName(path)}: {Describe(diagnostics)}");
            await FreshAsync(path);
        }
    }

    [Fact]
    public async Task X_bind_paths_are_errors_where_they_dont_compile()
    {
        await FreshAsync(MainPage);

        // From the page's class, through the view model; in a data template from its x:DataType; through a named
        // element; in x:Load. Unknown types, members and directive values are errors too.
        await winui.EditAsync(MainPage, "Text=\"{x:Bind ViewModel.NewTitle,", "Text=\"{x:Bind ViewModel.NewTitel,");
        await winui.EditAsync(MainPage, "Text=\"{x:Bind Title}\"", "Text=\"{x:Bind Titel}\"");
        await winui.EditAsync(MainPage, "Text=\"{x:Bind NewTitleBox.Text}\"", "Text=\"{x:Bind NewTitleBox.Txt}\"");
        await winui.EditAsync(MainPage, "x:Load=\"{x:Bind ViewModel.IsTyping}\"", "x:Load=\"{x:Bind ViewModel.IsTypin}\"");
        await winui.EditAsync(MainPage, "<ListView Grid.Row=\"1\"", "<ListVew Grid.Row=\"1\"");
        await winui.EditAsync(MainPage, "x:DefaultBindMode=\"OneWay\"", "x:DefaultBindMode=\"Sometimes\"");
        var diagnostics = await DiagnosticsAsync(MainPage, d => d.Count >= 6);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(diagnostics, d => d.Code == "XAML0006" && d.Message.Contains("'NewTitel' isn't a property, field or method of MainViewModel", StringComparison.Ordinal) && d.Message.Contains("MainPage", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0006" && d.Message.Contains("'Titel'", StringComparison.Ordinal) && d.Message.Contains("TodoItem", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0006" && d.Message.Contains("'Txt' isn't a property, field or method of TextBox", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0006" && d.Message.Contains("'IsTypin'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0003" && d.Message.Contains("'ListVew'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "XAML0005" && d.Message.Contains("'Sometimes'", StringComparison.Ordinal));
        Assert.True(diagnostics.Count == 6, Describe(diagnostics));

        // A setter's property must be its style's target type's; an unknown resource isn't reported (the framework's
        // theme resources aren't listed anywhere).
        await FreshAsync(MainPage);
        await FreshAsync(Colors);
        await winui.EditAsync(Colors, "<Setter Property=\"FontSize\" Value=\"14\" />", "<Setter Property=\"FontSiz\" Value=\"14\" />\n        <Setter Property=\"Foreground\" Value=\"{StaticResource NoSuchBrush}\" />");
        diagnostics = await DiagnosticsAsync(Colors, d => d.Count >= 1);
        var setter = Assert.Single(diagnostics);
        Assert.Equal("XAML0004", setter.Code);
        Assert.Contains("'TextBlock' has no property 'FontSiz'", setter.Message, StringComparison.Ordinal);
        await FreshAsync(Colors);
    }

    [Fact]
    public async Task Elements_attributes_and_values_complete()
    {
        await FreshAsync(MainPage);
        await winui.EditAsync(MainPage, "<ListView Grid.Row=\"1\"", "<Bu />\n        <Button HorizontalAlignment=\"\" Visibility=\"\" Background=\"\" FontWeight=\"\" />\n        <ListView Grid.Row=\"1\"");

        var elements = await LabelsAsync(MainPage, "<Bu />", 3);
        Assert.Contains("Button", elements);
        Assert.Contains("StackPanel", elements);
        Assert.Contains("NavigationView", elements);
        Assert.Contains("controls:RatingControl", elements);
        Assert.Contains("Grid.RowDefinitions", elements);

        // Properties, events, the projection's attached properties, WinUI's directives.
        var attributes = await LabelsAsync(MainPage, "<Button HorizontalAlignment", "<Button ".Length);
        Assert.Contains("Content", attributes);
        Assert.Contains("Click", attributes);
        Assert.Contains("Command", attributes);
        Assert.Contains("Grid.Row", attributes);
        Assert.Contains("ToolTipService.ToolTip", attributes);
        Assert.Contains("x:Name", attributes);
        Assert.Contains("x:Load", attributes);
        Assert.Contains("x:Phase", attributes);
        Assert.Contains("x:Uid", attributes);
        Assert.DoesNotContain("x:CompileBindings", attributes);

        // Values: enums, WinUI's named colors and font weights.
        Assert.Contains("Stretch", await LabelsAsync(MainPage, "HorizontalAlignment=\"\"", "HorizontalAlignment=\"".Length));
        Assert.Contains("Collapsed", await LabelsAsync(MainPage, "Visibility=\"\"", "Visibility=\"".Length));
        Assert.Contains("CornflowerBlue", await LabelsAsync(MainPage, "Background=\"\"", "Background=\"".Length));
        Assert.Contains("SemiBold", await LabelsAsync(MainPage, "FontWeight=\"\"", "FontWeight=\"".Length));
        Assert.Contains("TwoWay", await LabelsAsync(MainPage, "x:DefaultBindMode=\"OneWay\"", "x:DefaultBindMode=\"".Length));

        // The user control's inherited properties (its own are set already); xmlns with WinUI's namespace and the project's as using:.
        var rating = await LabelsAsync(MainPage, "<controls:RatingControl Value", "<controls:RatingControl ".Length);
        Assert.Contains("IsTabStop", rating);
        Assert.Contains("Foreground", rating);
        var namespaces = await LabelsAsync(MainPage, "xmlns:models=\"using:WinUIApp.Models\"", "xmlns:models=\"".Length);
        Assert.Contains("http://schemas.microsoft.com/winfx/2006/xaml/presentation", namespaces);
        Assert.Contains("using:WinUIApp.Controls", namespaces);
        Assert.DoesNotContain("clr-namespace:WinUIApp.Controls", namespaces);
    }

    [Fact]
    public async Task Markup_extensions_resources_and_x_bind_paths_complete()
    {
        await FreshAsync(MainPage);
        await winui.EditAsync(MainPage, "SelectionMode=\"None\"", "SelectionMode=\"None\" Tag=\"{\" Foreground=\"{ThemeResource }\" MinWidth=\"{x:Bind ViewModel.}\" Width=\"{x:Bind ViewModel.Rating, Mode=}\"");

        var extensions = await LabelsAsync(MainPage, "Tag=\"{\"", "Tag=\"{".Length);
        Assert.Contains("x:Bind", extensions);
        Assert.Contains("Binding", extensions);
        Assert.Contains("StaticResource", extensions);
        Assert.Contains("ThemeResource", extensions);
        Assert.Contains("CustomResource", extensions);
        Assert.DoesNotContain("DynamicResource", extensions);

        // Resources: the page's own, App.xaml's and the dictionaries it merges through ms-appx:///.
        var resources = await LabelsAsync(MainPage, "{ThemeResource }", "{ThemeResource ".Length);
        Assert.Contains("AccentBrush", resources);
        Assert.Contains("ItemText", resources);
        Assert.Contains("PagePadding", resources);
        Assert.Contains("TodoTemplate", resources);

        // x:Bind from the page: its properties, methods (function bindings) and named elements; then the view model's.
        var root = await LabelsAsync(MainPage, "Text=\"{x:Bind ViewModel.NewTitle,", "Text=\"{x:Bind ".Length);
        Assert.Contains("ViewModel", root);
        Assert.Contains("FormatRemaining", root);
        Assert.Contains("NewTitleBox", root);
        var members = await LabelsAsync(MainPage, "{x:Bind ViewModel.}", "{x:Bind ViewModel.".Length);
        Assert.Contains("Items", members);
        Assert.Contains("NewTitle", members);
        Assert.Contains("IsTyping", members);
        Assert.Contains("Title", await LabelsAsync(MainPage, "Text=\"{x:Bind Title}\"", "Text=\"{x:Bind ".Length));
        Assert.Contains("TwoWay", await LabelsAsync(MainPage, "Mode=}", "Mode=".Length));
        Assert.Contains("BindBack=", await LabelsAsync(MainPage, "{x:Bind ViewModel.Rating, Mode=}", "{x:Bind ViewModel.Rating, ".Length));

        // App.xaml: a merged dictionary's source is the project's dictionary as an ms-appx:/// URI.
        await FreshAsync(App);
        Assert.Contains("ms-appx:///Themes/Colors.xaml", await LabelsAsync(App, "Source=\"ms-appx:///Themes/Colors.xaml\"", "Source=\"".Length));
        await FreshAsync(MainPage);
    }

    [Fact]
    public async Task Hover_and_definition_reach_winui_types_the_code_and_resources()
    {
        await FreshAsync(MainPage);
        var hover = await winui.Client.HoverAsync(Uri(MainPage), winui.At(MainPage, "<ListView Grid", 2), Ct);
        Assert.Contains("Microsoft.UI.Xaml.Controls.ListView", hover?.Contents.Value, StringComparison.Ordinal);

        var viewModel = Assert.Single(await winui.Client.DefinitionAsync(Uri(MainPage), winui.At(MainPage, "{x:Bind ViewModel.NewTitle,", "{x:Bind Vi".Length), Ct));
        Assert.EndsWith("MainPage.xaml.cs", viewModel.Uri, StringComparison.Ordinal);
        var property = Assert.Single(await winui.Client.DefinitionAsync(Uri(MainPage), winui.At(MainPage, "{x:Bind ViewModel.NewTitle,", "{x:Bind ViewModel.Ne".Length), Ct));
        Assert.EndsWith("MainViewModel.cs", property.Uri, StringComparison.Ordinal);
        var control = Assert.Single(await winui.Client.DefinitionAsync(Uri(MainPage), winui.At(MainPage, "<controls:RatingControl", "<controls:Ra".Length), Ct));
        Assert.EndsWith("RatingControl.xaml.cs", control.Uri, StringComparison.Ordinal);
        var resource = Assert.Single(await winui.Client.DefinitionAsync(Uri(MainPage), winui.At(MainPage, "{StaticResource ItemText}", "{StaticResource It".Length), Ct));
        Assert.EndsWith("Colors.xaml", resource.Uri, StringComparison.Ordinal);
        var handler = Assert.Single(await winui.Client.DefinitionAsync(Uri(MainPage), winui.At(MainPage, "Click=\"OnAddClicked\"", "Click=\"On".Length), Ct));
        Assert.EndsWith("MainPage.xaml.cs", handler.Uri, StringComparison.Ordinal);
    }
}
