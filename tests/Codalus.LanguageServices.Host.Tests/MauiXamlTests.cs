using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>samples/Maui's library of pages (MauiViews.slnx) in the real build and language hosts: no workload needed.</summary>
public sealed class MauiLanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("Maui", "MauiViews.slnx", 1);

    /// <summary>The design-time build after the restore (MAUI's references and its XAML as additional files), and the host seeing MAUI's types.</summary>
    protected override async Task ReadyAsync()
    {
        static bool Restored(Codalus.Projects.Protocol.CompilerInputsInfo inputs) =>
            inputs.Arguments.Any(a => a.Contains("Microsoft.Maui.Controls.dll", StringComparison.OrdinalIgnoreCase))
            && inputs.Arguments.Any(a => a.StartsWith("/additionalfile:", StringComparison.OrdinalIgnoreCase) && a.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
        try
        {
            await WaitUntilAsync(() => Solutions.CompilerInputs.Count(i => Restored(i.Value)) == 1
                                       && Languages.Status("csharp") is { State: Protocol.WorkspaceStates.Ready, Projects: 1 });
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("The library's design-time build has no MAUI references or XAML files: "
                + string.Join("; ", Solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} restored={Restored(i.Value)}"))
                + "\n" + string.Join('\n', Log.TakeLast(40)));
        }

        var page = File("MauiViews", "Views", "TodoPage.xaml");
        var seen = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!seen && watch.Elapsed < 2 * Timeout)
        {
            var text = await System.IO.File.ReadAllTextAsync(page);
            var list = await Client.CompletionAsync(Protocol.DocumentUris.FromPath(page), PositionOf(text, text.IndexOf("<CollectionView", StringComparison.Ordinal) + 1), new Protocol.CompletionContext(Protocol.CompletionTriggerKind.Invoked, null), CancellationToken.None);
            seen = list.Items.Any(i => i.Label == "Button");
            if (!seen)
            {
                await Task.Delay(2000);
            }
        }

        Assert.True(seen, $"The language host doesn't see MAUI's types in TodoPage.xaml after {watch.Elapsed.TotalSeconds:0} s.\n" + string.Join('\n', Log.TakeLast(40)));
    }
}

/// <summary>
/// M20a (ADR 0064): .NET MAUI XAML in the C# language host, the M19 engine with MAUI's dialect: the XAML 2009
/// <c>x:</c> namespace, <c>clr-namespace:</c> prefixes, bindable and attached properties (MAUI's and the project's
/// own), <c>[ContentProperty]</c>, named colors, compiled bindings under <c>x:DataType</c>, diagnostics with no false
/// alarms, hover and Go to Definition.
/// </summary>
public sealed class MauiXamlTests(MauiLanguageFixture maui) : IClassFixture<MauiLanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Page => maui.File("MauiViews", "Views", "TodoPage.xaml");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    private async Task FreshAsync()
    {
        var disk = await File.ReadAllTextAsync(Page, Ct);
        string current;
        try
        {
            current = maui.Text(Page).Text;
        }
        catch (KeyNotFoundException)
        {
            await maui.OpenAsync(Page);
            return;
        }

        if (current != disk)
        {
            await maui.EditAsync(Page, current, disk);
        }
    }

    private async Task<IReadOnlyList<CompletionItem>> CompleteAsync(string marker, int delta)
    {
        var list = await maui.Client.CompletionAsync(Uri(Page), maui.At(Page, marker, delta), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        return list.Items;
    }

    private async Task<IReadOnlyList<string>> LabelsAsync(string marker, int delta) => [.. (await CompleteAsync(marker, delta)).Select(i => i.Label)];

    private async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(Func<IReadOnlyList<Diagnostic>, bool> until)
    {
        var version = maui.Text(Page).Version;
        IReadOnlyList<Diagnostic> current = [];
        try
        {
            await LanguageFixture.WaitUntilAsync(() =>
            {
                var file = maui.Languages.Diagnostics.Get(Page, "csharp");
                current = file?.Items.Where(d => d.Source == "XAML").ToList() ?? [];
                return file is not null && file.Version == version && until(current);
            }, TimeSpan.FromSeconds(60));
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No matching XAML diagnostics for version {version}: {string.Join("; ", current.Select(d => $"{d.Code} {d.Message}"))}\n{string.Join('\n', maui.Log.TakeLast(30))}");
        }

        return current;
    }

    [Fact]
    public async Task The_page_has_no_false_alarms_and_errors_are_reported()
    {
        await FreshAsync();
        await maui.EditAsync(Page, "<Label Text=\"Remaining\"", "<Label Bogus=\"1\" Text=\"Remaining\"");
        var diagnostics = await DiagnosticsAsync(d => d.Any(x => x.Message.Contains("'Bogus'", StringComparison.Ordinal)));
        Assert.True(diagnostics.Count == 1, string.Join("; ", diagnostics.Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Message}")));

        // A type that isn't there, and a binding path the data type doesn't have.
        await maui.EditAsync(Page, "<Label Bogus=\"1\" Text=\"Remaining\"", "<Labl Text=\"Remaining\"");
        await maui.EditAsync(Page, "Text=\"{Binding Title}\"", "Text=\"{Binding Titel}\"");
        diagnostics = await DiagnosticsAsync(d => d.Any(x => x.Message.Contains("'Labl'", StringComparison.Ordinal)) && d.Any(x => x.Message.Contains("Titel", StringComparison.Ordinal)));
        Assert.Equal(2, diagnostics.Count);
        await FreshAsync();
    }

    [Fact]
    public async Task Elements_attributes_values_and_binding_paths_complete()
    {
        await FreshAsync();
        await maui.EditAsync(Page, "<CollectionView Grid.Row=\"1\"", "<Bu />\n    <Button HorizontalOptions=\"\" IsEnabled=\"\" TextColor=\"\" />\n    <CollectionView Grid.Row=\"1\"");

        var elements = await LabelsAsync("<Bu />", 3);
        Assert.Contains("Button", elements);
        Assert.Contains("CollectionView", elements);
        Assert.Contains("controls:Badge", elements);
        Assert.Contains("Grid.RowDefinitions", elements);

        // MAUI's bindable properties (with CLR wrappers), events, attached properties (MAUI's and the library's).
        var attributes = await LabelsAsync("<Button HorizontalOptions", "<Button ".Length);
        Assert.Contains("Text", attributes);
        Assert.Contains("Command", attributes);
        Assert.Contains("Clicked", attributes);
        Assert.Contains("Grid.Row", attributes);
        Assert.Contains("controls:Badge.IsHighlighted", attributes);
        Assert.Contains("x:Name", attributes);

        Assert.Contains("True", await LabelsAsync("IsEnabled=\"\"", "IsEnabled=\"".Length));
        Assert.Contains("CornflowerBlue", await LabelsAsync("TextColor=\"\"", "TextColor=\"".Length));

        // The library's control: its own bindable property.
        Assert.Contains("Count", await LabelsAsync("<controls:Badge Count", "<controls:Badge ".Length));

        // Compiled binding paths: the page's x:DataType, and the item template's.
        var members = await LabelsAsync("{Binding RemainingCount}", "{Binding ".Length);
        Assert.Contains("Items", members);
        Assert.Contains("NewTitle", members);
        Assert.Contains("AddCommand", members);
        Assert.Contains("IsDone", await LabelsAsync("{Binding Title}", "{Binding ".Length));

        // xmlns values: MAUI's namespaces and the project's as clr-namespace:.
        await FreshAsync();
        await maui.EditAsync(Page, "xmlns:vm=\"clr-namespace:MauiViews.ViewModels\"", "xmlns:vm=\"clr-namespace:MauiViews.ViewModels\" xmlns:more=\"\"");
        var namespaces = await LabelsAsync("xmlns:more=\"\"", "xmlns:more=\"".Length);
        Assert.Contains("http://schemas.microsoft.com/dotnet/2021/maui", namespaces);
        Assert.Contains("http://schemas.microsoft.com/winfx/2009/xaml", namespaces);
        Assert.Contains("clr-namespace:MauiViews.Controls", namespaces);
        Assert.DoesNotContain("https://github.com/avaloniaui", namespaces);
        await FreshAsync();
    }

    [Fact]
    public async Task Hover_and_definition_reach_maui_types_the_view_model_and_the_control()
    {
        await FreshAsync();
        var hover = await maui.Client.HoverAsync(Uri(Page), maui.At(Page, "<CollectionView Grid", 2), Ct);
        Assert.Contains("class Microsoft.Maui.Controls.CollectionView", hover?.Contents.Value, StringComparison.Ordinal);

        var path = Assert.Single(await maui.Client.DefinitionAsync(Uri(Page), maui.At(Page, "{Binding NewTitle}", "{Binding Ne".Length), Ct));
        Assert.EndsWith("TodoViewModel.cs", path.Uri, StringComparison.Ordinal);
        var badge = Assert.Single(await maui.Client.DefinitionAsync(Uri(Page), maui.At(Page, "<controls:Badge", "<controls:Ba".Length), Ct));
        Assert.EndsWith("Badge.cs", badge.Uri, StringComparison.Ordinal);
        var codeBehind = await maui.Client.DefinitionAsync(Uri(Page), maui.At(Page, "x:Class=\"MauiViews.Views.TodoPage\"", "x:Class=\"Ma".Length), Ct);
        Assert.Contains(codeBehind, l => l.Uri.EndsWith("TodoPage.xaml.cs", StringComparison.Ordinal));
    }
}
