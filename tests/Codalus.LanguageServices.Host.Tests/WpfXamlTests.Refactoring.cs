using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// M64 (ADR 0103) on samples/WindowsDesktop's WPF app: rename across C# and XAML both ways (binding paths from
/// <c>d:DataContext</c>, <c>{x:Type}</c>, event handlers, resource keys through merged dictionaries; never WPF's
/// generated <c>obj/*.g.cs</c>), Extract Resource, Extract Style and generate from use, checked against golden files.
/// </summary>
public sealed partial class WpfXamlTests
{
    private string ViewModel => wpf.File("WpfApp", "ViewModels", "MainViewModel.cs");

    private string Render(WorkspaceEdit? edit)
    {
        Assert.NotNull(edit);
        Assert.DoesNotContain(edit.DocumentChanges, c => c.TextDocument.Uri.EndsWith(".g.cs", StringComparison.Ordinal) || c.TextDocument.Uri.EndsWith(".g.i.cs", StringComparison.Ordinal));
        return XamlGolden.Render(edit, wpf.Fixture, path =>
        {
            try
            {
                return wpf.Text(path).Text;
            }
            catch (KeyNotFoundException)
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
        });
    }

    private async Task<WorkspaceEdit?> RenameAtAsync(string path, string marker, int delta, string newName, string placeholder)
    {
        var prepare = await wpf.Client.PrepareRenameAsync(Uri(path), wpf.At(path, marker, delta), Ct);
        Assert.Equal(placeholder, prepare?.Placeholder);
        return await wpf.Client.RenameAsync(Uri(path), wpf.At(path, marker, delta), newName, Ct);
    }

    private async Task<CodeAction[]> ActionsAsync(string path, string marker, int delta)
    {
        var at = wpf.At(path, marker, delta);
        return await wpf.Client.CodeActionsAsync(Uri(path), new Protocol.Range(at, at), [], Ct);
    }

    [Fact]
    public async Task Renaming_a_view_model_property_in_csharp_renames_its_bindings()
    {
        await FreshAsync(ViewModel);
        await FreshAsync(MainWindow);
        var edit = await RenameAtAsync(ViewModel, "public string NewTitle", "public string New".Length, "Draft", "NewTitle");
        XamlGolden.Check("wpf-rename-property-from-csharp", Render(edit));
    }

    [Fact]
    public async Task Renaming_from_xaml_reaches_the_view_model_the_model_type_and_the_event_handler()
    {
        await FreshAsync(MainWindow);
        var path = await RenameAtAsync(MainWindow, "{Binding RemainingCount", "{Binding Rem".Length, "OpenCount", "RemainingCount");
        XamlGolden.Check("wpf-rename-binding-path-from-xaml", Render(path));

        var type = await RenameAtAsync(MainWindow, "{x:Type models:TodoItem}", "{x:Type models:To".Length, "TaskItem", "TodoItem");
        XamlGolden.Check("wpf-rename-type-from-xaml", Render(type));

        var handler = await RenameAtAsync(MainWindow, "Click=\"OnAddClicked\"", "Click=\"OnAdd".Length, "OnAdd", "OnAddClicked");
        XamlGolden.Check("wpf-rename-event-handler-from-xaml", Render(handler));
    }

    [Fact]
    public async Task Renaming_a_resource_key_reaches_the_merged_dictionary()
    {
        await FreshAsync(MainWindow);
        var edit = await RenameAtAsync(MainWindow, "{StaticResource AccentButton}", "{StaticResource Acc".Length, "PrimaryButton", "AccentButton");
        XamlGolden.Check("wpf-rename-resource-key", Render(edit));
    }

    [Fact]
    public async Task Extract_resource_style_and_a_generated_command()
    {
        await FreshAsync(MainWindow);
        var resources = await ActionsAsync(MainWindow, "Maximum=\"5\" controls:Highlight.IsOn=\"True\" Margin=\"12,0,0,0\"", "Maximum=\"5\" controls:Highlight.IsOn=\"True\" Margin=\"1".Length);
        var all = Assert.Single(resources, a => a.Title == "Extract Resource 'RatingControlMargin' and use it for 1 equal value");
        XamlGolden.Check("wpf-extract-resource-equal-values", Render(all.Edit));
        var app = Assert.Single(resources, a => a.Title == "Extract Resource 'RatingControlMargin' to App.xaml");
        XamlGolden.Check("wpf-extract-resource-to-app", Render(app.Edit));

        var style = Assert.Single(await ActionsAsync(MainWindow, "<TextBox x:Name", 2), a => a.Title.StartsWith("Extract Style", StringComparison.Ordinal));
        Assert.Equal("Extract Style 'TextBoxStyle'", style.Title);
        XamlGolden.Check("wpf-extract-style", Render(style.Edit));

        // Without CommunityToolkit.Mvvm: an ICommand property.
        await wpf.EditAsync(MainWindow, "Command=\"{Binding AddCommand}\"", "Command=\"{Binding ClearCommand}\"");
        var command = Assert.Single(await ActionsAsync(MainWindow, "{Binding ClearCommand}", "{Binding Cl".Length), a => a.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Generate command 'ClearCommand' in MainViewModel", command.Title);
        XamlGolden.Check("wpf-generate-command", Render(command.Edit));
        await FreshAsync(MainWindow);

        // A converter: WPF's IValueConverter, and a clr-namespace prefix.
        await wpf.EditAsync(MainWindow, "Text=\"{Binding Title}\" Style", "Text=\"{Binding Title, Converter={StaticResource UpperCaseConverter}}\" Style");
        var converter = Assert.Single(await ActionsAsync(MainWindow, "{StaticResource UpperCaseConverter}", "{StaticResource Up".Length), a => a.Kind == CodeActionKind.QuickFix);
        XamlGolden.Check("wpf-generate-converter", Render(converter.Edit));
        await FreshAsync(MainWindow);
    }
}
